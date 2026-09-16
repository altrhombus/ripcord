/* See halyard_registration.h, and HalyardRegistrationKdf.cs for the provenance of every constant here. */
#include "halyard_registration.h"

#include "halyard_v1.h"

#include <string.h>

/*
 * THE PER-BYTE BIAS IN THE WRAP, and the only thing besides the tables that separates the families.
 * Both compute ((material[i] ^ table[i]) + bias + i); PS5 subtracts 0x2d and PS4 adds 0x29. They are not
 * interchangeable - the wrong one produces a material the console will not agree with, which corrupts
 * the field IV, which corrupts exactly the first 16 bytes of the encrypted field, which is where
 * "Client-Type: " sits. The console then answers 403 / 80108b09 and says nothing about why.
 */
#define PS5_WRAP_BIAS  (-0x2d)
#define PS4_WRAP_BIAS  (0x29)

/* context[0] >> 3 selects the wrap entry; context[selector_offset] & 0x1f selects the key entry. */
#define MATERIAL_SELECTOR_OFFSET 0

int halyard_registration_available(void)
{
    return halyard_v1_registration_bundled;
}

static const uint8_t *key_table(int is_ps5)
{
    if (is_ps5)
        return halyard_v1_registration_table;
    return halyard_v1_has_ps4_registration ? halyard_v1_ps4_registration_table : NULL;
}

static const uint8_t *wrap_table(int is_ps5)
{
    if (is_ps5)
        return halyard_v1_material_wrap_table;
    return halyard_v1_has_ps4_registration ? halyard_v1_ps4_material_wrap_table : NULL;
}

int halyard_registration_derive_key(int is_ps5, const uint8_t *context, size_t context_length,
                                    uint32_t passcode, uint8_t out_key[16])
{
    const uint8_t *table = key_table(is_ps5);
    size_t offset;
    int index;
    int i;

    if (!halyard_v1_registration_bundled || table == NULL || context == NULL || out_key == NULL)
        return 0;

    offset = (size_t)halyard_v1_selector_offset;
    if (context_length <= offset)
        return 0;

    index = context[offset] & 0x1f;
    memcpy(out_key, table + (size_t)index * HALYARD_REGISTRATION_KEY_LENGTH,
           HALYARD_REGISTRATION_KEY_LENGTH);

    /*
     * The PIN, big-endian, into the LAST four bytes. Big-endian because that is what the console does,
     * not because a byte order was chosen - and a client that folds it the other way derives a key that
     * is wrong in four bytes and indistinguishable from a mistyped PIN.
     */
    for (i = 0; i < 4; i++) {
        unsigned shift = (unsigned)(24 - i * 8);

        out_key[HALYARD_REGISTRATION_KEY_LENGTH - 4 + i] ^= (uint8_t)(passcode >> shift);
    }
    return 1;
}

int halyard_registration_wrap_material(int is_ps5, const uint8_t material[16],
                                       const uint8_t *context, size_t context_length,
                                       uint8_t out_wrapped[16])
{
    const uint8_t *table = wrap_table(is_ps5);
    int bias = is_ps5 ? PS5_WRAP_BIAS : PS4_WRAP_BIAS;
    int i;

    if (!halyard_v1_registration_bundled || table == NULL || material == NULL || context == NULL
        || out_wrapped == NULL || context_length <= MATERIAL_SELECTOR_OFFSET)
        return 0;

    table += (size_t)(context[MATERIAL_SELECTOR_OFFSET] >> 3) * HALYARD_REGISTRATION_KEY_LENGTH;
    for (i = 0; i < HALYARD_REGISTRATION_KEY_LENGTH; i++)
        out_wrapped[i] = (uint8_t)((material[i] ^ table[i]) + bias + i);
    return 1;
}

int halyard_registration_unwrap_material(int is_ps5, const uint8_t wrapped[16],
                                         const uint8_t *context, size_t context_length,
                                         uint8_t out_material[16])
{
    const uint8_t *table = wrap_table(is_ps5);
    int bias = is_ps5 ? PS5_WRAP_BIAS : PS4_WRAP_BIAS;
    int i;

    if (!halyard_v1_registration_bundled || table == NULL || wrapped == NULL || context == NULL
        || out_material == NULL || context_length <= MATERIAL_SELECTOR_OFFSET)
        return 0;

    table += (size_t)(context[MATERIAL_SELECTOR_OFFSET] >> 3) * HALYARD_REGISTRATION_KEY_LENGTH;
    for (i = 0; i < HALYARD_REGISTRATION_KEY_LENGTH; i++)
        out_material[i] = (uint8_t)(((wrapped[i] - i - bias) & 0xff) ^ table[i]);
    return 1;
}

int halyard_registration_scatter(const uint8_t wrapped[16], uint8_t *context, size_t context_length)
{
    if (wrapped == NULL || context == NULL
        || context_length < HALYARD_REGISTRATION_WRAPPED_LOW + 8u)
        return 0;
    memcpy(context + HALYARD_REGISTRATION_WRAPPED_LOW, wrapped, 8);
    memcpy(context + HALYARD_REGISTRATION_WRAPPED_HIGH, wrapped + 8, 8);
    return 1;
}

int halyard_registration_field_init(struct halyard_control_field_tag *field, int is_ps5,
                                    const uint8_t *context, size_t context_length,
                                    uint32_t passcode, const uint8_t material[16])
{
    if (field == NULL || material == NULL)
        return 0;
    if (!halyard_registration_derive_key(is_ps5, context, context_length, passcode, field->key))
        return 0;

    memcpy(field->material, material, HALYARD_MATERIAL_LENGTH);
    /*
     * NOT A FIFTH KEY. Registration's field-cipher context key is the control plane's selector_one for a
     * PS5 and selector_zero for a PS4 - see the note in halyard_v1.h. The bundle stores the PS5 one
     * twice under two names and the build checks the copies agree, so reading it from here carries no
     * risk of using the other one by mistake.
     */
    memcpy(field->context_key,
           is_ps5 ? halyard_v1_ctx_selector_one : halyard_v1_ctx_selector_zero,
           HALYARD_CONTEXT_KEY_LENGTH);
    return 1;
}

int halyard_registration_gather(const uint8_t *context, size_t context_length, uint8_t out_wrapped[16])
{
    if (context == NULL || out_wrapped == NULL
        || context_length < HALYARD_REGISTRATION_WRAPPED_LOW + 8u)
        return 0;
    memcpy(out_wrapped, context + HALYARD_REGISTRATION_WRAPPED_LOW, 8);
    memcpy(out_wrapped + 8, context + HALYARD_REGISTRATION_WRAPPED_HIGH, 8);
    return 1;
}
