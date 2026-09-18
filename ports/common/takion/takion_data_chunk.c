#include "takion_data_chunk.h"

#include <string.h>

#define CHUNK_HEADER_SIZE 4
#define FIRST_VALUE_PREFIX 9      /* seq(4) + channel(2) + reserved(3) */
#define CONTINUATION_VALUE_PREFIX 8 /* seq(4) + reserved(4) */

static void write_be16(uint8_t *p, uint16_t value)
{
    p[0] = (uint8_t)(value >> 8);
    p[1] = (uint8_t)value;
}

static void write_be32(uint8_t *p, uint32_t value)
{
    p[0] = (uint8_t)(value >> 24);
    p[1] = (uint8_t)(value >> 16);
    p[2] = (uint8_t)(value >> 8);
    p[3] = (uint8_t)value;
}

static uint16_t read_be16(const uint8_t *p)
{
    return (uint16_t)(((uint16_t)p[0] << 8) | (uint16_t)p[1]);
}

static uint32_t read_be32(const uint8_t *p)
{
    return ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16) | ((uint32_t)p[2] << 8) | (uint32_t)p[3];
}

size_t takion_data_build_first(uint32_t seq_num, unsigned channel, int ending,
                               const uint8_t *payload, size_t payload_length,
                               uint8_t *buf, size_t buf_size)
{
    size_t value_length = FIRST_VALUE_PREFIX + payload_length;
    size_t total = CHUNK_HEADER_SIZE + value_length;

    if (total > buf_size || total > 0xffffu)
        return 0;

    buf[0] = 0x00; /* TAKION_CHUNK_DATA */
    buf[1] = ending ? (uint8_t)TAKION_DATA_FLAG_ENDING : 0;
    write_be16(buf + 2, (uint16_t)total);
    write_be32(buf + 4, seq_num);
    write_be16(buf + 8, (uint16_t)channel);
    buf[10] = 0;
    buf[11] = 0;
    buf[12] = 0;
    if (payload_length > 0)
        memcpy(buf + CHUNK_HEADER_SIZE + FIRST_VALUE_PREFIX, payload, payload_length);
    return total;
}

size_t takion_data_build_continuation(uint32_t seq_num, unsigned channel, int ending,
                                      const uint8_t *payload, size_t payload_length,
                                      uint8_t *buf, size_t buf_size)
{
    size_t value_length = CONTINUATION_VALUE_PREFIX + payload_length;
    size_t total = CHUNK_HEADER_SIZE + value_length;

    if (total > buf_size || total > 0xffffu)
        return 0;

    buf[0] = 0x00;
    buf[1] = ending ? (uint8_t)TAKION_DATA_FLAG_ENDING : 0;
    write_be16(buf + 2, (uint16_t)total);
    write_be32(buf + 4, seq_num);
    /* The channel goes here in a continuation too - see the header. Only the RESERVED region shrinks
     * (3 bytes to 2), not the channel field. */
    write_be16(buf + 8, (uint16_t)channel);
    buf[10] = 0;
    buf[11] = 0;
    if (payload_length > 0)
        memcpy(buf + CHUNK_HEADER_SIZE + CONTINUATION_VALUE_PREFIX, payload, payload_length);
    return total;
}

int takion_data_parse_first(const uint8_t *data, size_t length, uint32_t *out_seq_num,
                            unsigned *out_channel, int *out_ending,
                            const uint8_t **out_payload, size_t *out_payload_length)
{
    uint16_t chunk_length;

    if (length < CHUNK_HEADER_SIZE + FIRST_VALUE_PREFIX || data[0] != 0x00 /* TAKION_CHUNK_DATA */)
        return 0;

    chunk_length = read_be16(data + 2);
    if (chunk_length < CHUNK_HEADER_SIZE + FIRST_VALUE_PREFIX || (size_t)chunk_length > length)
        return 0;

    *out_seq_num = read_be32(data + 4);
    *out_channel = read_be16(data + 8);
    *out_ending = (data[1] & TAKION_DATA_FLAG_ENDING) != 0;
    *out_payload = data + CHUNK_HEADER_SIZE + FIRST_VALUE_PREFIX;
    *out_payload_length = (size_t)chunk_length - CHUNK_HEADER_SIZE - FIRST_VALUE_PREFIX;
    return 1;
}

int takion_data_parse_continuation(const uint8_t *data, size_t length, uint32_t *out_seq_num,
                                   unsigned *out_channel, int *out_ending,
                                   const uint8_t **out_payload, size_t *out_payload_length)
{
    uint16_t chunk_length;

    if (length < CHUNK_HEADER_SIZE + CONTINUATION_VALUE_PREFIX || data[0] != 0x00 /* TAKION_CHUNK_DATA */)
        return 0;

    chunk_length = read_be16(data + 2);
    if (chunk_length < CHUNK_HEADER_SIZE + CONTINUATION_VALUE_PREFIX || (size_t)chunk_length > length)
        return 0;

    *out_seq_num = read_be32(data + 4);
    if (out_channel != NULL)
        *out_channel = read_be16(data + 8);
    *out_ending = (data[1] & TAKION_DATA_FLAG_ENDING) != 0;
    *out_payload = data + CHUNK_HEADER_SIZE + CONTINUATION_VALUE_PREFIX;
    *out_payload_length = (size_t)chunk_length - CHUNK_HEADER_SIZE - CONTINUATION_VALUE_PREFIX;
    return 1;
}
