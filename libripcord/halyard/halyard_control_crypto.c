/*
 * The v1 control-plane crypto: session KDF, per-field IV, field and streaminfo ciphers.
 * See halyard_v1.h for what each piece is and which mistakes it exists to prevent.
 */
#include "halyard_v1.h"

#include "../crypto/rc_crypto.h"

#include <string.h>

/* ---- KDF ---- */

static const uint8_t *table_entry(const uint8_t *table, int index)
{
    return &table[index * HALYARD_KDF_TABLE_ENTRY_SIZE];
}

/*
 * Mode 1 (PS5). Two independent per-byte passes over static tables, each selected by the top 5 bits of one
 * nonce byte: table 1 by nonce[7] >> 3, table 2 by nonce[0] >> 3. Two different bytes select two different
 * tables - a port that reuses one index for both agrees with this one on most inputs, which is why the
 * vectors sweep all 32 entries and then deliberately drive the two indices apart.
 */
static void derive_ps5(const uint8_t nonce[HALYARD_KEY_LENGTH],
                       const uint8_t companion[HALYARD_KEY_LENGTH],
                       uint8_t key[HALYARD_KEY_LENGTH],
                       uint8_t material[HALYARD_MATERIAL_LENGTH])
{
    const uint8_t *entry1 = table_entry(halyard_v1_kdf_table1, nonce[7] >> 3);
    const uint8_t *entry2 = table_entry(halyard_v1_kdf_table2, nonce[0] >> 3);
    int i;

    for (i = 0; i < HALYARD_KEY_LENGTH; i++) {
        key[i] = (uint8_t)(((companion[i] + 0x18 + i) & 0xFF) ^ entry1[i] ^ nonce[i]);
        material[i] = (uint8_t)(((nonce[i] - i - 0x2d) & 0xFF) ^ entry2[i]);
    }
}

/*
 * Mode 0 (PS4). Same table-index roles and the same companion-derived-key / nonce-derived-material split;
 * only the per-byte constants and the key path's XOR-then-add ordering differ. Note the ordering carefully:
 * PS5 adds to the companion and then XORs the table, PS4 XORs the table into the companion and then adds.
 */
static void derive_ps4(const uint8_t nonce[HALYARD_KEY_LENGTH],
                       const uint8_t companion[HALYARD_KEY_LENGTH],
                       uint8_t key[HALYARD_KEY_LENGTH],
                       uint8_t material[HALYARD_MATERIAL_LENGTH])
{
    const uint8_t *entry1 = table_entry(halyard_v1_ps4_kdf_table1, nonce[7] >> 3);
    const uint8_t *entry2 = table_entry(halyard_v1_ps4_kdf_table2, nonce[0] >> 3);
    int i;

    for (i = 0; i < HALYARD_KEY_LENGTH; i++) {
        key[i] = (uint8_t)((((entry1[i] ^ companion[i]) + 0x21 + i) & 0xFF) ^ nonce[i]);
        material[i] = (uint8_t)(((nonce[i] + 0x36 + i) & 0xFF) ^ entry2[i]);
    }
}

int halyard_control_kdf_derive(const uint8_t nonce[HALYARD_KEY_LENGTH],
                               const uint8_t companion[HALYARD_KEY_LENGTH],
                               int version_selector,
                               uint8_t key[HALYARD_KEY_LENGTH],
                               uint8_t material[HALYARD_MATERIAL_LENGTH])
{
    if (!halyard_v1_constants_bundled)
        return -1;

    if (version_selector == HALYARD_VERSION_SELECTOR_PS4) {
        if (!halyard_v1_has_ps4_tables)
            return -1;
        derive_ps4(nonce, companion, key, material);
        return 0;
    }

    /* Anything that is not the PS4 selector takes the PS5 variant - matching the .NET side, where the
     * dispatch is `versionSelector == 0 ? ps4 : ps5`. */
    derive_ps5(nonce, companion, key, material);
    return 0;
}

/* ---- Context key selection ---- */

const uint8_t *halyard_field_context_key(int codec_selector, int version_selector)
{
    if (codec_selector == 8 || codec_selector == 9)
        return halyard_v1_ctx_codec_in_high;

    if (version_selector == 1)
        return halyard_v1_ctx_selector_one;
    if (version_selector == 0)
        return halyard_v1_ctx_selector_zero;

    return halyard_v1_ctx_fallback_zero;
}

/* ---- Per-field IV ---- */

void halyard_field_iv_derive(const uint8_t context_key[HALYARD_CONTEXT_KEY_LENGTH],
                             const uint8_t material[HALYARD_MATERIAL_LENGTH],
                             uint64_t counter,
                             uint8_t iv[HALYARD_IV_LENGTH])
{
    uint8_t message[HALYARD_MATERIAL_LENGTH + 8];
    uint8_t full[RC_SHA256_DIGEST_SIZE];
    int i;

    memcpy(message, material, HALYARD_MATERIAL_LENGTH);

    /* Big-endian uint64, written a byte at a time so this does not depend on host endianness. */
    for (i = 0; i < 8; i++)
        message[HALYARD_MATERIAL_LENGTH + i] = (uint8_t)(counter >> (8 * (7 - i)));

    rc_hmac_sha256(context_key, HALYARD_CONTEXT_KEY_LENGTH, message, sizeof(message), full);

    /* Truncate to the first 16 bytes; the remaining 16 are discarded. */
    memcpy(iv, full, HALYARD_IV_LENGTH);
}

/* ---- Field and streaminfo ciphers ---- */

int halyard_control_field_init(halyard_control_field *ctx,
                               const uint8_t nonce[HALYARD_KEY_LENGTH],
                               const uint8_t companion[HALYARD_KEY_LENGTH],
                               int codec_selector,
                               int version_selector)
{
    if (halyard_control_kdf_derive(nonce, companion, version_selector, ctx->key, ctx->material) != 0)
        return -1;

    memcpy(ctx->context_key,
           halyard_field_context_key(codec_selector, version_selector),
           HALYARD_CONTEXT_KEY_LENGTH);
    return 0;
}

void halyard_control_field_encrypt(const halyard_control_field *ctx, uint64_t counter,
                                   const uint8_t *in, uint8_t *out, size_t length)
{
    uint8_t iv[HALYARD_IV_LENGTH];
    halyard_field_iv_derive(ctx->context_key, ctx->material, counter, iv);
    rc_aes128_cfb128(ctx->key, iv, in, out, length, 1);
}

void halyard_control_field_decrypt(const halyard_control_field *ctx, uint64_t counter,
                                   const uint8_t *in, uint8_t *out, size_t length)
{
    uint8_t iv[HALYARD_IV_LENGTH];
    halyard_field_iv_derive(ctx->context_key, ctx->material, counter, iv);
    rc_aes128_cfb128(ctx->key, iv, in, out, length, 0);
}

void halyard_control_streaminfo_crypt(const halyard_control_field *ctx, uint64_t counter,
                                      const uint8_t *in, uint8_t *out, size_t length)
{
    uint8_t iv[HALYARD_IV_LENGTH];
    halyard_field_iv_derive(ctx->context_key, ctx->material, counter, iv);
    rc_aes128_ofb(ctx->key, iv, in, out, length);
}
