/*
 * libripcord - the 9303 association over a real transport: the pump that halyard_dgram.h leaves out.
 *
 * Ported from src/Ripcord.Protocol.Halyard/Transport/HalyardDatagramControlChannel.cs (and the
 * IHalyardDatagramTransport seam beside it), which remains the reference. Like that class, THIS HOLDS
 * NO PROTOCOL KNOWLEDGE: it puts whatever the association emits on the wire, feeds it whatever arrives,
 * re-sends on a quiet receive window, and gives up at a stage deadline with a message a person can act
 * on. Every decision about what the protocol does next is in halyard_dgram_assoc.c.
 *
 * WHERE C DIFFERS, AND WHY.
 *
 *   - It never owns a socket. The .NET channel owns one unless handed another; here the transport is
 *     always the caller's. That is what the A/V leg needs anyway: its socket must exist before the
 *     candidate exchange (the port it binds is what is advertised, and what STUN is gathered on) and must
 *     outlive the prelude, because the same socket then carries every Takion datagram.
 *   - No cancellation token. Every wait is bounded by `stage_timeout_ms`, which stands in for one - the
 *     same trade rc_stun_gather makes.
 *   - No threads, no async. Each call blocks until its stage completes or times out, polling a
 *     non-blocking transport with a short sleep - the idiom every port's sockets are known to honour (see
 *     rc_stun_client.c). halyard_dgram_channel_poll is the non-blocking step a single-threaded caller runs
 *     in its main loop, which is how the console's periodic prelude probes keep being echoed while the
 *     caller is busy elsewhere; .NET gets that for free from a pump that is always awaiting.
 *   - Errors are statuses and the log is an optional callback, where .NET throws and takes an
 *     Action<string>.
 */
#ifndef HALYARD_DGRAM_CHANNEL_H
#define HALYARD_DGRAM_CHANNEL_H

#include "halyard_dgram.h"

#include <netinet/in.h>
#include <stddef.h>
#include <stdint.h>

/*
 * IHalyardDatagramTransport. A seam for the same reason .NET has one: so the pump can be tested against a
 * scripted console in-process, without binding a port and talking to itself.
 *
 *   send     one datagram to the peer; 1 on success, 0 on failure.
 *   receive  MUST NOT BLOCK. > 0: a datagram of that many bytes is in `buffer`; 0: nothing waiting right
 *            now; < 0: the transport has failed. (.NET's ReceiveAsync throws on timeout instead; here the
 *            waiting is the channel's job, so the transport never has to know a deadline.)
 */
typedef struct {
    int (*send)(void *ctx, const uint8_t *datagram, size_t length);
    long (*receive)(void *ctx, uint8_t *buffer, size_t capacity);
    void *ctx;
} halyard_dgram_transport;

/*
 * HalyardUdpDatagramTransport: a caller's UDP socket, addressed at one console endpoint. The socket must be
 * bound; it is put into non-blocking mode (rc_socket_set_nonblocking) and never closed here.
 *
 * Datagrams from ANY source are passed up, as .NET's UdpChannel passes them: a stray one reaches the
 * association, which reports it as unhandled. On the A/V socket that includes STUN answers.
 */
typedef struct {
    int sock;
    struct sockaddr_in peer;
} halyard_dgram_udp;

/* Returns 1 with `*out` wired to `udp`, or 0 for a bad argument or a socket that will not go non-blocking. */
int halyard_dgram_udp_transport(halyard_dgram_udp *udp, int sock, const struct sockaddr_in *peer,
                                halyard_dgram_transport *out);

/* HalyardDatagramControlOptions, with the same defaults. */
typedef struct {
    uint32_t receive_timeout_ms;        /* one receive window before re-sending: 5000 */
    uint32_t stage_timeout_ms;          /* how long each stage may take: 30000 */
    uint32_t listen_before_opening_ms;  /* 0: we initiate (the .NET comment says why that is right) */
    halyard_dgram_addressing hello_addressing; /* PORT_PAIR, the shape every capture carries */
    void (*log)(void *ctx, const char *line);  /* optional progress sink */
    void *log_ctx;

    /*
     * C only: what a single-threaded caller does while a blocking stage waits. `tick` (optional) is called
     * on every empty poll - roughly every 5 ms - so the caller can keep something else alive meanwhile:
     * libripcord/client services the control session and pulls its host's commands there, because the
     * media leg's prelude is run while the console expects heartbeats answered. `abort` (optional) is asked
     * after each tick, and a non-zero answer ends the stage with HALYARD_DGRAM_CHANNEL_ABORTED. .NET needs
     * neither: its keep-alive is another task, and its cancellation is a token. A tick must not call into
     * this same channel.
     */
    void (*tick)(void *ctx);
    int (*abort)(void *ctx);
    void *tick_ctx;
} halyard_dgram_options;

void halyard_dgram_options_default(halyard_dgram_options *options);

typedef enum {
    HALYARD_DGRAM_CHANNEL_OK = 0,
    HALYARD_DGRAM_CHANNEL_TIMEOUT,          /* the stage deadline passed; the log says which stage */
    HALYARD_DGRAM_CHANNEL_PEER_CLOSED,      /* the peer closed the connection (receive, exchange) */
    HALYARD_DGRAM_CHANNEL_TRANSPORT_ERROR,  /* a send or receive failed */
    HALYARD_DGRAM_CHANNEL_NO_ENTROPY,       /* rc_random_bytes failed; nothing was sent with a guessable value */
    HALYARD_DGRAM_CHANNEL_NOT_CONNECTED,
    HALYARD_DGRAM_CHANNEL_TOO_LONG,         /* payload over HALYARD_DGRAM_MAX_PAYLOAD */
    HALYARD_DGRAM_CHANNEL_BUFFER_TOO_SMALL, /* the complete response did not fit the caller's buffer */
    HALYARD_DGRAM_CHANNEL_BAD_ARGUMENT,
    HALYARD_DGRAM_CHANNEL_ABORTED           /* options.abort asked to stop */
} halyard_dgram_channel_status;

/* Big enough for any datagram this transport carries; a larger one is truncated by the transport. */
#define HALYARD_DGRAM_RECEIVE_MAX 4096u

typedef struct {
    halyard_dgram_transport transport;
    halyard_dgram_options options;
    halyard_dgram_assoc assoc;
    int send_failed;
    int saw_data;
    int saw_close;
    uint8_t receive_buffer[HALYARD_DGRAM_RECEIVE_MAX];
} halyard_dgram_channel;

/*
 * The constructor. `peer_address` (network order) and `peer_port` (host order) name the console as DATA,
 * as .NET takes an IPEndPoint: the prelude's echo reflects them, and a channel that cannot name them sends
 * an echo the console cannot validate the path from. `options` may be NULL for the defaults.
 *
 * The two ids are the localHashedIds from the two signaling OFFERs - ours, and the console's from its OFFER
 * - which is why this transport cannot be opened before the candidate exchange.
 */
halyard_dgram_channel_status halyard_dgram_channel_init(halyard_dgram_channel *channel,
                                                        const halyard_dgram_transport *transport,
                                                        const uint8_t peer_address[4], uint16_t peer_port,
                                                        const uint8_t local_id[HALYARD_DGRAM_HASHED_ID_LENGTH],
                                                        const uint8_t peer_id[HALYARD_DGRAM_HASHED_ID_LENGTH],
                                                        const halyard_dgram_options *options);

/*
 * BeginAsync: put our opening Init on the wire and return without waiting. The protocol forces this
 * split: the console cannot answer until signaling has told it our candidate, and opens an association of
 * its own the moment our ACCEPT arrives - so the Init goes out after our OFFER and before our ACCEPT, and
 * blocking there would stall the very signaling that makes an answer possible.
 */
halyard_dgram_channel_status halyard_dgram_channel_begin(halyard_dgram_channel *channel);

/*
 * EstablishAsync: complete the prelude in whichever role the peer leaves us, re-sending our Init on every
 * quiet receive window. Idempotent. Also what the A/V leg runs on its own socket before Takion.
 */
halyard_dgram_channel_status halyard_dgram_channel_establish(halyard_dgram_channel *channel);

/*
 * OpenConnectionAsync: open a chunk connection (or accept the peer's), re-sending the hello on every quiet
 * window - the console routinely ignores the first. Waits for CONNECTED specifically, not merely "left
 * established": the peer's teardown of the previous connection often arrives after the next hello.
 */
halyard_dgram_channel_status halyard_dgram_channel_open_connection(halyard_dgram_channel *channel);

/* SendBytesAsync: send raw bytes on the open connection. */
halyard_dgram_channel_status halyard_dgram_channel_send_bytes(halyard_dgram_channel *channel, const uint8_t *data,
                                                              size_t length);

/*
 * ReceiveBytesAsync, as a byte pipe: waits for payload on the open connection and moves up to `capacity`
 * bytes of it into `out` (anything that did not fit stays for the next call). Returns OK with *out_length > 0,
 * PEER_CLOSED when the peer closed the connection with nothing pending, or TIMEOUT.
 */
halyard_dgram_channel_status halyard_dgram_channel_receive_bytes(halyard_dgram_channel *channel, uint8_t *out,
                                                                 size_t capacity, size_t *out_length);

/*
 * ExchangeAsync: establish, open a connection, send `request`, and return the complete HTTP response,
 * judged by halyard_dgram_http_complete. The /sess/rgst path runs exactly this. PEER_CLOSED if the console
 * closed the connection before answering.
 */
halyard_dgram_channel_status halyard_dgram_channel_exchange(halyard_dgram_channel *channel, const uint8_t *request,
                                                            size_t request_length, uint8_t *response,
                                                            size_t response_capacity, size_t *response_length);

/*
 * CloseConnectionAsync: end the open connection politely. Best-effort and never an error, because a
 * teardown must not fail and a console that never hears it is no worse off than before this existed.
 */
void halyard_dgram_channel_close_connection(halyard_dgram_channel *channel);

/*
 * C only: one non-blocking step. Feeds every datagram already waiting (up to a bounded number, so a busy
 * socket cannot hold the caller), answering prelude probes and acknowledging data as it goes. Sets
 * *out_data when payload is waiting in the association and *out_closed when the peer closed; either may be
 * NULL; the close it reports is one seen during this call. The persistent control connection after
 * /sess/ctrl is serviced with this.
 */
halyard_dgram_channel_status halyard_dgram_channel_poll(halyard_dgram_channel *channel, int *out_data,
                                                        int *out_closed);

/* How far the association has got. */
halyard_dgram_phase halyard_dgram_channel_phase(const halyard_dgram_channel *channel);

#endif /* HALYARD_DGRAM_CHANNEL_H */
