#include "takion_message.h"

#include <string.h>

static void write_be32(uint8_t *p, uint32_t value)
{
    p[0] = (uint8_t)(value >> 24);
    p[1] = (uint8_t)(value >> 16);
    p[2] = (uint8_t)(value >> 8);
    p[3] = (uint8_t)value;
}

static uint32_t read_be32(const uint8_t *p)
{
    return ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16) | ((uint32_t)p[2] << 8) | (uint32_t)p[3];
}

size_t takion_message_build(const takion_message_header *header, const uint8_t *chunk_data,
                            size_t chunk_length, uint8_t *buf, size_t buf_size)
{
    size_t total = TAKION_HEADER_SIZE + chunk_length;

    if (total > buf_size)
        return 0;

    buf[0] = (uint8_t)header->base_type;
    write_be32(buf + 1, header->verification_tag);
    write_be32(buf + 5, header->gmac_tag);
    write_be32(buf + 9, header->key_position);
    if (chunk_length > 0)
        memcpy(buf + TAKION_HEADER_SIZE, chunk_data, chunk_length);
    return total;
}

size_t takion_message_parse(const uint8_t *data, size_t length, takion_message_header *out_header,
                            const uint8_t **out_chunk, size_t *out_chunk_length)
{
    if (length < TAKION_HEADER_SIZE)
        return 0;

    out_header->base_type = data[0];
    out_header->verification_tag = read_be32(data + 1);
    out_header->gmac_tag = read_be32(data + 5);
    out_header->key_position = read_be32(data + 9);
    *out_chunk = data + TAKION_HEADER_SIZE;
    *out_chunk_length = length - TAKION_HEADER_SIZE;
    return TAKION_HEADER_SIZE;
}
