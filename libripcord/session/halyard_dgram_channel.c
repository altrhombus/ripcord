/*
 * libripcord - the 9303 socket pump. See halyard_dgram_channel.h; the reference is
 * HalyardDatagramControlChannel.cs.
 *
 * Separate from the association for the reason rc_stun_client.c is separate from rc_stun.c: this is the
 * only file of the transport that touches a socket, the clock or the CSPRNG, so the fuzz link can take
 * the association without dragging in an entropy source the host deliberately does not provide.
 */
#include "halyard_dgram_channel.h"

#include "../net/rc_tcp.h"
#include "../platform/rc_platform.h"

#include <stdio.h>
#include <string.h>

#include <sys/socket.h>
#include <sys/types.h>

/* Sleep between empty non-blocking reads; small against every window here (see rc_stun_client.c). */
#define POLL_INTERVAL_MS 5u

/* How many waiting datagrams one poll() feeds before returning to its caller. */
#define POLL_BATCH 64

/* ---- the UDP transport ---- */

static int udp_send(void *ctx, const uint8_t *datagram, size_t length)
{
    halyard_dgram_udp *udp = (halyard_dgram_udp *)ctx;
    ssize_t sent = sendto(udp->sock, datagram, length, 0, (const struct sockaddr *)&udp->peer,
                          (socklen_t)sizeof(udp->peer));

    return sent >= 0 && (size_t)sent == length;
}

/*
 * A failing recvfrom is reported as "nothing yet", not as a transport failure, for the reason
 * rc_stun_client.c gives: which errno a non-blocking read with no data reports is exactly what the ports
 * disagree about, and misreading "nothing yet" as fatal would kill every stage instantly. The cost of the
 * conservative reading is a stage timeout, never a wrong answer.
 */
static long udp_receive(void *ctx, uint8_t *buffer, size_t capacity)
{
    halyard_dgram_udp *udp = (halyard_dgram_udp *)ctx;
    ssize_t n = recvfrom(udp->sock, buffer, capacity, 0, NULL, NULL);

    return n > 0 ? (long)n : 0;
}

int halyard_dgram_udp_transport(halyard_dgram_udp *udp, int sock, const struct sockaddr_in *peer,
                                halyard_dgram_transport *out)
{
    if (udp == NULL || peer == NULL || out == NULL || sock < 0)
        return 0;
    if (!rc_socket_set_nonblocking(sock))
        return 0;
    udp->sock = sock;
    udp->peer = *peer;
    out->send = udp_send;
    out->receive = udp_receive;
    out->ctx = udp;
    return 1;
}

/* ---- plumbing between the association and the transport ---- */

static void logf_line(halyard_dgram_channel *channel, const char *text)
{
    if (channel->options.log != NULL)
        channel->options.log(channel->options.log_ctx, text);
}

static void on_send(void *ctx, const uint8_t *datagram, size_t length)
{
    halyard_dgram_channel *channel = (halyard_dgram_channel *)ctx;

    if (!channel->transport.send(channel->transport.ctx, datagram, length))
        channel->send_failed = 1;
}

/*
 * Every event is named in the log, always. A silent stall on this transport is what cost the first live
 * runs their diagnosis; an association that reports what it ignored turns one into evidence.
 */
static void on_event(void *ctx, const halyard_dgram_event *event)
{
    halyard_dgram_channel *channel = (halyard_dgram_channel *)ctx;

    if (channel->options.log != NULL) {
        char line[160];

        if (event->kind == HALYARD_DGRAM_EVENT_UNHANDLED)
            snprintf(line, sizeof(line), "ignored a datagram: %s (type 0x%02X, %lu bytes)", event->reason,
                     (unsigned)event->chunk_type, (unsigned long)event->data_length);
        else
            snprintf(line, sizeof(line), "%s", halyard_dgram_event_name(event->kind));
        logf_line(channel, line);
    }

    if (event->kind == HALYARD_DGRAM_EVENT_DATA_RECEIVED)
        channel->saw_data = 1;
    else if (event->kind == HALYARD_DGRAM_EVENT_PEER_CLOSED)
        channel->saw_close = 1;
}

static int on_random(void *ctx, uint8_t *out, size_t length)
{
    (void)ctx;
    return rc_random_bytes(out, length);
}

static halyard_dgram_channel_status from_assoc(const halyard_dgram_channel *channel, halyard_dgram_status status)
{
    if (channel->send_failed)
        return HALYARD_DGRAM_CHANNEL_TRANSPORT_ERROR;
    switch (status) {
    case HALYARD_DGRAM_OK: return HALYARD_DGRAM_CHANNEL_OK;
    case HALYARD_DGRAM_NO_ENTROPY: return HALYARD_DGRAM_CHANNEL_NO_ENTROPY;
    case HALYARD_DGRAM_NOT_CONNECTED: return HALYARD_DGRAM_CHANNEL_NOT_CONNECTED;
    case HALYARD_DGRAM_TOO_LONG: return HALYARD_DGRAM_CHANNEL_TOO_LONG;
    case HALYARD_DGRAM_BAD_ARGUMENT: return HALYARD_DGRAM_CHANNEL_BAD_ARGUMENT;
    }
    return HALYARD_DGRAM_CHANNEL_BAD_ARGUMENT;
}

void halyard_dgram_options_default(halyard_dgram_options *options)
{
    if (options == NULL)
        return;
    options->receive_timeout_ms = 5000u;
    options->stage_timeout_ms = 30000u;
    options->listen_before_opening_ms = 0u;
    options->hello_addressing = HALYARD_DGRAM_ADDRESS_PORT_PAIR;
    options->log = NULL;
    options->log_ctx = NULL;
}

halyard_dgram_channel_status halyard_dgram_channel_init(halyard_dgram_channel *channel,
                                                        const halyard_dgram_transport *transport,
                                                        const uint8_t peer_address[4], uint16_t peer_port,
                                                        const uint8_t local_id[HALYARD_DGRAM_HASHED_ID_LENGTH],
                                                        const uint8_t peer_id[HALYARD_DGRAM_HASHED_ID_LENGTH],
                                                        const halyard_dgram_options *options)
{
    halyard_dgram_callbacks callbacks;

    if (channel == NULL || transport == NULL || transport->send == NULL || transport->receive == NULL)
        return HALYARD_DGRAM_CHANNEL_BAD_ARGUMENT;

    channel->transport = *transport;
    if (options != NULL)
        channel->options = *options;
    else
        halyard_dgram_options_default(&channel->options);
    channel->send_failed = 0;
    channel->saw_data = 0;
    channel->saw_close = 0;

    callbacks.send = on_send;
    callbacks.event = on_event;
    callbacks.random = on_random;
    callbacks.ctx = channel;
    return halyard_dgram_assoc_init(&channel->assoc, &callbacks, local_id, peer_id, peer_address, peer_port)
                   == HALYARD_DGRAM_OK
               ? HALYARD_DGRAM_CHANNEL_OK
               : HALYARD_DGRAM_CHANNEL_BAD_ARGUMENT;
}

/*
 * Waits for one datagram, at most `window_ms` and never past the stage deadline. Returns its length,
 * 0 for a quiet window, or -1 for a transport failure.
 */
static long wait_datagram(halyard_dgram_channel *channel, uint32_t window_ms, uint64_t stage_start,
                          uint32_t stage_ms)
{
    uint64_t start = rc_time_ms();

    for (;;) {
        long n = channel->transport.receive(channel->transport.ctx, channel->receive_buffer,
                                            sizeof(channel->receive_buffer));
        uint64_t now;

        if (n != 0)
            return n < 0 ? -1 : n;
        now = rc_time_ms();
        if (now - start >= (uint64_t)window_ms || now - stage_start >= (uint64_t)stage_ms)
            return 0;
        rc_sleep_ms(POLL_INTERVAL_MS);
    }
}

static halyard_dgram_channel_status feed(halyard_dgram_channel *channel, size_t length)
{
    return from_assoc(channel, halyard_dgram_assoc_on_datagram(&channel->assoc, channel->receive_buffer, length));
}

typedef enum { UNTIL_PRELUDE, UNTIL_CONNECTED, UNTIL_DATA_OR_CLOSE, UNTIL_RESPONSE } pump_goal;
typedef enum { QUIET_NOTHING, QUIET_RETRY, QUIET_REOPEN } quiet_action;

static int goal_reached(halyard_dgram_channel *channel, pump_goal goal)
{
    halyard_dgram_phase phase = channel->assoc.phase;
    size_t length;
    const uint8_t *inbound;

    switch (goal) {
    case UNTIL_PRELUDE:
        return phase != HALYARD_DGRAM_PHASE_IDLE && phase != HALYARD_DGRAM_PHASE_HANDSHAKING;
    case UNTIL_CONNECTED:
        return phase == HALYARD_DGRAM_PHASE_CONNECTED;
    case UNTIL_DATA_OR_CLOSE:
        inbound = halyard_dgram_assoc_inbound(&channel->assoc, &length);
        (void)inbound;
        return length > 0 || channel->saw_close;
    case UNTIL_RESPONSE:
        inbound = halyard_dgram_assoc_inbound(&channel->assoc, &length);
        return (length > 0 && halyard_dgram_http_complete(inbound, length)) || phase == HALYARD_DGRAM_PHASE_CLOSED;
    }
    return 1;
}

/*
 * PumpUntilAsync: feed datagrams until `goal`, re-sending via `quiet` whenever one receive window passes
 * with nothing. Quiet is normal during a hole-punch, so only the stage deadline ends it.
 */
static halyard_dgram_channel_status pump(halyard_dgram_channel *channel, pump_goal goal, quiet_action quiet,
                                         const char *timeout_message)
{
    uint64_t stage_start = rc_time_ms();
    uint32_t stage_ms = channel->options.stage_timeout_ms;

    while (!goal_reached(channel, goal)) {
        long n;
        halyard_dgram_channel_status status;

        if (rc_time_ms() - stage_start >= (uint64_t)stage_ms) {
            logf_line(channel, timeout_message);
            return HALYARD_DGRAM_CHANNEL_TIMEOUT;
        }

        n = wait_datagram(channel, channel->options.receive_timeout_ms, stage_start, stage_ms);
        if (n < 0)
            return HALYARD_DGRAM_CHANNEL_TRANSPORT_ERROR;
        if (n == 0) {
            halyard_dgram_status quiet_status = HALYARD_DGRAM_OK;

            if (quiet == QUIET_RETRY)
                quiet_status = halyard_dgram_assoc_retry(&channel->assoc);
            else if (quiet == QUIET_REOPEN)
                quiet_status = halyard_dgram_assoc_reopen_connection(&channel->assoc);
            status = from_assoc(channel, quiet_status);
            if (status != HALYARD_DGRAM_CHANNEL_OK)
                return status;
            continue;
        }

        status = feed(channel, (size_t)n);
        if (status != HALYARD_DGRAM_CHANNEL_OK)
            return status;
    }
    return HALYARD_DGRAM_CHANNEL_OK;
}

/* ---- the stages ---- */

halyard_dgram_channel_status halyard_dgram_channel_begin(halyard_dgram_channel *channel)
{
    if (channel == NULL)
        return HALYARD_DGRAM_CHANNEL_BAD_ARGUMENT;
    return from_assoc(channel, halyard_dgram_assoc_open(&channel->assoc));
}

halyard_dgram_channel_status halyard_dgram_channel_establish(halyard_dgram_channel *channel)
{
    halyard_dgram_channel_status status;
    int answered = 0;

    if (channel == NULL)
        return HALYARD_DGRAM_CHANNEL_BAD_ARGUMENT;

    /* ListenForPeerAsync: only when configured to, and only if nothing has happened yet. Something arriving
     * in the window is fed first - for an Init, that makes us the responder - and then we do not open. */
    if (channel->options.listen_before_opening_ms > 0 && channel->assoc.phase == HALYARD_DGRAM_PHASE_IDLE) {
        uint64_t start = rc_time_ms();
        long n = wait_datagram(channel, channel->options.listen_before_opening_ms, start,
                               channel->options.listen_before_opening_ms);

        if (n < 0)
            return HALYARD_DGRAM_CHANNEL_TRANSPORT_ERROR;
        if (n > 0) {
            logf_line(channel, "the console opened the prelude; answering");
            status = feed(channel, (size_t)n);
            if (status != HALYARD_DGRAM_CHANNEL_OK)
                return status;
            answered = 1;
        }
    }

    if (!answered) {
        status = from_assoc(channel, halyard_dgram_assoc_open(&channel->assoc));
        if (status != HALYARD_DGRAM_CHANNEL_OK)
            return status;
    }

    status = pump(channel, UNTIL_PRELUDE, QUIET_RETRY,
                  "The console did not complete the control prelude. On a LAN this normally means 9303 is "
                  "unreachable; the account route also requires that the candidate exchange has completed, "
                  "since the prelude names both peers by their signaling id.");
    if (status == HALYARD_DGRAM_CHANNEL_OK)
        logf_line(channel, "prelude established");
    return status;
}

halyard_dgram_channel_status halyard_dgram_channel_open_connection(halyard_dgram_channel *channel)
{
    halyard_dgram_channel_status status;
    char line[64];

    if (channel == NULL)
        return HALYARD_DGRAM_CHANNEL_BAD_ARGUMENT;

    status = from_assoc(channel, halyard_dgram_assoc_open_connection(&channel->assoc,
                                                                     channel->options.hello_addressing));
    if (status != HALYARD_DGRAM_CHANNEL_OK)
        return status;
    channel->saw_close = 0;
    snprintf(line, sizeof(line), "hello sent, word count %u", (unsigned)channel->options.hello_addressing);
    logf_line(channel, line);

    status = pump(channel, UNTIL_CONNECTED, QUIET_REOPEN, "The console did not open a control connection.");
    if (status == HALYARD_DGRAM_CHANNEL_OK)
        logf_line(channel, "connection open");
    return status;
}

halyard_dgram_channel_status halyard_dgram_channel_send_bytes(halyard_dgram_channel *channel, const uint8_t *data,
                                                              size_t length)
{
    if (channel == NULL)
        return HALYARD_DGRAM_CHANNEL_BAD_ARGUMENT;
    return from_assoc(channel, halyard_dgram_assoc_send(&channel->assoc, data, length));
}

halyard_dgram_channel_status halyard_dgram_channel_receive_bytes(halyard_dgram_channel *channel, uint8_t *out,
                                                                 size_t capacity, size_t *out_length)
{
    halyard_dgram_channel_status status;
    const uint8_t *inbound;
    size_t length;
    size_t take;

    if (channel == NULL || out == NULL || out_length == NULL || capacity == 0)
        return HALYARD_DGRAM_CHANNEL_BAD_ARGUMENT;
    *out_length = 0;

    /* Per call, as .NET's ReceiveBytesAsync keeps its own flag: a Close that arrived earlier - routinely the
     * teardown of the PREVIOUS connection, landing while the next one opened - is not this one's end. */
    channel->saw_close = 0;
    status = pump(channel, UNTIL_DATA_OR_CLOSE, QUIET_NOTHING, "The console sent nothing on the control connection.");
    if (status != HALYARD_DGRAM_CHANNEL_OK)
        return status;

    inbound = halyard_dgram_assoc_inbound(&channel->assoc, &length);
    if (length == 0) {
        channel->saw_close = 0;
        return HALYARD_DGRAM_CHANNEL_PEER_CLOSED;
    }

    take = length < capacity ? length : capacity;
    memcpy(out, inbound, take);
    halyard_dgram_assoc_consume_inbound(&channel->assoc, take);
    *out_length = take;
    return HALYARD_DGRAM_CHANNEL_OK;
}

halyard_dgram_channel_status halyard_dgram_channel_exchange(halyard_dgram_channel *channel, const uint8_t *request,
                                                            size_t request_length, uint8_t *response,
                                                            size_t response_capacity, size_t *response_length)
{
    halyard_dgram_channel_status status;
    const uint8_t *inbound;
    size_t length;
    char line[64];

    if (channel == NULL || request == NULL || response == NULL || response_length == NULL)
        return HALYARD_DGRAM_CHANNEL_BAD_ARGUMENT;
    *response_length = 0;

    if ((status = halyard_dgram_channel_establish(channel)) != HALYARD_DGRAM_CHANNEL_OK)
        return status;
    if ((status = halyard_dgram_channel_open_connection(channel)) != HALYARD_DGRAM_CHANNEL_OK)
        return status;
    if ((status = halyard_dgram_channel_send_bytes(channel, request, request_length)) != HALYARD_DGRAM_CHANNEL_OK)
        return status;
    snprintf(line, sizeof(line), "request sent (%lu bytes of HTTP)", (unsigned long)request_length);
    logf_line(channel, line);

    status = pump(channel, UNTIL_RESPONSE, QUIET_NOTHING, "The console did not answer the request.");
    if (status != HALYARD_DGRAM_CHANNEL_OK)
        return status;

    inbound = halyard_dgram_assoc_inbound(&channel->assoc, &length);
    if (length == 0 || !halyard_dgram_http_complete(inbound, length)) {
        logf_line(channel, "The console closed the connection before answering.");
        return HALYARD_DGRAM_CHANNEL_PEER_CLOSED;
    }
    if (length > response_capacity)
        return HALYARD_DGRAM_CHANNEL_BUFFER_TOO_SMALL;

    memcpy(response, inbound, length);
    *response_length = length;
    halyard_dgram_assoc_clear_inbound(&channel->assoc);
    snprintf(line, sizeof(line), "response complete (%lu bytes)", (unsigned long)length);
    logf_line(channel, line);
    return HALYARD_DGRAM_CHANNEL_OK;
}

void halyard_dgram_channel_close_connection(halyard_dgram_channel *channel)
{
    if (channel != NULL)
        (void)halyard_dgram_assoc_close_connection(&channel->assoc);
}

halyard_dgram_channel_status halyard_dgram_channel_poll(halyard_dgram_channel *channel, int *out_data,
                                                        int *out_closed)
{
    int i;
    size_t length;

    if (channel == NULL)
        return HALYARD_DGRAM_CHANNEL_BAD_ARGUMENT;

    /* *out_closed reports a Close seen during THIS poll, for the reason receive_bytes gives. */
    channel->saw_close = 0;
    for (i = 0; i < POLL_BATCH; i++) {
        long n = channel->transport.receive(channel->transport.ctx, channel->receive_buffer,
                                            sizeof(channel->receive_buffer));
        halyard_dgram_channel_status status;

        if (n < 0)
            return HALYARD_DGRAM_CHANNEL_TRANSPORT_ERROR;
        if (n == 0)
            break;
        status = feed(channel, (size_t)n);
        if (status != HALYARD_DGRAM_CHANNEL_OK)
            return status;
    }

    (void)halyard_dgram_assoc_inbound(&channel->assoc, &length);
    if (out_data != NULL)
        *out_data = length > 0;
    if (out_closed != NULL)
        *out_closed = channel->saw_close;
    return HALYARD_DGRAM_CHANNEL_OK;
}

halyard_dgram_phase halyard_dgram_channel_phase(const halyard_dgram_channel *channel)
{
    return channel == NULL ? HALYARD_DGRAM_PHASE_IDLE : channel->assoc.phase;
}
