/*
 * libripcord - the 9303 control association: a state machine with no I/O.
 *
 * Ported from src/Ripcord.Protocol.Halyard.Common/Control/HalyardControlAssociation.cs, which remains
 * the reference and carries the full reasoning behind each rule; the comments here keep enough of it
 * that a reader of this file alone does not "fix" a rule the wire forced. Feed it a datagram, and it
 * calls back with the datagrams to send and the events that occurred.
 *
 * WHY A PEER ASSOCIATION AND NOT A CLIENT. On a WAN path the client opens the prelude to punch out
 * through NAT; on a shared LAN the console may open it as soon as signaling hands it our candidate. Both
 * sides echo, and either may open a chunk connection. Encoding a role would make every new observation a
 * restructuring, so neither role is a mode here - it is simply what arrives.
 *
 * RANDOMNESS ORDER IS PART OF THE PORT. Every branch draws its random bytes in the same order and sizes
 * as the .NET type (a 2-byte tag half then a 4-byte token; a 2-byte sequence then a 4-byte connection
 * tag; ...). That is what lets tests/dgram_test.c replay the .NET association's own transcript with the
 * same counting source and compare every emitted byte. Where C must draw before mutating state - so an
 * entropy failure leaves the association untouched, which .NET's infallible source never needed - the
 * draws are hoisted but their order is kept.
 */
#include "halyard_dgram.h"

#include <string.h>

/* The version/capability block every hello carries, constant in every observed connection. */
static const uint8_t k_capability_block[6] = { 0x0B, 0x01, 0x01, 0x00, 0x01, 0x00 };

/* 1410, where an MTU or receive window would sit. [X] read as an MTU, unconfirmed - sent verbatim. */
#define WINDOW_OR_MTU 0x0582u

/* The flags byte on nearly every chunk. 0x2F also occurs on the wire; what selects it is [X]. */
#define DEFAULT_FLAGS 0x30u

/*
 * How much of a cookie's body is a fixed header the echo must NOT return: observed as the words
 * 0x00000002, 0x00000000 in every cookie held. The captured client answers a 42-byte cookie body with
 * its last 34 bytes, and a console rejects an echo carrying all 42.
 */
#define COOKIE_HEADER_LENGTH 8u

#define SEQUENCE_LENGTH 2u

/*
 * What a retransmission inserts after the sequence. [X] its six bytes are unexplained; their LENGTH is
 * [W], from two captured retransmissions exactly six bytes longer than the originals.
 */
#define RETRANSMIT_HEADER_LENGTH 6u

#define HELLO_BODY_LENGTH 14u
#define HELLO_TAG_OFFSET 8u

/*
 * The cookie we offer when the peer opens a connection: the fixed words the console's own 42-byte cookie
 * carries, then 24 bytes of ours. [X] no capture shows a client in this position - see on_hello.
 */
static const uint8_t k_peer_cookie_prefix[18] = {
    0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x81, 0x20, 0x00, 0x96,
    0x84, 0xD0, 0x00, 0x00, 0x00, 0x00
};
#define PEER_COOKIE_RANDOM_LENGTH 24u

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

static int draw(halyard_dgram_assoc *assoc, uint8_t *out, size_t length)
{
    return assoc->cb.random(assoc->cb.ctx, out, length);
}

static void emit_send(halyard_dgram_assoc *assoc, const uint8_t *datagram, size_t length)
{
    assoc->cb.send(assoc->cb.ctx, datagram, length);
}

static void emit_event(halyard_dgram_assoc *assoc, halyard_dgram_event *event)
{
    if (assoc->cb.event != NULL)
        assoc->cb.event(assoc->cb.ctx, event);
}

static void emit_simple(halyard_dgram_assoc *assoc, halyard_dgram_event_kind kind, int opened_by_peer)
{
    halyard_dgram_event event;

    memset(&event, 0, sizeof(event));
    event.kind = kind;
    event.opened_by_peer = opened_by_peer;
    emit_event(assoc, &event);
}

/*
 * Reported, never dropped: the first live runs of this transport could only say "the console sent
 * nothing", which said nothing about what it HAD sent.
 */
static void emit_unhandled(halyard_dgram_assoc *assoc, const char *reason, const uint8_t *datagram,
                           size_t length, uint8_t chunk_type)
{
    halyard_dgram_event event;

    memset(&event, 0, sizeof(event));
    event.kind = HALYARD_DGRAM_EVENT_UNHANDLED;
    event.reason = reason;
    event.data = datagram;
    event.data_length = length;
    event.chunk_type = chunk_type;
    emit_event(assoc, &event);
}

/* Builds a prelude into the scratch buffer and sends it. An Init's tail is zero; an echo passes one. */
static void send_prelude(halyard_dgram_assoc *assoc, uint32_t type, uint32_t request_word, uint32_t token,
                         const uint8_t *tail)
{
    halyard_dgram_prelude prelude;

    memset(&prelude, 0, sizeof(prelude));
    prelude.type = type;
    memcpy(prelude.sender_id, assoc->local_id, HALYARD_DGRAM_HASHED_ID_LENGTH);
    memcpy(prelude.peer_id, assoc->peer_id, HALYARD_DGRAM_HASHED_ID_LENGTH);
    prelude.tag_pair = assoc->tag_pair;
    prelude.request_word = request_word;
    prelude.token = token;
    if (tail != NULL)
        memcpy(prelude.tail, tail, HALYARD_DGRAM_PRELUDE_TAIL_LENGTH);

    emit_send(assoc, assoc->scratch,
              halyard_dgram_prelude_write(&prelude, assoc->scratch, sizeof(assoc->scratch)));
}

/*
 * Builds one chunk into the scratch buffer and sends it. Returns 0 - having sent nothing - when the
 * chunk cannot be encoded, which is where .NET's encoder would throw out of OnDatagram.
 */
static int send_chunk(halyard_dgram_assoc *assoc, uint8_t type, uint8_t flags, const uint8_t *body,
                      size_t body_length, unsigned word_count)
{
    size_t n = halyard_dgram_chunk_write(assoc->scratch, sizeof(assoc->scratch), type, flags, body,
                                         body_length, word_count);

    if (n == 0)
        return 0;
    emit_send(assoc, assoc->scratch, n);
    return 1;
}

static void sequence_pair(uint8_t out[4], uint16_t sequence, uint16_t acknowledgement)
{
    write_be16(out, sequence);
    write_be16(out + 2, acknowledgement);
}

static void send_hello(halyard_dgram_assoc *assoc)
{
    (void)send_chunk(assoc, HALYARD_DGRAM_CHUNK_HELLO, DEFAULT_FLAGS, assoc->hello_body, HELLO_BODY_LENGTH,
                     (unsigned)assoc->hello_addressing);
}

halyard_dgram_status halyard_dgram_assoc_init(halyard_dgram_assoc *assoc, const halyard_dgram_callbacks *callbacks,
                                              const uint8_t local_id[HALYARD_DGRAM_HASHED_ID_LENGTH],
                                              const uint8_t peer_id[HALYARD_DGRAM_HASHED_ID_LENGTH],
                                              const uint8_t peer_address[4], uint16_t peer_port)
{
    if (assoc == NULL || callbacks == NULL || callbacks->send == NULL || callbacks->random == NULL
        || local_id == NULL || peer_id == NULL || peer_address == NULL)
        return HALYARD_DGRAM_BAD_ARGUMENT;

    /* Not a memset of the whole struct: the two buffers are ~18 KB that nothing reads before writing. */
    assoc->cb = *callbacks;
    memcpy(assoc->local_id, local_id, HALYARD_DGRAM_HASHED_ID_LENGTH);
    memcpy(assoc->peer_id, peer_id, HALYARD_DGRAM_HASHED_ID_LENGTH);
    memcpy(assoc->peer_address, peer_address, 4);
    assoc->peer_port = peer_port;
    assoc->phase = HALYARD_DGRAM_PHASE_IDLE;
    assoc->tag_pair = 0;
    assoc->our_token = 0;
    assoc->we_sent_echo = 0;
    assoc->peer_echoed = 0;
    assoc->have_hello = 0;
    memset(assoc->hello_body, 0, sizeof(assoc->hello_body));
    assoc->hello_addressing = HALYARD_DGRAM_ADDRESS_PORT_PAIR;
    assoc->sequence = 0;
    assoc->peer_sequence = 0;
    assoc->delivered = 0;
    assoc->inbound_length = 0;
    return HALYARD_DGRAM_OK;
}

/* ---- the prelude ---- */

halyard_dgram_status halyard_dgram_assoc_open(halyard_dgram_assoc *assoc)
{
    uint8_t half[2];
    uint8_t token[4];

    if (assoc == NULL)
        return HALYARD_DGRAM_BAD_ARGUMENT;
    if (assoc->phase != HALYARD_DGRAM_PHASE_IDLE)
        return HALYARD_DGRAM_OK;
    if (!draw(assoc, half, sizeof(half)) || !draw(assoc, token, sizeof(token)))
        return HALYARD_DGRAM_NO_ENTROPY;

    assoc->tag_pair = 0x00010000u | read_be16(half);
    assoc->our_token = read_be32(token);
    assoc->phase = HALYARD_DGRAM_PHASE_HANDSHAKING;
    send_prelude(assoc, HALYARD_DGRAM_PRELUDE_INIT, HALYARD_DGRAM_INITIATOR_REQUEST_WORD, assoc->our_token, NULL);
    return HALYARD_DGRAM_OK;
}

halyard_dgram_status halyard_dgram_assoc_retry(halyard_dgram_assoc *assoc)
{
    if (assoc == NULL)
        return HALYARD_DGRAM_BAD_ARGUMENT;
    if (assoc->phase == HALYARD_DGRAM_PHASE_HANDSHAKING && !assoc->we_sent_echo)
        send_prelude(assoc, HALYARD_DGRAM_PRELUDE_INIT, HALYARD_DGRAM_INITIATOR_REQUEST_WORD, assoc->our_token,
                     NULL);
    return HALYARD_DGRAM_OK;
}

static void settle_if_established(halyard_dgram_assoc *assoc)
{
    if (assoc->phase == HALYARD_DGRAM_PHASE_ESTABLISHED || assoc->phase == HALYARD_DGRAM_PHASE_CONNECTED)
        return;
    if (!assoc->we_sent_echo || !assoc->peer_echoed)
        return;
    assoc->phase = HALYARD_DGRAM_PHASE_ESTABLISHED;
    emit_simple(assoc, HALYARD_DGRAM_EVENT_PRELUDE_ESTABLISHED, 0);
}

static halyard_dgram_status on_prelude(halyard_dgram_assoc *assoc, const halyard_dgram_prelude *peer)
{
    uint8_t tail[HALYARD_DGRAM_PRELUDE_TAIL_LENGTH];

    if (peer->type == HALYARD_DGRAM_PRELUDE_COOKIE_ECHO) {
        assoc->peer_echoed = 1;
        settle_if_established(assoc);
        return HALYARD_DGRAM_OK;
    }

    /*
     * An Init. Either it answers ours, or the peer is opening the association itself - the two differ only
     * in whose tag pair it carries, which is why a wrongly-shaped answer is adopted rather than refused.
     */
    if (assoc->phase == HALYARD_DGRAM_PHASE_IDLE || peer->tag_pair != halyard_dgram_swap_halves(assoc->tag_pair)) {
        if (assoc->our_token == 0) {
            uint8_t token[4];

            if (!draw(assoc, token, sizeof(token)))
                return HALYARD_DGRAM_NO_ENTROPY;
            assoc->our_token = read_be32(token);
        }

        /* The responder's shape: its pair adopted with the halves exchanged, a zero request word, our token. */
        assoc->tag_pair = halyard_dgram_swap_halves(peer->tag_pair);
        assoc->phase = HALYARD_DGRAM_PHASE_HANDSHAKING;
        send_prelude(assoc, HALYARD_DGRAM_PRELUDE_INIT, 0, assoc->our_token, NULL);
    }

    /*
     * Echo the peer's token back - for EVERY Init, not only the first. The peer probes for as long as the
     * association lives, each probe a fresh timestamp; a client that echoes once and goes quiet is dropped
     * after about ten seconds. And the echo must reflect the peer's endpoint, or the console never
     * validates the path and ignores every chunk that follows.
     */
    halyard_dgram_reflect_peer_endpoint(assoc->peer_address, assoc->peer_port, assoc->tag_pair, tail);
    send_prelude(assoc, HALYARD_DGRAM_PRELUDE_COOKIE_ECHO, 0, peer->token, tail);
    assoc->we_sent_echo = 1;

    settle_if_established(assoc);
    return HALYARD_DGRAM_OK;
}

/* ---- connections ---- */

halyard_dgram_status halyard_dgram_assoc_open_connection(halyard_dgram_assoc *assoc,
                                                         halyard_dgram_addressing addressing)
{
    uint8_t seq[2];
    uint8_t tag[4];

    if (assoc == NULL || (unsigned)addressing < 1u || (unsigned)addressing > HALYARD_DGRAM_PAIRED_WORD_COUNT)
        return HALYARD_DGRAM_BAD_ARGUMENT;

    /* After the reset below the phase is always ESTABLISHED, so these are exactly the phases that proceed. */
    if (assoc->phase != HALYARD_DGRAM_PHASE_ESTABLISHED && assoc->phase != HALYARD_DGRAM_PHASE_CONNECTED
        && assoc->phase != HALYARD_DGRAM_PHASE_CLOSED)
        return HALYARD_DGRAM_OK;
    if (!draw(assoc, seq, sizeof(seq)) || !draw(assoc, tag, sizeof(tag)))
        return HALYARD_DGRAM_NO_ENTROPY;

    /*
     * Opening a connection says nothing about the previous one: the console tears each down once it has
     * answered, and the captured session runs rgst, init and ctrl as three connections over one prelude.
     * It has to work from CONNECTED too, because that teardown is often still in flight.
     */
    if (assoc->phase != HALYARD_DGRAM_PHASE_ESTABLISHED) {
        assoc->phase = HALYARD_DGRAM_PHASE_ESTABLISHED;
        assoc->peer_sequence = 0;
        assoc->delivered = 0;
        assoc->have_hello = 0;
        assoc->inbound_length = 0;
    }

    assoc->hello_addressing = addressing;
    assoc->sequence = read_be16(seq);
    write_be16(assoc->hello_body, assoc->sequence);
    memcpy(assoc->hello_body + 2, k_capability_block, sizeof(k_capability_block));
    memcpy(assoc->hello_body + HELLO_TAG_OFFSET, tag, sizeof(tag));
    write_be16(assoc->hello_body + 12, WINDOW_OR_MTU);
    assoc->have_hello = 1;
    send_hello(assoc);
    return HALYARD_DGRAM_OK;
}

halyard_dgram_status halyard_dgram_assoc_reopen_connection(halyard_dgram_assoc *assoc)
{
    if (assoc == NULL)
        return HALYARD_DGRAM_BAD_ARGUMENT;

    /* The peer's teardown of the PREVIOUS connection can land after this one's hello went out. Re-sending
     * is still right; the connection being opened is not the one that closed. */
    if (assoc->phase == HALYARD_DGRAM_PHASE_CLOSED && assoc->have_hello)
        assoc->phase = HALYARD_DGRAM_PHASE_ESTABLISHED;

    if (assoc->phase == HALYARD_DGRAM_PHASE_ESTABLISHED && assoc->have_hello)
        send_hello(assoc);
    return HALYARD_DGRAM_OK;
}

halyard_dgram_status halyard_dgram_assoc_close_connection(halyard_dgram_assoc *assoc)
{
    uint8_t body[8];

    if (assoc == NULL)
        return HALYARD_DGRAM_BAD_ARGUMENT;
    if (assoc->phase != HALYARD_DGRAM_PHASE_CONNECTED || !assoc->have_hello)
        return HALYARD_DGRAM_OK;

    /* The console's own teardown shape: four zero bytes, then the connection tag. */
    memset(body, 0, 4);
    memcpy(body + 4, assoc->hello_body + HELLO_TAG_OFFSET, 4);
    assoc->phase = HALYARD_DGRAM_PHASE_CLOSED;
    (void)send_chunk(assoc, HALYARD_DGRAM_CHUNK_CLOSE, 0x00, body, sizeof(body), (unsigned)assoc->hello_addressing);
    return HALYARD_DGRAM_OK;
}

halyard_dgram_status halyard_dgram_assoc_send(halyard_dgram_assoc *assoc, const uint8_t *payload, size_t length)
{
    uint8_t pair[4];
    size_t ack_length;
    size_t data_length;
    uint8_t *data;

    if (assoc == NULL || (payload == NULL && length != 0))
        return HALYARD_DGRAM_BAD_ARGUMENT;
    if (assoc->phase != HALYARD_DGRAM_PHASE_CONNECTED)
        return HALYARD_DGRAM_NOT_CONNECTED;
    if (length > HALYARD_DGRAM_MAX_PAYLOAD)
        return HALYARD_DGRAM_TOO_LONG;

    /*
     * An acknowledgement in front of the data, in one datagram: the shape the captured client sends. Both
     * at the paired word count regardless of how the hello was addressed, exactly as .NET's Send does.
     */
    sequence_pair(pair, assoc->sequence, (uint16_t)(assoc->peer_sequence + 1u));
    ack_length = halyard_dgram_chunk_write(assoc->scratch, sizeof(assoc->scratch), HALYARD_DGRAM_CHUNK_ACK,
                                           DEFAULT_FLAGS, pair, sizeof(pair), HALYARD_DGRAM_PAIRED_WORD_COUNT);

    /* The data chunk's body is seq || payload, written in place after the ack rather than via a copy. */
    data = assoc->scratch + ack_length;
    data_length = halyard_dgram_chunk_length(SEQUENCE_LENGTH + length, HALYARD_DGRAM_PAIRED_WORD_COUNT);
    write_be16(data, (HALYARD_DGRAM_PAIRED_WORD_COUNT << 14) | (unsigned)data_length);
    write_be16(data + 2, HALYARD_DGRAM_CONTROL_PORT);
    write_be16(data + 4, HALYARD_DGRAM_CONTROL_PORT);
    data[6] = HALYARD_DGRAM_CHUNK_DATA;
    data[7] = DEFAULT_FLAGS;
    write_be16(data + 8, assoc->sequence);
    if (length > 0)
        memcpy(data + 10, payload, length);

    /*
     * Each payload gets its own sequence. Without this the console took the first payload on a connection
     * and discarded every later one as a duplicate - invisible to request/response, which sends once per
     * connection, and fatal to the persistent control channel that follows.
     */
    assoc->sequence++;

    emit_send(assoc, assoc->scratch, ack_length + data_length);
    return HALYARD_DGRAM_OK;
}

/* ---- the chunk layer ---- */

static halyard_dgram_status on_data(halyard_dgram_assoc *assoc, const halyard_dgram_chunk *chunk)
{
    size_t header_length = chunk->type == HALYARD_DGRAM_CHUNK_DATA_RETRANSMIT
                               ? SEQUENCE_LENGTH + RETRANSMIT_HEADER_LENGTH
                               : SEQUENCE_LENGTH;
    uint16_t sequence;
    int already_delivered;
    uint8_t pair[4];
    size_t payload_length;
    halyard_dgram_event event;

    if (chunk->body_length <= header_length)
        return HALYARD_DGRAM_OK;

    sequence = read_be16(chunk->body);
    payload_length = chunk->body_length - header_length;

    /* Wrapping-safe "have we already taken this one?": the difference read as signed is <= 0 for anything
     * at or behind where we are, and stays right across the 16-bit wrap. */
    already_delivered = assoc->delivered && (int16_t)(uint16_t)(sequence - assoc->peer_sequence) <= 0;

    if (already_delivered) {
        /* Acknowledge, always - the console retransmits until it hears one - but deliver once. */
        sequence_pair(pair, assoc->sequence, (uint16_t)(assoc->peer_sequence + 1u));
        (void)send_chunk(assoc, HALYARD_DGRAM_CHUNK_ACK, DEFAULT_FLAGS, pair, sizeof(pair), chunk->word_count);
        return HALYARD_DGRAM_OK;
    }

    /*
     * C only: the inbound buffer is bounded. Refuse the chunk outright - no delivery and, crucially, no
     * acknowledgement - so the peer retransmits once the caller has drained, rather than acknowledging data
     * that was then thrown away.
     */
    if (payload_length > sizeof(assoc->inbound) - assoc->inbound_length) {
        emit_unhandled(assoc, "received payload does not fit the inbound buffer; drain it", chunk->body,
                       chunk->body_length, chunk->type);
        return HALYARD_DGRAM_OK;
    }

    assoc->peer_sequence = sequence;
    assoc->delivered = 1;
    memcpy(assoc->inbound + assoc->inbound_length, chunk->body + header_length, payload_length);
    assoc->inbound_length += payload_length;

    sequence_pair(pair, assoc->sequence, (uint16_t)(sequence + 1u));
    (void)send_chunk(assoc, HALYARD_DGRAM_CHUNK_ACK, DEFAULT_FLAGS, pair, sizeof(pair), chunk->word_count);

    memset(&event, 0, sizeof(event));
    event.kind = HALYARD_DGRAM_EVENT_DATA_RECEIVED;
    event.data = assoc->inbound;
    event.data_length = assoc->inbound_length;
    event.delta = assoc->inbound + assoc->inbound_length - payload_length;
    event.delta_length = payload_length;
    emit_event(assoc, &event);
    return HALYARD_DGRAM_OK;
}

static halyard_dgram_status on_chunk(halyard_dgram_assoc *assoc, const halyard_dgram_chunk *chunk,
                                     const uint8_t *datagram, size_t datagram_length)
{
    uint8_t body[HALYARD_DGRAM_MAX_CHUNK_LENGTH];

    switch (chunk->type) {
    case HALYARD_DGRAM_CHUNK_COOKIE:
        /* We opened the connection and the peer wants its cookie back: our hello body, then the cookie's
         * echoable region - NOT its first 8 bytes (see COOKIE_HEADER_LENGTH). */
        if (!assoc->have_hello) {
            emit_unhandled(assoc, "a cookie arrived for a connection we never opened", datagram, datagram_length, 0);
            return HALYARD_DGRAM_OK;
        }
        if (chunk->body_length < COOKIE_HEADER_LENGTH) {
            emit_unhandled(assoc, "a cookie too short to carry the 8-byte header the echo skips", datagram,
                           datagram_length, 0);
            return HALYARD_DGRAM_OK;
        }
        memcpy(body, assoc->hello_body, HELLO_BODY_LENGTH);
        if (HELLO_BODY_LENGTH + chunk->body_length - COOKIE_HEADER_LENGTH <= sizeof(body))
            memcpy(body + HELLO_BODY_LENGTH, chunk->body + COOKIE_HEADER_LENGTH,
                   chunk->body_length - COOKIE_HEADER_LENGTH);
        /* C only: a cookie so long its echo would overflow one chunk is reported, where .NET's encoder throws. */
        if (HELLO_BODY_LENGTH + chunk->body_length - COOKIE_HEADER_LENGTH > sizeof(body)
            || !send_chunk(assoc, HALYARD_DGRAM_CHUNK_HELLO_ECHO, DEFAULT_FLAGS, body,
                           HELLO_BODY_LENGTH + chunk->body_length - COOKIE_HEADER_LENGTH,
                           (unsigned)assoc->hello_addressing))
            emit_unhandled(assoc, "a cookie too long to echo in one chunk", datagram, datagram_length, 0);
        return HALYARD_DGRAM_OK;

    case HALYARD_DGRAM_CHUNK_ACCEPT:
        if (chunk->body_length < 4) {
            emit_unhandled(assoc, "an accept too short to carry a sequence and an acknowledgement", datagram,
                           datagram_length, 0);
            return HALYARD_DGRAM_OK;
        }
        assoc->peer_sequence = read_be16(chunk->body);
        assoc->delivered = 0;
        assoc->sequence = read_be16(chunk->body + 2);
        assoc->phase = HALYARD_DGRAM_PHASE_CONNECTED;
        emit_simple(assoc, HALYARD_DGRAM_EVENT_CONNECTION_OPENED, 0);
        return HALYARD_DGRAM_OK;

    case HALYARD_DGRAM_CHUNK_HELLO: {
        /*
         * The peer is opening a connection. [X] No capture shows this side - every recording has the client
         * opening - so the cookie mirrors the console's own shape with fresh bytes where its cookie sat. If
         * a console ignores it, this is the line to suspect. The reply mirrors the peer's addressing.
         */
        uint8_t cookie[sizeof(k_peer_cookie_prefix) + PEER_COOKIE_RANDOM_LENGTH];

        memcpy(cookie, k_peer_cookie_prefix, sizeof(k_peer_cookie_prefix));
        if (!draw(assoc, cookie + sizeof(k_peer_cookie_prefix), PEER_COOKIE_RANDOM_LENGTH))
            return HALYARD_DGRAM_NO_ENTROPY;
        assoc->peer_sequence = chunk->body_length >= 2 ? read_be16(chunk->body) : 0;
        assoc->delivered = 0;
        (void)send_chunk(assoc, HALYARD_DGRAM_CHUNK_COOKIE, 0x00, cookie, sizeof(cookie), chunk->word_count);
        return HALYARD_DGRAM_OK;
    }

    case HALYARD_DGRAM_CHUNK_HELLO_ECHO: {
        /* It returned our cookie; the connection is open with us as the responder. */
        uint8_t seq[2];
        uint8_t accept[4 + sizeof(k_capability_block) + 4 + 2];

        if (!draw(assoc, seq, sizeof(seq)) || !draw(assoc, accept + 4 + sizeof(k_capability_block), 4))
            return HALYARD_DGRAM_NO_ENTROPY;
        assoc->sequence = read_be16(seq);
        assoc->phase = HALYARD_DGRAM_PHASE_CONNECTED;
        sequence_pair(accept, assoc->sequence, (uint16_t)(assoc->peer_sequence + 1u));
        memcpy(accept + 4, k_capability_block, sizeof(k_capability_block));
        write_be16(accept + 4 + sizeof(k_capability_block) + 4, WINDOW_OR_MTU);
        (void)send_chunk(assoc, HALYARD_DGRAM_CHUNK_ACCEPT, DEFAULT_FLAGS, accept, sizeof(accept),
                         chunk->word_count);
        emit_simple(assoc, HALYARD_DGRAM_EVENT_CONNECTION_OPENED, 1);
        return HALYARD_DGRAM_OK;
    }

    case HALYARD_DGRAM_CHUNK_DATA:
    case HALYARD_DGRAM_CHUNK_DATA_RETRANSMIT:
        return on_data(assoc, chunk);

    case HALYARD_DGRAM_CHUNK_ACK:
    case HALYARD_DGRAM_CHUNK_ACK_EXTENDED:
        /* Nothing is retransmitted at this layer yet, so an acknowledgement is only information. */
        return HALYARD_DGRAM_OK;

    case HALYARD_DGRAM_CHUNK_CLOSE:
        assoc->phase = HALYARD_DGRAM_PHASE_CLOSED;
        emit_simple(assoc, HALYARD_DGRAM_EVENT_PEER_CLOSED, 0);
        return HALYARD_DGRAM_OK;

    default:
        emit_unhandled(assoc, "unknown chunk type", datagram, datagram_length, chunk->type);
        return HALYARD_DGRAM_OK;
    }
}

halyard_dgram_status halyard_dgram_assoc_on_datagram(halyard_dgram_assoc *assoc, const uint8_t *data, size_t length)
{
    halyard_dgram_prelude prelude;
    halyard_dgram_chunk chunk;
    size_t offset = 0;
    int any = 0;

    if (assoc == NULL || (data == NULL && length != 0))
        return HALYARD_DGRAM_BAD_ARGUMENT;

    if (halyard_dgram_prelude_parse(data, length, &prelude))
        return on_prelude(assoc, &prelude);

    /*
     * .NET reads every chunk into a list and then reacts; reacting as each is read is the same, because
     * reading depends on nothing the reaction changes. An entropy failure stops at that chunk, having
     * reacted to the ones before it - there is no undoing a datagram already sent.
     */
    while (halyard_dgram_chunk_next(data, length, &offset, &chunk)) {
        halyard_dgram_status status;

        any = 1;
        status = on_chunk(assoc, &chunk, data, length);
        if (status != HALYARD_DGRAM_OK)
            return status;
    }

    if (!any)
        emit_unhandled(assoc, "neither a prelude nor a well-formed chunk", data, length, 0);
    return HALYARD_DGRAM_OK;
}

const uint8_t *halyard_dgram_assoc_inbound(const halyard_dgram_assoc *assoc, size_t *out_length)
{
    if (assoc == NULL) {
        if (out_length != NULL)
            *out_length = 0;
        return NULL;
    }
    if (out_length != NULL)
        *out_length = assoc->inbound_length;
    return assoc->inbound;
}

void halyard_dgram_assoc_clear_inbound(halyard_dgram_assoc *assoc)
{
    if (assoc != NULL)
        assoc->inbound_length = 0;
}

void halyard_dgram_assoc_consume_inbound(halyard_dgram_assoc *assoc, size_t count)
{
    if (assoc == NULL)
        return;
    if (count >= assoc->inbound_length) {
        assoc->inbound_length = 0;
        return;
    }
    memmove(assoc->inbound, assoc->inbound + count, assoc->inbound_length - count);
    assoc->inbound_length -= count;
}

const char *halyard_dgram_phase_name(halyard_dgram_phase phase)
{
    switch (phase) {
    case HALYARD_DGRAM_PHASE_IDLE: return "idle";
    case HALYARD_DGRAM_PHASE_HANDSHAKING: return "handshaking";
    case HALYARD_DGRAM_PHASE_ESTABLISHED: return "established";
    case HALYARD_DGRAM_PHASE_CONNECTED: return "connected";
    case HALYARD_DGRAM_PHASE_CLOSED: return "closed";
    }
    return "unknown";
}

const char *halyard_dgram_event_name(halyard_dgram_event_kind kind)
{
    switch (kind) {
    case HALYARD_DGRAM_EVENT_PRELUDE_ESTABLISHED: return "PreludeEstablished";
    case HALYARD_DGRAM_EVENT_CONNECTION_OPENED: return "ConnectionOpened";
    case HALYARD_DGRAM_EVENT_DATA_RECEIVED: return "DataReceived";
    case HALYARD_DGRAM_EVENT_PEER_CLOSED: return "PeerClosed";
    case HALYARD_DGRAM_EVENT_UNHANDLED: return "Unhandled";
    }
    return "unknown";
}
