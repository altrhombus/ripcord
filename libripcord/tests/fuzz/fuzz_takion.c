/*
 * libripcord fuzzing - the Takion transport: message framing, handshake chunks, SACK, DATA and
 * reassembly.
 *
 * Each record is one datagram. It is parsed as a Takion message and its chunk is dispatched by type,
 * the way the receive path does it. DATA payloads feed one reassembler that lives for the whole input,
 * because the interesting states - a first fragment in flight, a continuation with nothing before it -
 * only exist between datagrams.
 */
#include "../../takion/takion_message.h"
#include "../../takion/takion_handshake.h"
#include "../../takion/takion_sack_chunk.h"
#include "../../takion/takion_data_chunk.h"
#include "../../takion/takion_reassembler.h"
#include "fuzz_input.h"

#include <stddef.h>
#include <stdint.h>

int LLVMFuzzerTestOneInput(const uint8_t *data, size_t size);

static takion_reassembler s_reassembler;

static void feed_data_chunk(const uint8_t *chunk, size_t chunk_length)
{
    uint32_t seq;
    unsigned channel;
    int ending;
    const uint8_t *payload;
    size_t payload_length;
    const uint8_t *message;
    size_t message_length;

    if (takion_data_parse_first(chunk, chunk_length, &seq, &channel, &ending, &payload, &payload_length)) {
        rc_fuzz_touch(payload, payload_length);
        if (takion_reassembler_first(&s_reassembler, channel, payload, payload_length, ending,
                                     &message, &message_length) == 1)
            rc_fuzz_touch(message, message_length);
    } else if (takion_data_parse_continuation(chunk, chunk_length, &seq, &channel, &ending,
                                              &payload, &payload_length)) {
        rc_fuzz_touch(payload, payload_length);
        if (takion_reassembler_continue(&s_reassembler, payload, payload_length, ending, &channel,
                                        &message, &message_length) == 1)
            rc_fuzz_touch(message, message_length);
    }
}

static void feed_chunk(const uint8_t *chunk, size_t chunk_length)
{
    uint32_t tag;
    uint32_t tsn;
    uint8_t cookie[TAKION_COOKIE_SIZE];
    takion_sack_info sack;

    switch (takion_chunk_type(chunk, chunk_length)) {
    case TAKION_CHUNK_INIT:
        (void)takion_parse_init(chunk, chunk_length, &tag);
        break;
    case TAKION_CHUNK_INIT_ACK:
        (void)takion_parse_init_ack(chunk, chunk_length, &tag, &tsn, cookie);
        break;
    case TAKION_CHUNK_COOKIE_ECHO:
        (void)takion_parse_cookie_echo(chunk, chunk_length, cookie);
        break;
    case TAKION_CHUNK_COOKIE_ACK:
        (void)takion_is_cookie_ack(chunk, chunk_length);
        break;
    case TAKION_CHUNK_SACK:
        (void)takion_sack_parse(chunk, chunk_length, &sack);
        break;
    case TAKION_CHUNK_DATA:
        feed_data_chunk(chunk, chunk_length);
        break;
    default:
        break;
    }
}

int LLVMFuzzerTestOneInput(const uint8_t *data, size_t size)
{
    rc_fuzz_input in;
    const uint8_t *datagram;
    size_t datagram_length;

    takion_reassembler_init(&s_reassembler);
    rc_fuzz_input_init(&in, data, size);
    while (rc_fuzz_next(&in, &datagram, &datagram_length)) {
        takion_message_header header;
        const uint8_t *chunk = NULL;
        size_t chunk_length = 0;

        if (takion_message_parse(datagram, datagram_length, &header, &chunk, &chunk_length) == 0)
            continue;
        rc_fuzz_touch(chunk, chunk_length);
        feed_chunk(chunk, chunk_length);
    }
    return 0;
}
