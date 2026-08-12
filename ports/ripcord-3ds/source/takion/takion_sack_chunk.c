#include "takion_sack_chunk.h"

#define CUMULATIVE_ONLY_VALUE_SIZE 12 /* cum_tsn_ack(4) + a_rwnd(4) + gap_count(2) + dup_count(2) */
#define CHUNK_HEADER_SIZE 4

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

size_t takion_sack_build(uint32_t cumulative_tsn_ack, uint32_t a_rwnd, uint8_t *buf, size_t buf_size)
{
    size_t total = CHUNK_HEADER_SIZE + CUMULATIVE_ONLY_VALUE_SIZE;

    if (total > buf_size)
        return 0;

    buf[0] = 0x03; /* TAKION_CHUNK_SACK */
    buf[1] = 0;
    write_be16(buf + 2, (uint16_t)total);
    write_be32(buf + 4, cumulative_tsn_ack);
    write_be32(buf + 8, a_rwnd);
    write_be16(buf + 12, 0); /* gap-ack block count */
    write_be16(buf + 14, 0); /* duplicate-TSN count */
    return total;
}

int takion_sack_parse(const uint8_t *data, size_t length, takion_sack_info *out)
{
    uint16_t chunk_length;
    unsigned gap_count, dup_count;
    size_t expected_length;

    if (length < CHUNK_HEADER_SIZE + CUMULATIVE_ONLY_VALUE_SIZE || data[0] != 0x03 /* TAKION_CHUNK_SACK */)
        return 0;

    chunk_length = read_be16(data + 2);
    gap_count = read_be16(data + 12);
    dup_count = read_be16(data + 14);
    expected_length = CHUNK_HEADER_SIZE + CUMULATIVE_ONLY_VALUE_SIZE
        + (size_t)gap_count * 4 + (size_t)dup_count * 4;

    if (chunk_length != expected_length || (size_t)chunk_length > length)
        return 0;

    out->cumulative_tsn_ack = read_be32(data + 4);
    out->a_rwnd = read_be32(data + 8);
    out->gap_ack_block_count = gap_count;
    out->dup_tsn_count = dup_count;
    return 1;
}
