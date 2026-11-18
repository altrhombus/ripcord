/*
 * ripcord-3ds - the connect flow: control plane -> senkusha -> Takion -> stream keys -> A/V and input.
 *
 * Nothing below the control plane can be tested in isolation: source/takion/main.c
 * drove an INIT at a console standalone on 2026-08-12 and got silence, because a console opens its
 * stream UDP ports ONLY inside a live session. Validating Takion, the ECDH key agreement, or anything
 * above them means carrying a real control session all the way there. This program does that, then
 * decodes the video (rc_mvd), plays the audio (rc_audio) and sends the 3DS pad as controller input.
 *
 * THE SEQUENCE, and every step's reason (HalyardStreamingSession.ConnectAsync):
 *   1. arm -> /sess/init -> /sess/ctrl                     (halyard_control_session, hardware-confirmed)
 *   2. the binary control channel, answering HEARTBEAT_REQ  - from here to the end, continuously
 *   3. the sign-in gate: wait 1 s for LOGIN_PROMPT (0x0004). A LOCKED CONSOLE SILENTLY DROPS EVERY
 *      TAKION INIT, so this is a hard gate. This program does not answer a PIN prompt (it has no
 *      on-screen entry); it reports and stops.
 *   4. wait for session-ready (SESSION_ID, 0x0033)
 *   5. senkusha bring-up on UDP 9297 - the console requires this to have happened before it will answer
 *      the stream's SESSION exchange. Keyless: full Takion framing with the GMAC and keystream-offset
 *      fields left zero. Includes the RTT echo probe and both MTU legs.
 *   6. Takion handshake on UDP 9296 (NOT 9297 - that is senkusha's port)
 *   7. SESSION_REQUEST -> SESSION_REPLY -> per-direction stream keys (takion_session_negotiator)
 *   8. run_media: sealing on, STREAM_INFO acked, then the A/V, input and loss-report loop
 *
 * With probe_resolutions set in the pairing record, run_resolution_probe runs this once per candidate
 * resolution instead, and each connect stops after acking STREAM_INFO.
 *
 * THE CONTROL CHANNEL HAS NO THREAD OF ITS OWN, AND THAT IS THE HARD PART. The console resets the
 * session ~15-30 s after heartbeat replies stop. Steps 5-7 involve UDP waits far longer than that, so
 * every blocking wait here either polls the control channel itself or hands takion_channel_connect_ticked
 * a tick callback that does. A blocking UDP call anywhere in this file is a bug that presents as the
 * console hanging up mid-bring-up.
 *
 * See HARDWARE-PROBES.md.
 */
#include "crypto/rc_ecdh.h"
#include "halyard/halyard_v1.h"
#include "../net/rc_soc.h"
#include "session/halyard_control_session.h"
#include "session/halyard_launch_spec.h"
#include "session/halyard_pairing_file.h"
#include "session/halyard_ctrl_message.h"
#include "takion/takion_control_proto.h"
#include "takion/senkusha_echo.h"
#include "takion/takion_data_chunk.h"
#include "takion/takion_reliable_channel.h"
#include "takion/takion_session_negotiator.h"
#include "../media/rc_mvd.h"
#include "../media/rc_audio.h"
#include "input/halyard_input.h"
#include "stream/stream_demux.h"
#include "stream/stream_header.h"
#include "stream/stream_packet_crypto.h"
#include "util/rc_base64.h"
#include "util/rc_log.h"
#include "util/rc_program_dir.h"
#include "../util/rc_profile.h"
#include "util/rc_random.h"

#include <3ds.h>

#include <arpa/inet.h>
#include <errno.h>
#include <fcntl.h>
#include <netinet/in.h>
#include <stdio.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/types.h>
#include <unistd.h>

#define SENKUSHA_PORT 9297u
#define STREAM_PORT   9296u

#define LOGIN_PROMPT_WINDOW_MS   1000u
#define SESSION_READY_TIMEOUT_MS 10000u
#define SENKUSHA_ATTEMPTS        10u
#define SENKUSHA_ATTEMPT_MS      300u
#define SENKUSHA_REPLY_MS        3000u
#define STREAM_ATTEMPTS          20u
#define STREAM_ATTEMPT_MS        300u
#define NEGOTIATE_TIMEOUT_MS     10000u
#define STREAM_INFO_TIMEOUT_MS   15000u
/* CANDIDATE_DWELL_MS is not referenced in this file. */
#define CANDIDATE_DWELL_MS        8000u
#define LOSS_REPORT_INTERVAL_MS   200u
/* 60 Hz. Integer milliseconds: 16 rather than 16.67, which paces slightly fast and so never starves the
 * display of a frame that is ready. */
#define VBLANK_INTERVAL_MS        16u

/* Control DATA/SACK carry the GMAC tag at offset 5 and the keystream offset at 9 (both [V] against captures:
 * 2528 type-0 packets for the offsets, 727/727 for the AAD rule). Loss reports use 7 and 0x0b. */
#define CONTROL_TAG_OFFSET   5
#define CONTROL_KEYSTREAM_OFFSET_AT 9
#define LOSS_REPORT_TAG_OFFSET 7
#define LOSS_REPORT_KEYSTREAM_OFFSET_AT 0x0b

/* The launch spec's declared MTU when senkusha's measurement legs are not run. LinkMetrics.VendorMtu. */
#define DEFAULT_DECLARED_MTU 1454

/* Per-ping echo window. The .NET side uses 80 ms; a LAN round trip is a few ms and anything past this is
 * a lost ping, not a slow one. */
#define SENKUSHA_ECHO_TIMEOUT_MS 80u

/* MTU legs get longer than a ping: the console has to generate a full-size datagram, and the .NET side
 * allows 600 ms per reply for the same reason. */
#define SENKUSHA_MTU_TIMEOUT_MS 600u

/* Both Takion channels are ~50 KB and must not be locals - see takion_reliable_channel.h. */
static takion_reliable_channel g_senkusha_channel;
static takion_reliable_channel g_stream_channel;
static takion_session_negotiator g_negotiator;
static char g_launch_spec[HALYARD_LAUNCH_SPEC_MAX];
static char g_launch_spec_b64[HALYARD_LAUNCH_SPEC_B64_MAX];
static uint8_t g_request[4096];

/* The control session, reachable from the tick callback below. */
static halyard_control_session g_control;
static int g_control_alive;
static u64 g_last_heartbeat_ms;
static long g_heartbeats_answered, g_control_messages_other;
static uint32_t g_last_control_type = 0xffffffffu;

/* 513 KB - file scope is mandatory, not a preference (stream_demux.h). */
static stream_demux g_demux;
static long g_frames;
static long g_keyframes;
static long g_audio_frames;
static long g_loss_events;

/*
 * The decoder. rc_mvd_decode_frame runs on the same thread as the receive loop, so if it costs more than
 * the inter-packet gap it starves the socket and the symptom (packet loss) looks like a network problem
 * rather than a CPU one. The link test (source/linktest) measured that CPU contention on this core costs
 * real UDP throughput.
 */
static rc_mvd g_mvd;
static rc_audio g_audio;
static halyard_input_writer g_input;
static long g_input_state_packets, g_input_history_packets;
static u64 g_last_input_ms;
static u64 g_last_input_history_ms;
static u64 g_last_input_poll_ms;

/* cap48's cadence: state every 15 ms, history on a transition and again every 200 ms. */
#define INPUT_STATE_INTERVAL_MS 15u
#define INPUT_HISTORY_REPEAT_MS 200u

/*
 * INPUT IS POLLED ON A CLOCK, NOT ON THE LOOP. The outer loop iterates at roughly 1 kHz when the socket
 * is quiet, and the RAW circle-pad reading jitters by +/-1 continuously, so an unthrottled "has anything
 * changed" test sent the console about a thousand state packets a second, against a real DualSense's
 * 250 Hz. On hardware that presented as input arriving delayed, repeated, or in the wrong order.
 *
 * 8 ms is 125 Hz: comfortably above the 30-60 fps the picture updates at, and far below the rate at
 * which our own jitter becomes traffic.
 */
#define INPUT_POLL_INTERVAL_MS 8u

/*
 * Circle-pad deadzone, in raw libctru units (full deflection is ~155). The pad rests a few units off
 * centre and wanders; without this, resting drift is indistinguishable from a real nudge and every
 * sample becomes a state packet. Applied before scaling so the wire sees a clean zero at rest.
 */
#define INPUT_STICK_DEADZONE 12

/*
 * THE 3DS PAD, MAPPED POSITIONALLY.
 *
 * The face buttons go by POSITION, not by letter: the 3DS's B is where the PlayStation's Cross is, and
 * its A is where Circle is. Mapping A->Cross by name would put "confirm" on the wrong side of the pad
 * and feel wrong in every menu.
 *
 * What a 3DS cannot offer: L3/R3 (no stick click), a touchpad, and analog triggers - ZL/ZR are digital,
 * so L2/R2 send 0x00 or 0xff rather than a level. The PS button has no key of its own, so it is
 * START+SELECT together, which is also why both are checked before either is reported alone.
 */
static uint32_t map_3ds_buttons(u32 down)
{
    uint32_t out = 0;

    if (down & KEY_B)      out |= HALYARD_PAD_CROSS;      /* bottom face, both pads */
    if (down & KEY_A)      out |= HALYARD_PAD_CIRCLE;     /* right face */
    if (down & KEY_Y)      out |= HALYARD_PAD_SQUARE;     /* left face */
    if (down & KEY_X)      out |= HALYARD_PAD_TRIANGLE;   /* top face */
    if (down & KEY_DUP)    out |= HALYARD_PAD_DPAD_UP;
    if (down & KEY_DDOWN)  out |= HALYARD_PAD_DPAD_DOWN;
    if (down & KEY_DLEFT)  out |= HALYARD_PAD_DPAD_LEFT;
    if (down & KEY_DRIGHT) out |= HALYARD_PAD_DPAD_RIGHT;
    if (down & KEY_L)      out |= HALYARD_PAD_L1;
    if (down & KEY_R)      out |= HALYARD_PAD_R1;
    if (down & KEY_ZL)     out |= HALYARD_PAD_L2;         /* New 3DS only; digital here */
    if (down & KEY_ZR)     out |= HALYARD_PAD_R2;

    /* START+SELECT together is the PS button - the only chord, because there is no key left for it. */
    if ((down & KEY_START) && (down & KEY_SELECT)) {
        out |= HALYARD_PAD_PS;
    } else {
        if (down & KEY_START)  out |= HALYARD_PAD_OPTIONS;
        if (down & KEY_SELECT) out |= HALYARD_PAD_CREATE;
    }
    return out;
}

/*
 * The circle pad reads +/-155ish at full deflection; the C-stick (New 3DS) reads a similar range. Scale
 * to the wire's s16 and clamp, then invert Y: the console expects up NEGATIVE, which cap48 established
 * from the scripted "full up" excursion driving left-Y to -32767. libctru reports up POSITIVE.
 */
static int16_t scale_stick(int v)
{
    long scaled;

    /* Deadzone first: rest must read exactly zero, or drift becomes traffic. */
    if (v > -INPUT_STICK_DEADZONE && v < INPUT_STICK_DEADZONE)
        return 0;
    scaled = (long)v * 32767L / 155L;

    if (scaled > 32767L)
        scaled = 32767L;
    if (scaled < -32767L)
        scaled = -32767L;
    return (int16_t)scaled;
}
static int g_decode_enabled = 1;
static rc_profile g_profile;

/* argv[0], kept so the video dump can be written beside the .3dsx. */
static const char *g_argv0;
/*
 * RAW BITSTREAM DUMP (dumpvideo in the pairing file). Writes the Annex-B elementary stream to the SD card
 * so a PC decoder can say whether the console sends a picture, whether our demuxer assembles it intact,
 * and whether MVD mishandles a good stream:
 *
 *   ffprobe -show_frames video.264      # frame types, resolution, whether it parses at all
 *   ffmpeg -i video.264 frame%03d.png   # look at the pictures
 *
 * Capped at 2 MB (~700 frames at the ~2.8 KB/frame this stream runs at). A diagnostic switch, not
 * something to leave on.
 *
 * BUFFERED IN RAM, WRITTEN AT THE END - because writing during the session damages what it captures.
 * fwrite()ing each frame as it arrives puts SD writes on the receive thread and stalls the drain: a run
 * that did so lost 2168 units, and the captured stream had 332 frame_num discontinuities across 941
 * pictures. ffmpeg conceals such gaps silently, so the capture looked fine on a PC.
 */
static uint8_t g_video_dump_buf[2u * 1024u * 1024u];
static unsigned long g_video_dump_bytes;
static int g_video_dump_armed;
#define VIDEO_DUMP_LIMIT (sizeof(g_video_dump_buf))

static int g_scale_thread_enabled;
static unsigned long g_video_bytes;
static long g_total_received, g_total_lost;
static unsigned g_core_mask;
static int g_measured_rtt_ms;   /* 0 until the echo probe produces a majority of samples */
static int g_actual_width, g_actual_height;  /* what STREAM_INFO reported, not what we asked for */
static int g_measured_mtu;      /* 0 until both MTU directions confirm; the declared default otherwise */
static int g_stream_bitrate_kbps;
static int g_stream_width = 640;
static int g_stream_height = 360;
/* Set by the video callback when a picture decodes and cleared by maybe_present after a swap. Nothing
 * reads it: maybe_present asks rc_mvd_scale_begin whether there is a picture to show. */
static int g_picture_pending;
/* Set from the pairing record before the media window; run_media has no record of its own. */
static int g_video_rgb565;
static u64 g_last_present_ms;
static long g_presents;

static void on_video_frame(void *userdata, const uint8_t *data, size_t length, int is_keyframe)
{
    (void)userdata;
    g_frames++;
    if (is_keyframe)
        g_keyframes++;
    /* The first frame is the one worth describing: if the demuxer assembled it and it starts with an
     * Annex-B start code, then framing, reassembly, the crypto seam and the parameter-set prepend all
     * worked on real traffic. */
    if (g_frames == 1) {
        rc_log("\x1b[32mFIRST VIDEO FRAME\x1b[0m %u bytes, %s, starts %02x%02x%02x%02x\n",
            (unsigned)length, is_keyframe ? "KEYFRAME" : "inter",
            length > 3 ? data[0] : 0, length > 3 ? data[1] : 0,
            length > 3 ? data[2] : 0, length > 3 ? data[3] : 0);
    }

    if (g_video_dump_armed && g_video_dump_bytes + length <= VIDEO_DUMP_LIMIT) {
        memcpy(g_video_dump_buf + g_video_dump_bytes, data, length);
        g_video_dump_bytes += (unsigned long)length;
    }

    if (g_decode_enabled && g_mvd.ready) {
        long before = g_mvd.frames_rendered;

        if (rc_mvd_decode_frame(&g_mvd, data, length, is_keyframe))
            g_picture_pending = 1;
        if (before == 0 && g_mvd.frames_rendered == 1) {
            rc_log("\x1b[32mFIRST DECODED PICTURE\x1b[0m - MVD rendered to the framebuffer\n");
        }
    }
}

static void on_audio_frame(void *userdata, const uint8_t *data, size_t length)
{
    (void)userdata;
    g_audio_frames++;
    /* `data` is unit 0 only - stream_demux strips the redundant copies. See rc_audio.h for what happens
     * if the whole payload is handed to Opus instead. */
    rc_audio_submit(&g_audio, data, length);
}

static u64 g_last_idr_request_ms;
static long g_idr_requests;

/*
 * Ask for a fresh keyframe when a frame is lost. WITHOUT THIS THE PICTURE NEVER RECOVERS: H.264 only
 * resynchronises at an IDR, and the console does not send one unprompted. Hardware runs without it showed
 * one keyframe per session and then 15-181 loss events.
 *
 * Rate-limited because asking for a keyframe per loss event is wasteful on a link with hundreds of them.
 *
 * IDR_REQUEST is the blunt instrument; CORRUPT_FRAME (type 5) carries the actual damaged frame range and
 * lets the console decide, which is what the .NET reference sends. This file sends IDR_REQUEST only.
 */
#define IDR_REQUEST_MIN_INTERVAL_MS 500u


static void on_video_loss(void *userdata, int first_frame_index, int last_frame_index)
{
    u64 now_ms = osGetTime();

    (void)userdata;
    g_loss_events++;
    if (g_loss_events <= 3)
        rc_log("  video loss: frames %d..%d\n", first_frame_index, last_frame_index);
    /* With skip-until-keyframe on, this stops feeding the decoder until the next keyframe. Otherwise it
     * does nothing. */
    rc_mvd_signal_loss(&g_mvd);

    if (now_ms - g_last_idr_request_ms >= (u64)IDR_REQUEST_MIN_INTERVAL_MS) {
        uint8_t request[8];
        size_t request_len = takion_control_build_bare(TAKION_CONTROL_IDR_REQUEST,
                                                       request, sizeof(request));
        if (request_len > 0
            && takion_channel_send(&g_stream_channel, TAKION_CHANNEL_SESSION, request, request_len)) {
            g_idr_requests++;
        }
        g_last_idr_request_ms = now_ms;
    }
}

/*
 * The stream-plane sender state. One advancing keystream offset is shared by every outgoing sealed packet -
 * control DATA, SACKs, loss reports and input alike - and it advances by the packet's 16-byte-aligned
 * length, not by 1. Reusing a position across two packets reuses a GMAC nonce, which is a real forgery
 * problem and produces no visible symptom.
 */
static stream_packet_crypto g_send_crypto;
static stream_packet_crypto g_recv_crypto;
static uint64_t g_send_keystream_offset;
static int g_sealing;

static uint64_t reserve_keystream_offset(size_t packet_length)
{
    uint64_t reserved = g_send_keystream_offset;
    size_t remainder = packet_length % 16u;
    size_t aligned = packet_length + ((remainder == 0u) ? 0u : (16u - remainder));

    g_send_keystream_offset += (uint64_t)aligned;
    return reserved;
}

/* Seals one outgoing control DATA or SACK: keystream offset at offset 9, 4-byte GMAC tag at offset 5, with
 * both the tag and the keystream-offset field zeroed in the AAD (the control and loss-report rule; A/V zeroes the
 * tag only). Handed to takion_channel_enable_sealing. */
static void seal_control_packet(void *ctx, uint8_t *packet, size_t length)
{
    uint64_t keystream_offset;

    (void)ctx;
    if (!g_sealing || length < (size_t)(CONTROL_KEYSTREAM_OFFSET_AT + 4))
        return;

    keystream_offset = reserve_keystream_offset(length);
    packet[CONTROL_KEYSTREAM_OFFSET_AT + 0] = (uint8_t)(keystream_offset >> 24);
    packet[CONTROL_KEYSTREAM_OFFSET_AT + 1] = (uint8_t)(keystream_offset >> 16);
    packet[CONTROL_KEYSTREAM_OFFSET_AT + 2] = (uint8_t)(keystream_offset >> 8);
    packet[CONTROL_KEYSTREAM_OFFSET_AT + 3] = (uint8_t)keystream_offset;
    (void)stream_packet_crypto_seal(&g_send_crypto, keystream_offset, packet, length,
                                    CONTROL_TAG_OFFSET, 1 /* zero_keystream_offset */);
}

/* How many control messages service_control drains per call. Bounded so a flood cannot starve the A/V
 * drain - the same reasoning that caps the packet drain at 64. */
#define CONTROL_DRAIN_LIMIT 8

/* The console starts every stream HEARTBEAT exchange and the client answers it (cap48). */
static void answer_stream_heartbeat(const uint8_t *message, size_t message_length)
{
    uint32_t type = 0xffffffffu;
    uint8_t beat[8];
    size_t beat_len;

    if (!takion_control_peek_type(message, message_length, &type) || type != TAKION_CONTROL_HEARTBEAT)
        return;
    beat_len = takion_control_build_bare(TAKION_CONTROL_HEARTBEAT, beat, sizeof(beat));
    if (beat_len > 0)
        (void)takion_channel_send(&g_stream_channel, TAKION_CHANNEL_SESSION, beat, beat_len);
}

/*
 * The heartbeat pump. Called from the main loop AND from inside takion_channel_connect_ticked's wait
 * loops, so that a 6-second UDP handshake does not cost us the control connection.
 *
 * Drains the control channel up to CONTROL_DRAIN_LIMIT rather than one message per call. One per call
 * falls behind under a real game: such a session, healthy in every other respect (5% loss, 1.75 Mbps,
 * 25 fps), ended with "control channel closed by console" at 70 seconds.
 */
static void service_control(void *ctx)
{
    halyard_control_event event;
    int drained;

    (void)ctx;
    if (!g_control_alive)
        return;

    for (drained = 0; drained < CONTROL_DRAIN_LIMIT; drained++) {
        if (!halyard_control_session_service(&g_control, &event)) {
            u64 now = osGetTime();

            g_control_alive = 0;
            /*
             * SAY WHAT THE CHANNEL WAS DOING WHEN IT DIED. "closed by console" on its own is consistent
             * with two different faults: we stopped answering HEARTBEAT_REQ (a starved loop), or the
             * console stopped asking and hung up for a reason of its own. The gap since the last
             * heartbeat separates them: a few seconds means the console was still talking to us right
             * up to the close; tens of seconds means we went deaf first.
             */
            rc_log("\x1b[31mFAIL\x1b[0m control channel %s\n",
                event.kind == HALYARD_CONTROL_EVENT_CLOSED ? "closed by console" : "errored");
            /*
             * Hardware shows a ~37-38 s gap in both observed failures, against a heartbeat that arrives
             * every ~5 s. The failure count is printed because the reply is fire-and-forget through a
             * non-blocking socket that can time out.
             */
            rc_log("       last HEARTBEAT_REQ %lu ms ago; %ld answered, %ld other message(s) seen\n",
                (unsigned long)(g_last_heartbeat_ms ? now - g_last_heartbeat_ms : 0),
                g_heartbeats_answered, g_control_messages_other);
            rc_log("       %ld heartbeat repl%s FAILED to send\n",
                g_control.heartbeat_send_failures,
                g_control.heartbeat_send_failures == 1 ? "y" : "ies");
            /*
             * THE TAKION SIDE. The console closes on a fixed ~38 s timer, consistent to within a second
             * across four runs. If it has already decided the SESSION is dead, the likeliest place for
             * that is the reliable channel: control DATA that we never acknowledge, or unacked chunks of
             * ours piling up. A stalled reliable channel is invisible in every other number printed,
             * because the A/V path is unreliable and keeps flowing regardless.
             */
            {
                int i, unacked = 0;

                for (i = 0; i < TAKION_MAX_UNACKED; i++) {
                    if (g_stream_channel.unacked[i].length > 0)
                        unacked++;
                }
                rc_log("       takion: %d/%d unacked, next_send_tsn %u, expected_recv_tsn %u, "
                       "last_acked %u\n",
                    unacked, TAKION_MAX_UNACKED,
                    (unsigned)g_stream_channel.next_send_tsn,
                    (unsigned)g_stream_channel.expected_recv_tsn,
                    (unsigned)g_stream_channel.last_acked_tsn);
            }
            if (g_last_control_type != 0xffffffffu) {
                rc_log("       last control message type was %u\n",
                    (unsigned)g_last_control_type);
            rc_log("       tcp: %ld recv call(s), %ld byte(s), %ld would-block, %u buffered\n",
                g_control.recv_calls, g_control.recv_bytes, g_control.recv_would_block,
                (unsigned)g_control.buffered);
            /*
             * WHAT IS STUCK IN THE BUFFER. The control header is 8 bytes with a big-endian payload
             * length at offset 0, so "56 buffered" was seven complete zero-payload frames - the ~7
             * heartbeat requests the console sent during its 37 s of apparent silence, received and
             * never parsed. For the parser to stall on a complete frame, the length it reads must be
             * implausible - i.e. the buffer is desynced and we are reading a length out of the middle
             * of a message. These bytes say which.
             */
            if (g_control.buffered > 0) {
                unsigned i, show = g_control.buffered < 24u ? (unsigned)g_control.buffered : 24u;

                rc_log("       stuck bytes:");
                for (i = 0; i < show; i++)
                    rc_log(" %02x", g_control.buffer[i]);
                rc_log("\n");
                if (g_control.buffered >= 8u) {
                    unsigned long claimed = ((unsigned long)g_control.buffer[0] << 24)
                                          | ((unsigned long)g_control.buffer[1] << 16)
                                          | ((unsigned long)g_control.buffer[2] << 8)
                                          | (unsigned long)g_control.buffer[3];
                    rc_log("       first frame claims a %lu-byte payload, type %u - %s\n",
                        claimed,
                        (unsigned)(((unsigned)g_control.buffer[4] << 8) | g_control.buffer[5]),
                        claimed + 8u > g_control.buffered ? "INCOMPLETE, so we wait forever" : "parseable");
                }
            }
            }
            return;
        }
        if (event.kind == HALYARD_CONTROL_EVENT_SESSION_READY)
            g_control.session_ready = 1;
        if (event.kind == HALYARD_CONTROL_EVENT_MESSAGE) {
            g_last_control_type = event.type;
            if (event.type == HALYARD_CTRL_TYPE_HEARTBEAT_REQ) {
                g_last_heartbeat_ms = osGetTime();
                g_heartbeats_answered++;
            } else {
                g_control_messages_other++;
            }
        }
        if (event.kind == HALYARD_CONTROL_EVENT_NONE)
            return;   /* nothing left to read */
    }
}


/* Waits for session-ready while keeping the control channel serviced. Returns 1 if it arrived. */
static int wait_for_session_ready(unsigned timeout_ms)
{
    u64 start_ms = osGetTime();

    while (osGetTime() - start_ms < (u64)timeout_ms) {
        if (!g_control_alive)
            return 0;
        if (g_control.session_ready)
            return 1;
        service_control(NULL);
        svcSleepThread(10000000); /* 10 ms */
    }
    return g_control.session_ready;
}

/*
 * The sign-in gate. Returns 1 if the console is unlocked (no prompt inside the window), 0 if it wants a
 * PIN - which this build cannot supply, and which means every later Takion INIT would be dropped in
 * silence. Reporting that clearly is worth more than proceeding and timing out mysteriously.
 */
static int check_sign_in_gate(void)
{
    u64 start_ms = osGetTime();

    while (osGetTime() - start_ms < (u64)LOGIN_PROMPT_WINDOW_MS) {
        halyard_control_event event;

        if (!g_control_alive)
            return 0;
        if (!halyard_control_session_service(&g_control, &event)) {
            g_control_alive = 0;
            return 0;
        }
        if (event.kind == HALYARD_CONTROL_EVENT_SESSION_READY)
            g_control.session_ready = 1;
        if (event.kind == HALYARD_CONTROL_EVENT_MESSAGE
            && event.type == HALYARD_CTRL_TYPE_LOGIN_PROMPT) {
            rc_log("\x1b[31mFAIL\x1b[0m console requires a sign-in PIN; this build cannot answer one.\n");
            rc_log("        Unlock the console (sign in on it directly), then re-run.\n");
            return 0;
        }
        svcSleepThread(10000000); /* 10 ms */
    }
    return 1;
}

/* Opens a non-blocking UDP socket pointed at `port` on the console. Returns -1 on failure. */
static int open_udp(const char *host, unsigned port, struct sockaddr_in *out_peer)
{
    int sock = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);

    if (sock < 0)
        return -1;
    fcntl(sock, F_SETFL, O_NONBLOCK);

    /*
     * A RECEIVE CUSHION BIG ENOUGH FOR A KEYFRAME BURST, as source/linktest/main.c also sets. A 960x540
     * keyframe is ~24 KB, which arrives as roughly twenty back-to-back datagrams - if the socket's
     * default buffer is smaller than the burst, the tail is dropped by the stack before any amount of
     * draining can reach it. Consistent with the profile: moving the scale to core 2 took core 0 down
     * to ~47% - the same load that lost 1.8% at 640x360 - and the loss stayed at 16%.
     */
    {
        int rcvbuf = 262144;
        setsockopt(sock, SOL_SOCKET, SO_RCVBUF, &rcvbuf, sizeof(rcvbuf));
    }

    memset(out_peer, 0, sizeof(*out_peer));
    out_peer->sin_family = AF_INET;
    out_peer->sin_port = htons((unsigned short)port);
    inet_aton(host, &out_peer->sin_addr);
    return sock;
}

/*
 * Waits for a reliable message of `want_type` on `ch`, servicing the control channel meanwhile.
 * Returns 1 if it arrived.
 */
static int await_control_message(takion_reliable_channel *ch, uint32_t want_type, unsigned timeout_ms)
{
    u64 start_ms = osGetTime();

    while (osGetTime() - start_ms < (u64)timeout_ms) {
        unsigned channel_id;
        const uint8_t *message;
        size_t message_length;
        int result;

        service_control(NULL);
        if (!g_control_alive)
            return 0;

        result = takion_channel_poll(ch, &channel_id, &message, &message_length);
        if (result == 1) {
            uint32_t type = 0xffffffffu;
            if (takion_control_peek_type(message, message_length, &type)) {
                if (type == want_type)
                    return 1;
                rc_log("  (ignoring control type %u on channel 0x%04x)\n", (unsigned)type, channel_id);
            }
        } else if (result == -1) {
            return 0;
        }
        svcSleepThread(10000000); /* 10 ms */
    }
    return 0;
}

/*
 * The senkusha bring-up: handshake, PROTOCOL_VERSION_REQUEST, a KEYLESS SESSION_REQUEST (no ECDH fields,
 * clientVersion 9, empty strings), then the RTT echo probe and both MTU legs (spec 6.4's MTU-in and
 * MTU-out), in the capture's order. The echo leg is run because the declared RTT is an input the console
 * uses; left at 0, every session negotiates against a figure we never measured.
 *
 * Best-effort by design, matching the .NET reference: a failure is logged and the flow continues, since
 * the console's requirement is that the bring-up *happened*, and our reading of what it must contain is
 * itself provisional.
 */
static int run_senkusha(const char *host)
{
    struct sockaddr_in peer;
    int sock = open_udp(host, SENKUSHA_PORT, &peer);
    uint8_t payload[256];
    size_t payload_len;
    int ok = 0;

    if (sock < 0) {
        rc_log("\x1b[33mNOTE\x1b[0m senkusha: socket() failed: %d\n", errno);
        return 0;
    }

    rc_log("senkusha: INIT to %s:%u\n", host, SENKUSHA_PORT);
    if (!takion_channel_connect_ticked(&g_senkusha_channel, sock, peer,
                                       SENKUSHA_ATTEMPTS, SENKUSHA_ATTEMPT_MS,
                                       service_control, NULL)) {
        rc_log("\x1b[33mNOTE\x1b[0m senkusha: handshake did not complete\n");
        close(sock);
        return 0;
    }
    rc_log("senkusha: established (local 0x%08x peer 0x%08x)\n",
        (unsigned)g_senkusha_channel.local_tag, (unsigned)g_senkusha_channel.peer_tag);

    /*
     * PROTOCOL_VERSION_REQUEST{supportedVersions=[9]} on channel 0x0015, as a literal. Field 31
     * length-delimited is (31 << 3) | 2 = 250, which is >= 0x80 and therefore a TWO-byte varint tag,
     * 0xFA 0x01 - not a single 0xFA. The literal is checked against takion_control_proto.c's varint
     * writer by libripcord/tests/control_proto_test.c, which carries these exact seven bytes twice and
     * the same tag arithmetic beside them.
     */
    {
        static const uint8_t kVersionRequest[] = {
            0x08, 0x1F,                   /* field 1 (type) varint = 31, PROTOCOL_VERSION_REQUEST */
            0xFA, 0x01, 0x02, 0x08, 0x09, /* field 31, length 2: { field 1 varint = 9 } */
        };
        memcpy(payload, kVersionRequest, sizeof(kVersionRequest));
        payload_len = sizeof(kVersionRequest);
    }
    if (takion_channel_send(&g_senkusha_channel, TAKION_CHANNEL_PROTOCOL_VERSION, payload, payload_len)) {
        if (await_control_message(&g_senkusha_channel, 32u, SENKUSHA_REPLY_MS))
            rc_log("senkusha: PROTOCOL_VERSION_ACK\n");
        else
            rc_log("\x1b[33mNOTE\x1b[0m senkusha: no PROTOCOL_VERSION_ACK\n");
    }

    /* The keyless SESSION_REQUEST: clientVersion 9, empty sessionKey/launchSpec, 4 zero encryptedKey
     * bytes, and NO ecdh fields at all. This is the exchange the console appears to want to have seen. */
    {
        takion_session_request request;
        static const uint8_t kZeroKey[4] = { 0, 0, 0, 0 };

        memset(&request, 0, sizeof(request));
        request.client_version = 9;
        request.session_key = "";
        request.session_key_length = 0;
        request.launch_spec_json = "";
        request.launch_spec_json_length = 0;
        request.encrypted_key = kZeroKey;
        request.encrypted_key_length = sizeof(kZeroKey);

        payload_len = takion_control_build_session_request(&request, payload, sizeof(payload));
        if (payload_len > 0
            && takion_channel_send(&g_senkusha_channel, TAKION_CHANNEL_SESSION, payload, payload_len)) {
            if (await_control_message(&g_senkusha_channel, TAKION_CONTROL_SESSION_REPLY, SENKUSHA_REPLY_MS)) {
                rc_log("senkusha: SESSION_REPLY - bring-up complete\n");
                ok = 1;
            } else {
                rc_log("\x1b[33mNOTE\x1b[0m senkusha: no SESSION_REPLY\n");
            }
        }
    }

    /*
     * The RTT echo probe, ported from HalyardSenkusha.RunEchoProbeAsync. Ordered exactly as the capture
     * shows and spec 6.4 records: after SESSION_REPLY, before DISCONNECT. Arm echo mode, send ten
     * 548-byte pings, time each against its byte-identical reply, disarm.
     *
     * Needs five of ten, not all ten: cap47 shows a real loss and retry - the first ping went unanswered
     * for ~200 ms, was resent, and eleven were sent for ten echoes. Takes the MINIMUM of what it gets:
     * a sample can only be inflated by delay, never deflated, so the smallest is the closest to the
     * true path time.
     *
     * Best-effort throughout. A failed probe leaves g_measured_rtt_ms at 0, and the launch spec then
     * declares rtt 0.
     */
    if (ok) {
        uint8_t ping[SENKUSHA_ECHO_PAYLOAD];
        unsigned samples = 0;
        u64 best_ms = 0;
        uint8_t seq;

        payload_len = takion_control_build_echo_command(1, payload, sizeof(payload));
        if (payload_len > 0)
            (void)takion_channel_send(&g_senkusha_channel, TAKION_CHANNEL_BANDWIDTH,
                                      payload, payload_len);

        for (seq = 0; seq < (uint8_t)SENKUSHA_PING_COUNT; seq++) {
            u64 sent_ms = osGetTime();
            size_t n = senkusha_echo_build(seq, (uint64_t)sent_ms * 1000ull,
                                           SENKUSHA_ECHO_PAYLOAD, 0x00u, ping, sizeof(ping));
            u64 deadline;

            if (n == 0)
                break;
            if (sendto(sock, ping, n, 0, (struct sockaddr *)&peer, sizeof(peer)) < 0)
                continue;

            /* Wait for THIS ping's echo. A late echo of an earlier one is dropped rather than credited
             * here, which would report a round trip shorter than it was. */
            deadline = sent_ms + SENKUSHA_ECHO_TIMEOUT_MS;
            while (osGetTime() < deadline) {
                uint8_t rx[SENKUSHA_ECHO_PAYLOAD];
                uint8_t got;
                ssize_t r = recvfrom(sock, rx, sizeof(rx), 0, NULL, NULL);

                if (r > 0 && senkusha_echo_is_echo(rx, (size_t)r, &got) && got == seq) {
                    u64 rtt = osGetTime() - sent_ms;
                    if (samples == 0 || rtt < best_ms)
                        best_ms = rtt;
                    samples++;
                    break;
                }
                svcSleepThread(1000000); /* 1 ms */
            }
        }

        payload_len = takion_control_build_echo_command(0, payload, sizeof(payload));
        if (payload_len > 0)
            (void)takion_channel_send(&g_senkusha_channel, TAKION_CHANNEL_BANDWIDTH,
                                      payload, payload_len);

        if (samples * 2u >= SENKUSHA_PING_COUNT) {
            g_measured_rtt_ms = (int)best_ms;
            rc_log("senkusha: RTT \x1b[36m%d ms\x1b[0m from %u/%u echoes\n",
                g_measured_rtt_ms, samples, (unsigned)SENKUSHA_PING_COUNT);
        } else {
            rc_log("\x1b[33mNOTE\x1b[0m senkusha: only %u/%u echoes - keeping the declared RTT\n",
                samples, (unsigned)SENKUSHA_PING_COUNT);
        }
    }

    /*
     * The MTU legs - spec 6.4's MTU-in (downstream) then MTU-out (upstream), in the capture's order.
     *
     * DOWNSTREAM: ask the console for one datagram of the candidate size. ITS ARRIVAL IS THE ENTIRE
     * RESULT, so the contents are never inspected. The console's reply reports what it SENT, which is a
     * different question.
     *
     * UPSTREAM: CLIENT_MTU_COMMAND{state=true} puts the console into echo mode for one large packet; we
     * send it in the echo format at that size, padded with 0x47 rather than zeros because a zero-filled
     * payload is compressible and a link doing compression would let this pass at a size the path cannot
     * really carry.
     *
     * THE CLOSE IS UNCONDITIONAL. Past the open the console IS in client-MTU mode and has no other way
     * of being cleared, so state=false is sent on every exit path including failure. The .NET side
     * records the same rule.
     *
     * Only the value we actually intend to declare is tested. The vendor probes a smaller size upstream
     * than downstream, but it has reason to know its own uplink and we do not, so verifying the figure
     * that will go in the launch spec is more useful than reproducing theirs.
     */
    if (ok) {
        int downstream = 0, upstream = 0;
        size_t probe_len = (size_t)DEFAULT_DECLARED_MTU - SENKUSHA_IP_UDP_OVERHEAD;
        u64 deadline;

        payload_len = takion_control_build_mtu_command(1u, (uint32_t)DEFAULT_DECLARED_MTU, 1u,
                                                       payload, sizeof(payload));
        if (payload_len > 0
            && takion_channel_send(&g_senkusha_channel, TAKION_CHANNEL_BANDWIDTH,
                                   payload, payload_len)) {
            deadline = osGetTime() + SENKUSHA_MTU_TIMEOUT_MS;
            while (osGetTime() < deadline && !downstream) {
                uint8_t rx[1600];
                ssize_t r = recvfrom(sock, rx, sizeof(rx), 0, NULL, NULL);

                if (r > 0 && (rx[0] & 0x0Fu) == 0x02u)
                    downstream = 1;          /* base type 2: the console's MTU datagram, arrived intact */
                else if (r <= 0)
                    svcSleepThread(1000000); /* 1 ms */
            }
        }

        if (downstream) {
            payload_len = takion_control_build_client_mtu_command(1u, (uint32_t)DEFAULT_DECLARED_MTU, 1,
                                                                  payload, sizeof(payload));
            if (payload_len > 0
                && takion_channel_send(&g_senkusha_channel, TAKION_CHANNEL_BANDWIDTH,
                                       payload, payload_len)) {
                static uint8_t probe[1600];  /* static: 1.6 KB is too much for a 32 KB main stack */
                size_t n = senkusha_echo_build(0u, (uint64_t)osGetTime() * 1000ull, probe_len,
                                               0x47u, probe, sizeof(probe));

                if (n > 0 && sendto(sock, probe, n, 0,
                                    (struct sockaddr *)&peer, sizeof(peer)) >= 0) {
                    deadline = osGetTime() + SENKUSHA_MTU_TIMEOUT_MS;
                    while (osGetTime() < deadline && !upstream) {
                        uint8_t rx[1600];
                        uint8_t got;
                        ssize_t r = recvfrom(sock, rx, sizeof(rx), 0, NULL, NULL);

                        if (r > 0 && senkusha_echo_is_echo(rx, (size_t)r, &got) && got == 0u)
                            upstream = 1;
                        else if (r <= 0)
                            svcSleepThread(1000000);
                    }
                }
            }

            /* Unconditional - see above. */
            payload_len = takion_control_build_client_mtu_command(2u, (uint32_t)DEFAULT_DECLARED_MTU, 0,
                                                                  payload, sizeof(payload));
            if (payload_len > 0)
                (void)takion_channel_send(&g_senkusha_channel, TAKION_CHANNEL_BANDWIDTH,
                                          payload, payload_len);
        }

        if (downstream && upstream) {
            g_measured_mtu = DEFAULT_DECLARED_MTU;
            rc_log("senkusha: MTU \x1b[36m%d\x1b[0m confirmed both directions\n", g_measured_mtu);
        } else {
            rc_log("\x1b[33mNOTE\x1b[0m senkusha: MTU %d unconfirmed (down %s, up %s) - declaring it "
                   "anyway, as before\n", DEFAULT_DECLARED_MTU,
                   downstream ? "ok" : "no", upstream ? "ok" : "no");
        }
    }

    /* DISCONNECT, best-effort: ControlMessage{type=8, disconnectPayload{reason=...}}. Leaving the
     * association open would have the console still tracking it while we open the stream one. */
    {
        static const uint8_t kDisconnect[] = {
            0x08, 0x08,             /* field 1 (type) varint = 8, DISCONNECT */
            0x52, 0x0D,             /* field 10 (disconnectPayload), length 13 */
            0x0A, 0x0B,             /*   field 1 (reason), length 11 */
            'r','i','p','c','o','r','d','-','3','d','s',
        };
        (void)takion_channel_send(&g_senkusha_channel, TAKION_CHANNEL_SESSION,
                                  kDisconnect, sizeof(kDisconnect));
    }

    close(sock);
    return ok;
}

static int run_media(int sock); /* defined below; called once the keys exist */

/*
 * RESOLUTION PROBE STATE. The console refuses 400x240 outright (DISCONNECT within a second of sealing)
 * and accepts 640x360. MVD will not scale, so whether the console can be talked into sending something
 * the 3DS screen can display directly decides whether scaling on this side is mandatory.
 *
 * 400x240 is 25x16 by 15x16, i.e. perfectly macroblock-aligned, and it was still refused. So "must be a
 * multiple of 16" is NOT the constraint, and the candidates deliberately mix aligned and unaligned sizes.
 *
 * `g_probe_width/height` override the launch spec when probing; `g_probe_reported_*` capture what
 * STREAM_INFO said the console actually chose - a console may also silently clamp rather than refuse,
 * and that is a different finding from either accepting or rejecting.
 */
static int g_probing;
static int g_probe_width, g_probe_height;
static int g_probe_reported_width, g_probe_reported_height;
static int g_probe_got_stream_info;

/* The stream half: Takion on 9296, SESSION_REQUEST/REPLY and the key derivation, then run_media. */
static int run_stream(const halyard_pairing_record *rec)
{
    struct sockaddr_in peer;
    int sock;
    uint8_t handshake_key[16];
    halyard_launch_spec_params params;
    size_t spec_len, b64_len, request_len;
    int derived = 0;

    sock = open_udp(rec->host, STREAM_PORT, &peer);
    if (sock < 0) {
        rc_log("\x1b[31mFAIL\x1b[0m stream: socket() failed: %d\n", errno);
        return 0;
    }

    rc_log("stream: INIT to %s:%u\n", rec->host, STREAM_PORT);
    if (!takion_channel_connect_ticked(&g_stream_channel, sock, peer,
                                       STREAM_ATTEMPTS, STREAM_ATTEMPT_MS,
                                       service_control, NULL)) {
        rc_log("\x1b[31mFAIL\x1b[0m stream: Takion handshake did not complete\n");
        close(sock);
        return 0;
    }
    rc_log("stream: Takion ESTABLISHED (local 0x%08x peer 0x%08x)\n",
        (unsigned)g_stream_channel.local_tag, (unsigned)g_stream_channel.peer_tag);

    /* handshakeKey: 16 real random bytes. A predictable one here would let anything on the LAN that can
     * inject a DATA chunk substitute its own ECDH key, and the session would still come up. */
    if (!rc_random_bytes(handshake_key, sizeof(handshake_key))) {
        rc_log("\x1b[31mFAIL\x1b[0m no entropy source; refusing to negotiate with a predictable key\n");
        close(sock);
        return 0;
    }

    memset(&params, 0, sizeof(params));
    /*
     * THE PAIRING RECORD IS COPIED OUT BEFORE ANYTHING READS IT. Copied after the params assignment, the
     * launch spec is built from g_stream_width's static initialiser while the throughput report sees the
     * record's value: 640x360 requested, bits/pixel reported against 960x540.
     */
    g_video_rgb565 = rec->video_rgb565;
    g_video_dump_armed = rec->dump_video;
    g_scale_thread_enabled = rec->scale_thread;
    g_stream_bitrate_kbps = rec->stream_bitrate_kbps;
    g_stream_width = rec->stream_width;
    g_stream_height = rec->stream_height;
    rc_mvd_set_skip_until_keyframe(rec->skip_until_keyframe);
    rc_mvd_set_widescreen(rec->widescreen);
    rc_mvd_set_smoothing(rec->smoothing);
    if (g_video_dump_armed)
        rc_log("video dump: buffering up to %u KB in RAM, written to video.264 on exit\n",
            (unsigned)(VIDEO_DUMP_LIMIT / 1024u));

    /*
     * The pairing record's stream size, or the probe candidate. NOT the screen's 400x240: asking for that
     * produced two DISCONNECT messages within a second of sealing and a closed control channel, twice,
     * reproducibly. So the mismatch between the stream and a 400x240 screen is resolved on this side -
     * see rc_mvd.c.
     */
    params.width = g_probing ? g_probe_width : g_stream_width;
    params.height = g_probing ? g_probe_height : g_stream_height;
    params.fps = (rec->fps == 60) ? 60 : 30;
    /* What we ask the console to actually SEND - not RP-StartBitrate. Taken from the pairing record
     * (streambitrate). The link test measured this hardware's link at 2.16% loss at 2 Mbps and much
     * worse above ~5, while the vendor default of 10000 asks for five times what the link was shown to
     * carry. At 10000, hardware runs lost a third of all units, which kept the decoder permanently
     * waiting for a keyframe. */
    params.bitrate_kbps = rec->stream_bitrate_kbps;
    /* DEFAULT_DECLARED_MTU either way: g_measured_mtu is set only when senkusha's MTU legs confirmed
     * that same value in both directions. */
    params.mtu = g_measured_mtu > 0 ? g_measured_mtu : DEFAULT_DECLARED_MTU;
    /* Measured by senkusha's echo probe when it got at least half of its echoes; 0 otherwise. */
    params.rtt_ms = g_measured_rtt_ms;
    params.is_hevc = 0;   /* MVD decodes H.264 only; HEVC must never be requested from this hardware */
    params.is_hdr = 0;

    /*
     * SAY WHAT WE ASKED FOR, not only what STREAM_INFO came back with: otherwise a request the console
     * declined is indistinguishable from a request never made.
     */
    rc_log("launch spec: requesting %dx%d @ %d fps, bwKbpsSent=%d, rtt=%d ms, mtu=%d\n",
        params.width, params.height, params.fps, params.bitrate_kbps, params.rtt_ms, params.mtu);

    spec_len = halyard_launch_spec_build(&params, handshake_key, g_launch_spec, sizeof(g_launch_spec));
    if (spec_len == 0) {
        rc_log("\x1b[31mFAIL\x1b[0m could not build the launch spec\n");
        close(sock);
        return 0;
    }

    /* AES-128-OFB under out1 at counter 0 - see halyard_launch_spec.h on why that counter is a suspect
     * if the console goes quiet from here. In place: OFB is symmetric and the plaintext is not needed. */
    halyard_control_streaminfo_crypt(&g_control.ctrl, 0,
        (const uint8_t *)g_launch_spec, (uint8_t *)g_launch_spec, spec_len);

    b64_len = rc_base64_encode((const uint8_t *)g_launch_spec, spec_len,
                               g_launch_spec_b64, sizeof(g_launch_spec_b64));
    if (b64_len == 0) {
        rc_log("\x1b[31mFAIL\x1b[0m launch spec did not base64-encode\n");
        close(sock);
        return 0;
    }

    request_len = takion_session_negotiator_begin(&g_negotiator, TAKION_CLIENT_VERSION, handshake_key,
        g_launch_spec_b64, b64_len, rc_random_rng_callback, NULL, g_request, sizeof(g_request));
    memset(handshake_key, 0, sizeof(handshake_key));
    if (request_len == 0) {
        rc_log("\x1b[31mFAIL\x1b[0m could not build SESSION_REQUEST (ECDH backend missing?)\n");
        close(sock);
        return 0;
    }
    rc_log("stream: SESSION_REQUEST %u bytes (curve %s), fragmenting\n",
        (unsigned)request_len, g_negotiator.curve == RC_ECDH_CURVE_P521 ? "P-521" : "P-256");

    if (!takion_channel_send(&g_stream_channel, TAKION_CHANNEL_SESSION, g_request, request_len)) {
        rc_log("\x1b[31mFAIL\x1b[0m could not send SESSION_REQUEST\n");
        close(sock);
        return 0;
    }

    /* Wait for SESSION_REPLY, then verify + derive. */
    {
        u64 start_ms = osGetTime();
        int messages_seen = 0;
        int rejected = 0;

        while (osGetTime() - start_ms < (u64)NEGOTIATE_TIMEOUT_MS && !derived) {
            unsigned channel_id;
            const uint8_t *message;
            size_t message_length;
            int result;

            service_control(NULL);
            if (!g_control_alive)
                break;

            result = takion_channel_poll(&g_stream_channel, &channel_id, &message, &message_length);
            if (result == 1) {
                uint32_t type = 0xffffffffu;

                messages_seen++;
                answer_stream_heartbeat(message, message_length);
                if (takion_control_peek_type(message, message_length, &type)
                    && type == TAKION_CONTROL_SESSION_REPLY) {
                    rc_log("stream: SESSION_REPLY (%u bytes)\n", (unsigned)message_length);
                    if (takion_session_negotiator_accept_reply(&g_negotiator, message, message_length,
                                                               rc_random_rng_callback, NULL)) {
                        derived = 1;
                    } else {
                        rc_log("\x1b[31mFAIL\x1b[0m SESSION_REPLY rejected - signature, curve or "
                               "version. NOT proceeding.\n");
                        rejected = 1;
                        break;
                    }
                } else if (type != 0xffffffffu) {
                    rc_log("  (control type %u on channel 0x%04x)\n", (unsigned)type, channel_id);
                }
            } else if (result == -1) {
                rc_log("\x1b[31mFAIL\x1b[0m stream: socket error %d\n", errno);
                break;
            }
            svcSleepThread(10000000); /* 10 ms */
        }

        /*
         * A wait that ends without a result must name itself. "How many reliable messages arrived at
         * all" is the most useful thing to know next: zero means the console never accepted the
         * request, non-zero means it was talking to us about something else.
         */
        if (!derived && !rejected) {
            rc_log("\x1b[31mFAIL\x1b[0m no SESSION_REPLY within %u ms (%d reliable message(s) arrived "
                   "on the stream channel)\n", NEGOTIATE_TIMEOUT_MS, messages_seen);
            if (messages_seen == 0) {
                rc_log("        The console accepted the Takion handshake and then ignored the request.\n");
                rc_log("        Suspects, in order: the launch spec's OFB counter (0, not pinned by any\n");
                rc_log("        captured vector), the omitted adaptiveStreamMode key, the protobuf.\n");
            }
        }
    }

    if (derived) {
        /* The keys themselves are secret and are NOT logged. A prefix of each is enough to compare two
         * runs or to check against the .NET side by hand, and is what the .NET diagnostics print too. */
        rc_log("\n\x1b[32mSTREAM KEYS DERIVED\x1b[0m\n");
        rc_log("  send    aes %02x%02x%02x%02x..  iv %02x%02x%02x%02x..\n",
            g_negotiator.send_aes_key[0], g_negotiator.send_aes_key[1],
            g_negotiator.send_aes_key[2], g_negotiator.send_aes_key[3],
            g_negotiator.send_base_iv[0], g_negotiator.send_base_iv[1],
            g_negotiator.send_base_iv[2], g_negotiator.send_base_iv[3]);
        rc_log("  receive aes %02x%02x%02x%02x..  iv %02x%02x%02x%02x..\n",
            g_negotiator.receive_aes_key[0], g_negotiator.receive_aes_key[1],
            g_negotiator.receive_aes_key[2], g_negotiator.receive_aes_key[3],
            g_negotiator.receive_base_iv[0], g_negotiator.receive_base_iv[1],
            g_negotiator.receive_base_iv[2], g_negotiator.receive_base_iv[3]);
        rc_log("\nThis is the milestone: Takion, the protobuf, the launch spec and the ECDH\n");
        rc_log("key agreement were all accepted by a real console.\n");
    }

    if (derived && !g_probing)
        (void)run_media(sock);
    else if (derived && g_probing)
        (void)run_media(sock); /* run_media returns immediately when probing - see its head */

    g_sealing = 0;
    close(sock);
    return derived;
}

/*
 * Scale-and-swap, rate-limited to the display. CALLED FROM INSIDE THE DRAIN LOOP as well as after it: a
 * 64-packet drain costs tens of milliseconds on this CPU, so presenting once per outer iteration caps
 * presentation at the drain batch - measured at 6.5 swaps a second against a 60 Hz throttle and a
 * decoder producing 53 pictures a second.
 *
 * It stays cheap to call: the timer check gates it, and rc_mvd_scale_begin returns immediately unless a
 * picture is ready. Nothing here blocks - gspWaitForVBlank is deliberately absent, because
 * stalling the drain is what caused this port's largest loss regression.
 */
static int g_scale_inflight;

static void maybe_present(u64 now_ms)
{
    /*
     * A scale handed to the worker core is collected here rather than waited on: the receive loop must
     * never block, which is the same rule that keeps gspWaitForVBlank out of this path. When the scale
     * runs inline (no worker) rc_mvd_scale_complete returns 1 straight away, so the swap happens below
     * in the same call that began it.
     */
    if (g_scale_inflight) {
        if (!rc_mvd_scale_complete())
            return;
        gfxSwapBuffers();
        g_scale_inflight = 0;
        g_picture_pending = 0;
        g_presents++;
        g_last_present_ms = now_ms;
        return;
    }

    if (now_ms - g_last_present_ms < VBLANK_INTERVAL_MS)
        return;
    if (!rc_mvd_scale_begin(&g_mvd))
        return;

    g_scale_inflight = 1;
    if (rc_mvd_scale_complete()) {
        gfxSwapBuffers();
        g_scale_inflight = 0;
        g_picture_pending = 0;
        g_presents++;
        g_last_present_ms = now_ms;
    }
}

/*
 * run_media is everything after the key agreement: sealing on, STREAM_INFO acked, heartbeats answered,
 * and the demuxer fed real packets.
 *
 * ORDER MATTERS AND IS NOT NEGOTIABLE. Sealing must be enabled before anything else goes out, because
 * from the console's point of view the session became authenticated the moment it sent SESSION_REPLY.
 * The STREAM_INFO_ACK in particular is sealed - and its SACK is too; leaving the SACK unsealed is the
 * documented way to get dropped with "streaminfoack fail" while believing you acked correctly.
 *
 * The client does not REQUEST STREAM_INFO; the console sends it unprompted once the session is sealed,
 * carrying the SPS/PPS parameter sets in resolution[0].videoHeader. Those are NOT in the video stream,
 * so stream_demux is given them and prepends them to the first IDR.
 */
static int run_media(int sock)
{
    uint8_t ack[16];
    size_t ack_len;
    u64 start_ms, elapsed_ms;
    double elapsed_secs;
    u64 last_loss_report_ms = 0;
    int stream_info_seen = 0;
    int video_packets = 0;
    int audio_packets = 0;
    int verify_failures = 0;

    /* 1. Sealing on, before anything else is sent. */
    rc_profile_reset(&g_profile);
    rc_mvd_set_profile(&g_profile);
    stream_packet_crypto_init(&g_send_crypto, g_negotiator.send_aes_key, g_negotiator.send_base_iv);
    stream_packet_crypto_init(&g_recv_crypto, g_negotiator.receive_aes_key, g_negotiator.receive_base_iv);
    g_send_keystream_offset = 0;
    g_sealing = 1;

    /* The demuxer, opened on the receive-direction packet crypto (stream_demux_packet_crypto). */
    {
        stream_demux_sink sink;

        memset(&sink, 0, sizeof(sink));
        sink.video_frame_ready = on_video_frame;
        sink.audio_frame_ready = on_audio_frame;
        sink.video_loss_detected = on_video_loss;
        stream_demux_init(&g_demux, stream_demux_packet_crypto(&g_recv_crypto), sink);
    }
    takion_channel_enable_sealing(&g_stream_channel, seal_control_packet, NULL);
    rc_log("stream: GMAC sealing enabled\n");

    /* 2. Wait for STREAM_INFO, then ack it on channel 0x0009. */
    start_ms = osGetTime();
    while (osGetTime() - start_ms < (u64)STREAM_INFO_TIMEOUT_MS && !stream_info_seen) {
        unsigned channel_id;
        const uint8_t *message;
        size_t message_length;

        service_control(NULL);
        if (!g_control_alive)
            return 0;

        if (takion_channel_poll(&g_stream_channel, &channel_id, &message, &message_length) == 1) {
            uint32_t type = 0xffffffffu;

            answer_stream_heartbeat(message, message_length);
            if (takion_control_peek_type(message, message_length, &type)) {
                if (type == TAKION_CONTROL_STREAM_INFO) {
                    takion_stream_info info;

                    rc_log("stream: STREAM_INFO (%u bytes)\n", (unsigned)message_length);
                    if (takion_control_parse_stream_info(message, message_length, &info)
                        && info.has_resolution) {
                        rc_log("stream: %ux%u, %u-byte video header, %u-byte audio header\n",
                            (unsigned)info.width, (unsigned)info.height,
                            (unsigned)info.video_header_length, (unsigned)info.audio_header_length);
                        g_probe_reported_width = (int)info.width;
                        g_probe_reported_height = (int)info.height;
                        g_probe_got_stream_info = 1;
                        /* The SPS/PPS. Without these the first IDR is undecodable, because they are
                         * not carried in the video stream itself. */
                        if (info.video_header_length > 0) {
                            stream_demux_set_video_header(&g_demux, info.video_header,
                                                          info.video_header_length);
                        }
                        /* Now, not earlier: the console decides the resolution, and configuring MVD for
                         * what we asked for rather than what we were given is a good way to decode into
                         * a wrongly-sized buffer. */
                        g_actual_width = (int)info.width;
                        g_actual_height = (int)info.height;
                        if (g_actual_width != g_stream_width || g_actual_height != g_stream_height) {
                            rc_log("\x1b[33mNOTE\x1b[0m console declined %dx%d and chose %dx%d\n",
                                g_stream_width, g_stream_height, g_actual_width, g_actual_height);
                        }
                        (void)rc_mvd_init(&g_mvd, (int)info.width, (int)info.height, g_video_rgb565);
                        /* Independent of video: no sound is a worse session, a dead one is worse still. */
                        (void)rc_audio_init(&g_audio);
                        halyard_input_writer_init(&g_input);
                        if (g_mvd.ready && g_scale_thread_enabled)
                            rc_profile_set_scale_threaded(rc_mvd_start_scale_thread(g_core_mask));
                    } else {
                        rc_log("\x1b[33mNOTE\x1b[0m STREAM_INFO did not parse - no parameter sets\n");
                    }
                    stream_info_seen = 1;
                } else if (type == TAKION_CONTROL_DISCONNECT) {
                    const char *reason = NULL;
                    size_t reason_length = 0;

                    /* Say what the console said. A DISCONNECT carries a reason string, and logging it as
                     * "type 8" throws away the one piece of information that explains the hang-up. */
                    if (takion_control_parse_disconnect(message, message_length, &reason, &reason_length)
                        && reason_length > 0) {
                        rc_log("\x1b[31mDISCONNECT\x1b[0m from console: %.*s\n",
                            (int)reason_length, reason);
                    } else {
                        rc_log("\x1b[31mDISCONNECT\x1b[0m from console (%u bytes, no reason parsed)\n",
                            (unsigned)message_length);
                    }
                } else {
                    rc_log("  (control type %u on channel 0x%04x, %u bytes)\n",
                        (unsigned)type, channel_id, (unsigned)message_length);
                }
            }
        }
        svcSleepThread(10000000);
    }

    if (!stream_info_seen) {
        rc_log("\x1b[31mFAIL\x1b[0m no STREAM_INFO within %u ms.\n", STREAM_INFO_TIMEOUT_MS);
        rc_log("        The keys are agreed, so suspect the SEALING: tag offset 5, keystream offset 9,\n");
        rc_log("        AAD with tag+keystream_offset zeroed, position advancing by 16-byte-aligned length.\n");
        return 0;
    }

    /*
     * When probing resolutions, STREAM_INFO's answer is all that is needed - the console has told us
     * what it chose. Ack it so the session ends cleanly, and skip the media loop so a sweep of six
     * resolutions takes under a minute rather than six.
     */
    if (g_probing) {
        ack_len = takion_control_build_bare(TAKION_CONTROL_STREAM_INFO_ACK, ack, sizeof(ack));
        if (ack_len > 0)
            (void)takion_channel_send(&g_stream_channel, TAKION_CHANNEL_STREAM_INFO, ack, ack_len);
        return 1;
    }

    ack_len = takion_control_build_bare(TAKION_CONTROL_STREAM_INFO_ACK, ack, sizeof(ack));
    if (ack_len == 0
        || !takion_channel_send(&g_stream_channel, TAKION_CHANNEL_STREAM_INFO, ack, ack_len)) {
        rc_log("\x1b[31mFAIL\x1b[0m could not send STREAM_INFO_ACK\n");
        return 0;
    }
    rc_log("stream: STREAM_INFO_ACK sent - A/V should start now\n");
    rc_log("streaming - press HOME and close the app to end the session\n\n");

    /*
     * 3. The media window. The console's Takion HEARTBEATs are answered as they arrive, and a 200 ms
     * loss report runs off the same tick as everything else.
     */
    start_ms = osGetTime();
    /*
     * EVERY BUTTON BELONGS TO THE CONSOLE, so there are no debug bindings: START is Options,
     * START+SELECT is the PS button, X is Triangle. Exit is HOME, which aptMainLoop already reports:
     * pressing HOME and closing the app ends this loop.
     */
    while (aptMainLoop()) {
        uint8_t packet[STREAM_PACKET_CRYPTO_MAX_PACKET];
        ssize_t n;
        u64 now_ms;

        now_ms = osGetTime();

        service_control(NULL);
        if (!g_control_alive)
            break;

        if (now_ms - last_loss_report_ms >= (u64)LOSS_REPORT_INTERVAL_MS) {
            /* 15 bytes: [0]=0x05, u16BE received at 3, u16BE lost at 5, tag at 7, keystream offset at 0x0b.
             * Sealed with the same shared counter and the same tag+keystream_offset-zeroed AAD as control. */
            uint8_t report[15];
            uint64_t keystream_offset;

            memset(report, 0, sizeof(report));
            long window_received = 0, window_lost = 0;

            /*
             * REPORT THE LOSS, not only `received`. With `lost` left at zero the console is told every
             * 200 ms that the link is perfect and has no reason to back off: a run that did so under a
             * real game (roughly 2.5 Mbps) lost 60,066 units against 39,177 received, and each of the
             * 314 IDR requests our own loss detection fired added another 30 KB keyframe burst.
             *
             * take_packet_stats RESETS the counters as it reads, so this is the periodic drain and the
             * end-of-run report adds the running totals.
             */
            stream_demux_take_packet_stats(&g_demux, &window_received, &window_lost);
            g_total_received += window_received;
            g_total_lost += window_lost;

            report[0] = 0x05;
            report[3] = (uint8_t)((unsigned long)window_received >> 8);
            report[4] = (uint8_t)window_received;
            report[5] = (uint8_t)((unsigned long)window_lost >> 8);
            report[6] = (uint8_t)window_lost;
            keystream_offset = reserve_keystream_offset(sizeof(report));
            report[LOSS_REPORT_KEYSTREAM_OFFSET_AT + 0] = (uint8_t)(keystream_offset >> 24);
            report[LOSS_REPORT_KEYSTREAM_OFFSET_AT + 1] = (uint8_t)(keystream_offset >> 16);
            report[LOSS_REPORT_KEYSTREAM_OFFSET_AT + 2] = (uint8_t)(keystream_offset >> 8);
            report[LOSS_REPORT_KEYSTREAM_OFFSET_AT + 3] = (uint8_t)keystream_offset;
            (void)stream_packet_crypto_seal(&g_send_crypto, keystream_offset, report, sizeof(report),
                                            LOSS_REPORT_TAG_OFFSET, 1);
            sendto(sock, report, sizeof(report), 0,
                   (struct sockaddr *)&g_stream_channel.peer, sizeof(g_stream_channel.peer));
            last_loss_report_ms = now_ms;
        }

        /*
         * CONTROLLER INPUT, up the same socket.
         *
         * Two packets. Each takes its keystream offset from the shared send counter
         * (reserve_keystream_offset), writes it at offset 4 of the header, AES-CTR encrypts the payload,
         * then calls stream_packet_crypto_seal with the tag at offset 8 and zero_keystream_offset = 0,
         * so the GMAC's AAD zeroes ONLY the tag. The loss report above passes tag offset 7 and
         * zero_keystream_offset = 1, which zeroes the tag and the keystream-offset field. A history
         * packet goes whenever a button transitioned and again every 200 ms; a state packet every 15 ms
         * (cap48).
         *
         * Sent from the outer loop rather than the drain: input is a wall-clock activity like the
         * heartbeat, and the drain's period depends on load.
         */
        if (now_ms - g_last_input_poll_ms >= (u64)INPUT_POLL_INTERVAL_MS) {
            halyard_input_state in;
            circlePosition circle, cstick;
            uint8_t input_packet[HALYARD_INPUT_MAX_PACKET];
            size_t payload_len;
            uint64_t input_keystream_offset;
            u32 held;

            hidScanInput();
            held = hidKeysHeld();
            hidCircleRead(&circle);
            hidCstickRead(&cstick);

            memset(&in, 0, sizeof(in));
            in.buttons = map_3ds_buttons(held);
            in.left_x = scale_stick(circle.dx);
            in.left_y = (int16_t)-scale_stick(circle.dy);   /* wire wants up NEGATIVE */
            in.right_x = scale_stick(cstick.dx);
            in.right_y = (int16_t)-scale_stick(cstick.dy);

            payload_len = halyard_input_build_history_payload(&g_input, &in, input_packet + HALYARD_INPUT_HEADER_LENGTH,
                                                              sizeof(input_packet) - HALYARD_INPUT_HEADER_LENGTH);
            if (payload_len == 0 && now_ms - g_last_input_history_ms >= (u64)INPUT_HISTORY_REPEAT_MS)
                payload_len = halyard_input_repeat_history_payload(&g_input, input_packet + HALYARD_INPUT_HEADER_LENGTH,
                                                                   sizeof(input_packet) - HALYARD_INPUT_HEADER_LENGTH);
            if (payload_len > 0) {
                size_t total = HALYARD_INPUT_HEADER_LENGTH + payload_len;

                g_last_input_history_ms = now_ms;
                (void)halyard_input_write_header(0x01, g_input.history_seq++, input_packet, sizeof(input_packet));
                input_keystream_offset = reserve_keystream_offset(total);
                input_packet[4] = (uint8_t)(input_keystream_offset >> 24);
                input_packet[5] = (uint8_t)(input_keystream_offset >> 16);
                input_packet[6] = (uint8_t)(input_keystream_offset >> 8);
                input_packet[7] = (uint8_t)input_keystream_offset;
                /*
                 * ENCRYPT, THEN SEAL. stream_packet_crypto_seal only computes the GMAC tag; it does not
                 * touch the payload. Loss reports need nothing more, but spec 6.3 is explicit that input
                 * payloads ARE AES-CTR encrypted. Sent in plaintext, the console decrypts our button
                 * events into noise that has passed authentication the whole way.
                 *
                 * Order matters: the tag is computed over the packet AS IT GOES ON THE WIRE, i.e. over
                 * ciphertext.
                 */
                stream_packet_crypto_crypt_payload(&g_send_crypto, input_keystream_offset,
                                                   input_packet + HALYARD_INPUT_HEADER_LENGTH,
                                                   total - HALYARD_INPUT_HEADER_LENGTH);
                (void)stream_packet_crypto_seal(&g_send_crypto, input_keystream_offset, input_packet, total, 8, 0);
                sendto(sock, input_packet, total, 0,
                       (struct sockaddr *)&g_stream_channel.peer, sizeof(g_stream_channel.peer));
                g_input_history_packets++;
            }

            if (!g_input.have_previous || now_ms - g_last_input_ms >= (u64)INPUT_STATE_INTERVAL_MS) {
                size_t total;

                payload_len = halyard_input_build_state_payload(&in, input_packet + HALYARD_INPUT_HEADER_LENGTH,
                                                                sizeof(input_packet) - HALYARD_INPUT_HEADER_LENGTH);
                total = HALYARD_INPUT_HEADER_LENGTH + payload_len;
                (void)halyard_input_write_header(0x06, g_input.state_seq++, input_packet, sizeof(input_packet));
                input_keystream_offset = reserve_keystream_offset(total);
                input_packet[4] = (uint8_t)(input_keystream_offset >> 24);
                input_packet[5] = (uint8_t)(input_keystream_offset >> 16);
                input_packet[6] = (uint8_t)(input_keystream_offset >> 8);
                input_packet[7] = (uint8_t)input_keystream_offset;
                /* Encrypt then seal - see the history packet above. */
                stream_packet_crypto_crypt_payload(&g_send_crypto, input_keystream_offset,
                                                   input_packet + HALYARD_INPUT_HEADER_LENGTH,
                                                   total - HALYARD_INPUT_HEADER_LENGTH);
                (void)stream_packet_crypto_seal(&g_send_crypto, input_keystream_offset, input_packet, total, 8, 0);
                sendto(sock, input_packet, total, 0,
                       (struct sockaddr *)&g_stream_channel.peer, sizeof(g_stream_channel.peer));
                g_input_state_packets++;
                g_last_input_ms = now_ms;
            }

            g_input.previous = in;
            g_input.have_previous = 1;
            g_last_input_poll_ms = now_ms;
        }

        /*
         * DRAIN UNTIL EMPTY, then sleep - never one packet per tick. A single datagram per 2 ms sleep
         * caps the receive rate at 500 packets/second and guarantees that any burst larger than one
         * packet overflows the socket buffer, and a keyframe is a burst of a dozen or more units. CPU
         * cost was ruled out as the cause: the decode-on/decode-off A/B showed identical loss (1799 vs
         * 1743 units). source/linktest/main.c found the same with a drain loop tied to vblank, which
         * produced a fake ~2 Mbps ceiling.
         *
         * Bounded per tick so the control channel and the button poll still get serviced under a
         * sustained flood.
         */
        int drained = 0;
        while (drained++ < 64
               && (n = recvfrom(sock, packet, sizeof(packet), MSG_PEEK, NULL, NULL)) > 0) {
            unsigned base_type = (unsigned)(packet[0] & 0x0fu);

            if (base_type == 0u) {
                unsigned channel_id;
                const uint8_t *message;
                size_t message_length;
                if (takion_channel_poll(&g_stream_channel, &channel_id, &message, &message_length) == 1)
                    answer_stream_heartbeat(message, message_length);
            } else {
                n = recvfrom(sock, packet, sizeof(packet), 0, NULL, NULL);
                if (n > 0 && (size_t)n >= STREAM_HEADER_LENGTH) {
                    /*
                     * The A/V rule differs from control's: the keystream offset travels IN the packet
                     * (u32 BE at offset 14) rather than being ours to choose, and only the TAG region is
                     * zeroed in the AAD, not the keystream offset.
                     */
                    uint64_t keystream_offset =
                        ((uint64_t)packet[STREAM_HEADER_KEYSTREAM_OFFSET_AT + 0] << 24) |
                        ((uint64_t)packet[STREAM_HEADER_KEYSTREAM_OFFSET_AT + 1] << 16) |
                        ((uint64_t)packet[STREAM_HEADER_KEYSTREAM_OFFSET_AT + 2] << 8) |
                        ((uint64_t)packet[STREAM_HEADER_KEYSTREAM_OFFSET_AT + 3]);
                    /*
                     * SAMPLED, one packet in sixteen. stream_demux_ingest below already verifies every
                     * packet through its crypto seam (packet_crypto_open_packet) and drops what fails,
                     * so verifying every packet here too would compute GMAC TWICE - about 10,000
                     * redundant times a minute at ~1,100 bytes each. The sampled check is kept because
                     * it is the only thing that distinguishes link corruption from a crypto fault; a
                     * systematic failure would hit every packet.
                     */
                    int verified = 1;

                    if ((video_packets + audio_packets) % 16 == 0) {
                        uint64_t t = rc_profile_start();
                        verified = stream_packet_crypto_verify(&g_recv_crypto, keystream_offset, packet,
                            (size_t)n, STREAM_HEADER_TAG_OFFSET, 0 /* A/V zeroes the tag only */);
                        rc_profile_stop(&g_profile, RC_STAGE_GMAC_VERIFY, t);
                    }

                    if (base_type == 2u) {
                        video_packets++;
                        g_video_bytes += (unsigned long)n;
                    } else if (base_type == 3u) {
                        audio_packets++;
                    }
                    if (!verified) {
                        verify_failures++;
                        /*
                         * Log the first few individually. A systematic crypto error fails
                         * deterministically or in bulk, whereas a corrupted datagram on a 2.4 GHz link
                         * is isolated and unrepeatable. On 2026-08-12 exactly one packet in 3,879 failed
                         * on one run and none in the 11,448 that followed. The rotation window is
                         * printed because a fault at a window boundary would be the one shape that IS a
                         * crypto bug.
                         */
                        if (verify_failures <= 4) {
                            rc_log("\x1b[33mGMAC fail #%d\x1b[0m base %u, %u bytes, keystream_offset %u "
                                   "(rotation window %u)\n", verify_failures, base_type, (unsigned)n,
                                   (unsigned)keystream_offset, (unsigned)(keystream_offset / 16u / 45000u));
                        }
                    }

                    if (video_packets + audio_packets == 1) {
                        rc_log("\x1b[32mFIRST A/V PACKET\x1b[0m base type %u, %u bytes, keystream_offset %u, "
                               "GMAC %s\n", base_type, (unsigned)n, (unsigned)keystream_offset,
                               verified ? "\x1b[32mVERIFIED\x1b[0m" : "\x1b[31mFAILED\x1b[0m");
                    }

                    /* Into the demuxer: decrypt, reassemble, FEC-recover, emit whole frames. It
                     * re-verifies the tag itself through the crypto seam; the check above is kept
                     * separate so a framing bug and an authentication bug stay distinguishable. */
                    {
                        uint64_t t = rc_profile_start();
                        uint64_t inner0 = g_profile.inner;
                        stream_demux_ingest(&g_demux, packet, (size_t)n);
                        rc_profile_stop_nested(&g_profile, RC_STAGE_DEMUX, t, inner0);
                    }

                    /*
                     * THE CONTROL CHANNEL IS SERVICED FROM INSIDE THE DRAIN TOO. An outer iteration
                     * drains up to 64 packets; at 60 fps that is ~20 frames of decode and scale -
                     * comfortably 200 ms of work between two chances to answer a HEARTBEAT_REQ. Both
                     * 60 fps runs went black at ~29 s with "control channel closed by console". A TCP
                     * recv on an empty socket is cheap, so 1-in-16 is frequent enough to be safe and
                     * rare enough not to matter.
                     */
                    if ((drained & 15) == 0) {
                        service_control(NULL);
                        if (!g_control_alive)
                            break;
                    }

                    /*
                     * EVERY FOURTH PACKET, NOT EVERY SIXTEENTH - this interval is the frame-rate cap. At
                     * ~290 packets/s a 1-in-16 check runs about 18 times a second, and the threaded
                     * scale needs one call to start and another to collect, which put the ceiling near
                     * 9 fps: the decoder produced 1,661 pictures and only 1,236 were shown. The check
                     * itself is a clock comparison and a TryWait.
                     */
                    if ((drained & 3) == 0)
                        maybe_present(osGetTime());
                }
            }
        }
        /*
         * Present, on the display's clock rather than the decoder's. rc_mvd scales the decoded frame
         * into the framebuffer with the CPU and flushes it (rc_mvd_scale_begin inline, the worker when
         * threaded). libctru documents that flush as required for exactly this software-rendering
         * case: without it the pixels sit in the data cache and the display controller never sees them
         * (16 pictures decoded, zero render errors, blank screen). maybe_present swaps only when rc_mvd
         * reports a picture: swapping at loop rate would flicker the last frame against whatever is in
         * the other buffer.
         *
         * gspWaitForVBlank is deliberately NOT used: it would block the receive loop for up to 16.7 ms,
         * and a stalled drain is exactly what caused this port's largest packet-loss regression. Instead
         * the swap is rate-limited to the 60 Hz refresh interval and the loop keeps draining; a frame
         * that arrives early waits in the back buffer rather than stalling the network.
         */
        maybe_present(osGetTime());
        svcSleepThread(1000000); /* 1 ms, and only once the socket is empty */
    }

    if (g_mvd.ready) {
        /* frames_rendered counts pictures since rc_mvd_init, which run_media calls once per session. */
        rc_log("\nMVD (decode was %s): %ld picture(s) rendered\n",
            g_decode_enabled ? "ON" : "off", g_mvd.frames_rendered);
        rc_log("     %ld NAL unit(s) fed, %ld accepted non-picture unit(s)\n",
            g_mvd.nal_units_fed, g_mvd.param_sets);
        rc_log("     %ld process error(s)%s", g_mvd.process_errors, g_mvd.process_errors ? " (first " : "\n");
        if (g_mvd.process_errors)
            rc_log("0x%08x)\n", g_mvd.first_process_error);
        rc_log("     %ld render error(s)%s", g_mvd.render_errors, g_mvd.render_errors ? " (first " : "\n");
        if (g_mvd.render_errors)
            rc_log("0x%08x)\n", g_mvd.first_render_error);
        rc_log("     %ld frame(s) skipped awaiting a keyframe, %ld oversized unit(s)\n",
            g_mvd.frames_skipped, g_mvd.oversized_units);
        rc_log("     ProcessNALUnit said: OK %ld, FRAMEREADY %ld, NALUPROCFLAG %ld, other %ld\n",
            g_mvd.status_ok, g_mvd.status_frameready, g_mvd.status_nalucproc, g_mvd.status_other);
        if (g_mvd.status_frameready == 0) {
            rc_log("     \x1b[33mNo FRAMEREADY at all\x1b[0m - every render was asking for a picture\n");
            rc_log("     the decoder may never have had. That would explain the whole thing.\n");
        }
    }
    /* MEASURED elapsed time, not a constant. Every per-second figure below divides by this. */
    elapsed_ms = osGetTime() - start_ms;
    if (elapsed_ms == 0)
        elapsed_ms = 1;
    elapsed_secs = (double)elapsed_ms / 1000.0;
    rc_log("\nsession ran %.1f s\n", elapsed_secs);

    rc_profile_report(&g_profile, (unsigned)elapsed_ms);
    {
        /*
         * The running totals plus the tail. take_packet_stats resets as it reads and the loss reporter
         * drains it every 200 ms, so the read here returns only what arrived since the last report.
         */
        long received, lost, tail_received = 0, tail_lost = 0;

        stream_demux_take_packet_stats(&g_demux, &tail_received, &tail_lost);
        received = g_total_received + tail_received;
        lost = g_total_lost + tail_lost;

        rc_log("\ndemux: %ld video frame(s) (%ld keyframe(s)), %ld audio frame(s), %ld loss event(s)\n",
            g_frames, g_keyframes, g_audio_frames, g_loss_events);
        rc_log("       %ld IDR request(s) sent\n", g_idr_requests);
        rc_log("       %ld frame(s) presented (%.1f/s)\n", g_presents,
            (double)g_presents / elapsed_secs);
        rc_log("       %ld unit(s) received, %ld lost\n", received, lost);

        /*
         * WHAT THE CONSOLE ACTUALLY SENT. DO NOT READ bits/pixel AS A LEGIBILITY THRESHOLD: decoding a
         * capture on a PC showed the PS5 home screen with every label legible at 0.10 bits/pixel. The
         * figure is a description of what arrived, not a verdict.
         */
        /* Against the dimensions STREAM_INFO reported, not the ones requested - see below. */
        if (g_frames > 0 && g_actual_width > 0 && g_actual_height > 0) {
            double secs = elapsed_secs;
            double mbps = (double)g_video_bytes * 8.0 / secs / 1000000.0;
            double bpp = ((double)g_video_bytes * 8.0 / (double)g_frames)
                / ((double)g_actual_width * (double)g_actual_height);

            rc_log("\nvideo throughput: \x1b[36m%.2f Mbps\x1b[0m received (asked for %d kbps)\n",
                mbps, g_stream_bitrate_kbps);
            /*
             * THE RESOLUTION HERE IS THE ONE THAT ARRIVED, not the one requested: the console declines
             * silently, and dividing by 960x540 while 640x360 is on the wire understates bits/pixel
             * by 2.25x.
             */
            rc_log("       %.1f KB per frame at %dx%d = \x1b[36m%.3f bits/pixel\x1b[0m%s\n",
                (double)g_video_bytes / (double)g_frames / 1024.0,
                g_actual_width, g_actual_height, bpp,
                (g_actual_width != g_stream_width || g_actual_height != g_stream_height)
                    ? "   \x1b[33m(console declined the requested size)\x1b[0m" : "");
        }
    }
    /* Now that the media window is over and the socket no longer matters, commit the capture. */
    if (g_video_dump_armed && g_video_dump_bytes > 0) {
        char path[512];
        FILE *f;

        rc_program_dir(g_argv0, path, sizeof(path));
        strncat(path, "video.264", sizeof(path) - strlen(path) - 1);
        f = fopen(path, "wb");
        if (f != NULL) {
            fwrite(g_video_dump_buf, 1, g_video_dump_bytes, f);
            fclose(f);
            rc_log("video dump: %lu KB written to %s\n", g_video_dump_bytes / 1024ul, path);
        } else {
            rc_log("\x1b[31mvideo dump: could not open %s\x1b[0m\n", path);
        }
    }

    if (g_audio.ready || g_audio.frames_decoded > 0) {
        rc_log("audio: %ld frame(s) decoded (%.1f/s), %ld decode error(s), %ld dropped (queue full)%s\n",
            g_audio.frames_decoded,
            (double)g_audio.frames_decoded / elapsed_secs,
            g_audio.decode_errors, g_audio.queue_full,
            g_audio.first_error != 0 ? " \x1b[33m(see first_error)\x1b[0m" : "");
        if (g_audio.depth_samples > 0) {
            double avg = (double)g_audio.depth_sum / (double)g_audio.depth_samples;

            rc_log("       ring avg %.0f samples (\x1b[36m%.0f ms\x1b[0m behind the picture), "
                   "peak %.0f ms of %d ms, %ld starved\n",
                avg, avg * 1000.0 / RC_AUDIO_SAMPLE_RATE,
                (double)g_audio.depth_peak * 1000.0 / RC_AUDIO_SAMPLE_RATE,
                RC_AUDIO_RING_SAMPLES * 1000 / RC_AUDIO_SAMPLE_RATE, g_audio.starved);
            rc_log("       playback rate trimmed %+d ppm to hold %d ms\n",
                g_audio.rate_trim_ppm, RC_AUDIO_TARGET_SAMPLES * 1000 / RC_AUDIO_SAMPLE_RATE);
        }
    }

    rc_log("control: %ld heartbeat(s) answered, %ld other message(s), %ld reply send failure(s)\n",
        g_heartbeats_answered, g_control_messages_other, g_control.heartbeat_send_failures);
    if (g_control.resyncs > 0) {
        rc_log("         \x1b[33m%ld frame resync(s), %ld byte(s) discarded\x1b[0m - the desync is real, "
               "not merely survived\n", g_control.resyncs, g_control.resync_discarded);
    }

    rc_log("input: %ld state packet(s), %ld history packet(s) sent\n",
        g_input_state_packets, g_input_history_packets);

    rc_log("media window: %d video, %d audio packet(s), %d GMAC failure(s) in a 1-in-16 sample\n",
        video_packets, audio_packets, verify_failures);
    if (video_packets + audio_packets > 0) {
        if (verify_failures == 0)
            rc_log("\x1b[32mEvery A/V packet authenticated\x1b[0m - the stream keys and the per-packet\n"
                   "GMAC derivation are both correct against real traffic.\n");
        else
            rc_log("\x1b[31m%d packet(s) failed GMAC\x1b[0m - suspect the receive-direction key (dir 3),\n"
                   "the nonce derivation, or the A/V AAD rule (tag zeroed, keystream offset NOT).\n",
                   verify_failures);
    }
    if (video_packets == 0 && audio_packets == 0) {
        rc_log("\x1b[31mFAIL\x1b[0m the console acked our STREAM_INFO_ACK and sent no media.\n");
        rc_log("        Suspect the heartbeat replies, the 200 ms loss report, or their sealing.\n");
    }
    return (video_packets > 0) ? 1 : 0;
}

static int run_connect(const halyard_pairing_record *rec)
{
    int ok = 0;

    if (!halyard_control_session_open(rec, &g_control))
        return 0;
    g_control_alive = 1;

    rc_log("control plane up; checking the sign-in gate\n");
    if (!check_sign_in_gate())
        goto done;

    rc_log("waiting for session-ready\n");
    if (!wait_for_session_ready(SESSION_READY_TIMEOUT_MS)) {
        rc_log("\x1b[31mFAIL\x1b[0m no SESSION_ID within %u ms - the console is not willing to stream\n",
            SESSION_READY_TIMEOUT_MS);
        goto done;
    }
    rc_log("session ready\n");

    (void)run_senkusha(rec->host); /* best-effort by design - see run_senkusha's comment */
    if (!g_control_alive) {
        rc_log("\x1b[31mFAIL\x1b[0m lost the control channel during senkusha\n");
        goto done;
    }

    ok = run_stream(rec);

done:
    /* REST_MODE is deliberately NOT sent - this probe should leave the console awake for the next run. */
    halyard_control_session_close(&g_control);
    g_control_alive = 0;
    takion_session_negotiator_reset(&g_negotiator);
    return ok;
}

/*
 * Ask the console for each candidate resolution in turn, one full connect per candidate, and report what
 * it actually chose. See the RESOLUTION PROBE STATE comment above run_stream for why this matters.
 */
static void run_resolution_probe(const halyard_pairing_record *rec)
{
    static const struct { int w, h; const char *note; } kCandidates[] = {
        { 640, 360, "known good - the bottom standard rung" },
        { 400, 240, "known refused - the screen's own size, macroblock-aligned" },
        { 320, 180, "half of 640x360; 180 is NOT a multiple of 16" },
        { 480, 270, "16:9, 270 not a multiple of 16" },
        { 512, 288, "both multiples of 16, below the bottom rung" },
        { 960, 540, "the next standard rung up - expected to work" },
    };
    const int count = (int)(sizeof(kCandidates) / sizeof(kCandidates[0]));
    static int result_w[6], result_h[6], result_state[6]; /* 0 refused/failed, 1 accepted */
    int i;

    rc_log("\n=== resolution probe: %d candidates, one connect each ===\n\n", count);

    for (i = 0; i < count; i++) {
        g_probing = 1;
        g_probe_width = kCandidates[i].w;
        g_probe_height = kCandidates[i].h;
        g_probe_got_stream_info = 0;
        g_probe_reported_width = 0;
        g_probe_reported_height = 0;

        rc_log("--- asking for %dx%d (%s)\n",
            kCandidates[i].w, kCandidates[i].h, kCandidates[i].note);

        (void)run_connect(rec);

        result_state[i] = g_probe_got_stream_info;
        result_w[i] = g_probe_reported_width;
        result_h[i] = g_probe_reported_height;

        if (g_probe_got_stream_info) {
            rc_log("--- %dx%d -> console chose %dx%d%s\n\n",
                kCandidates[i].w, kCandidates[i].h, result_w[i], result_h[i],
                (result_w[i] == kCandidates[i].w && result_h[i] == kCandidates[i].h)
                    ? " \x1b[32m(as asked)\x1b[0m" : " \x1b[33m(CLAMPED)\x1b[0m");
        } else {
            rc_log("--- %dx%d -> \x1b[31mno STREAM_INFO (refused or failed)\x1b[0m\n\n",
                kCandidates[i].w, kCandidates[i].h);
        }

        /* The console needs a real gap between sessions. At 2 s the next connect reliably found the
         * previous session still tearing down, producing /sess/init -> 403 or an ECONNRESET that looks
         * exactly like a rejected resolution and is not one. */
        svcSleepThread(5000000000LL); /* 5 s */
    }

    g_probing = 0;

    rc_log("=== resolution probe summary ===\n");
    for (i = 0; i < count; i++) {
        if (result_state[i]) {
            rc_log("  asked %4dx%-4d -> got %4dx%-4d %s\n",
                kCandidates[i].w, kCandidates[i].h, result_w[i], result_h[i],
                (result_w[i] == kCandidates[i].w && result_h[i] == kCandidates[i].h) ? "OK" : "clamped");
        } else {
            rc_log("  asked %4dx%-4d -> refused\n", kCandidates[i].w, kCandidates[i].h);
        }
    }
    rc_log("\nIf anything at or below 400x240 was accepted, MVD can render it directly and the\n");
    rc_log("scaling problem disappears. If only the standard ladder works, scaling is mandatory.\n");
}

int main(int argc, char **argv)
{
    halyard_pairing_record rec;

    osSetSpeedupEnable(true);

    /*
     * TOP SCREEN IS RGB565 AND BELONGS TO MVD; the console goes on the bottom. MVD emits 16-bit BGR565,
     * and gfxInitDefault() gives the top screen a 24-bit BGR8 framebuffer - a format mismatch.
     * consoleInit(GFX_TOP) would also hand that same framebuffer to the text console. With that setup,
     * hardware runs produced 0 pictures with 15 render failures per session and ZERO process
     * errors. Matches the devkitPro MVD example.
     */
    gfxInit(GSP_RGB565_OES, GSP_BGR8_OES, false);
    consoleInit(GFX_BOTTOM, NULL);

    g_argv0 = argc > 0 ? argv[0] : NULL;
    rc_log_open(g_argv0, "connect.log");

    rc_log("ripcord-3ds connect flow (control -> senkusha -> Takion -> stream keys)\n");
    rc_log("-----------------------------------------------------------------------\n");
    g_core_mask = rc_profile_probe_cores();
    rc_profile_calibrate();

    if (!rc_random_init()) {
        rc_log("\x1b[31mFAIL\x1b[0m no entropy service (ps:ps); cannot negotiate safely\n");
    } else if (rc_soc_init() != 0) {
        rc_log("\x1b[31mFAIL\x1b[0m SOC init failed\n");
    } else {
        if (halyard_pairing_file_load(argc > 0 ? argv[0] : NULL, &rec)) {
            rc_log("connecting to %s (%s)\n", rec.host, rec.is_ps5 ? "PS5" : "PS4");
            if (rec.probe_resolutions)
                run_resolution_probe(&rec);
            else
                (void)run_connect(&rec);
        }
        rc_soc_exit();
    }
    rc_random_exit();

    rc_log("\nPress START to exit.\n");
    rc_log_close();

    while (aptMainLoop()) {
        hidScanInput();
        if (hidKeysDown() & KEY_START)
            break;
        gfxFlushBuffers();
        gfxSwapBuffers();
        gspWaitForVBlank();
    }

    gfxExit();
    return 0;
}
