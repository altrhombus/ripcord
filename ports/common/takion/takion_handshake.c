#include "takion_handshake.h"

#include <string.h>

#define INIT_CHUNK_SIZE 20
#define INIT_ACK_CHUNK_SIZE (4 + 16 + TAKION_COOKIE_SIZE) /* header(4) + tag/a_rwnd/streams/tsn(16) + cookie */
#define COOKIE_ECHO_CHUNK_SIZE (4 + TAKION_COOKIE_SIZE)
#define COOKIE_ACK_CHUNK_SIZE 4

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

int takion_chunk_type(const uint8_t *data, size_t length)
{
    if (length < 1)
        return -1;
    return data[0];
}

/* Every chunk here declares its own length in the standard 2-byte SCTP field at offset 2; checking it
 * against the fixed size this port expects catches a truncated or malformed frame before the fixed
 * field reads below run past what the sender actually meant to send. */
static int chunk_header_ok(const uint8_t *data, size_t length, unsigned expect_type, size_t expect_size)
{
    return length >= expect_size
        && data[0] == expect_type
        && read_be16(data + 2) == expect_size;
}

size_t takion_build_init(uint32_t initiate_tag, uint8_t *buf, size_t buf_size)
{
    if (buf_size < INIT_CHUNK_SIZE)
        return 0;

    buf[0] = (uint8_t)TAKION_CHUNK_INIT;
    buf[1] = 0;
    write_be16(buf + 2, INIT_CHUNK_SIZE);
    write_be32(buf + 4, initiate_tag);
    write_be32(buf + 8, TAKION_INIT_A_RWND);
    write_be16(buf + 12, (uint16_t)TAKION_INIT_STREAMS);
    write_be16(buf + 14, (uint16_t)TAKION_INIT_STREAMS);
    write_be32(buf + 16, initiate_tag); /* initial_tsn = tag - wire-confirmed */
    return INIT_CHUNK_SIZE;
}

int takion_parse_init(const uint8_t *data, size_t length, uint32_t *out_initiate_tag)
{
    if (!chunk_header_ok(data, length, TAKION_CHUNK_INIT, INIT_CHUNK_SIZE))
        return 0;
    *out_initiate_tag = read_be32(data + 4);
    return 1;
}

size_t takion_build_init_ack(uint32_t server_tag, uint32_t initial_tsn,
                             const uint8_t cookie[TAKION_COOKIE_SIZE], uint8_t *buf, size_t buf_size)
{
    if (buf_size < INIT_ACK_CHUNK_SIZE)
        return 0;

    buf[0] = (uint8_t)TAKION_CHUNK_INIT_ACK;
    buf[1] = 0;
    write_be16(buf + 2, INIT_ACK_CHUNK_SIZE);
    write_be32(buf + 4, server_tag);
    write_be32(buf + 8, TAKION_INIT_A_RWND);
    write_be16(buf + 12, (uint16_t)TAKION_INIT_STREAMS);
    write_be16(buf + 14, (uint16_t)TAKION_INIT_STREAMS);
    write_be32(buf + 16, initial_tsn);
    memcpy(buf + 20, cookie, TAKION_COOKIE_SIZE);
    return INIT_ACK_CHUNK_SIZE;
}

int takion_parse_init_ack(const uint8_t *data, size_t length, uint32_t *out_server_tag,
                          uint32_t *out_initial_tsn, uint8_t out_cookie[TAKION_COOKIE_SIZE])
{
    if (!chunk_header_ok(data, length, TAKION_CHUNK_INIT_ACK, INIT_ACK_CHUNK_SIZE))
        return 0;
    *out_server_tag = read_be32(data + 4);
    *out_initial_tsn = read_be32(data + 16);
    memcpy(out_cookie, data + 20, TAKION_COOKIE_SIZE);
    return 1;
}

size_t takion_build_cookie_echo(const uint8_t cookie[TAKION_COOKIE_SIZE], uint8_t *buf, size_t buf_size)
{
    if (buf_size < COOKIE_ECHO_CHUNK_SIZE)
        return 0;

    buf[0] = (uint8_t)TAKION_CHUNK_COOKIE_ECHO;
    buf[1] = 0;
    write_be16(buf + 2, COOKIE_ECHO_CHUNK_SIZE);
    memcpy(buf + 4, cookie, TAKION_COOKIE_SIZE);
    return COOKIE_ECHO_CHUNK_SIZE;
}

int takion_parse_cookie_echo(const uint8_t *data, size_t length, uint8_t out_cookie[TAKION_COOKIE_SIZE])
{
    if (!chunk_header_ok(data, length, TAKION_CHUNK_COOKIE_ECHO, COOKIE_ECHO_CHUNK_SIZE))
        return 0;
    memcpy(out_cookie, data + 4, TAKION_COOKIE_SIZE);
    return 1;
}

size_t takion_build_cookie_ack(uint8_t *buf, size_t buf_size)
{
    if (buf_size < COOKIE_ACK_CHUNK_SIZE)
        return 0;

    buf[0] = (uint8_t)TAKION_CHUNK_COOKIE_ACK;
    buf[1] = 0;
    write_be16(buf + 2, COOKIE_ACK_CHUNK_SIZE);
    return COOKIE_ACK_CHUNK_SIZE;
}

int takion_is_cookie_ack(const uint8_t *data, size_t length)
{
    return chunk_header_ok(data, length, TAKION_CHUNK_COOKIE_ACK, COOKIE_ACK_CHUNK_SIZE);
}
