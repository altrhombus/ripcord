#include "halyard_ctrl_message.h"

#include <string.h>

static void write_be32(uint8_t *p, uint32_t value)
{
    p[0] = (uint8_t)(value >> 24);
    p[1] = (uint8_t)(value >> 16);
    p[2] = (uint8_t)(value >> 8);
    p[3] = (uint8_t)value;
}

static void write_be16(uint8_t *p, uint16_t value)
{
    p[0] = (uint8_t)(value >> 8);
    p[1] = (uint8_t)value;
}

static uint32_t read_be32(const uint8_t *p)
{
    return ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16) | ((uint32_t)p[2] << 8) | (uint32_t)p[3];
}

static uint16_t read_be16(const uint8_t *p)
{
    return (uint16_t)(((uint16_t)p[0] << 8) | (uint16_t)p[1]);
}

size_t halyard_ctrl_message_build(unsigned type, const uint8_t *payload, size_t payload_length,
                                  uint8_t *buf, size_t buf_size)
{
    size_t total = HALYARD_CTRL_HEADER_SIZE + payload_length;

    if (total > buf_size)
        return 0;

    write_be32(buf, (uint32_t)payload_length);
    write_be16(buf + 4, (uint16_t)type);
    write_be16(buf + 6, 0);
    if (payload_length > 0)
        memcpy(buf + HALYARD_CTRL_HEADER_SIZE, payload, payload_length);
    return total;
}

size_t halyard_ctrl_message_parse(const uint8_t *data, size_t length,
                                  unsigned *out_type, const uint8_t **out_payload, size_t *out_payload_length)
{
    uint32_t payload_length;

    if (length < HALYARD_CTRL_HEADER_SIZE)
        return 0;

    payload_length = read_be32(data);

    /* Guard against overflow before adding, not after: on a 32-bit size_t target a corrupt/hostile
     * length near UINT32_MAX would otherwise wrap the sum back below `length` and pass the next check. */
    if (payload_length > (uint32_t)(SIZE_MAX - HALYARD_CTRL_HEADER_SIZE))
        return 0;
    if (HALYARD_CTRL_HEADER_SIZE + (size_t)payload_length > length)
        return 0; /* incomplete - wait for more of the stream */

    *out_type = read_be16(data + 4);
    *out_payload = data + HALYARD_CTRL_HEADER_SIZE;
    *out_payload_length = payload_length;
    return HALYARD_CTRL_HEADER_SIZE + (size_t)payload_length;
}
