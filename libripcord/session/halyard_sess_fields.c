#include "halyard_sess_fields.h"

#include <stdio.h>
#include <string.h>

void halyard_sess_field_auth_plaintext(const uint8_t *registration_key, size_t registration_key_length,
                                       uint8_t out[HALYARD_SESS_AUTH_PLAINTEXT_SIZE])
{
    size_t n = (registration_key_length < HALYARD_SESS_AUTH_PLAINTEXT_SIZE)
        ? registration_key_length : HALYARD_SESS_AUTH_PLAINTEXT_SIZE;

    memset(out, 0, HALYARD_SESS_AUTH_PLAINTEXT_SIZE);
    memcpy(out, registration_key, n);
}

/* Wire-confirmed prefix (cap22); the console treats the following 16 bytes as an opaque client id and
 * the trailing 6 bytes as fixed zero padding. */
static const uint8_t kDidPrefix[10] = { 0x00, 0x18, 0x00, 0x00, 0x00, 0x07, 0x00, 0x40, 0x00, 0x80 };
#define DID_MIDDLE_SIZE 16 /* HALYARD_SESS_DID_PLAINTEXT_SIZE - sizeof(kDidPrefix) - 6 trailing zero bytes */

void halyard_sess_field_did_plaintext(const uint8_t *device_id, size_t device_id_length,
                                      uint8_t out[HALYARD_SESS_DID_PLAINTEXT_SIZE])
{
    size_t n = (device_id_length < DID_MIDDLE_SIZE) ? device_id_length : DID_MIDDLE_SIZE;

    memset(out, 0, HALYARD_SESS_DID_PLAINTEXT_SIZE);
    memcpy(out, kDidPrefix, sizeof(kDidPrefix));
    memcpy(out + sizeof(kDidPrefix), device_id, n);
    /* out[sizeof(kDidPrefix) + n .. HALYARD_SESS_DID_PLAINTEXT_SIZE) stays zero, whether that is the
     * unused tail of the 16-byte middle or the fixed 6-byte suffix. */
}

size_t halyard_sess_field_os_type_plaintext(int major, int minor, char *buf, size_t buf_size)
{
    int written = snprintf(buf, buf_size, "Win%d.%d", major, minor);

    /* snprintf's return value excludes the NUL it writes (when it fits); include that NUL in the
     * plaintext deliberately - the spec's own examples show "Win10.0\0", not "Win10.0". */
    if (written < 0 || (size_t)written + 1 > buf_size)
        return 0;
    return (size_t)written + 1;
}

void halyard_sess_field_int32le_plaintext(int32_t value, uint8_t out[4])
{
    uint32_t bits = (uint32_t)value;

    out[0] = (uint8_t)bits;
    out[1] = (uint8_t)(bits >> 8);
    out[2] = (uint8_t)(bits >> 16);
    out[3] = (uint8_t)(bits >> 24);
}

size_t halyard_sess_field_login_pin_plaintext(const char *pin, size_t pin_length, uint8_t *buf, size_t buf_size)
{
    size_t i;

    if (pin_length == 0 || pin_length > buf_size)
        return 0;

    for (i = 0; i < pin_length; i++) {
        if (pin[i] < '0' || pin[i] > '9')
            return 0;
    }

    memcpy(buf, pin, pin_length);
    return pin_length;
}
