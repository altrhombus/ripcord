/* See halyard_account_seed.h, and HalyardAccountSeedDelivery.cs for the provenance. */
#include "halyard_account_seed.h"

#include "halyard_registration.h"
#include "halyard_v1.h"
#include "../util/rc_base64.h"

#include <string.h>

/*
 * The inner layer is itself base64 text, so its length bounds the outer decode: base64 of up to
 * HALYARD_ACCOUNT_SEED_MAX_CIPHERTEXT bytes, without the terminator RC_BASE64_ENCODED_SIZE counts.
 */
#define INNER_MAX (RC_BASE64_ENCODED_SIZE(HALYARD_ACCOUNT_SEED_MAX_CIPHERTEXT) - 1)

/*
 * The field cipher with data1 as its key and data2 as its material, under the family's registration
 * context key. data1/data2 keep the roles .NET gives them (HalyardControlFieldCrypto(key: data1,
 * material: data2)); swapping them yields noise that nothing downstream can tell from a wrong seed.
 */
static void seed_field(halyard_control_field *field, int is_ps5,
                       const uint8_t data1[16], const uint8_t data2[16])
{
    memcpy(field->key, data1, HALYARD_KEY_LENGTH);
    memcpy(field->material, data2, HALYARD_MATERIAL_LENGTH);
    memcpy(field->context_key,
           is_ps5 ? halyard_v1_ctx_selector_one : halyard_v1_ctx_selector_zero,
           HALYARD_CONTEXT_KEY_LENGTH);
}

size_t halyard_account_seed_decode_custom_data1(const char *text, size_t text_length,
                                                uint8_t *out, size_t out_size)
{
    uint8_t inner[INNER_MAX];
    size_t inner_length;
    size_t n;

    if (text == NULL || out == NULL || text_length == 0)
        return 0;

    inner_length = rc_base64_decode(text, text_length, inner, sizeof(inner));
    if (inner_length == (size_t)-1 || inner_length == 0)
        return 0;

    n = rc_base64_decode((const char *)inner, inner_length, out, out_size);
    if (n == (size_t)-1)
        return 0;
    return n;
}

int halyard_account_seed_recover(int is_ps5, const uint8_t data1[16], const uint8_t data2[16],
                                 const uint8_t *ciphertext, size_t ciphertext_length,
                                 uint8_t out_seed[16])
{
    halyard_control_field field;

    if (data1 == NULL || data2 == NULL || ciphertext == NULL || out_seed == NULL
        || ciphertext_length < HALYARD_ACCOUNT_SEED_LENGTH)
        return 0;

    seed_field(&field, is_ps5, data1, data2);

    /*
     * Only the first sixteen bytes, and only those are decrypted. CFB's first sixteen bytes depend on
     * nothing after them, so this is the same seed .NET gets by decrypting everything and truncating -
     * and it means a longer ciphertext never needs a buffer here.
     */
    halyard_control_field_decrypt(&field, HALYARD_REGISTRATION_FIELD_COUNTER,
                                  ciphertext, out_seed, HALYARD_ACCOUNT_SEED_LENGTH);
    return 1;
}

int halyard_account_seed_recover_custom_data1(int is_ps5, const uint8_t data1[16], const uint8_t data2[16],
                                              const char *custom_data1, size_t custom_data1_length,
                                              uint8_t out_seed[16])
{
    uint8_t ciphertext[HALYARD_ACCOUNT_SEED_MAX_CIPHERTEXT];
    size_t n = halyard_account_seed_decode_custom_data1(custom_data1, custom_data1_length,
                                                         ciphertext, sizeof(ciphertext));

    if (n == 0)
        return 0;
    return halyard_account_seed_recover(is_ps5, data1, data2, ciphertext, n, out_seed);
}

void halyard_account_seed_seal(int is_ps5, const uint8_t data1[16], const uint8_t data2[16],
                               const uint8_t seed[16], uint8_t out_ciphertext[16])
{
    halyard_control_field field;

    seed_field(&field, is_ps5, data1, data2);
    halyard_control_field_encrypt(&field, HALYARD_REGISTRATION_FIELD_COUNTER,
                                  seed, out_ciphertext, HALYARD_ACCOUNT_SEED_LENGTH);
}

size_t halyard_account_seed_encode_custom_data1(const uint8_t *ciphertext, size_t ciphertext_length,
                                                char *out, size_t out_size)
{
    char inner[RC_BASE64_ENCODED_SIZE(HALYARD_ACCOUNT_SEED_MAX_CIPHERTEXT)];
    size_t inner_length;

    if (ciphertext == NULL || out == NULL || ciphertext_length > HALYARD_ACCOUNT_SEED_MAX_CIPHERTEXT)
        return 0;

    inner_length = rc_base64_encode(ciphertext, ciphertext_length, inner, sizeof(inner));
    if (inner_length == 0 && ciphertext_length != 0)
        return 0;
    return rc_base64_encode((const uint8_t *)inner, inner_length, out, out_size);
}
