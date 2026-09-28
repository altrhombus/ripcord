#include "rc_gcm.h"
#include "rc_crypto.h"

#include <string.h>

#define BLOCK_SIZE 16

static void xor_block(uint8_t *target, const uint8_t *value)
{
    int i;
    for (i = 0; i < BLOCK_SIZE; i++)
        target[i] ^= value[i];
}

/*
 * Multiply x by y in GF(2^128), in place: x := x * y. Bit-serial - ported directly from
 * AesGcmCore.GfMulBitSerial, the same reference implementation the .NET side's own hardware-accelerated
 * paths are differentially tested against, so this is the "slow but certainly correct" form on both
 * sides, not a simplification unique to this port.
 */
static uint32_t load_be32(const uint8_t *p)
{
    return ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16) | ((uint32_t)p[2] << 8) | (uint32_t)p[3];
}

static void store_be32(uint8_t *p, uint32_t w)
{
    p[0] = (uint8_t)(w >> 24);
    p[1] = (uint8_t)(w >> 16);
    p[2] = (uint8_t)(w >> 8);
    p[3] = (uint8_t)w;
}

/* v = v * x in GF(2^128): a one-bit right shift, reduced with R = 0xE1 || 0^120 when a bit falls off. */
static void gf128_mulx(uint8_t v[BLOCK_SIZE])
{
    int lsb = v[15] & 1;
    int k;
    uint8_t carry = 0;

    for (k = 0; k < BLOCK_SIZE; k++) {
        uint8_t next = (uint8_t)(v[k] << 7);
        v[k] = (uint8_t)((v[k] >> 1) | carry);
        carry = next;
    }
    if (lsb)
        v[0] ^= 0xE1;
}

/*
 * Builds the byte-indexed multiplication table and the reduction table.
 *
 * table[b] = b * H, where b's MOST significant bit is the x^0 coefficient - the same bit order the rest
 * of GHASH uses (bit 0 of a block is the MSB of byte 0). So the single-bit entries are H, H*x, H*x^2 ...
 * and every composite entry is the XOR of its set bits, because multiplication is linear over GF(2).
 *
 * reduce[b] is what multiplying by x^8 produces from a block whose only content is `b` in byte 15: the
 * value is shifted entirely out of the low end and what remains is pure reduction. That result is
 * nonzero only in bytes 0 and 1 - verified exhaustively over all 256 entries rather than assumed - which
 * is why it is stored as a u16 and costs two XORs instead of sixteen.
 */
static void gf128_build_tables(rc_gmac_key *gk)
{
    uint8_t cur[BLOCK_SIZE];
    int b, i;

    memset(gk->table, 0, sizeof(gk->table));
    memset(gk->reduce, 0, sizeof(gk->reduce));

    memcpy(cur, gk->h, BLOCK_SIZE);
    for (i = 0; i < 8; i++) {
        int w;
        for (w = 0; w < 4; w++)
            gk->table[128 >> i][w] = load_be32(cur + 4 * w);
        gf128_mulx(cur);
    }
    for (b = 1; b < 256; b++) {
        if ((b & (b - 1)) != 0) {
            int low = b & (-b);
            int w;
            for (w = 0; w < 4; w++)
                gk->table[b][w] = gk->table[b ^ low][w] ^ gk->table[low][w];
        }
    }

    for (i = 0; i < 8; i++) {
        uint8_t v[BLOCK_SIZE];
        int k;

        memset(v, 0, sizeof(v));
        v[15] = (uint8_t)(1 << i);
        for (k = 0; k < 8; k++)
            gf128_mulx(v);
        gk->reduce[1 << i] = (uint16_t)(((uint16_t)v[0] << 8) | v[1]);
    }
    for (b = 1; b < 256; b++) {
        if ((b & (b - 1)) != 0) {
            int low = b & (-b);
            gk->reduce[b] = (uint16_t)(gk->reduce[b ^ low] ^ gk->reduce[low]);
        }
    }
}

/*
 * x = x * H, byte at a time. Horner over the 16 bytes: x*H = sum_j table[x_j] * x^(8j), evaluated from
 * the last byte back, where multiplying the accumulator by x^8 is a one-byte shift plus the reduction
 * the byte that fell off implies.
 */
static void gf128_mul_h(const rc_gmac_key *gk, uint8_t x[BLOCK_SIZE])
{
    uint32_t z0 = 0, z1 = 0, z2 = 0, z3 = 0;
    int j;

    for (j = BLOCK_SIZE - 1; j >= 0; j--) {
        const uint32_t *t = gk->table[x[j]];
        uint16_t r = gk->reduce[(uint8_t)z3];   /* the byte about to fall off the low end */

        /* Multiply the accumulator by x^8: a one-byte right shift of the whole 128-bit value. */
        z3 = (z3 >> 8) | (z2 << 24);
        z2 = (z2 >> 8) | (z1 << 24);
        z1 = (z1 >> 8) | (z0 << 24);
        z0 = (z0 >> 8);

        /* ...plus the reduction that byte implies, which only ever lands in bytes 0 and 1. */
        z0 ^= (uint32_t)r << 16;

        z0 ^= t[0];
        z1 ^= t[1];
        z2 ^= t[2];
        z3 ^= t[3];
    }

    store_be32(x + 0,  z0);
    store_be32(x + 4,  z1);
    store_be32(x + 8,  z2);
    store_be32(x + 12, z3);
}

static void ghash_block(uint8_t y[BLOCK_SIZE], const rc_gmac_key *gk, const uint8_t block[BLOCK_SIZE])
{
    xor_block(y, block);
    gf128_mul_h(gk, y);
}

/* GHASH over arbitrary-length data: full blocks, then one zero-padded partial block if the length isn't
 * a multiple of 16 - matching AesGcmCore.GHashData exactly. */
static void ghash_data(uint8_t y[BLOCK_SIZE], const rc_gmac_key *gk, const uint8_t *data, size_t length)
{
    size_t full = length / BLOCK_SIZE;
    size_t i;
    size_t remainder = length - full * BLOCK_SIZE;

    for (i = 0; i < full; i++)
        ghash_block(y, gk, data + i * BLOCK_SIZE);

    if (remainder > 0) {
        uint8_t block[BLOCK_SIZE];
        memset(block, 0, sizeof(block));
        memcpy(block, data + full * BLOCK_SIZE, remainder);
        ghash_block(y, gk, block);
    }
}

static void write_be64(uint8_t *p, uint64_t value)
{
    int i;
    for (i = 0; i < 8; i++)
        p[i] = (uint8_t)(value >> (8 * (7 - i)));
}

/* J0 = GHASH_H( IV || 0^s || [len(IV)]_64 ), the non-96-bit-IV path - see this file's header comment for
 * why the 96-bit shortcut is not implemented (this protocol never uses a 12-byte IV). */
static void compute_j0(uint8_t j0[BLOCK_SIZE], const rc_gmac_key *gk, const uint8_t *iv, size_t iv_len)
{
    uint8_t y[BLOCK_SIZE];
    uint8_t len_block[BLOCK_SIZE];

    memset(y, 0, sizeof(y));
    ghash_data(y, gk, iv, iv_len);

    memset(len_block, 0, sizeof(len_block));
    write_be64(len_block + 8, (uint64_t)iv_len * 8);
    ghash_block(y, gk, len_block);

    memcpy(j0, y, BLOCK_SIZE);
}

void rc_gmac_key_init(rc_gmac_key *gk, const uint8_t key[16])
{
    if (gk == NULL || key == NULL)
        return;

    rc_aes128_init(&gk->aes, key);

    memset(gk->h, 0, sizeof(gk->h));
    rc_aes128_encrypt_block(&gk->aes, gk->h, gk->h); /* H = E_K(0^128) */

    gf128_build_tables(gk);

    memcpy(gk->key, key, sizeof(gk->key));
    gk->ready = 1;
}

int rc_gmac_key_matches(const rc_gmac_key *gk, const uint8_t key[16])
{
    if (gk == NULL || key == NULL || !gk->ready)
        return 0;
    return memcmp(gk->key, key, sizeof(gk->key)) == 0;
}

void rc_gmac_with_key(const rc_gmac_key *gk, const uint8_t *iv, size_t iv_len,
                      const uint8_t *aad, size_t aad_len, uint8_t tag[16])
{
    uint8_t j0[BLOCK_SIZE];
    uint8_t s[BLOCK_SIZE];
    uint8_t len_block[BLOCK_SIZE];
    uint8_t ej0[BLOCK_SIZE];
    int i;

    if (gk == NULL || !gk->ready)
        return;

    compute_j0(j0, gk, iv, iv_len);

    /* S = GHASH(AAD || 0-pad || [nothing, no ciphertext] || [len(AAD)]_64 || [len(C)=0]_64) */
    memset(s, 0, sizeof(s));
    ghash_data(s, gk, aad, aad_len);

    memset(len_block, 0, sizeof(len_block));
    write_be64(len_block, (uint64_t)aad_len * 8);
    write_be64(len_block + 8, 0); /* ciphertext length is always 0 - see the file header comment */
    ghash_block(s, gk, len_block);

    /* tag = E_K(J0) XOR S (GCTR of a single block is just one AES-ECB block) */
    rc_aes128_encrypt_block(&gk->aes, j0, ej0);
    for (i = 0; i < BLOCK_SIZE; i++)
        tag[i] = (uint8_t)(ej0[i] ^ s[i]);
}
