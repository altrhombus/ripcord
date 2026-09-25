/*
 * libripcord fuzzing - the account route's 9303 transport: the prelude, the chunk walk, HTTP completeness,
 * and the association fed a sequence of datagrams.
 *
 * On the internet path this port faces whatever can reach the socket's reflexive address, before anything
 * is authenticated. Each record is one datagram, fed to one association that lives for the whole input,
 * because the interesting states - a connection half open, a payload half accumulated, a sequence about to
 * wrap - only exist between datagrams. The association is driven through each role: it opens the prelude
 * itself, and opens a connection once it has one, so records can reach the chunk layer in every phase.
 *
 * Its random source is a counter: the harness is about lengths and states, not unpredictability, and a
 * deterministic source keeps a crash reproducible. Outgoing datagrams are parsed back, so the builders are
 * checked against the parsers as a side effect.
 */
#include "../../session/halyard_dgram.h"
#include "fuzz_input.h"

#include <stddef.h>
#include <stdint.h>
#include <string.h>

int LLVMFuzzerTestOneInput(const uint8_t *data, size_t size);

static halyard_dgram_assoc s_assoc;
static uint8_t s_next;

static int fuzz_random(void *ctx, uint8_t *out, size_t length)
{
    size_t i;

    (void)ctx;
    for (i = 0; i < length; i++)
        out[i] = s_next++;
    return 1;
}

static void fuzz_send(void *ctx, const uint8_t *datagram, size_t length)
{
    halyard_dgram_prelude prelude;
    halyard_dgram_chunk chunk;
    size_t offset = 0;

    (void)ctx;
    rc_fuzz_touch(datagram, length);
    if (!halyard_dgram_prelude_parse(datagram, length, &prelude))
        while (halyard_dgram_chunk_next(datagram, length, &offset, &chunk))
            rc_fuzz_touch(chunk.body, chunk.body_length);
}

static void fuzz_event(void *ctx, const halyard_dgram_event *event)
{
    (void)ctx;
    rc_fuzz_touch(event->data, event->data_length);
    rc_fuzz_touch(event->delta, event->delta_length);
    if (event->kind == HALYARD_DGRAM_EVENT_DATA_RECEIVED) {
        (void)halyard_dgram_http_complete(event->data, event->data_length);
        /* Draining from inside the callback is allowed, and a real byte pipe does it. */
        if (event->data_length > HALYARD_DGRAM_INBOUND_MAX / 2)
            halyard_dgram_assoc_consume_inbound(&s_assoc, event->data_length / 2);
    }
}

int LLVMFuzzerTestOneInput(const uint8_t *data, size_t size)
{
    static const uint8_t local_id[HALYARD_DGRAM_HASHED_ID_LENGTH] = { 1 };
    static const uint8_t peer_id[HALYARD_DGRAM_HASHED_ID_LENGTH] = { 2 };
    static const uint8_t peer_address[4] = { 192, 0, 2, 7 };
    halyard_dgram_callbacks callbacks;
    halyard_dgram_prelude prelude;
    halyard_dgram_chunk chunk;
    rc_fuzz_input in;
    const uint8_t *record;
    size_t record_length;
    size_t offset = 0;
    unsigned index = 0;

    /* Stateless parsers, over the raw input. */
    (void)halyard_dgram_prelude_parse(data, size, &prelude);
    while (halyard_dgram_chunk_next(data, size, &offset, &chunk))
        rc_fuzz_touch(chunk.body, chunk.body_length);
    (void)halyard_dgram_http_complete(data, size);

    s_next = size > 0 ? data[0] : 0;
    callbacks.send = fuzz_send;
    callbacks.event = fuzz_event;
    callbacks.random = fuzz_random;
    callbacks.ctx = NULL;
    halyard_dgram_assoc_init(&s_assoc, &callbacks, local_id, peer_id, peer_address, 9303);
    if (size > 0 && (data[0] & 1))
        halyard_dgram_assoc_open(&s_assoc);

    /*
     * Random bytes almost never form a valid 88-byte prelude, so most inputs would never leave the
     * handshake. Half of them therefore start from an association the peer has already opened and echoed,
     * and half of those from an open connection, which puts every record straight into the chunk layer.
     */
    if (size > 0 && (data[0] & 2)) {
        uint8_t wire[HALYARD_DGRAM_PRELUDE_LENGTH];
        uint8_t accept[12] = { 0 };
        uint8_t chunk_wire[32];

        memset(&prelude, 0, sizeof(prelude));
        memcpy(prelude.sender_id, peer_id, sizeof(peer_id));
        memcpy(prelude.peer_id, local_id, sizeof(local_id));
        prelude.type = HALYARD_DGRAM_PRELUDE_INIT;
        prelude.tag_pair = 0x00017777u;
        prelude.token = 1;
        halyard_dgram_assoc_on_datagram(&s_assoc, wire, halyard_dgram_prelude_write(&prelude, wire, sizeof(wire)));
        prelude.type = HALYARD_DGRAM_PRELUDE_COOKIE_ECHO;
        halyard_dgram_assoc_on_datagram(&s_assoc, wire, halyard_dgram_prelude_write(&prelude, wire, sizeof(wire)));

        if (data[0] & 4) {
            halyard_dgram_assoc_open_connection(&s_assoc, HALYARD_DGRAM_ADDRESS_PORT_PAIR);
            accept[0] = size > 1 ? data[1] : 0;
            accept[1] = size > 2 ? data[2] : 0;
            halyard_dgram_assoc_on_datagram(&s_assoc, chunk_wire,
                                            halyard_dgram_chunk_write(chunk_wire, sizeof(chunk_wire),
                                                                      HALYARD_DGRAM_CHUNK_ACCEPT, 0x30, accept,
                                                                      sizeof(accept), 3));
        }
    }

    rc_fuzz_input_init(&in, data, size);
    while (rc_fuzz_next(&in, &record, &record_length)) {
        (void)halyard_dgram_assoc_on_datagram(&s_assoc, record, record_length);

        /* Drive the caller's side too, keyed off the record so the fuzzer can steer it. */
        switch ((record_length > 0 ? record[0] : 0) + index++ % 7) {
        case 0: halyard_dgram_assoc_retry(&s_assoc); break;
        case 1: halyard_dgram_assoc_open_connection(&s_assoc, (halyard_dgram_addressing)(1 + index % 3)); break;
        case 2: halyard_dgram_assoc_reopen_connection(&s_assoc); break;
        case 3: halyard_dgram_assoc_send(&s_assoc, record, record_length); break;
        case 4: halyard_dgram_assoc_close_connection(&s_assoc); break;
        case 5: halyard_dgram_assoc_clear_inbound(&s_assoc); break;
        default: break;
        }
    }
    return 0;
}
