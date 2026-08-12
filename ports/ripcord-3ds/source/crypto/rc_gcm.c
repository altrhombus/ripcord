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
static void gf128_mul(uint8_t x[BLOCK_SIZE], const uint8_t y[BLOCK_SIZE])
{
    uint8_t z[BLOCK_SIZE];
    uint8_t v[BLOCK_SIZE];
    int i;

    memset(z, 0, sizeof(z));
    memcpy(v, y, sizeof(v));

    for (i = 0; i < 128; i++) {
        /* Bit i of x, taken from the MSB of byte 0 downward. */
        int bit = (x[i >> 3] >> (7 - (i & 7))) & 1;
        if (bit)
            xor_block(z, v);

        /* v = v >> 1 in GF(2^128); if the bit shifted out (LSB) was 1, reduce with R = 0xE1 || 0^120. */
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
    }

    memcpy(x, z, sizeof(z));
}

static void ghash_block(uint8_t y[BLOCK_SIZE], const uint8_t h[BLOCK_SIZE], const uint8_t block[BLOCK_SIZE])
{
    xor_block(y, block);
    gf128_mul(y, h);
}

/* GHASH over arbitrary-length data: full blocks, then one zero-padded partial block if the length isn't
 * a multiple of 16 - matching AesGcmCore.GHashData exactly. */
static void ghash_data(uint8_t y[BLOCK_SIZE], const uint8_t h[BLOCK_SIZE], const uint8_t *data, size_t length)
{
    size_t full = length / BLOCK_SIZE;
    size_t i;
    size_t remainder = length - full * BLOCK_SIZE;

    for (i = 0; i < full; i++)
        ghash_block(y, h, data + i * BLOCK_SIZE);

    if (remainder > 0) {
        uint8_t block[BLOCK_SIZE];
        memset(block, 0, sizeof(block));
        memcpy(block, data + full * BLOCK_SIZE, remainder);
        ghash_block(y, h, block);
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
static void compute_j0(uint8_t j0[BLOCK_SIZE], const uint8_t h[BLOCK_SIZE], const uint8_t *iv, size_t iv_len)
{
    uint8_t y[BLOCK_SIZE];
    uint8_t len_block[BLOCK_SIZE];

    memset(y, 0, sizeof(y));
    ghash_data(y, h, iv, iv_len);

    memset(len_block, 0, sizeof(len_block));
    write_be64(len_block + 8, (uint64_t)iv_len * 8);
    ghash_block(y, h, len_block);

    memcpy(j0, y, BLOCK_SIZE);
}

void rc_gmac(const uint8_t key[16], const uint8_t *iv, size_t iv_len,
            const uint8_t *aad, size_t aad_len, uint8_t tag[16])
{
    rc_aes128 ctx;
    uint8_t h[BLOCK_SIZE];
    uint8_t j0[BLOCK_SIZE];
    uint8_t s[BLOCK_SIZE];
    uint8_t len_block[BLOCK_SIZE];
    uint8_t ej0[BLOCK_SIZE];
    int i;

    rc_aes128_init(&ctx, key);

    memset(h, 0, sizeof(h));
    rc_aes128_encrypt_block(&ctx, h, h); /* H = E_K(0^128) */

    compute_j0(j0, h, iv, iv_len);

    /* S = GHASH(AAD || 0-pad || [nothing, no ciphertext] || [len(AAD)]_64 || [len(C)=0]_64) */
    memset(s, 0, sizeof(s));
    ghash_data(s, h, aad, aad_len);

    memset(len_block, 0, sizeof(len_block));
    write_be64(len_block, (uint64_t)aad_len * 8);
    write_be64(len_block + 8, 0); /* ciphertext length is always 0 - see the file header comment */
    ghash_block(s, h, len_block);

    /* tag = E_K(J0) XOR S (GCTR of a single block is just one AES-ECB block) */
    rc_aes128_encrypt_block(&ctx, j0, ej0);
    for (i = 0; i < BLOCK_SIZE; i++)
        tag[i] = (uint8_t)(ej0[i] ^ s[i]);
}
