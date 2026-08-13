#include "stream_packet_crypto.h"
#include "../crypto/rc_crypto.h"
#include "../crypto/rc_gcm.h"

#include <stdint.h>
#include <string.h>

static uint64_t read_le64(const uint8_t *p)
{
    uint64_t v = 0;
    int i;
    for (i = 7; i >= 0; i--)
        v = (v << 8) | p[i];
    return v;
}

static void write_le64(uint8_t *p, uint64_t v)
{
    int i;
    for (i = 0; i < 8; i++)
        p[i] = (uint8_t)(v >> (8 * i));
}

/* Add a 64-bit addend to a 16-byte little-endian IV, modulo 2^128. Every caller here derives the addend
 * from a key position (>>4), so 64 bits is always enough - matches HalyardPacketCrypto.IvAddFast. */
static void iv_add_fast(const uint8_t iv[16], uint64_t addend, uint8_t out[16])
{
    uint64_t low = read_le64(iv);
    uint64_t high = read_le64(iv + 8);
    uint64_t sum = low + addend;

    if (sum < low)
        high++; /* carry into the high half; wrapping past that is the mod-2^128 behaviour we want */

    write_le64(out, sum);
    write_le64(out + 8, high);
}

/* Full 64x64 -> 128-bit unsigned multiply without __int128 (a GNU extension this file doesn't assume),
 * via the standard four-partial-product widening technique. */
static void mul64x64_128(uint64_t a, uint64_t b, uint64_t *out_high, uint64_t *out_low)
{
    uint64_t a_lo = (uint32_t)a, a_hi = a >> 32;
    uint64_t b_lo = (uint32_t)b, b_hi = b >> 32;
    uint64_t lo_lo = a_lo * b_lo;
    uint64_t hi_lo = a_hi * b_lo;
    uint64_t lo_hi = a_lo * b_hi;
    uint64_t hi_hi = a_hi * b_hi;
    uint64_t cross = hi_lo + (lo_lo >> 32) + (uint32_t)lo_hi;

    *out_low = (cross << 32) | (uint32_t)lo_lo;
    *out_high = hi_hi + (cross >> 32) + (lo_hi >> 32);
}

/* Add (multiplier * factor) to a 16-byte little-endian IV, modulo 2^128 - matches HalyardPacketCrypto's
 * BigInteger-based IvAdd, used only for the GMAC key rotation, where `window * RotationFactor` can
 * exceed 64 bits and IvAddFast's single-limb addend would silently truncate it. */
static void iv_add_wide(const uint8_t iv[16], uint64_t multiplier, uint64_t factor, uint8_t out[16])
{
    uint64_t prod_high, prod_low;
    uint64_t iv_low = read_le64(iv);
    uint64_t iv_high = read_le64(iv + 8);
    uint64_t sum_low, sum_high;
    int carry;

    mul64x64_128(multiplier, factor, &prod_high, &prod_low);

    sum_low = iv_low + prod_low;
    carry = (sum_low < iv_low) ? 1 : 0;
    sum_high = iv_high + prod_high + (uint64_t)carry; /* mod 2^128 - further overflow is simply discarded */

    write_le64(out, sum_low);
    write_le64(out + 8, sum_high);
}

/* fold(a, b) = SHA-256(a || b)[0:16] XOR SHA-256(a || b)[16:32], for two 16-byte inputs. */
static void fold(const uint8_t a[16], const uint8_t b[16], uint8_t out[16])
{
    uint8_t input[32];
    uint8_t digest[32];
    int i;

    memcpy(input, a, 16);
    memcpy(input + 16, b, 16);
    rc_sha256_hash(input, sizeof(input), digest);
    for (i = 0; i < 16; i++)
        out[i] = (uint8_t)(digest[i] ^ digest[i + 16]);
}

void stream_packet_crypto_init(stream_packet_crypto *ctx, const uint8_t aes_key[16], const uint8_t base_iv[16])
{
    memcpy(ctx->aes_key, aes_key, 16);
    memcpy(ctx->base_iv, base_iv, 16);
    fold(ctx->aes_key, ctx->base_iv, ctx->gmac_base_key);
}

void stream_packet_crypto_gmac_nonce(const stream_packet_crypto *ctx, uint64_t key_pos, uint8_t out[16])
{
    iv_add_fast(ctx->base_iv, key_pos >> 4, out);
}

void stream_packet_crypto_ctr_nonce(const stream_packet_crypto *ctx, uint64_t key_pos, uint8_t out[16])
{
    iv_add_fast(ctx->base_iv, (key_pos + 16) >> 4, out);
}

void stream_packet_crypto_gmac_key(const stream_packet_crypto *ctx, uint64_t key_pos, uint8_t out[16])
{
    uint64_t window = key_pos / 45000u;

    if (window == 0) {
        memcpy(out, ctx->gmac_base_key, 16);
        return;
    }

    {
        uint8_t rotated_iv[16];
        iv_add_wide(ctx->base_iv, window, 0xAF6Eu, rotated_iv);
        fold(ctx->gmac_base_key, rotated_iv, out);
    }
}

void stream_packet_crypto_crypt_payload(const stream_packet_crypto *ctx, uint64_t key_pos,
                                       uint8_t *payload, size_t payload_length)
{
    uint8_t nonce[16];

    stream_packet_crypto_ctr_nonce(ctx, key_pos, nonce);
    /* Little-endian counter increment - the PS5 packet cipher's own convention, matching its GMAC nonce
     * derivation above (both add into the IV little-endian). */
    rc_aes128_ctr(ctx->aes_key, nonce, payload, payload, payload_length, 1);
}

int stream_packet_crypto_compute_tag(const stream_packet_crypto *ctx, uint64_t key_pos,
                                     const uint8_t *packet, size_t packet_length,
                                     int tag_offset, int zero_key_pos, uint8_t out_tag[4])
{
    uint8_t aad[STREAM_PACKET_CRYPTO_MAX_PACKET];
    size_t clear_len;
    uint8_t gmac_key[16];
    uint8_t nonce[16];
    uint8_t full_tag[16];

    if (packet_length > STREAM_PACKET_CRYPTO_MAX_PACKET || (size_t)tag_offset > packet_length)
        return 0;

    memcpy(aad, packet, packet_length);

    clear_len = (size_t)STREAM_PACKET_CRYPTO_TAG_LENGTH + (zero_key_pos ? 4u : 0u);
    if (clear_len > packet_length - (size_t)tag_offset)
        clear_len = packet_length - (size_t)tag_offset;
    memset(aad + tag_offset, 0, clear_len);

    stream_packet_crypto_gmac_key(ctx, key_pos, gmac_key);
    stream_packet_crypto_gmac_nonce(ctx, key_pos, nonce);

    /*
     * Build the GHASH tables only when the rotation window has actually changed. The const-cast is
     * deliberate and narrow: `prepared` is a cache of a value derived entirely from the context's own
     * key material, so refreshing it does not change what this function computes - only how long it
     * takes. Keeping the public signature const matters more than the purity here, because every caller
     * treats the crypto context as read-only and should continue to.
     */
    {
        stream_packet_crypto *mutable_ctx = (stream_packet_crypto *)(uintptr_t)ctx;

        if (!rc_gmac_key_matches(&mutable_ctx->prepared, gmac_key))
            rc_gmac_key_init(&mutable_ctx->prepared, gmac_key);
        rc_gmac_with_key(&mutable_ctx->prepared, nonce, sizeof(nonce), aad, packet_length, full_tag);
    }
    memcpy(out_tag, full_tag, STREAM_PACKET_CRYPTO_TAG_LENGTH);
    return 1;
}

int stream_packet_crypto_seal(const stream_packet_crypto *ctx, uint64_t key_pos,
                             uint8_t *packet, size_t packet_length, int tag_offset, int zero_key_pos)
{
    uint8_t tag[4];

    if (!stream_packet_crypto_compute_tag(ctx, key_pos, packet, packet_length, tag_offset, zero_key_pos, tag))
        return 0;
    memcpy(packet + tag_offset, tag, sizeof(tag));
    return 1;
}

int stream_packet_crypto_verify(const stream_packet_crypto *ctx, uint64_t key_pos,
                                const uint8_t *packet, size_t packet_length, int tag_offset, int zero_key_pos)
{
    uint8_t expected[4];

    if (!stream_packet_crypto_compute_tag(ctx, key_pos, packet, packet_length, tag_offset, zero_key_pos, expected))
        return 0;
    return memcmp(expected, packet + tag_offset, sizeof(expected)) == 0;
}
