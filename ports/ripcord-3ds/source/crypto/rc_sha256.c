/*
 * SHA-256 (FIPS 180-4) and HMAC-SHA-256 (RFC 2104).
 *
 * The control plane needs these for exactly one thing - the per-field IV derivation in
 * source/halyard/halyard_field_iv.c - but that one thing runs on every encrypted header field of every
 * connect, so it is worth having rather than pulling in a TLS stack for.
 */
#include "rc_crypto.h"

#include <string.h>

static const uint32_t kRoundConstants[64] = {
    0x428a2f98u, 0x71374491u, 0xb5c0fbcfu, 0xe9b5dba5u, 0x3956c25bu, 0x59f111f1u, 0x923f82a4u, 0xab1c5ed5u,
    0xd807aa98u, 0x12835b01u, 0x243185beu, 0x550c7dc3u, 0x72be5d74u, 0x80deb1feu, 0x9bdc06a7u, 0xc19bf174u,
    0xe49b69c1u, 0xefbe4786u, 0x0fc19dc6u, 0x240ca1ccu, 0x2de92c6fu, 0x4a7484aau, 0x5cb0a9dcu, 0x76f988dau,
    0x983e5152u, 0xa831c66du, 0xb00327c8u, 0xbf597fc7u, 0xc6e00bf3u, 0xd5a79147u, 0x06ca6351u, 0x14292967u,
    0x27b70a85u, 0x2e1b2138u, 0x4d2c6dfcu, 0x53380d13u, 0x650a7354u, 0x766a0abbu, 0x81c2c92eu, 0x92722c85u,
    0xa2bfe8a1u, 0xa81a664bu, 0xc24b8b70u, 0xc76c51a3u, 0xd192e819u, 0xd6990624u, 0xf40e3585u, 0x106aa070u,
    0x19a4c116u, 0x1e376c08u, 0x2748774cu, 0x34b0bcb5u, 0x391c0cb3u, 0x4ed8aa4au, 0x5b9cca4fu, 0x682e6ff3u,
    0x748f82eeu, 0x78a5636fu, 0x84c87814u, 0x8cc70208u, 0x90befffau, 0xa4506cebu, 0xbef9a3f7u, 0xc67178f2u
};

static uint32_t rotr32(uint32_t value, int bits)
{
    return (value >> bits) | (value << (32 - bits));
}

static void sha256_compress(uint32_t state[8], const uint8_t block[RC_SHA256_BLOCK_SIZE])
{
    uint32_t w[64];
    uint32_t a, b, c, d, e, f, g, h;
    int i;

    /* Message schedule. Big-endian load, explicitly: the 3DS ARM11 is little-endian, so a memcpy here
     * would be wrong on the target and right on nothing. */
    for (i = 0; i < 16; i++) {
        w[i] = ((uint32_t)block[i * 4] << 24)
             | ((uint32_t)block[i * 4 + 1] << 16)
             | ((uint32_t)block[i * 4 + 2] << 8)
             | ((uint32_t)block[i * 4 + 3]);
    }
    for (i = 16; i < 64; i++) {
        uint32_t s0 = rotr32(w[i - 15], 7) ^ rotr32(w[i - 15], 18) ^ (w[i - 15] >> 3);
        uint32_t s1 = rotr32(w[i - 2], 17) ^ rotr32(w[i - 2], 19) ^ (w[i - 2] >> 10);
        w[i] = w[i - 16] + s0 + w[i - 7] + s1;
    }

    a = state[0]; b = state[1]; c = state[2]; d = state[3];
    e = state[4]; f = state[5]; g = state[6]; h = state[7];

    for (i = 0; i < 64; i++) {
        uint32_t s1 = rotr32(e, 6) ^ rotr32(e, 11) ^ rotr32(e, 25);
        uint32_t ch = (e & f) ^ (~e & g);
        uint32_t temp1 = h + s1 + ch + kRoundConstants[i] + w[i];
        uint32_t s0 = rotr32(a, 2) ^ rotr32(a, 13) ^ rotr32(a, 22);
        uint32_t maj = (a & b) ^ (a & c) ^ (b & c);
        uint32_t temp2 = s0 + maj;

        h = g; g = f; f = e;
        e = d + temp1;
        d = c; c = b; b = a;
        a = temp1 + temp2;
    }

    state[0] += a; state[1] += b; state[2] += c; state[3] += d;
    state[4] += e; state[5] += f; state[6] += g; state[7] += h;
}

void rc_sha256_init(rc_sha256 *ctx)
{
    ctx->state[0] = 0x6a09e667u; ctx->state[1] = 0xbb67ae85u;
    ctx->state[2] = 0x3c6ef372u; ctx->state[3] = 0xa54ff53au;
    ctx->state[4] = 0x510e527fu; ctx->state[5] = 0x9b05688cu;
    ctx->state[6] = 0x1f83d9abu; ctx->state[7] = 0x5be0cd19u;
    ctx->bit_length = 0;
    ctx->buffered = 0;
}

void rc_sha256_update(rc_sha256 *ctx, const uint8_t *data, size_t length)
{
    ctx->bit_length += (uint64_t)length * 8u;

    /* Top up a partial buffer first, then run whole blocks straight from the caller's memory. */
    if (ctx->buffered > 0) {
        size_t needed = RC_SHA256_BLOCK_SIZE - ctx->buffered;
        size_t take = (length < needed) ? length : needed;

        memcpy(ctx->buffer + ctx->buffered, data, take);
        ctx->buffered += take;
        data += take;
        length -= take;

        if (ctx->buffered == RC_SHA256_BLOCK_SIZE) {
            sha256_compress(ctx->state, ctx->buffer);
            ctx->buffered = 0;
        }
    }

    while (length >= RC_SHA256_BLOCK_SIZE) {
        sha256_compress(ctx->state, data);
        data += RC_SHA256_BLOCK_SIZE;
        length -= RC_SHA256_BLOCK_SIZE;
    }

    if (length > 0) {
        memcpy(ctx->buffer, data, length);
        ctx->buffered = length;
    }
}

void rc_sha256_final(rc_sha256 *ctx, uint8_t digest[RC_SHA256_DIGEST_SIZE])
{
    uint64_t bit_length = ctx->bit_length;
    size_t i;

    /* 0x80, then zeros, then the 64-bit big-endian length in the last 8 bytes. If the 0x80 and the length
     * will not both fit in this block, the length goes in the next one. */
    ctx->buffer[ctx->buffered++] = 0x80;
    if (ctx->buffered > RC_SHA256_BLOCK_SIZE - 8) {
        memset(ctx->buffer + ctx->buffered, 0, RC_SHA256_BLOCK_SIZE - ctx->buffered);
        sha256_compress(ctx->state, ctx->buffer);
        ctx->buffered = 0;
    }
    memset(ctx->buffer + ctx->buffered, 0, RC_SHA256_BLOCK_SIZE - 8 - ctx->buffered);

    for (i = 0; i < 8; i++)
        ctx->buffer[RC_SHA256_BLOCK_SIZE - 1 - i] = (uint8_t)(bit_length >> (8 * i));

    sha256_compress(ctx->state, ctx->buffer);

    for (i = 0; i < 8; i++) {
        digest[i * 4]     = (uint8_t)(ctx->state[i] >> 24);
        digest[i * 4 + 1] = (uint8_t)(ctx->state[i] >> 16);
        digest[i * 4 + 2] = (uint8_t)(ctx->state[i] >> 8);
        digest[i * 4 + 3] = (uint8_t)(ctx->state[i]);
    }
}

void rc_sha256_hash(const uint8_t *data, size_t length, uint8_t digest[RC_SHA256_DIGEST_SIZE])
{
    rc_sha256 ctx;
    rc_sha256_init(&ctx);
    rc_sha256_update(&ctx, data, length);
    rc_sha256_final(&ctx, digest);
}

void rc_hmac_sha256(const uint8_t *key, size_t key_length,
                    const uint8_t *message, size_t message_length,
                    uint8_t mac[RC_SHA256_DIGEST_SIZE])
{
    uint8_t padded_key[RC_SHA256_BLOCK_SIZE];
    uint8_t pad[RC_SHA256_BLOCK_SIZE];
    uint8_t inner[RC_SHA256_DIGEST_SIZE];
    rc_sha256 ctx;
    size_t i;

    /* RFC 2104: keys longer than the block size are hashed first; shorter ones are zero-padded. Every key
     * this port passes is 16 bytes, so only the padding branch is exercised in practice - the long-key
     * branch is here so the primitive is not quietly wrong for the next caller. */
    memset(padded_key, 0, sizeof(padded_key));
    if (key_length > RC_SHA256_BLOCK_SIZE)
        rc_sha256_hash(key, key_length, padded_key);
    else
        memcpy(padded_key, key, key_length);

    for (i = 0; i < RC_SHA256_BLOCK_SIZE; i++)
        pad[i] = (uint8_t)(padded_key[i] ^ 0x36);

    rc_sha256_init(&ctx);
    rc_sha256_update(&ctx, pad, RC_SHA256_BLOCK_SIZE);
    rc_sha256_update(&ctx, message, message_length);
    rc_sha256_final(&ctx, inner);

    for (i = 0; i < RC_SHA256_BLOCK_SIZE; i++)
        pad[i] = (uint8_t)(padded_key[i] ^ 0x5c);

    rc_sha256_init(&ctx);
    rc_sha256_update(&ctx, pad, RC_SHA256_BLOCK_SIZE);
    rc_sha256_update(&ctx, inner, RC_SHA256_DIGEST_SIZE);
    rc_sha256_final(&ctx, mac);
}
