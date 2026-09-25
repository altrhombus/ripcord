/*
 * libripcord - the connect sequence's timing and bookkeeping, as pure functions.
 *
 * WHY A SEPARATE FILE. halyard_client.c owns sockets and a console, and nothing that owns a console can
 * be tested without one. The decisions it makes on a clock - when to ask for a keyframe again, when an
 * input STATE packet is owed, what a zero in the config means - do not need either, and each of them has
 * a hardware finding behind it that a test should be able to hold in place:
 *
 *   the IDR latch            b124 (asking once per loss starves the decoder) and b141 (armed at start,
 *                            because a loss-free stream otherwise never asks at all)
 *   the input cadence        history on a transition, state on change or every 200 ms (ports/ripcord-3ds,
 *                            ported to the PS3 as send_input, rc_connect.c 1238-1408)
 *   the config defaults      each one named, with the side of the PS3/.NET disagreement it follows
 *
 * Internal to libripcord/client: a host includes halyard_client.h, not this.
 */
#ifndef HALYARD_CLIENT_POLICY_H
#define HALYARD_CLIENT_POLICY_H

#include "halyard_client.h"
#include "../input/halyard_input.h"

#include <stddef.h>
#include <stdint.h>

/* ---- The defaults, one place, each with its source. See halyard_client_config in halyard_client.h. ---- */

/* The PS3's 20 s (b36: a console just woken took 12.3 s merely to answer a SRCH). .NET's 1 s rests on
 * cap50's 60 ms prompt, which was measured on a console already awake. */
#define HALYARD_CLIENT_DEFAULT_SIGNIN_WINDOW_MS 20000u
/* .NET's senkusha budget: 20 attempts at 300 ms, inside the 8 s box below. */
#define HALYARD_CLIENT_DEFAULT_SENKUSHA_ATTEMPTS 20u
/* .NET's stream budget, matched to the vendor client persisting ~100 tries / ~30 s under loss (cap55).
 * The PS3 used 20; its console was on a clean wire. */
#define HALYARD_CLIENT_DEFAULT_STREAM_ATTEMPTS 100u
#define HALYARD_CLIENT_DEFAULT_ATTEMPT_INTERVAL_MS 300u
/* .NET's ask. The granted size is reported: the PS3's lv2 capped a 1 MB ask at 124,800 bytes (b128). */
#define HALYARD_CLIENT_DEFAULT_RCVBUF (4 * 1024 * 1024)
/* .NET's launch-spec defaults (HalyardStreamingSession.BuildLaunchSpecJson). The PS3's 640x360@30 and
 * 2000 kbps were that port's decoder talking, as its 720p cap was. */
#define HALYARD_CLIENT_DEFAULT_WIDTH 1280
#define HALYARD_CLIENT_DEFAULT_HEIGHT 720
#define HALYARD_CLIENT_DEFAULT_FPS 60
#define HALYARD_CLIENT_DEFAULT_BITRATE_KBPS 10000

/*
 * Fills every 0 in `in` with its default and normalises the rest, into `out` (which may be `in`).
 *
 * fps: anything above 30 becomes 60 and anything else 30, because the only two rates either reference
 * implementation has ever asked for are 30 and 60 and the console's answer to a third is unknown [X].
 * width/height: if either is 0 both take the default, since half a resolution is not one.
 * require_session_ready: 0 is the default, which is 1 (wait); a NEGATIVE value is the explicit "do not
 * wait". The header's "Default 1" and its "0 takes the default" could not both hold for a flag
 * otherwise, and a zero-initialised config is how every host starts.
 * rcvbuf_bytes: 0 is the default ask; negative asks for nothing and keeps the platform's size.
 * route: 0 is LOCAL.
 */
void halyard_client_config_resolve(const halyard_client_config *in, halyard_client_config *out);

/* ---- The IDR latch ---------------------------------------------------------------------------------- */

/*
 * "We are blind until a keyframe arrives." ARMED AT THE START of streaming (b141: 890 frames, no
 * keyframe, no request ever sent, because the only thing that had ever armed it was loss and the crypto
 * rewrite removed the loss), re-armed by loss and by the host's KEYFRAME command, and cleared only when a
 * keyframe is actually delivered - a request that produced nothing has fixed nothing (b124: one request
 * per loss event, 2 keyframes in thirty seconds, 835 of 857 frames undecodable).
 *
 * While armed, a request is due at most every HALYARD_CLIENT_IDR_MIN_MS: one IDR repairs the whole
 * chain, so asking while the answer is in flight spends upstream bandwidth on a repair already coming.
 */
#define HALYARD_CLIENT_IDR_MIN_MS 200u

typedef struct {
    int awaiting;
    int requested_once;
    uint64_t last_request_ms;
    uint32_t requests;
} halyard_client_idr_latch;

void halyard_client_idr_reset(halyard_client_idr_latch *latch);
void halyard_client_idr_arm(halyard_client_idr_latch *latch);
void halyard_client_idr_keyframe(halyard_client_idr_latch *latch);

/* 1 if a request should go out now, and records it as sent; 0 if not armed or still throttled. */
int halyard_client_idr_due(halyard_client_idr_latch *latch, uint64_t now_ms);

/* Milliseconds until the latch next wants to send, or UINT32_MAX when it is not armed. */
uint32_t halyard_client_idr_wait_ms(const halyard_client_idr_latch *latch, uint64_t now_ms);

/* ---- Periodic timers -------------------------------------------------------------------------------- */

/*
 * 1 when `now` has reached `*next`, which then moves to now + interval. From now rather than from the
 * old deadline, as the PS3's send_periodic does: a late pump should not be followed by a burst of
 * catch-up heartbeats.
 */
int halyard_client_timer_due(uint64_t *next_ms, uint64_t now_ms, uint32_t interval_ms);

/* Milliseconds until `next`, 0 if already due. Saturates at UINT32_MAX. */
uint32_t halyard_client_timer_wait_ms(uint64_t next_ms, uint64_t now_ms);

/* ---- Input cadence ---------------------------------------------------------------------------------- */

#define HALYARD_CLIENT_INPUT_POLL_MS  4u    /* rc_connect.c RC_INPUT_POLL_INTERVAL_MS */
#define HALYARD_CLIENT_INPUT_STATE_MS 200u  /* rc_connect.c RC_INPUT_STATE_INTERVAL_MS */

/* The two packet types, spec 6.3 via HalyardInputPacketWriter.cs. */
#define HALYARD_CLIENT_INPUT_TYPE_HISTORY 0x01u
#define HALYARD_CLIENT_INPUT_TYPE_STATE   0x06u

typedef struct {
    uint64_t last_state_ms;
    int state_sent;
} halyard_client_input_cadence;

/* Up to two packets, header written, payload built, NOT sealed. A length of 0 means "nothing owed". */
typedef struct {
    uint8_t history[HALYARD_INPUT_MAX_PACKET];
    size_t history_length;
    uint8_t state[HALYARD_INPUT_MAX_PACKET];
    size_t state_length;
} halyard_client_input_packets;

/*
 * One input poll's worth of decisions, ported from the PS3's send_input (rc_connect.c 1374-1407) minus
 * its menu. A HISTORY packet when a button or trigger transitioned (the writer decides that, and the
 * list is cumulative so a dropped packet does not lose a transition); a STATE packet when anything
 * differs from the previous poll, or HALYARD_CLIENT_INPUT_STATE_MS have passed since the last one,
 * because the console wants to keep hearing from a controller that is merely still. The first poll
 * always produces a STATE packet.
 *
 * `writer->previous` and `have_previous` are updated here, once both packets are built - both decisions
 * are against the SAME previous frame, which is what rc_connect.c 1404-1407 records. Sequence numbers
 * advance only for packets actually produced.
 *
 * Compared field by field, never with memcmp: the struct has padding, and padding is not copied
 * reliably by assignment.
 */
void halyard_client_input_step(halyard_client_input_cadence *cadence, halyard_input_writer *writer,
                               const halyard_input_state *state, uint64_t now_ms,
                               halyard_client_input_packets *out);

int halyard_client_input_equal(const halyard_input_state *a, const halyard_input_state *b);

/* ---- Statistics ------------------------------------------------------------------------------------- */

/* Bytes over a window in ms as kbps (bits per millisecond), 0 for an empty window. Saturates. */
uint32_t halyard_client_kbps(uint64_t bytes, uint64_t window_ms);

#endif /* HALYARD_CLIENT_POLICY_H */
