/*
 * AES-128 keystream cipher modes (NIST SP 800-38A): CFB128, OFB, CTR.
 *
 * These mirror Ripcord.Core.Net.Crypto.AesKeystreamModes byte for byte, and the vector runner checks
 * exactly that. The .NET side implements them by hand for the same reason this does: the protocol needs
 * full CFB with a partial trailing block, OFB, and a CTR whose counter increments across the whole 128-bit
 * width in a caller-chosen byte order - none of which the one-shot platform helpers offer.
 */
#include "rc_crypto.h"

#include <string.h>

void rc_aes128_cfb128(const uint8_t key[RC_AES128_KEY_SIZE],
                      const uint8_t iv[RC_AES_BLOCK_SIZE],
                      const uint8_t *in, uint8_t *out, size_t length,
                      int encrypt)
{
    rc_aes128 ctx;
    uint8_t feedback[RC_AES_BLOCK_SIZE];
    uint8_t keystream[RC_AES_BLOCK_SIZE];
    uint8_t cipher_block[RC_AES_BLOCK_SIZE];
    size_t offset;

    rc_aes128_init(&ctx, key);
    memcpy(feedback, iv, RC_AES_BLOCK_SIZE);

    for (offset = 0; offset < length; offset += RC_AES_BLOCK_SIZE) {
        size_t remaining = length - offset;
        size_t n = (remaining < RC_AES_BLOCK_SIZE) ? remaining : RC_AES_BLOCK_SIZE;
        size_t j;

        rc_aes128_encrypt_block(&ctx, feedback, keystream);

        for (j = 0; j < n; j++) {
            uint8_t input = in[offset + j];
            uint8_t result = (uint8_t)(input ^ keystream[j]);

            /* Read `input` before writing `out`, so in == out (in-place) stays correct. */
            out[offset + j] = result;
            cipher_block[j] = encrypt ? result : input;
        }

        /* Only a full block feeds forward; a short final block ends the stream. */
        if (n == RC_AES_BLOCK_SIZE)
            memcpy(feedback, cipher_block, RC_AES_BLOCK_SIZE);
    }
}

void rc_aes128_ofb(const uint8_t key[RC_AES128_KEY_SIZE],
                   const uint8_t iv[RC_AES_BLOCK_SIZE],
                   const uint8_t *in, uint8_t *out, size_t length)
{
    rc_aes128 ctx;
    uint8_t feedback[RC_AES_BLOCK_SIZE];
    size_t offset;

    rc_aes128_init(&ctx, key);
    memcpy(feedback, iv, RC_AES_BLOCK_SIZE);

    for (offset = 0; offset < length; offset += RC_AES_BLOCK_SIZE) {
        size_t remaining = length - offset;
        size_t n = (remaining < RC_AES_BLOCK_SIZE) ? remaining : RC_AES_BLOCK_SIZE;
        size_t j;

        /* O_{i+1} = E(O_i): the feedback chain is the keystream, independent of the data. */
        rc_aes128_encrypt_block(&ctx, feedback, feedback);

        for (j = 0; j < n; j++)
            out[offset + j] = (uint8_t)(in[offset + j] ^ feedback[j]);
    }
}

static void increment_big_endian(uint8_t counter[RC_AES_BLOCK_SIZE])
{
    int i;
    for (i = RC_AES_BLOCK_SIZE - 1; i >= 0; i--) {
        if (++counter[i] != 0)
            break;
    }
}

static void increment_little_endian(uint8_t counter[RC_AES_BLOCK_SIZE])
{
    int i;
    for (i = 0; i < RC_AES_BLOCK_SIZE; i++) {
        if (++counter[i] != 0)
            break;
    }
}

void rc_aes128_ctr(const uint8_t key[RC_AES128_KEY_SIZE],
                   const uint8_t initial_counter[RC_AES_BLOCK_SIZE],
                   const uint8_t *in, uint8_t *out, size_t length,
                   int little_endian_counter)
{
    rc_aes128 ctx;
    uint8_t counter[RC_AES_BLOCK_SIZE];
    uint8_t keystream[RC_AES_BLOCK_SIZE];
    size_t offset;

    rc_aes128_init(&ctx, key);
    memcpy(counter, initial_counter, RC_AES_BLOCK_SIZE);

    for (offset = 0; offset < length; offset += RC_AES_BLOCK_SIZE) {
        size_t remaining = length - offset;
        size_t n = (remaining < RC_AES_BLOCK_SIZE) ? remaining : RC_AES_BLOCK_SIZE;
        size_t j;

        rc_aes128_encrypt_block(&ctx, counter, keystream);

        for (j = 0; j < n; j++)
            out[offset + j] = (uint8_t)(in[offset + j] ^ keystream[j]);

        if (little_endian_counter)
            increment_little_endian(counter);
        else
            increment_big_endian(counter);
    }
}
