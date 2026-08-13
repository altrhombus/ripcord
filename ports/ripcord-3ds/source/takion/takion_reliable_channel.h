/*
 * ripcord-3ds - Phase 5 Takion transport: the reliable channel.
 *
 * Ported from Ripcord.Protocol.Halyard.Takion.TakionConnection + TakionReliableChannel, combined into
 * one socket-facing object: drives the SCTP 4-way handshake (see takion_handshake.h) to ESTABLISHED,
 * then sends/receives DATA chunks with a minimal in-order-accept + fixed-interval-retransmit +
 * cumulative-SACK policy - "sufficient for a clean-LAN first picture" per
 * docs/protocol/ps5-remoteplay-v1-spec.md sec8.2, which explicitly declares RTO/window tuning
 * unspecified and unnecessary for that. No RTT estimation, no Karn's algorithm, no GMAC sealing (that
 * needs the stream cipher, a separate, later phase) - all deliberately deferred, not overlooked.
 *
 * Unlike every other Takion module, this one is 3DS-only (like source/net/rc_soc.c and rc_tcp.c): it
 * owns a UDP socket and a libctru clock, so it has no host-side test - source/takion/main.c is its only
 * exercise, the same position rc_soc.c/rc_tcp.c are in.
 */
#ifndef TAKION_RELIABLE_CHANNEL_H
#define TAKION_RELIABLE_CHANNEL_H

#include "takion_reassembler.h"

#include <stddef.h>
#include <stdint.h>
#include <sys/socket.h>
#include <netinet/in.h>

#define TAKION_MAX_UNACKED 32
#define TAKION_MAX_PACKET 1500
#define TAKION_RETRANSMIT_INTERVAL_MS 300u
#define TAKION_MAX_PAYLOAD_PER_CHUNK 1000 /* matches the .NET reference; fragments larger sends */

typedef struct {
    int in_use;
    uint32_t tsn;
    uint8_t packet[TAKION_MAX_PACKET];
    size_t length;
    uint64_t last_sent_ms;
} takion_unacked_chunk;

/*
 * ~49.6 KB (TAKION_MAX_UNACKED x TAKION_MAX_PACKET dominates). NEVER DECLARE ONE AS A LOCAL: libctru
 * gives a .3dsx's main thread a 32 KB stack by default, so an ordinary local overflows it in the
 * function prologue and data-aborts before the body runs. Real crash, real hardware, 2026-08-12 - and it
 * logged nothing at all, because the function never started. Use file scope, `static`, or the heap.
 * The 3DS build passes -Wframe-larger-than=8192 so this fails at compile time now rather than on device.
 */
/*
 * Seals one outgoing packet in place - writes its key position and GMAC tag. Supplied by the caller
 * rather than implemented here on purpose: this layer is pure transport and knows nothing about the
 * stream cipher (that is source/stream's business, and Phase 5 was built deliberately without it). The
 * callback owns the key-position counter, because control DATA and SACKs share one advancing sequence.
 */
typedef void (*takion_seal_fn)(void *ctx, uint8_t *packet, size_t length);

typedef struct {
    int sock;                      /* caller-owned, non-blocking, not connect()ed (we use sendto/recvfrom) */
    struct sockaddr_in peer;
    uint32_t local_tag;
    uint32_t peer_tag;
    uint32_t next_send_tsn;
    uint32_t expected_recv_tsn;
    uint32_t last_acked_tsn;       /* the cumulative_tsn_ack we most recently sent */
    int have_received_any;         /* expected_recv_tsn/last_acked_tsn are meaningless until this is set */
    int established;
    takion_seal_fn seal;           /* NULL until the stream keys exist - see enable_sealing below */
    void *seal_ctx;
    takion_reassembler reassembler;
    takion_unacked_chunk unacked[TAKION_MAX_UNACKED];
} takion_reliable_channel;

/*
 * Drives the SCTP handshake to ESTABLISHED over `sock` (already bound/non-blocking) against `peer`,
 * retrying INIT and COOKIE_ECHO up to `max_attempts` times with `per_attempt_timeout_ms` between
 * attempts. Returns 1 on success (ch is ready for takion_channel_send/poll), 0 on failure/timeout.
 *
 * NOTE THAT THIS BLOCKS for up to max_attempts x per_attempt_timeout_ms. In the real connect flow that
 * is a problem rather than a detail: the TCP control channel must keep answering HEARTBEAT_REQ the whole
 * time or the console resets the session ~15-30 s in, and this port has no second thread to do it on.
 * Use takion_channel_connect_ticked() there.
 */
int takion_channel_connect(takion_reliable_channel *ch, int sock, struct sockaddr_in peer,
                           unsigned max_attempts, unsigned per_attempt_timeout_ms);

/* Called on every poll iteration while the handshake waits, so a single-threaded caller can service
 * something else - in practice the control channel's heartbeats. Must not block. */
typedef void (*takion_tick_fn)(void *ctx);

/*
 * As above, but invokes `tick` (may be NULL) roughly every 20 ms while waiting. This is what makes a
 * single-threaded connect flow possible: the handshake keeps its own timing while the caller keeps the
 * control channel alive underneath it.
 */
int takion_channel_connect_ticked(takion_reliable_channel *ch, int sock, struct sockaddr_in peer,
                                  unsigned max_attempts, unsigned per_attempt_timeout_ms,
                                  takion_tick_fn tick, void *tick_ctx);

/*
 * Switches on GMAC sealing for everything sent from now on. Call immediately after the stream keys are
 * derived and BEFORE sending anything else.
 *
 * SACKs are sealed too, and forgetting that is a specific, well-documented failure: the console never
 * sees the acknowledgement of its STREAM_INFO and drops the session reporting "streaminfoack fail". A
 * retransmitted DATA chunk keeps the tag it was sealed with, because the stored packet is resent
 * verbatim - its key position was reserved once, when it was built.
 */
void takion_channel_enable_sealing(takion_reliable_channel *ch, takion_seal_fn seal, void *seal_ctx);

/*
 * Sends `payload` reliably on `channel`, fragmenting at TAKION_MAX_PAYLOAD_PER_CHUNK if needed. Returns
 * 1 on success, 0 if too many chunks are already unacked (TAKION_MAX_UNACKED) or a send failed.
 */
int takion_channel_send(takion_reliable_channel *ch, unsigned channel,
                        const uint8_t *payload, size_t payload_length);

/*
 * Services the channel: retransmits anything unacked past TAKION_RETRANSMIT_INTERVAL_MS, and processes
 * one pending incoming packet if there is one. Returns 1 and fills *out_channel, *out_message and
 * *out_length if a complete reliable message was just reassembled (valid until the next call - copy it
 * out before calling again), 0 if nothing completed this call (the common case - call this in a loop),
 * -1 on a socket error.
 */
int takion_channel_poll(takion_reliable_channel *ch, unsigned *out_channel,
                        const uint8_t **out_message, size_t *out_length);

#endif /* TAKION_RELIABLE_CHANNEL_H */
