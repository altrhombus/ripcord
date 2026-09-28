/*
 * libripcord - what the session layers need from the UDP 9303 transport: the control session's byte pipe,
 * the account registration's exchange, and a "leg" (one socket, its STUN mapping and its association).
 *
 * WHY THESE THREE, TOGETHER. halyard_dgram_channel.h is the transport and deliberately knows nothing above
 * it. The code above it was written against other contracts before the transport existed in C: the
 * control session against a byte pipe (halyard_control_session.h, halyard_control_pipe), and account
 * pairing against "send this request, give me the whole reply" (halyard_account_regist_flow.h,
 * halyard_account_regist_exchange_fn). Each needs a few lines of adapter, and those adapters are the whole
 * of this file's protocol content; the leg is the socket bookkeeping every rendezvous caller repeats.
 *
 * Ported from src/Ripcord.Protocol.Halyard/Transport/:
 *
 *   HalyardDatagramSessionControlChannel.cs   -> halyard_dgram_control_pipe
 *   HalyardDatagramRegistrationTransport.cs   -> halyard_dgram_regist_exchange (its ExchangeAsync), and
 *                                                halyard_rendezvous_leg (its PrepareAsync, plus the socket
 *                                                and STUN work HalyardAccountConsoleSession does beside it)
 *
 * NO ALLOCATION AND NO THREADS, like everything under it. Blocking calls are bounded by the channel's
 * stage deadline and can be ticked and aborted through halyard_dgram_options.
 */
#ifndef HALYARD_DGRAM_SESSION_H
#define HALYARD_DGRAM_SESSION_H

#include "halyard_control_session.h"
#include "halyard_dgram_channel.h"
#include "../net/rc_stun.h"

#include <stddef.h>
#include <stdint.h>

/* ==== the control session's byte pipe (HalyardDatagramSessionControlChannel) =========================== */

/*
 * One association, several connections. The console tears down each chunk connection once it has
 * answered, so every pipe open() is a FRESH CONNECTION over the existing prelude, not a fresh socket -
 * which is what the captured client does: rgst, init and ctrl as three connections over one association,
 * the last kept for the persistent binary frames.
 *
 *   open      establish (idempotent) then open_connection - ConnectAsync. `host`/`port` are ignored: the
 *             association is already addressed at the console, as .NET ignores its endpoint argument.
 *             Returns `handle` from init, or -1.
 *   send_all  send_bytes - SendBytesAsync. Not fragmented, as .NET does not: a payload over
 *             HALYARD_DGRAM_MAX_PAYLOAD fails, and the largest this pipe carries is the /sess/ctrl request.
 *   recv      NON-BLOCKING: one halyard_dgram_channel_poll, which also answers the console's prelude probes
 *             and acknowledges data, then whatever payload has accumulated. -2 once the peer has closed
 *             the connection AND everything it sent before closing has been read.
 *   close     polite: close_connection (DisposeAsync's goodbye). Not polite (/sess/init, which the console
 *             closes itself): nothing, as .NET sends nothing.
 *
 * WHERE C DIFFERS. .NET's ReceiveBytesAsync forgets a Close that arrived in the same pump as data, and its
 * next read then waits out the stage deadline before reporting failure. This latches the Close and reports
 * it once the data before it has been consumed, which ends a closed session at once instead of 30 s later.
 */
typedef struct {
    halyard_control_pipe pipe;          /* hand &pipe to halyard_control_open_options.pipe */
    halyard_dgram_channel *channel;
    int handle;
    int peer_closed;
    halyard_dgram_channel_status last_status; /* the last non-OK status, for a caller's diagnosis */
} halyard_dgram_control_pipe;

/*
 * Wires `p` to `channel`, which must outlive it. `handle` is what open() hands the session as its
 * socket: the leg's UDP socket, so a host can wait on it, or any value >= 0 for a transport with no
 * descriptor (a scripted test console).
 */
void halyard_dgram_control_pipe_init(halyard_dgram_control_pipe *p, halyard_dgram_channel *channel, int handle);

/*
 * .NET's HostHeader(): the address right-aligned in three columns, then the port - "Host: 192.  0.  2.104:9303",
 * shown with a documentation address - the "%3d.%3d.%3d.%3d" form every captured vendor request writes.
 * A LAN console accepts the plain form; the account route once refused /sess/init while this was one of
 * two remaining differences from the capture, so the datagram route sends the byte-faithful one. A host
 * that is not a dotted quad is written as is. Returns the length, or 0 if it does not fit.
 */
size_t halyard_dgram_host_header(const char *host, unsigned port, char *out, size_t out_size);

/* ==== the account registration's transport (HalyardDatagramRegistrationTransport.ExchangeAsync) ========= */

/*
 * A halyard_account_regist_exchange_fn over the 9303 association: `user` is a halyard_dgram_channel *.
 * Establishes (idempotent), opens a fresh connection, sends the request and returns the complete HTTP
 * reply, judged by halyard_dgram_http_complete - halyard_dgram_channel_exchange, exactly.
 *
 *     halyard_account_regist_run(&params, halyard_dgram_regist_exchange, &leg.channel, &result);
 *
 * The connection is left for the console to close, as .NET leaves it: the same association then serves
 * /sess/init and /sess/ctrl on connections of their own.
 */
int halyard_dgram_regist_exchange(void *user, const uint8_t *request, size_t request_length, uint8_t *response,
                                  size_t response_size, size_t *out_response_length);

/* ==== a rendezvous leg: one socket, its reflexive mapping, its association ============================== */

/*
 * THE SOCKET COMES FIRST, AND STAYS. A NAT maps per source port, so the port a signaling OFFER advertises,
 * the socket STUN is asked from and the socket that then carries the traffic must all be one socket. An
 * internet session has two legs - the control association and the A/V connection - and so two of these.
 *
 * The order a caller runs, taken from HalyardAccountConsoleSession.ConnectAsync and HalyardAccountPairing:
 *
 *   halyard_rendezvous_leg_open     bind; `local_port` is what our OFFER advertises
 *   halyard_rendezvous_leg_gather   optional: STUN on this socket, for the OFFER's STUN/STATIC candidates
 *   ... the cloud tier sends our OFFER ...
 *   halyard_rendezvous_leg_attach   the console's chosen candidate and both hashed ids
 *   halyard_dgram_channel_begin     our Init - after our OFFER and before our ACCEPT (control leg)
 *   ... the cloud tier sends our ACCEPT ...
 *   halyard_dgram_channel_*         establish, exchange, or hand the channel to a pipe
 *   halyard_rendezvous_leg_close
 */
typedef struct {
    int sock;
    uint16_t local_port;                /* host order */
    int have_mapping;
    rc_stun_mapping mapping;            /* valid when have_mapping */
    rc_stun_gather_status gather_status;
    int attached;
    struct sockaddr_in peer;
    halyard_dgram_udp udp;
    halyard_dgram_channel channel;      /* ~23 KB: a leg belongs on the heap or in static storage */
} halyard_rendezvous_leg;

/* A leg that owns nothing (sock -1), so close is safe on it. Call once before anything else. */
void halyard_rendezvous_leg_init(halyard_rendezvous_leg *leg);

/* Binds a non-blocking UDP socket (rc_udp_open_bound). Returns 1, or 0 with leg->sock == -1. */
int halyard_rendezvous_leg_open(halyard_rendezvous_leg *leg, const uint8_t bind_address[4], uint16_t port,
                                int rcvbuf_bytes);

/*
 * rc_stun_discover_mapping on the leg's socket: `servers` in order until two answer, so the NAT's
 * behaviour is classified too. With no servers it does nothing and returns NO_ANSWER - an OFFER with only
 * the LOCAL candidate, which is what .NET offers when discovery fails. Blocks for at most
 * server_count * attempts * per_attempt_timeout_ms; 0 takes RC_STUN_DEFAULT_ATTEMPTS / _TIMEOUT_MS.
 */
rc_stun_gather_status halyard_rendezvous_leg_gather(halyard_rendezvous_leg *leg, const struct sockaddr_in *servers,
                                                    size_t server_count, unsigned attempts,
                                                    uint32_t per_attempt_timeout_ms);

/*
 * Aims the leg at the console: `peer_address` (network order) and `peer_port` (host order) are the
 * candidate chosen with halyard_wan_choose_candidate, and the ids are ours and the console's from the two
 * OFFERs. `options` may be NULL for the defaults. Sends nothing.
 */
halyard_dgram_channel_status halyard_rendezvous_leg_attach(halyard_rendezvous_leg *leg, const uint8_t peer_address[4],
                                                           uint16_t peer_port,
                                                           const uint8_t local_id[HALYARD_DGRAM_HASHED_ID_LENGTH],
                                                           const uint8_t peer_id[HALYARD_DGRAM_HASHED_ID_LENGTH],
                                                           const halyard_dgram_options *options);

/* Closes the socket. Safe on a leg that never opened; says goodbye to nobody (see the pipe's close). */
void halyard_rendezvous_leg_close(halyard_rendezvous_leg *leg);

#endif /* HALYARD_DGRAM_SESSION_H */
