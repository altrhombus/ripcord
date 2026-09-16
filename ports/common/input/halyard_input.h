/*
 * ripcord-3ds - controller input, up the stream socket.
 *
 * Ported from src/Ripcord.Protocol.Halyard.Common/Input/HalyardInputPacketWriter.cs, which carries the
 * provenance: spec 6.3, re-derived from this project's own capture cap48 by correlating 10,165 state
 * packets and 1,258 history packets against a deliberately scripted input sequence. Every field position
 * below is cited there; nothing here is guessed.
 *
 * TWO PACKET TYPES, one 12-byte header, both sealed with the shared up-direction key position:
 *
 *   off 0      u8    6 = state, 1 = history
 *   off 1..2   u16   sequence number, per type, +1 per packet, BIG-endian
 *   off 3      u8    0
 *   off 4..7   u32   key position (drives the crypto nonce)          [written at seal]
 *   off 8..11  u32   GMAC tag                                        [written at seal]
 *   off 0xc..        AES-CTR encrypted payload
 *
 * TWO DECRYPTION FACTS THAT EACH COST A DEBUGGING PASS on the .NET side, recorded in the spec so this
 * port did not have to repeat them: the key position is a u32 at offset 4 (not where control puts it),
 * and the CTR counter increments LITTLE-endian - a stock big-endian CTR decodes only the first 16-byte
 * block correctly and produces noise after it.
 *
 * STATE (type 6) is a periodic analog snapshot, fixed 0x1c bytes. HISTORY (type 1) carries button
 * transitions only when something changes, newest event first.
 *
 * WHAT A GIVEN PAD CANNOT SEND is the front end's business, not this file's. The six motion fields go out
 * at their captured resting values and orientation at a fixed identity unless a caller supplies better -
 * the spec notes a pad without a motion sensor may do exactly this. L2/R2 are still sent as 0x00 or 0xff
 * rather than a level, which is the one place this is behind HalyardInputPacketWriter.cs; every button
 * the .NET writer knows about now has a bit here, including the stick clicks.
 */
#ifndef HALYARD_INPUT_H
#define HALYARD_INPUT_H

#include <stddef.h>
#include <stdint.h>

#define HALYARD_INPUT_HEADER_LENGTH 0x0cu
#define HALYARD_INPUT_STATE_PAYLOAD 0x1cu

/* Big enough for the header plus a state payload, or a history packet of several events. */
#define HALYARD_INPUT_MAX_PACKET 64u

/* Neutral, wire-facing controller snapshot. Sticks are s16, left/up NEGATIVE - established in cap48 from
 * the scripted excursions, where "full up" drove the left-Y field to -32767. */
typedef struct {
    uint32_t buttons;   /* HALYARD_PAD_* bits */
    int16_t left_x, left_y;
    int16_t right_x, right_y;
} halyard_input_state;

/* Button bits. Names are the PlayStation control's, because that is what goes on the wire; the 3DS
 * mapping lives in the connect flow, not here. */
#define HALYARD_PAD_CROSS      (1u << 0)
#define HALYARD_PAD_CIRCLE     (1u << 1)
#define HALYARD_PAD_SQUARE     (1u << 2)
#define HALYARD_PAD_TRIANGLE   (1u << 3)
#define HALYARD_PAD_DPAD_UP    (1u << 4)
#define HALYARD_PAD_DPAD_DOWN  (1u << 5)
#define HALYARD_PAD_DPAD_LEFT  (1u << 6)
#define HALYARD_PAD_DPAD_RIGHT (1u << 7)
#define HALYARD_PAD_L1         (1u << 8)
#define HALYARD_PAD_R1         (1u << 9)
#define HALYARD_PAD_L2         (1u << 10)
#define HALYARD_PAD_R2         (1u << 11)
#define HALYARD_PAD_OPTIONS    (1u << 12)
#define HALYARD_PAD_CREATE     (1u << 13)
#define HALYARD_PAD_PS         (1u << 14)
/*
 * THE STICK CLICKS, added when a port arrived that has them.
 *
 * They were absent because the first port to use this was a 3DS, whose hardware has no stick to click -
 * and a gap in the shared layer that exists for one front end's hardware is a gap for every front end
 * after it. HalyardInputPacketWriter.cs has carried both codes all along.
 */
#define HALYARD_PAD_L3         (1u << 15)
#define HALYARD_PAD_R3         (1u << 16)

/*
 * How many recent events every history packet repeats.
 *
 * NOT OPTIONAL, and leaving it out is what made input unusable on hardware. History packets are
 * CUMULATIVE with newest events first, and the console tracks button state from them - so a single
 * dropped packet loses a transition permanently. A press whose release never arrives leaves the console
 * believing the button is still held, which presents as input that is delayed, repeated, fighting itself,
 * or attributed to the wrong button entirely.
 *
 * Re-sending is free of risk because events carry ABSOLUTE state, not toggles: seeing the same press
 * twice is identical to seeing it once. Four is what the .NET writer uses.
 */
#define HALYARD_INPUT_HISTORY_RESEND 4
#define HALYARD_INPUT_EVENT_MAX 3

typedef struct {
    uint16_t state_seq;
    uint16_t history_seq;
    halyard_input_state previous;
    int have_previous;

    /* Recent events, NEWEST FIRST - the ordering cap48 established, and what let the atomic event
     * boundaries be recovered in the first place (an older packet is a strict suffix of the next). */
    uint8_t history[HALYARD_INPUT_HISTORY_RESEND][HALYARD_INPUT_EVENT_MAX];
    uint8_t history_len[HALYARD_INPUT_HISTORY_RESEND];
    int history_count;
} halyard_input_writer;

void halyard_input_writer_init(halyard_input_writer *w);

/*
 * Builds the 0x1c-byte STATE payload (no header, no sealing) into `buf`. Returns bytes written, or 0 if
 * the buffer is too small. The caller prepends the header and seals.
 */
size_t halyard_input_build_state_payload(const halyard_input_state *state, uint8_t *buf, size_t buf_size);

/*
 * Records the transitions between `w->previous` and `state`, then writes the whole recent history -
 * newest first, including re-sent events from earlier packets.
 *
 * Returns bytes written, or 0 when nothing transitioned, which is the signal not to send a packet.
 * MUTATES the writer's history list, so it must be called exactly once per polled frame. Does not update
 * w->previous; the caller does that once both payloads are built.
 */
size_t halyard_input_build_history_payload(halyard_input_writer *w,
                                           const halyard_input_state *state,
                                           uint8_t *buf, size_t buf_size);

/* Writes the 12-byte header. Key position and tag are left zero for the sealer to fill. */
size_t halyard_input_write_header(uint8_t type, uint16_t sequence, uint8_t *buf, size_t buf_size);

#endif /* HALYARD_INPUT_H */
