#include "takion_reliable_channel.h"
#include "takion_data_chunk.h"
#include "takion_handshake.h"
#include "takion_message.h"
#include "takion_sack_chunk.h"
#include "takion_control_proto.h"

#include "rc_platform.h"

#include <errno.h>
#include <string.h>

/* RFC 1982 serial-number comparison, so TSN wraparound compares correctly on a long-lived connection -
 * matching TakionReliableChannel.TsnLessOrEqual. */
static int tsn_less_or_equal(uint32_t a, uint32_t b)
{
    return (int32_t)(a - b) <= 0;
}

void takion_channel_enable_sealing(takion_reliable_channel *ch, takion_seal_fn seal, void *seal_ctx)
{
    if (ch == NULL)
        return;
    ch->seal = seal;
    ch->seal_ctx = seal_ctx;
}

/* Seals in place if sealing is on; a no-op before the keys exist, which is the correct behaviour for the
 * handshake and the SESSION exchange that precede them. */
static void seal_if_enabled(takion_reliable_channel *ch, uint8_t *packet, size_t length)
{
    if (ch->seal != NULL)
        ch->seal(ch->seal_ctx, packet, length);
}

static void send_sack(takion_reliable_channel *ch)
{
    uint8_t sack_chunk[32];
    size_t sack_len = takion_sack_build(ch->last_acked_tsn, TAKION_INIT_A_RWND, sack_chunk, sizeof(sack_chunk));
    takion_message_header header;
    uint8_t packet[64];
    size_t packet_len;

    if (sack_len == 0)
        return;

    header.base_type = TAKION_BASE_TYPE_CONTROL;
    header.verification_tag = ch->peer_tag;
    header.gmac_tag = 0;
    header.key_position = 0;
    packet_len = takion_message_build(&header, sack_chunk, sack_len, packet, sizeof(packet));
    if (packet_len > 0) {
        seal_if_enabled(ch, packet, packet_len);
        sendto(ch->sock, packet, packet_len, 0, (struct sockaddr *)&ch->peer, sizeof(ch->peer));
    }
}

int takion_channel_connect(takion_reliable_channel *ch, int sock, struct sockaddr_in peer,
                           unsigned max_attempts, unsigned per_attempt_timeout_ms)
{
    return takion_channel_connect_ticked(ch, sock, peer, max_attempts, per_attempt_timeout_ms, NULL, NULL);
}

int takion_channel_connect_ticked(takion_reliable_channel *ch, int sock, struct sockaddr_in peer,
                                  unsigned max_attempts, unsigned per_attempt_timeout_ms,
                                  takion_tick_fn tick, void *tick_ctx)
{
    uint8_t init_chunk[32];
    size_t init_chunk_len;
    unsigned attempt;
    int got_init_ack = 0;
    uint32_t server_tag = 0, initial_tsn = 0;
    uint8_t cookie[TAKION_COOKIE_SIZE];
    int established = 0;

    memset(ch, 0, sizeof(*ch));
    ch->sock = sock;
    ch->peer = peer;

    /* Not a cryptographic value - SCTP's own verification tag only needs to be "probably unique enough
     * to reject stale packets from a prior association," the same bar rc_tick() clears. */
    ch->local_tag = (uint32_t)rc_tick();
    if (ch->local_tag == 0)
        ch->local_tag = 1;

    init_chunk_len = takion_build_init(ch->local_tag, init_chunk, sizeof(init_chunk));
    if (init_chunk_len == 0)
        return 0;

    /* ---- INIT / INIT_ACK ---- */
    for (attempt = 0; attempt < max_attempts && !got_init_ack; attempt++) {
        takion_message_header header;
        uint8_t packet[64];
        size_t packet_len;
        uint64_t start_ms;

        header.base_type = TAKION_BASE_TYPE_CONTROL;
        header.verification_tag = 0; /* the server's tag is not known yet */
        header.gmac_tag = 0;
        header.key_position = 0;
        packet_len = takion_message_build(&header, init_chunk, init_chunk_len, packet, sizeof(packet));
        if (packet_len > 0)
            sendto(sock, packet, packet_len, 0, (struct sockaddr *)&peer, sizeof(peer));

        start_ms = rc_time_ms();
        while (rc_time_ms() - start_ms <= (uint64_t)per_attempt_timeout_ms) {
            uint8_t recv_buf[TAKION_MAX_PACKET];
            ssize_t n = recvfrom(sock, recv_buf, sizeof(recv_buf), 0, NULL, NULL);

            if (n > 0) {
                takion_message_header in_header;
                const uint8_t *in_chunk;
                size_t in_chunk_len;

                if (takion_message_parse(recv_buf, (size_t)n, &in_header, &in_chunk, &in_chunk_len) == TAKION_HEADER_SIZE
                    && takion_parse_init_ack(in_chunk, in_chunk_len, &server_tag, &initial_tsn, cookie)) {
                    got_init_ack = 1;
                    break;
                }
            }
            if (tick != NULL)
                tick(tick_ctx);
            rc_sleep_ms(20); /* 20 ms */
        }
    }

    if (!got_init_ack)
        return 0;

    ch->peer_tag = server_tag;
    ch->next_send_tsn = ch->local_tag;
    ch->expected_recv_tsn = initial_tsn;

    /* ---- COOKIE_ECHO / COOKIE_ACK ---- */
    for (attempt = 0; attempt < max_attempts && !established; attempt++) {
        takion_message_header header;
        uint8_t echo_chunk[64];
        size_t echo_chunk_len;
        uint8_t packet[128];
        size_t packet_len;
        uint64_t start_ms;

        echo_chunk_len = takion_build_cookie_echo(cookie, echo_chunk, sizeof(echo_chunk));
        if (echo_chunk_len == 0)
            return 0;

        header.base_type = TAKION_BASE_TYPE_CONTROL;
        header.verification_tag = ch->peer_tag;
        header.gmac_tag = 0;
        header.key_position = 0;
        packet_len = takion_message_build(&header, echo_chunk, echo_chunk_len, packet, sizeof(packet));
        if (packet_len > 0)
            sendto(sock, packet, packet_len, 0, (struct sockaddr *)&peer, sizeof(peer));

        start_ms = rc_time_ms();
        while (rc_time_ms() - start_ms <= (uint64_t)per_attempt_timeout_ms) {
            uint8_t peek_buf[TAKION_MAX_PACKET];
            /* MSG_PEEK: a DATA chunk here is equally valid proof of establishment (some real captures
             * skip an explicit COOKIE_ACK), but this function has no reassembly/ack state to process it
             * with - peeking leaves it in the socket's receive queue so the first takion_channel_poll()
             * call handles it exactly like any other incoming DATA chunk, instead of this function
             * quietly dropping it. */
            ssize_t n = recvfrom(sock, peek_buf, sizeof(peek_buf), MSG_PEEK, NULL, NULL);

            if (n > 0) {
                takion_message_header in_header;
                const uint8_t *in_chunk;
                size_t in_chunk_len;

                if (takion_message_parse(peek_buf, (size_t)n, &in_header, &in_chunk, &in_chunk_len) == TAKION_HEADER_SIZE
                    && in_header.verification_tag == ch->local_tag) {
                    int chunk_type = takion_chunk_type(in_chunk, in_chunk_len);

                    if (chunk_type == (int)TAKION_CHUNK_COOKIE_ACK) {
                        recvfrom(sock, peek_buf, sizeof(peek_buf), 0, NULL, NULL); /* consume for real */
                        established = 1;
                        break;
                    }
                    if (chunk_type == (int)TAKION_CHUNK_DATA) {
                        established = 1; /* left in the queue on purpose - see the comment above */
                        break;
                    }
                }
            }
            if (tick != NULL)
                tick(tick_ctx);
            rc_sleep_ms(20);
        }
    }

    ch->established = established;
    return established;
}

int takion_channel_send(takion_reliable_channel *ch, unsigned channel,
                        const uint8_t *payload, size_t payload_length)
{
    size_t offset = 0;
    int is_first = 1;

    do {
        size_t remaining = payload_length - offset;
        size_t take = (remaining > TAKION_MAX_PAYLOAD_PER_CHUNK) ? TAKION_MAX_PAYLOAD_PER_CHUNK : remaining;
        int ending = (offset + take >= payload_length);
        uint8_t chunk[TAKION_MAX_PAYLOAD_PER_CHUNK + 32];
        size_t chunk_len;
        takion_message_header header;
        size_t slot_index;
        takion_unacked_chunk *slot = NULL;

        if (is_first)
            chunk_len = takion_data_build_first(ch->next_send_tsn, channel, ending,
                payload + offset, take, chunk, sizeof(chunk));
        else
            chunk_len = takion_data_build_continuation(ch->next_send_tsn, channel, ending,
                payload + offset, take, chunk, sizeof(chunk));
        if (chunk_len == 0)
            return 0;

        for (slot_index = 0; slot_index < TAKION_MAX_UNACKED; slot_index++) {
            if (!ch->unacked[slot_index].in_use) {
                slot = &ch->unacked[slot_index];
                break;
            }
        }
        if (slot == NULL)
            return 0; /* too many chunks already in flight unacked */

        header.base_type = TAKION_BASE_TYPE_CONTROL;
        header.verification_tag = ch->peer_tag;
        header.gmac_tag = 0;
        header.key_position = 0;
        slot->length = takion_message_build(&header, chunk, chunk_len, slot->packet, sizeof(slot->packet));
        if (slot->length == 0)
            return 0;

        slot->tsn = ch->next_send_tsn;
        slot->in_use = 1;
        slot->last_sent_ms = rc_time_ms();
        /* Sealed once, here. Retransmits below resend these exact bytes rather than re-sealing, so the
         * key position reserved for this packet stays associated with it. */
        seal_if_enabled(ch, slot->packet, slot->length);
        sendto(ch->sock, slot->packet, slot->length, 0, (struct sockaddr *)&ch->peer, sizeof(ch->peer));

        ch->next_send_tsn++;
        offset += take;
        is_first = 0;
    } while (offset < payload_length);

    return 1;
}

/* Shared by takion_channel_poll()'s two DATA-handling paths: accepts an in-order chunk into the
 * reassembler, advances the ack state, and SACKs. Returns whatever the reassembler returned
 * (1 = message complete, 0 = still assembling). */
static int accept_data_chunk(takion_reliable_channel *ch, int is_first, unsigned channel,
                             const uint8_t *payload, size_t payload_length, int ending, uint32_t seq,
                             unsigned *out_channel, const uint8_t **out_message, size_t *out_length)
{
    int complete;

    if (is_first) {
        /* takion_reassembler_first does not fill out_channel - it already knows the channel, because we
         * just passed it in. Setting it here is not tidiness: without it a single-chunk message (the
         * common case - anything under 1000 bytes) leaves the caller's channel variable uninitialised,
         * which on hardware printed a control message as arriving on "channel 0x11bdc4". Only the
         * continuation path was ever filling it in. */
        *out_channel = channel;
        complete = takion_reassembler_first(&ch->reassembler, channel, payload, payload_length, ending,
            out_message, out_length);
    }
    else {
        complete = takion_reassembler_continue(&ch->reassembler, payload, payload_length, ending,
            out_channel, out_message, out_length);
    }

    ch->expected_recv_tsn = seq + 1;
    ch->last_acked_tsn = seq;
    ch->have_received_any = 1;
    send_sack(ch);

    return complete == 1;
}

int takion_channel_await_control(takion_reliable_channel *ch, uint32_t want_type, unsigned timeout_ms,
                                 takion_tick_fn tick, void *tick_ctx,
                                 const uint8_t **out_message, size_t *out_length)
{
    uint64_t start_ms;

    if (out_message != NULL)
        *out_message = NULL;
    if (out_length != NULL)
        *out_length = 0u;
    if (ch == NULL)
        return 0;

    start_ms = rc_time_ms();
    while (rc_time_ms() - start_ms < (uint64_t)timeout_ms) {
        unsigned channel_id;
        const uint8_t *message;
        size_t message_length;
        int result;

        if (tick != NULL)
            tick(tick_ctx);

        result = takion_channel_poll(ch, &channel_id, &message, &message_length);
        if (result == 1) {
            uint32_t type = 0xffffffffu;

            /*
             * A message whose type will not parse is dropped rather than treated as the one wanted.
             * Returning success on an unparseable reply would turn "the console said something" into
             * "the console said yes", which is the wrong direction to be wrong in.
             */
            if (takion_control_peek_type(message, message_length, &type) && type == want_type) {
                if (out_message != NULL)
                    *out_message = message;
                if (out_length != NULL)
                    *out_length = message_length;
                return 1;
            }
            (void)channel_id;
        } else if (result == -1) {
            return 0;
        }
        rc_sleep_ms(10);
    }
    return 0;
}

int takion_channel_poll(takion_reliable_channel *ch, unsigned *out_channel,
                        const uint8_t **out_message, size_t *out_length)
{
    uint64_t now_ms = rc_time_ms();
    size_t i;
    uint8_t recv_buf[TAKION_MAX_PACKET];
    ssize_t n;
    takion_message_header header;
    const uint8_t *chunk;
    size_t chunk_length;
    int chunk_type;

    for (i = 0; i < TAKION_MAX_UNACKED; i++) {
        if (ch->unacked[i].in_use && now_ms - ch->unacked[i].last_sent_ms >= TAKION_RETRANSMIT_INTERVAL_MS) {
            sendto(ch->sock, ch->unacked[i].packet, ch->unacked[i].length, 0,
                (struct sockaddr *)&ch->peer, sizeof(ch->peer));
            ch->unacked[i].last_sent_ms = now_ms;
        }
    }

    n = recvfrom(ch->sock, recv_buf, sizeof(recv_buf), 0, NULL, NULL);
    if (n < 0)
        return (errno == EAGAIN || errno == EWOULDBLOCK) ? 0 : -1;
    if (n == 0)
        return 0;

    if (takion_message_parse(recv_buf, (size_t)n, &header, &chunk, &chunk_length) != TAKION_HEADER_SIZE)
        return 0;
    if (header.verification_tag != ch->local_tag)
        return 0; /* not this association - ignore rather than trust an unverified peer */

    chunk_type = takion_chunk_type(chunk, chunk_length);

    if (chunk_type == (int)TAKION_CHUNK_SACK) {
        takion_sack_info info;
        if (takion_sack_parse(chunk, chunk_length, &info)) {
            for (i = 0; i < TAKION_MAX_UNACKED; i++) {
                if (ch->unacked[i].in_use && tsn_less_or_equal(ch->unacked[i].tsn, info.cumulative_tsn_ack))
                    ch->unacked[i].in_use = 0;
            }
        }
        return 0;
    }

    if (chunk_type == (int)TAKION_CHUNK_DATA) {
        int is_first = !ch->reassembler.pending;
        uint32_t seq;
        unsigned channel = 0;
        int ending;
        const uint8_t *payload;
        size_t payload_length;
        int parsed = is_first
            ? takion_data_parse_first(chunk, chunk_length, &seq, &channel, &ending, &payload, &payload_length)
            : takion_data_parse_continuation(chunk, chunk_length, &seq, &channel, &ending,
                                             &payload, &payload_length);

        if (!parsed)
            return 0;

        if (!ch->have_received_any || seq == ch->expected_recv_tsn) {
            return accept_data_chunk(ch, is_first, channel, payload, payload_length, ending, seq,
                out_channel, out_message, out_length) ? 1 : 0;
        }

        /* Out of order: re-ack what we actually have, so the sender retransmits from there instead of
         * waiting on a timer - "minimal in-order delivery" per spec sec8.2, not a buffering reassembler. */
        if (ch->have_received_any)
            send_sack(ch);
        return 0;
    }

    return 0; /* an unrecognised chunk type on this connection - ignore rather than guess */
}
