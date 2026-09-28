/* See halyard_dgram_session.h. */
#include "halyard_dgram_session.h"

#include "halyard_wan_candidates.h"
#include "../net/rc_udp.h"

#include <stdio.h>
#include <string.h>
#include <unistd.h>

/* ---- the control pipe ---- */

static int pipe_fail(halyard_dgram_control_pipe *p, halyard_dgram_channel_status status)
{
    p->last_status = status;
    return -1;
}

/*
 * ConnectAsync. Its "_pending.Clear()" - anything buffered belonged to the connection that just closed -
 * is the session's own `buffered = 0` after each open, and the association drops a closed connection's
 * inbound bytes itself when the next one opens, so neither needs doing here.
 */
static int dgram_open(void *ctx, const char *host, unsigned short port)
{
    halyard_dgram_control_pipe *p = (halyard_dgram_control_pipe *)ctx;
    halyard_dgram_channel_status status;

    (void)host;
    (void)port;
    p->peer_closed = 0;
    if ((status = halyard_dgram_channel_establish(p->channel)) != HALYARD_DGRAM_CHANNEL_OK)
        return pipe_fail(p, status);
    if ((status = halyard_dgram_channel_open_connection(p->channel)) != HALYARD_DGRAM_CHANNEL_OK)
        return pipe_fail(p, status);
    return p->handle;
}

static int dgram_send_all(void *ctx, int handle, const uint8_t *data, size_t length)
{
    halyard_dgram_control_pipe *p = (halyard_dgram_control_pipe *)ctx;
    halyard_dgram_channel_status status;

    (void)handle;
    status = halyard_dgram_channel_send_bytes(p->channel, data, length);
    if (status != HALYARD_DGRAM_CHANNEL_OK)
        return pipe_fail(p, status);
    return 0;
}

static long dgram_recv(void *ctx, int handle, uint8_t *buffer, size_t capacity)
{
    halyard_dgram_control_pipe *p = (halyard_dgram_control_pipe *)ctx;
    const uint8_t *inbound;
    size_t length = 0;
    size_t take;

    (void)handle;
    if (capacity == 0u)
        return 0;

    /* Only when nothing is waiting already, so a reader that took part of a burst is not made to wait on
     * the socket for the rest of what it has. */
    inbound = halyard_dgram_assoc_inbound(&p->channel->assoc, &length);
    if (length == 0u) {
        int closed = 0;
        halyard_dgram_channel_status status = halyard_dgram_channel_poll(p->channel, NULL, &closed);

        if (status != HALYARD_DGRAM_CHANNEL_OK) {
            p->last_status = status;
            return -1;
        }
        if (closed)
            p->peer_closed = 1;
        inbound = halyard_dgram_assoc_inbound(&p->channel->assoc, &length);
    }
    if (length > 0u) {
        take = length < capacity ? length : capacity;
        memcpy(buffer, inbound, take);
        halyard_dgram_assoc_consume_inbound(&p->channel->assoc, take);
        return (long)take;
    }
    return p->peer_closed ? -2 : 0;
}

static void dgram_close(void *ctx, int handle, int polite)
{
    halyard_dgram_control_pipe *p = (halyard_dgram_control_pipe *)ctx;

    (void)handle;
    if (polite)
        halyard_dgram_channel_close_connection(p->channel);
}

void halyard_dgram_control_pipe_init(halyard_dgram_control_pipe *p, halyard_dgram_channel *channel, int handle)
{
    if (p == NULL)
        return;
    memset(p, 0, sizeof(*p));
    p->channel = channel;
    p->handle = handle < 0 ? 0 : handle;
    p->last_status = HALYARD_DGRAM_CHANNEL_OK;
    p->pipe.name = "datagram";
    p->pipe.open = dgram_open;
    p->pipe.send_all = dgram_send_all;
    p->pipe.recv = dgram_recv;
    p->pipe.close = dgram_close;
    p->pipe.ctx = p;
}

size_t halyard_dgram_host_header(const char *host, unsigned port, char *out, size_t out_size)
{
    uint8_t a[4];
    int n;

    if (host == NULL || out == NULL || out_size == 0u)
        return 0;
    if (halyard_wan_parse_ipv4(host, a))
        n = snprintf(out, out_size, "%3u.%3u.%3u.%3u:%u", (unsigned)a[0], (unsigned)a[1], (unsigned)a[2],
                     (unsigned)a[3], port);
    else
        n = snprintf(out, out_size, "%s:%u", host, port);
    if (n < 0 || (size_t)n >= out_size) {
        out[0] = '\0';
        return 0;
    }
    return (size_t)n;
}

/* ---- the registration exchange ---- */

int halyard_dgram_regist_exchange(void *user, const uint8_t *request, size_t request_length, uint8_t *response,
                                  size_t response_size, size_t *out_response_length)
{
    halyard_dgram_channel *channel = (halyard_dgram_channel *)user;

    if (channel == NULL || out_response_length == NULL)
        return 0;
    *out_response_length = 0;
    return halyard_dgram_channel_exchange(channel, request, request_length, response, response_size,
                                          out_response_length)
           == HALYARD_DGRAM_CHANNEL_OK;
}

/* ---- the leg ---- */

void halyard_rendezvous_leg_init(halyard_rendezvous_leg *leg)
{
    if (leg == NULL)
        return;
    leg->sock = -1;
    leg->local_port = 0;
    leg->have_mapping = 0;
    leg->attached = 0;
    leg->gather_status = RC_STUN_GATHER_NO_ANSWER;
    memset(&leg->mapping, 0, sizeof(leg->mapping));
    memset(&leg->peer, 0, sizeof(leg->peer));
}

int halyard_rendezvous_leg_open(halyard_rendezvous_leg *leg, const uint8_t bind_address[4], uint16_t port,
                                int rcvbuf_bytes)
{
    if (leg == NULL)
        return 0;
    halyard_rendezvous_leg_close(leg);
    leg->sock = rc_udp_open_bound(bind_address, port, rcvbuf_bytes, &leg->local_port);
    return leg->sock >= 0;
}

rc_stun_gather_status halyard_rendezvous_leg_gather(halyard_rendezvous_leg *leg, const struct sockaddr_in *servers,
                                                    size_t server_count, unsigned attempts,
                                                    uint32_t per_attempt_timeout_ms)
{
    if (leg == NULL || leg->sock < 0)
        return RC_STUN_GATHER_BAD_ARGUMENT;
    leg->have_mapping = 0;
    if (servers == NULL || server_count == 0u) {
        leg->gather_status = RC_STUN_GATHER_NO_ANSWER;
        return leg->gather_status;
    }
    leg->gather_status = rc_stun_discover_mapping(leg->sock, servers, server_count,
                                                  attempts == 0u ? RC_STUN_DEFAULT_ATTEMPTS : attempts,
                                                  per_attempt_timeout_ms == 0u ? RC_STUN_DEFAULT_TIMEOUT_MS
                                                                               : per_attempt_timeout_ms,
                                                  &leg->mapping);
    /* IPv4 only, like the rest of the stack: an IPv6 mapping is decoded by rc_stun but advertises nothing. */
    leg->have_mapping = leg->gather_status == RC_STUN_GATHER_OK
                        && leg->mapping.reflexive.family == RC_STUN_FAMILY_IPV4;
    return leg->gather_status;
}

halyard_dgram_channel_status halyard_rendezvous_leg_attach(halyard_rendezvous_leg *leg, const uint8_t peer_address[4],
                                                           uint16_t peer_port,
                                                           const uint8_t local_id[HALYARD_DGRAM_HASHED_ID_LENGTH],
                                                           const uint8_t peer_id[HALYARD_DGRAM_HASHED_ID_LENGTH],
                                                           const halyard_dgram_options *options)
{
    halyard_dgram_transport transport;

    if (leg == NULL || leg->sock < 0 || peer_address == NULL || local_id == NULL || peer_id == NULL)
        return HALYARD_DGRAM_CHANNEL_BAD_ARGUMENT;

    memset(&leg->peer, 0, sizeof(leg->peer));
    leg->peer.sin_family = AF_INET;
    leg->peer.sin_port = htons(peer_port);
    memcpy(&leg->peer.sin_addr.s_addr, peer_address, 4);

    if (!halyard_dgram_udp_transport(&leg->udp, leg->sock, &leg->peer, &transport))
        return HALYARD_DGRAM_CHANNEL_BAD_ARGUMENT;
    leg->attached = 0;
    if (halyard_dgram_channel_init(&leg->channel, &transport, peer_address, peer_port, local_id, peer_id, options)
        != HALYARD_DGRAM_CHANNEL_OK)
        return HALYARD_DGRAM_CHANNEL_BAD_ARGUMENT;
    leg->attached = 1;
    return HALYARD_DGRAM_CHANNEL_OK;
}

void halyard_rendezvous_leg_close(halyard_rendezvous_leg *leg)
{
    if (leg == NULL)
        return;
    if (leg->sock >= 0)
        (void)close(leg->sock);
    leg->sock = -1;
    leg->attached = 0;
}
