/* See senkusha_echo.h for the wire format and where every field came from. */

#include "senkusha_echo.h"

#include <string.h>

#define ECHO_BASE_TYPE      0x03u
#define ECHO_SEQUENCE_OFF   5u
#define ECHO_MARKER1_OFF    6u
#define ECHO_MARKER2_OFF    9u
#define ECHO_TIMESTAMP_OFF  22u
#define ECHO_PADDING_OFF    27u
#define ECHO_MARKER         0xFFu

size_t senkusha_echo_build(uint8_t sequence, uint64_t microseconds, size_t payload_length,
                           uint8_t padding, uint8_t *buf, size_t buf_size)
{
    uint64_t value;

    if (buf == NULL || payload_length < ECHO_TIMESTAMP_OFF + 5u || payload_length > buf_size)
        return 0;

    memset(buf, 0, payload_length);
    if (padding != 0x00u && payload_length > ECHO_PADDING_OFF)
        memset(buf + ECHO_PADDING_OFF, padding, payload_length - ECHO_PADDING_OFF);

    buf[0] = ECHO_BASE_TYPE;
    buf[ECHO_SEQUENCE_OFF] = sequence;
    buf[ECHO_MARKER1_OFF] = ECHO_MARKER;
    buf[ECHO_MARKER2_OFF] = ECHO_MARKER;

    /* Five bytes, matching the observed width: a 32-bit microsecond counter wraps in about 71 minutes,
     * and the capture's high byte was clearly the top of a wider value. */
    value = microseconds & 0xFFFFFFFFFFull;
    buf[ECHO_TIMESTAMP_OFF]     = (uint8_t)(value >> 32);
    buf[ECHO_TIMESTAMP_OFF + 1] = (uint8_t)(value >> 24);
    buf[ECHO_TIMESTAMP_OFF + 2] = (uint8_t)(value >> 16);
    buf[ECHO_TIMESTAMP_OFF + 3] = (uint8_t)(value >> 8);
    buf[ECHO_TIMESTAMP_OFF + 4] = (uint8_t)value;
    return payload_length;
}

int senkusha_echo_is_echo(const uint8_t *datagram, size_t length, uint8_t *out_sequence)
{
    if (datagram == NULL || length < ECHO_TIMESTAMP_OFF + 5u
        || datagram[0] != ECHO_BASE_TYPE
        || datagram[ECHO_MARKER1_OFF] != ECHO_MARKER
        || datagram[ECHO_MARKER2_OFF] != ECHO_MARKER) {
        return 0;
    }
    if (out_sequence != NULL)
        *out_sequence = datagram[ECHO_SEQUENCE_OFF];
    return 1;
}
