/*
 * libripcord - the 9303 transport's two framings: the 88-byte prelude and the chunk layer.
 *
 * See halyard_dgram.h. Ported from HalyardControlPrelude.cs and HalyardControlChunk.cs; pure, so the
 * fuzzer reaches it with nothing else linked.
 */
#include "halyard_dgram.h"

#include <string.h>

/* Prelude offsets (HalyardControlPrelude's private constants). Every byte not listed is zero. */
#define PRELUDE_SENDER_ID_OFFSET 4
#define PRELUDE_PEER_ID_OFFSET 36
#define PRELUDE_TAG_PAIR_OFFSET 68
#define PRELUDE_REQUEST_WORD_OFFSET 72
#define PRELUDE_TOKEN_OFFSET 76
#define PRELUDE_TAIL_OFFSET 80

/* The 2-bit word count sits in the top of the header word; the length is its low 11 bits. */
#define CHUNK_WORD_COUNT_SHIFT 14
#define CHUNK_LENGTH_MASK 0x07FFu

static uint16_t read_be16(const uint8_t *p)
{
    return (uint16_t)(((unsigned)p[0] << 8) | (unsigned)p[1]);
}

static uint32_t read_be32(const uint8_t *p)
{
    return ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16) | ((uint32_t)p[2] << 8) | (uint32_t)p[3];
}

static void write_be16(uint8_t *p, unsigned value)
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

/* ---- the prelude ---- */

size_t halyard_dgram_prelude_write(const halyard_dgram_prelude *prelude, uint8_t *out, size_t out_size)
{
    if (prelude == NULL || out == NULL || out_size < HALYARD_DGRAM_PRELUDE_LENGTH)
        return 0;

    memset(out, 0, HALYARD_DGRAM_PRELUDE_LENGTH);

    /* Little-endian: the one such field in the protocol. Written big-endian it reads as 0x06000000 and
     * the console sees an unknown type. */
    out[0] = (uint8_t)prelude->type;
    out[1] = (uint8_t)(prelude->type >> 8);
    out[2] = (uint8_t)(prelude->type >> 16);
    out[3] = (uint8_t)(prelude->type >> 24);

    memcpy(out + PRELUDE_SENDER_ID_OFFSET, prelude->sender_id, HALYARD_DGRAM_HASHED_ID_LENGTH);
    memcpy(out + PRELUDE_PEER_ID_OFFSET, prelude->peer_id, HALYARD_DGRAM_HASHED_ID_LENGTH);
    write_be32(out + PRELUDE_TAG_PAIR_OFFSET, prelude->tag_pair);
    write_be32(out + PRELUDE_REQUEST_WORD_OFFSET, prelude->request_word);
    write_be32(out + PRELUDE_TOKEN_OFFSET, prelude->token);
    memcpy(out + PRELUDE_TAIL_OFFSET, prelude->tail, HALYARD_DGRAM_PRELUDE_TAIL_LENGTH);
    return HALYARD_DGRAM_PRELUDE_LENGTH;
}

int halyard_dgram_prelude_parse(const uint8_t *data, size_t length, halyard_dgram_prelude *out)
{
    uint32_t type;

    if (data == NULL || out == NULL || length != HALYARD_DGRAM_PRELUDE_LENGTH)
        return 0;

    type = (uint32_t)data[0] | ((uint32_t)data[1] << 8) | ((uint32_t)data[2] << 16) | ((uint32_t)data[3] << 24);
    if (type != HALYARD_DGRAM_PRELUDE_INIT && type != HALYARD_DGRAM_PRELUDE_COOKIE_ECHO)
        return 0;

    out->type = type;
    memcpy(out->sender_id, data + PRELUDE_SENDER_ID_OFFSET, HALYARD_DGRAM_HASHED_ID_LENGTH);
    memcpy(out->peer_id, data + PRELUDE_PEER_ID_OFFSET, HALYARD_DGRAM_HASHED_ID_LENGTH);
    out->tag_pair = read_be32(data + PRELUDE_TAG_PAIR_OFFSET);
    out->request_word = read_be32(data + PRELUDE_REQUEST_WORD_OFFSET);
    out->token = read_be32(data + PRELUDE_TOKEN_OFFSET);
    memcpy(out->tail, data + PRELUDE_TAIL_OFFSET, HALYARD_DGRAM_PRELUDE_TAIL_LENGTH);
    return 1;
}

uint32_t halyard_dgram_swap_halves(uint32_t tag_pair)
{
    return (tag_pair >> 16) | (tag_pair << 16);
}

void halyard_dgram_reflect_peer_endpoint(const uint8_t peer_address[4], uint16_t peer_port,
                                         uint32_t tag_pair, uint8_t tail[HALYARD_DGRAM_PRELUDE_TAIL_LENGTH])
{
    uint8_t key[4];
    int i;

    write_be32(key, tag_pair);
    for (i = 0; i < 4; i++)
        tail[i] = (uint8_t)(peer_address[i] ^ key[i]);
    write_be16(tail + 4, (unsigned)(peer_port ^ (uint16_t)(tag_pair >> 16)));
    tail[6] = 0;
    tail[7] = 0;
}

/* ---- the chunk layer ---- */

size_t halyard_dgram_chunk_length(size_t body_length, unsigned word_count)
{
    return (size_t)word_count * 2u + HALYARD_DGRAM_TYPE_AND_FLAGS_LENGTH + body_length;
}

size_t halyard_dgram_chunk_write(uint8_t *out, size_t out_size, uint8_t type, uint8_t flags,
                                 const uint8_t *body, size_t body_length, unsigned word_count)
{
    size_t total;
    size_t offset = 2;
    unsigned word;

    if (out == NULL || (body == NULL && body_length != 0))
        return 0;
    if (word_count < 1 || word_count > HALYARD_DGRAM_PAIRED_WORD_COUNT)
        return 0;
    /* Checked before the addition can matter: a body near SIZE_MAX must not wrap into a short total. */
    if (body_length > HALYARD_DGRAM_MAX_CHUNK_LENGTH)
        return 0;

    total = halyard_dgram_chunk_length(body_length, word_count);
    if (total > HALYARD_DGRAM_MAX_CHUNK_LENGTH || out_size < total)
        return 0;

    write_be16(out, (word_count << CHUNK_WORD_COUNT_SHIFT) | (unsigned)total);

    /* One port word per prefix word after the header. The receiver demultiplexes on the last; with none
     * it matches on the peer address record instead. */
    for (word = 1; word < word_count; word++, offset += 2)
        write_be16(out + offset, HALYARD_DGRAM_CONTROL_PORT);

    out[offset] = type;
    out[offset + 1] = flags;
    if (body_length > 0)
        memcpy(out + offset + HALYARD_DGRAM_TYPE_AND_FLAGS_LENGTH, body, body_length);
    return total;
}

int halyard_dgram_chunk_read(const uint8_t *data, size_t length, halyard_dgram_chunk *out, size_t *consumed)
{
    unsigned header;
    unsigned words;
    size_t total;
    size_t prefix;

    if (data == NULL || out == NULL || consumed == NULL || length < 2)
        return 0;

    header = read_be16(data);
    words = header >> CHUNK_WORD_COUNT_SHIFT;
    total = header & CHUNK_LENGTH_MASK;

    /* A zero count is what makes the vendor's loop give up on the rest of the datagram. Bits 13..11 are
     * not examined: the receiver masks them away, so requiring anything of them would reject traffic it
     * accepts. */
    if (words == 0 || total > length)
        return 0;

    prefix = (size_t)words * 2u;
    if (total < prefix + HALYARD_DGRAM_TYPE_AND_FLAGS_LENGTH)
        return 0;

    out->source_port = HALYARD_DGRAM_CONTROL_PORT;
    out->destination_port = HALYARD_DGRAM_CONTROL_PORT;
    if (words == HALYARD_DGRAM_PAIRED_WORD_COUNT) {
        out->source_port = read_be16(data + 2);
        out->destination_port = read_be16(data + 4);
    } else if (words == 2) {
        out->destination_port = read_be16(data + 2);
        out->source_port = out->destination_port;
    }

    out->type = data[prefix];
    out->flags = data[prefix + 1];
    out->body = data + prefix + HALYARD_DGRAM_TYPE_AND_FLAGS_LENGTH;
    out->body_length = total - prefix - HALYARD_DGRAM_TYPE_AND_FLAGS_LENGTH;
    out->word_count = (uint8_t)words;
    *consumed = total;
    return 1;
}

int halyard_dgram_chunk_next(const uint8_t *data, size_t length, size_t *offset, halyard_dgram_chunk *out)
{
    size_t consumed;

    if (data == NULL || offset == NULL || *offset >= length)
        return 0;
    if (!halyard_dgram_chunk_read(data + *offset, length - *offset, out, &consumed))
        return 0;
    *offset += consumed;
    return 1;
}

/* ---- HTTP completeness ---- */

/* .NET's string.Trim() over ASCII text: the characters Char.IsWhiteSpace accepts below 0x80. */
static int is_trim_space(uint8_t c)
{
    return c == ' ' || (c >= 0x09 && c <= 0x0D);
}

/*
 * int.TryParse with NumberStyles.Integer over an already-trimmed span: an optional sign, then one or
 * more ASCII digits, within Int32. Returns 1 and sets *out, or 0.
 */
static int parse_int32(const uint8_t *p, size_t n, int64_t *out)
{
    size_t i = 0;
    int negative = 0;
    int64_t value = 0;

    if (n > 0 && (p[0] == '+' || p[0] == '-')) {
        negative = p[0] == '-';
        i = 1;
    }
    if (i == n)
        return 0;
    for (; i < n; i++) {
        if (p[i] < '0' || p[i] > '9')
            return 0;
        value = value * 10 + (int64_t)(p[i] - '0');
        if (value > 2147483648LL)
            return 0;
    }
    if (!negative && value > 2147483647LL)
        return 0;
    *out = negative ? -value : value;
    return 1;
}

static int equals_ci(const uint8_t *p, size_t n, const char *word)
{
    size_t i;

    for (i = 0; i < n; i++) {
        uint8_t a = p[i];
        uint8_t b = (uint8_t)word[i];

        if (b == '\0')
            return 0;
        if (a >= 'A' && a <= 'Z')
            a = (uint8_t)(a + 32);
        if (b >= 'A' && b <= 'Z')
            b = (uint8_t)(b + 32);
        if (a != b)
            return 0;
    }
    return word[n] == '\0';
}

int halyard_dgram_http_complete(const uint8_t *message, size_t length)
{
    size_t header_end;
    size_t line_start = 0;
    size_t body_length;

    if (message == NULL)
        return 0;

    for (header_end = 0; header_end + 4 <= length; header_end++) {
        if (message[header_end] == '\r' && message[header_end + 1] == '\n' && message[header_end + 2] == '\r'
            && message[header_end + 3] == '\n')
            break;
    }
    if (header_end + 4 > length)
        return 0;

    body_length = length - (header_end + 4);

    /* Split the header block on CRLF, as .NET's text[..headerEnd].Split("\r\n") does. */
    while (line_start <= header_end) {
        size_t line_end = line_start;
        size_t colon;

        while (line_end < header_end && !(message[line_end] == '\r' && line_end + 1 < header_end
                                          && message[line_end + 1] == '\n'))
            line_end++;

        for (colon = line_start; colon < line_end && message[colon] != ':'; colon++) {
        }

        if (colon < line_end && colon > line_start) {
            size_t name_start = line_start;
            size_t name_end = colon;
            size_t value_start = colon + 1;
            size_t value_end = line_end;
            int64_t declared;

            while (name_start < name_end && is_trim_space(message[name_start]))
                name_start++;
            while (name_end > name_start && is_trim_space(message[name_end - 1]))
                name_end--;
            while (value_start < value_end && is_trim_space(message[value_start]))
                value_start++;
            while (value_end > value_start && is_trim_space(message[value_end - 1]))
                value_end--;

            if (equals_ci(message + name_start, name_end - name_start, "Content-Length")
                && parse_int32(message + value_start, value_end - value_start, &declared))
                return declared < 0 || (uint64_t)body_length >= (uint64_t)declared;
        }

        line_start = line_end + 2;
    }
    return 1;
}
