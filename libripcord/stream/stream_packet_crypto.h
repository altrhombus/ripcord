/*
 * ripcord-3ds - the v1 per-packet stream crypto (HalyardPacketCrypto), one channel direction.
 *
 * AES-128-CTR payload encryption plus a 4-byte GMAC, both keyed off the packet's 64-bit keystream offset.
 * Encrypt-then-MAC: the tag is computed over the packet with its tag field (and, for some packet
 * classes, the following keystream-offset field) zeroed, so a receiver verifies the GMAC first and only then
 * AES-CTR-decrypts the payload.
 *
 * Nonces (16 bytes, little-endian addition into the base IV, modulo 2^128):
 *   GMAC nonce = baseIv + keystreamOffset/16
 *   CTR nonce  = baseIv + keystreamOffset/16 + 1        (one block above the GMAC's)
 *
 * The GMAC key rotates; the payload AES key does not:
 *   window 0:  gmacKey = fold(aesKey, baseIv)
 *   window n>=1: gmacKey = fold(gmacKey_window0, ivAdd(baseIv, n * 0xAF6E))     (n = (keystreamOffset - 1) / 45000, 0 at keystreamOffset 0)
 *   fold(a, b) = SHA-256(a || b)[0:16] XOR SHA-256(a || b)[16:32]
 *
 * AAD zeroing convention (which bytes of the packet are treated as zero when computing the tag) differs
 * by packet class - this is the one place this layer is genuinely protocol-specific rather than "just
 * GCM": A/V (tag offset 10) and input (tag offset 8) zero only the 4-byte tag; control (tag offset 5)
 * and loss reports (tag offset 7) zero the tag AND the following 4-byte keystream-offset field. Callers pass
 * `zero_keystream_offset` explicitly rather than this module guessing from the offset.
 *
 * Checked against cross-language vectors (tests/vectors/stream-crypto.kat's `packetnonce` and
 * `packettag` lines, generated via HalyardPacketCrypto directly) covering the rotation-window boundary,
 * the 32-/64-bit keystream-offset edges, and both AAD-zeroing conventions.
 */
#ifndef STREAM_PACKET_CRYPTO_H
#define STREAM_PACKET_CRYPTO_H

#include <stddef.h>
#include <stdint.h>

#include "../crypto/rc_gcm.h"

#define STREAM_PACKET_CRYPTO_TAG_LENGTH 4
#define STREAM_PACKET_CRYPTO_AV_TAG_OFFSET 10
#define STREAM_PACKET_CRYPTO_INPUT_TAG_OFFSET 8

/* The largest packet this module will compute a tag over. Generous for control, loss-report and A/V packets
 * at this protocol's MTU; compute_tag/seal/verify fail closed (return 0) rather than overrun past it. */
#define STREAM_PACKET_CRYPTO_MAX_PACKET 2048

typedef struct {
    uint8_t aes_key[16];
    uint8_t base_iv[16];
    uint8_t gmac_base_key[16]; /* fold(aes_key, base_iv) - the window-0 GMAC key */
    /*
     * The prepared GMAC key for whichever rotation window was last used, so the GHASH tables are built
     * once per window rather than once per packet. The protocol rotates every 45,000 keystream-offset units
     * - roughly 650 packets - so one build serves hundreds of tags, which is the entire reason the table
     * is affordable at all (see rc_gcm.h).
     *
     * This makes the struct ~4.8 KB. It must not be a stack local in the 3DS build; both live instances
     * are file-scope in source/connect, and -Wframe-larger-than=8192 catches any attempt otherwise.
     *
     * `mutable` in spirit: the tag functions take a const context and this is a pure cache of a value
     * derived from it, hence the const-cast at the one call site, explained there.
     */
    rc_gmac_key prepared;
} stream_packet_crypto;

void stream_packet_crypto_init(stream_packet_crypto *ctx, const uint8_t aes_key[16], const uint8_t base_iv[16]);

void stream_packet_crypto_gmac_nonce(const stream_packet_crypto *ctx, uint64_t keystream_offset, uint8_t out[16]);
void stream_packet_crypto_ctr_nonce(const stream_packet_crypto *ctx, uint64_t keystream_offset, uint8_t out[16]);
void stream_packet_crypto_gmac_key(const stream_packet_crypto *ctx, uint64_t keystream_offset, uint8_t out[16]);

/* AES-128-CTR encrypt/decrypt `payload` in place (symmetric - the same call both ways). */
void stream_packet_crypto_crypt_payload(const stream_packet_crypto *ctx, uint64_t keystream_offset,
                                       uint8_t *payload, size_t payload_length);

/*
 * Computes the 4-byte tag over `packet` as it stands on the wire (encrypted payload), with the AAD
 * zeroing described above applied to a scratch copy - `packet` itself is never modified. Returns 1 on
 * success, 0 if packet_length exceeds STREAM_PACKET_CRYPTO_MAX_PACKET.
 */
int stream_packet_crypto_compute_tag(const stream_packet_crypto *ctx, uint64_t keystream_offset,
                                     const uint8_t *packet, size_t packet_length,
                                     int tag_offset, int zero_keystream_offset, uint8_t out_tag[4]);

/* Writes the computed tag into packet[tag_offset .. tag_offset+4) in place (sender side). Returns 1 on
 * success, 0 on the same failure condition as compute_tag. */
int stream_packet_crypto_seal(const stream_packet_crypto *ctx, uint64_t keystream_offset,
                             uint8_t *packet, size_t packet_length, int tag_offset, int zero_keystream_offset);

/* Verifies packet's on-wire tag (receiver side). Returns 1 if it matches, 0 if it does not or
 * packet_length exceeds STREAM_PACKET_CRYPTO_MAX_PACKET. */
int stream_packet_crypto_verify(const stream_packet_crypto *ctx, uint64_t keystream_offset,
                                const uint8_t *packet, size_t packet_length, int tag_offset, int zero_keystream_offset);

#endif /* STREAM_PACKET_CRYPTO_H */
