/*
 * ripcord-3ds - the combined connect-flow probe: control plane -> senkusha -> Takion -> stream keys.
 *
 * WHY THIS EXISTS. Every phase below the control plane was previously testable only in isolation, and
 * that turned out to be no test at all: source/takion/main.c drove an INIT at a console standalone on
 * 2026-08-12 and got silence, because a console opens its stream UDP ports ONLY inside a live session.
 * There is no way to validate Takion, the ECDH key agreement, or anything above them without carrying a
 * real control session all the way there. This program does that.
 *
 * WHAT IT PROVES, IF IT WORKS: that this port's Takion handshake, its hand-rolled SESSION_REQUEST
 * protobuf, its launch spec, and its ECDH key agreement are all accepted by a real PS5 - i.e. that the
 * stream keys it derives are the ones the console derived. That is everything between the (already
 * hardware-confirmed) control plane and the A/V decode that does not exist yet.
 *
 * THE SEQUENCE, and every step's reason (HalyardStreamingSession.ConnectAsync):
 *   1. arm -> /sess/init -> /sess/ctrl                     (halyard_control_session, hardware-confirmed)
 *   2. the binary control channel, answering HEARTBEAT_REQ  - from here to the end, continuously
 *   3. the sign-in gate: wait 1 s for LOGIN_PROMPT (0x0004). A LOCKED CONSOLE SILENTLY DROPS EVERY
 *      TAKION INIT, so this is a hard gate, not a courtesy. This build cannot answer a PIN prompt (no
 *      on-screen entry yet), so it reports and stops rather than proceeding into guaranteed silence.
 *   4. wait for session-ready (SESSION_ID, 0x0033)
 *   5. senkusha bring-up on UDP 9297 - the console requires this to have happened before it will answer
 *      the stream's SESSION exchange. Keyless: full Takion framing with the GMAC and key-position fields
 *      left zero.
 *   6. Takion handshake on UDP 9296 (NOT 9297 - that is senkusha's port)
 *   7. SESSION_REQUEST -> SESSION_REPLY -> per-direction stream keys (takion_session_negotiator)
 *
 * SINGLE-THREADED, AND THAT IS THE HARD PART. This port creates no threads, and the console resets the
 * session ~15-30 s after heartbeat replies stop. Steps 5-7 involve UDP waits far longer than that, so
 * every blocking wait here either polls the control channel itself or hands takion_channel_connect a
 * tick callback that does. A blocking UDP call anywhere in this file is a bug that presents as the
 * console hanging up mid-bring-up.
 *
 * SCOPE. This stops once the stream keys exist. It does NOT enable GMAC sealing, ack STREAM_INFO, or
 * decode anything - those need the A/V pipeline. It also implements only the senkusha legs the console
 * gates on (PROTOCOL_VERSION and a keyless SESSION exchange), not the echo/MTU measurement probes, which
 * exist to tune bitrate rather than to unlock anything; the launch spec declares a default MTU and rtt 0
 * accordingly. See HARDWARE-PROBES.md.
 */
#include "../crypto/rc_ecdh.h"
#include "../halyard/halyard_v1.h"
#include "../net/rc_soc.h"
#include "../session/halyard_control_session.h"
#include "../session/halyard_launch_spec.h"
#include "../session/halyard_pairing_file.h"
#include "../session/halyard_ctrl_message.h"
#include "../takion/takion_control_proto.h"
#include "../takion/senkusha_echo.h"
#include "../takion/takion_data_chunk.h"
#include "../takion/takion_reliable_channel.h"
#include "../takion/takion_session_negotiator.h"
#include "../media/rc_mvd.h"
#include "../media/rc_audio.h"
#include "../input/halyard_input.h"
#include "../stream/stream_demux.h"
#include "../stream/stream_header.h"
#include "../stream/stream_packet_crypto.h"
#include "../util/rc_base64.h"
#include "../util/rc_log.h"
#include "../util/rc_program_dir.h"
#include "../util/rc_profile.h"
#include "../util/rc_random.h"

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
/*
 * Long enough for the candidate sweep to complete unattended. Five geometries at eight seconds each is
 * forty; sixty leaves room for the console's first keyframe and a little slack at the end.
 */
/*
 * NO FIXED MEDIA WINDOW. This ran for a fixed 60 seconds because it was a probe: a bounded run that
 * printed a report was exactly what was wanted while the question was "does any of this work at all".
 * It now decodes video, plays audio and sends controller input, so the natural end of a session is the
 * user deciding it is over.
 *
 * Every rate in the report is therefore computed against MEASURED elapsed time. Reporting per-second
 * figures against a window the run did not actually fill is how the "receive core 40%" line came to be
 * quoted at 60 fps when the session had died at 29 seconds - the arithmetic was right and the
 * denominator was fiction.
 */
#define CANDIDATE_DWELL_MS        8000u
#define TAKION_HEARTBEAT_MS      1000u
#define CONGESTION_INTERVAL_MS   200u
/* 60 Hz. Integer milliseconds: 16 rather than 16.67, which paces slightly fast and so never starves the
 * display of a frame that is ready. */
#define VBLANK_INTERVAL_MS        16u

/* Control DATA/SACK carry the GMAC tag at offset 5 and the key position at 9 (both [V] against captures:
 * 2528 type-0 packets for the offsets, 727/727 for the AAD rule). Congestion uses 7 and 0x0b. */
#define CONTROL_TAG_OFFSET   5
#define CONTROL_KEYPOS_OFFSET 9
#define CONGESTION_TAG_OFFSET 7
#define CONGESTION_KEYPOS_OFFSET 0x0b

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

/*
 * The stream-plane sender state. One advancing key position is shared by every outgoing sealed packet -
 * control DATA, SACKs and congestion reports alike - and it advances by the packet's 16-byte-aligned
 * length, not by 1. Reusing a position across two packets reuses a GMAC nonce, which is a real forgery
 * problem and produces no visible symptom.
 */
/* 513 KB - file scope is mandatory, not a preference (stream_demux.h). */
static stream_demux g_demux;
static long g_frames;
static long g_keyframes;
static long g_audio_frames;
static long g_loss_events;

/*
 * The decoder. OFF by default and toggled with X, which is not laziness: decode runs on the same thread
 * as the receive loop, so if it costs more than the inter-packet gap it starves the socket and the
 * symptom (packet loss) looks like a network problem rather than a CPU one. Being able to A/B it inside
 * a single session is the only honest way to tell those apart, and Phase 2 already measured that CPU
 * contention on this core costs real UDP throughput.
 */
static rc_mvd g_mvd;
static rc_audio g_audio;
static halyard_input_writer g_input;
static long g_input_state_packets, g_input_history_packets;
static u64 g_last_input_ms;
static u64 g_last_input_poll_ms;

/*
 * State cadence. The .NET writer re-sends a state packet every 200 ms when nothing analog moved; that is
 * OUR policy rather than a wire requirement, and the note there is honest that lengthening it risks a
 * console-side timeout nobody has tested for. Matched rather than re-guessed.
 */
#define INPUT_STATE_INTERVAL_MS 200u

/*
 * INPUT IS POLLED ON A CLOCK, NOT ON THE LOOP.
 *
 * The first version sampled and sent inside the outer loop with no rate limit. That loop iterates at
 * roughly 1 kHz when the socket is quiet, and the "has anything changed" test was a memcmp over a struct
 * holding the RAW circle-pad reading - which jitters by +/-1 continuously. So it was true nearly every
 * iteration and the console was receiving about a thousand state packets a second, against a real
 * DualSense's 250 Hz. On hardware that presented as input arriving delayed, repeated, or in the wrong
 * order, which is what flooding a console's input queue looks like from the far side.
 *
 * 8 ms is 125 Hz: comfortably above the 30-60 fps the picture updates at, well inside what the console
 * expects, and far below the rate at which our own jitter becomes traffic.
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

/* Set by the video callback (which runs deep inside stream_demux_ingest) and consumed by the media loop,
 * because the swap belongs to the loop that owns the frame timing, not to a demux callback. */
/*
 * RAW BITSTREAM DUMP - the test that stops the guessing.
 *
 * Twelve rounds of hardware runs have narrowed the grey field by elimination and every remaining theory
 * is about a component we cannot see inside: does the console actually send a picture, does our demuxer
 * assemble it intact, or does MVD mishandle a good stream? Writing the Annex-B elementary stream to the
 * SD card answers all three at once, because ffmpeg on a PC is a decoder we can trust.
 *
 *   ffprobe -show_frames video.264      # frame types, resolution, whether it parses at all
 *   ffmpeg -i video.264 frame%03d.png   # look at the pictures
 *
 * Capped at 2 MB (~700 frames at the ~2.8 KB/frame this stream runs at). Writes cost SD time on the
 * receive thread, so this is a diagnostic switch, not something to leave on.
 */
static const char *g_argv0;
/*
 * BUFFERED IN RAM, WRITTEN AT THE END - because writing during the session damages what it captures.
 *
 * The first version fwrite()'d each frame as it arrived. SD writes happen on the receive thread, the
 * drain stalls behind them, and that run lost 2168 units - so the captured stream had 332 frame_num
 * discontinuities across 941 pictures. THIRTY-FIVE PERCENT of the pictures referenced a frame that was
 * not in the file. ffmpeg conceals gaps silently, so the capture looked fine on a PC and was used as
 * known-good input for several rounds of decoder debugging.
 *
 * A capture taken to diagnose a decoder must not be corrupted by the act of capturing it.
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
     * worked on real traffic - which is the entire Phase 5.5 layer, previously exercised only against
     * synthetic packets. */
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
 * Ask for a fresh keyframe when a frame is lost.
 *
 * WITHOUT THIS THE PICTURE NEVER RECOVERS, and the first hardware runs showed exactly that: one keyframe
 * per session and then 15-181 loss events, each of which corrupts every following inter-frame that
 * references it. H.264 only resynchronises at an IDR, and the console does not send one unprompted.
 *
 * Rate-limited, though NOT for the reason first assumed. The original note here claimed that requesting
 * more keyframes was itself amplifying the loss, because loss rose sharply once IDR requests were added.
 * A decode-on/decode-off A/B then showed identical loss either way, and the real cause turned out to be
 * the receive loop draining one packet per tick - a keyframe is a burst, and a burst met by a one-packet
 * drain is a guaranteed overflow. The limit stays because asking for a keyframe per loss event is still
 * wasteful on a link with hundreds of them, but it was not the bug.
 *
 * IDR_REQUEST is the blunt instrument; CORRUPT_FRAME (type 5) carries the actual damaged frame range and
 * lets the console decide, which is what the .NET reference sends. Not implemented here yet.
 */
#define IDR_REQUEST_MIN_INTERVAL_MS 500u


static void on_video_loss(void *userdata, int first_frame_index, int last_frame_index)
{
    u64 now_ms = osGetTime();

    (void)userdata;
    g_loss_events++;
    if (g_loss_events <= 3)
        rc_log("  video loss: frames %d..%d\n", first_frame_index, last_frame_index);
    /* Stop feeding the decoder until the IDR we are about to ask for arrives. */
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

static stream_packet_crypto g_send_crypto;
static stream_packet_crypto g_recv_crypto;
static uint64_t g_send_key_pos;
static int g_sealing;

static uint64_t reserve_key_pos(size_t packet_length)
{
    uint64_t reserved = g_send_key_pos;
    size_t remainder = packet_length % 16u;
    size_t aligned = packet_length + ((remainder == 0u) ? 0u : (16u - remainder));

    g_send_key_pos += (uint64_t)aligned;
    return reserved;
}

/* Seals one outgoing control DATA or SACK: key position at offset 9, 4-byte GMAC tag at offset 5, with
 * both the tag and the key-position field zeroed in the AAD (the control/congestion rule; A/V zeroes the
 * tag only). Handed to takion_channel_enable_sealing. */
static void seal_control_packet(void *ctx, uint8_t *packet, size_t length)
{
    uint64_t key_pos;

    (void)ctx;
    if (!g_sealing || length < (size_t)(CONTROL_KEYPOS_OFFSET + 4))
        return;

    key_pos = reserve_key_pos(length);
    packet[CONTROL_KEYPOS_OFFSET + 0] = (uint8_t)(key_pos >> 24);
    packet[CONTROL_KEYPOS_OFFSET + 1] = (uint8_t)(key_pos >> 16);
    packet[CONTROL_KEYPOS_OFFSET + 2] = (uint8_t)(key_pos >> 8);
    packet[CONTROL_KEYPOS_OFFSET + 3] = (uint8_t)key_pos;
    (void)stream_packet_crypto_seal(&g_send_crypto, key_pos, packet, length,
                                    CONTROL_TAG_OFFSET, 1 /* zero_key_pos */);
}

/*
 * The heartbeat pump. Called from the main loop AND from inside takion_channel_connect's wait loops, so
 * that a 6-second UDP handshake does not cost us the control connection. Deliberately does one unit of
 * work and returns.
 */
/*
 * Drain the control channel, up to a bound, rather than one message per call.
 *
 * This did one unit of work and returned - fine while the drain loop was idle, but under a real game the
 * console sends far more control traffic and we fell behind one message at a time. A session that looked
 * healthy in every other respect (5% loss, 1.75 Mbps, 25 fps) still ended with "control channel closed
 * by console" at 70 seconds, which is what falling behind on HEARTBEAT_REQ looks like from this side.
 *
 * Bounded so a flood cannot starve the A/V drain - the same reasoning that caps the packet drain at 64.
 */
#define CONTROL_DRAIN_LIMIT 8

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
             * SAY WHAT THE CHANNEL WAS DOING WHEN IT DIED.
             *
             * "closed by console" on its own has now cost several hardware runs, because it is
             * consistent with two completely different faults: we stopped answering HEARTBEAT_REQ (our
             * problem, a starved loop), or the console stopped asking and hung up for a reason of its
             * own (not our problem, and not fixable by draining harder). Those want opposite responses.
             *
             * The gap since the last heartbeat separates them: a few seconds means the console was still
             * talking to us right up to the close; tens of seconds means we went deaf first.
             */
            rc_log("\x1b[31mFAIL\x1b[0m control channel %s\n",
                event.kind == HALYARD_CONTROL_EVENT_CLOSED ? "closed by console" : "errored");
            /*
             * The gap is the discriminator. Hardware shows ~37-38 s in both observed failures, against a
             * heartbeat that arrives every ~5 s - so the console went quiet long before it hung up. That
             * rules out this loop starving, which three earlier rounds assumed.
             *
             * What it does NOT yet distinguish is whether the console lost interest on its own, or
             * stopped hearing our replies. Hence the failure count: the reply is fire-and-forget through
             * a non-blocking socket that can time out.
             */
            rc_log("       last HEARTBEAT_REQ %lu ms ago; %ld answered, %ld other message(s) seen\n",
                (unsigned long)(g_last_heartbeat_ms ? now - g_last_heartbeat_ms : 0),
                g_heartbeats_answered, g_control_messages_other);
            rc_log("       %ld heartbeat repl%s FAILED to send\n",
                g_control.heartbeat_send_failures,
                g_control.heartbeat_send_failures == 1 ? "y" : "ies");
            /*
             * THE TAKION SIDE, because the TCP channel going quiet may be a symptom rather than the
             * cause. We answer every heartbeat and the console still stops asking, then closes on a
             * fixed ~38 s timer - consistent to within a second across four runs. If the console has
             * already decided the SESSION is dead, the TCP heartbeats stopping is what that looks like
             * from here, and the likeliest place for it to decide that is the reliable channel: control
             * DATA that we never acknowledge, or unacked chunks of ours piling up unanswered.
             *
             * A stalled reliable channel is invisible in every number printed so far, because the A/V
             * path is unreliable and keeps flowing regardless.
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
             * WHAT IS STUCK IN THE BUFFER, because "56 buffered" is the whole story and the bytes name
             * the cause.
             *
             * The control header is 8 bytes with a big-endian payload length at offset 0, so 56 bytes is
             * seven complete zero-payload frames - exactly the ~7 heartbeat requests the console sent
             * during its 37 s of apparent silence. It was never silent: we stopped PARSING, so we stopped
             * replying, and it hung up.
             *
             * For the parser to stall on a complete frame, the length it reads must be implausible -
             * i.e. the buffer is desynced and we are reading a length out of the middle of a message.
             * These bytes say which.
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


/* Waits for a condition while keeping the control channel serviced. Returns 1 if it became true. */
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
     * A RECEIVE CUSHION BIG ENOUGH FOR A KEYFRAME BURST. source/linktest/main.c has set this since its
     * first hardware run; this program never did, and the omission only became expensive once frames got
     * large. A 960x540 keyframe is ~24 KB, which arrives as roughly twenty back-to-back datagrams - if
     * the socket's default buffer is smaller than the burst, the tail is dropped by the stack before any
     * amount of draining can reach it, and no CPU work on this end can recover it.
     *
     * That is consistent with what the profile showed: moving the scale to core 2 took core 0 down to
     * ~47% - the same load that lost 1.8% at 640x360 - and the loss stayed at 16%. Loss that does not
     * respond to CPU is not caused by CPU.
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
 * Waits for a reliable message of `want_type` on `channel`, servicing the control channel meanwhile.
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
 * clientVersion 9, empty strings), then the RTT echo probe.
 *
 * THE ECHO LEG USED TO BE OMITTED HERE, with the note that it "tunes bitrate, it does not unlock
 * anything". Both halves of that were true and the conclusion still cost a great deal: the declared RTT
 * is an input the console uses, and leaving it at 0 means every session negotiates against a figure we
 * never measured. Both MTU legs (spec 6.4's MTU-in and MTU-out) now run too, in the capture's order.
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
     * PROTOCOL_VERSION_REQUEST{supportedVersions=[9]} on channel 0x0015, as a literal.
     *
     * Worth reading the tag arithmetic rather than trusting it: field 31 length-delimited is
     * (31 << 3) | 2 = 250, which is >= 0x80 and therefore a TWO-byte varint tag, 0xFA 0x01 - not the
     * single 0xFA that writing it out by hand produces. That is exactly the sort of thing
     * takion_control_proto.c's varint writer exists to get right, and this literal is checked against it
     * by tests/connect_test.c rather than being taken on faith.
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
     * The RTT echo probe - the measurement leg this port omitted for five phases.
     *
     * Ported from HalyardSenkusha.RunEchoProbeAsync. Ordered exactly as the capture shows and spec 6.4
     * records: after SESSION_REPLY, before DISCONNECT. Arm echo mode, send ten 548-byte pings, time each
     * against its byte-identical reply, disarm.
     *
     * THE MAJORITY RULE IS NOT DEFENSIVE PADDING. cap47 shows a real loss and retry - the first ping went
     * unanswered for ~200 ms, was resent, and eleven were sent for ten echoes. A probe demanding all ten
     * would have failed a perfectly healthy session, so this needs five of ten and takes the MINIMUM of
     * what it gets: a sample can only be inflated by delay, never deflated, so the smallest is the
     * closest to the true path time.
     *
     * Best-effort throughout. A failed probe leaves rtt at 0 and the launch spec falls back to its
     * declared default, which is exactly the behaviour that has shipped until now.
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
     * RESULT - a datagram of that size reaching us intact is what "this MTU works" means, so the
     * contents are never inspected. The console's reply reports what it SENT, which is a different
     * question and not the one being asked.
     *
     * UPSTREAM: CLIENT_MTU_COMMAND{state=true} puts the console into echo mode for one large packet; we
     * send it in the echo format at that size, padded with 0x47 rather than zeros because a zero-filled
     * payload is compressible and a link doing compression would let this pass at a size the path cannot
     * really carry.
     *
     * THE CLOSE IS UNCONDITIONAL. Past the open the console IS in client-MTU mode and has no other way
     * of being cleared, so state=false is sent on every exit path including failure. The .NET side has a
     * comment recording that this was once a plain sequential send which a timeout could unwind past,
     * leaving the console stuck; that is a mistake worth not repeating in a second implementation.
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
 * RESOLUTION PROBE STATE.
 *
 * The console refuses 400x240 outright (DISCONNECT within a second of sealing) and accepts 640x360.
 * Nothing else has been tried, and the answer decides a real architectural question: MVD will not scale,
 * so if the console can be talked into sending something the 3DS screen can display directly, the whole
 * scaling problem disappears. If it only ever accepts its own standard ladder, then scaling on this side
 * is mandatory and the design has to account for it.
 *
 * Note what the first data point already rules out: 400x240 is 25x16 by 15x16, i.e. perfectly
 * macroblock-aligned, and it was still refused. So "must be a multiple of 16" is NOT the constraint, and
 * the candidates below deliberately mix aligned and unaligned sizes rather than assuming it is.
 *
 * `g_probe_width/height` override the launch spec when probing; `g_probe_reported_*` capture what
 * STREAM_INFO said the console actually chose, which is the answer - a console may also silently clamp
 * rather than refuse, and that is a different finding from either accepting or rejecting.
 */
static int g_probing;
static int g_probe_width, g_probe_height;
static int g_probe_reported_width, g_probe_reported_height;
static int g_probe_got_stream_info;

/* The stream half: Takion on 9296, then SESSION_REQUEST/REPLY and the key derivation. */
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
     * 640x360, the bottom standard rung. NOT the screen's 400x240, which was tried and rejected.
     *
     * MVD cannot scale and will only render into a framebuffer, so a screen-sized stream would have been
     * the tidy answer - ask the console for 400x240 and hand the frames straight to the display. The
     * console declines: asking for it produced two DISCONNECT messages within a second of sealing and a
     * closed control channel, twice, reproducibly. A non-standard resolution is not negotiable, whatever
     * the launch spec's ladder scores it.
     *
     * So the mismatch between a 640x360 stream and a 400x240 screen has to be resolved on this side, and
     * the decoder is the wrong place for it - see rc_mvd.c.
     */
    /*
     * THE PAIRING RECORD IS COPIED OUT BEFORE ANYTHING READS IT. It used to be copied thirteen lines
     * BELOW the params assignment, so the launch spec was built from g_stream_width's static
     * initialiser while the record's value landed too late to matter - and the throughput report, which
     * reads the same globals afterwards, saw the later value. The two halves of one ordering bug
     * disagreed with each other: we requested 640x360 and then reported bits/pixel against 960x540.
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

    params.width = g_probing ? g_probe_width : g_stream_width;
    params.height = g_probing ? g_probe_height : g_stream_height;
    params.fps = (rec->fps == 60) ? 60 : 30;
    /* What we ask the console to actually SEND - not RP-StartBitrate. Defaults to 2000 kbps because
     * Phase 2 measured this hardware's link at 2.16% loss at 2 Mbps and much worse above ~5, while the
     * vendor default of 10000 asks for five times what the link was shown to carry. At 10000 the first
     * runs lost a third of all units, which kept the decoder permanently waiting for a keyframe. */
    params.bitrate_kbps = rec->stream_bitrate_kbps;
    /* Confirmed in both directions by senkusha's MTU legs, or the declared default when they could not
     * verify it. Declaring a figure the probe just showed to be too large would be worse than not
     * probing at all; declaring an unverified one is merely the status quo. */
    params.mtu = g_measured_mtu > 0 ? g_measured_mtu : DEFAULT_DECLARED_MTU;
    /* Measured by senkusha's echo probe when it got a majority of echoes; 0 otherwise, which is what
     * the launch spec has always declared. */
    params.rtt_ms = g_measured_rtt_ms;
    params.is_hevc = 0;   /* MVD decodes H.264 only; HEVC must never be requested from this hardware */
    params.is_hdr = 0;

    /*
     * SAY WHAT WE ASKED FOR. The log has never printed the requested resolution, only what STREAM_INFO
     * came back with - so a request the console declined was indistinguishable from a request never
     * made. That cost a run: 960x540 was set as the default, the console answered 640x360, and there was
     * no way to tell from the log whether pairing.txt had overridden the default or the console had
     * simply chosen otherwise.
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
         * Say what happened. The first hardware runs of this program ended here having logged NOTHING
         * past "SESSION_REQUEST ... fragmenting", which made a timeout indistinguishable from a crash
         * and cost a debugging session. A wait that ends without a result must always name itself, and
         * "how many reliable messages arrived at all" is the single most useful thing to know next:
         * zero means the console never accepted the request, non-zero means it was talking to us about
         * something else.
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
 * Everything after the key agreement: sealing on, STREAM_INFO acked, keepalives running, and the
 * demuxer fed real packets for the first time.
 *
 * ORDER MATTERS AND IS NOT NEGOTIABLE. Sealing must be enabled before anything else goes out, because
 * from the console's point of view the session became authenticated the moment it sent SESSION_REPLY.
 * The STREAM_INFO_ACK in particular is sealed - and its SACK is too, which is the documented way to get
 * dropped with "streaminfoack fail" while believing you acked correctly.
 *
 * The client does not REQUEST STREAM_INFO; the console sends it unprompted once the session is sealed,
 * carrying the SPS/PPS parameter sets in resolution[0].videoHeader. Those are NOT in the video stream,
 * so they must be kept and prepended to the first IDR - which is exactly what stream_demux already does
 * with the parameter sets it is given.
 */
/*
 * Scale-and-swap, rate-limited to the display.
 *
 * CALLED FROM INSIDE THE DRAIN LOOP as well as after it, and that is the point. It used to run once per
 * outer iteration, after a drain of up to 64 packets - and a 64-packet drain costs tens of milliseconds
 * on this CPU, so the outer loop only came round a few hundred times a minute. The 16 ms throttle was
 * never the limit: presentation was capped by the drain batch, which is why hardware showed 6.5 swaps a
 * second against a 60 Hz throttle and a decoder producing 53 pictures a second.
 *
 * It stays cheap to call: rc_mvd_present returns immediately unless a new picture has decoded, and the
 * timer check gates it before that. Nothing here blocks - gspWaitForVBlank is still deliberately absent,
 * because stalling the drain is what caused this port's largest loss regression.
 */
static int g_scale_inflight;

static void maybe_present(u64 now_ms)
{
    /*
     * A scale handed to the worker core is collected here rather than waited on: the receive loop must
     * never block, which is the same rule that keeps gspWaitForVBlank out of this path. When the scale
     * runs inline (no spare core) rc_mvd_scale_complete returns 1 straight away and this collapses to
     * the old behaviour.
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

static int run_media(int sock)
{
    uint8_t ack[16];
    size_t ack_len;
    u64 start_ms, elapsed_ms;
    double elapsed_secs;
    u64 last_heartbeat_ms = 0;
    u64 last_congestion_ms = 0;
    int stream_info_seen = 0;
    int video_packets = 0;
    int audio_packets = 0;
    int verify_failures = 0;

    /* 1. Sealing on, before anything else is sent. */
    rc_profile_reset(&g_profile);
    rc_mvd_set_profile(&g_profile);
    stream_packet_crypto_init(&g_send_crypto, g_negotiator.send_aes_key, g_negotiator.send_base_iv);
    stream_packet_crypto_init(&g_recv_crypto, g_negotiator.receive_aes_key, g_negotiator.receive_base_iv);
    g_send_key_pos = 0;
    g_sealing = 1;

    /* The demuxer, wired to the REAL crypto for the first time - every host test of this layer so far
     * has used the passthrough stub, because there were no keys to give it. */
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

    /*
     * When probing resolutions, all we need is STREAM_INFO's answer - the console has already told us
     * what it chose by then. Ack it so the session ends cleanly, and skip the media window entirely so a
     * sweep of six resolutions takes under a minute rather than six.
     */

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
        rc_log("        The keys are agreed, so suspect the SEALING: tag offset 5, key position 9,\n");
        rc_log("        AAD with tag+keypos zeroed, position advancing by 16-byte-aligned length.\n");
        return 0;
    }

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
     * 3. The media window. Keepalives run off the same tick as everything else: a 1 s Takion HEARTBEAT
     * on channel 1 and a 200 ms congestion report, both required for the console to keep sending.
     *
     * This build does NOT decode. It counts what arrives and whether it authenticates, which is the one
     * question worth answering first - the whole stream_demux/FEC layer has 2,808 host cases behind it
     * and has never seen a real packet.
     */
    start_ms = osGetTime();
    /*
     * EVERY BUTTON BELONGS TO THE CONSOLE NOW, so the debug bindings are gone.
     *
     * START used to exit and X used to toggle decode. Both are game buttons today - START is Options,
     * START+SELECT is the PS button, X is Triangle - and a debug key that silently eats input would be a
     * miserable thing to diagnose from the far side of a stream.
     *
     * Exit is HOME, which aptMainLoop already reports: pressing HOME and closing the app ends this loop.
     * That is the 3DS's own convention and it costs no game button. The decode toggle existed to A/B
     * whether decode cost was driving packet loss - a question that was answered (it was not; it was a
     * missing SO_RCVBUF) and is not worth a permanent binding.
     */
    while (aptMainLoop()) {
        uint8_t packet[STREAM_PACKET_CRYPTO_MAX_PACKET];
        ssize_t n;
        u64 now_ms;

        now_ms = osGetTime();

        service_control(NULL);
        if (!g_control_alive)
            break;

        if (now_ms - last_heartbeat_ms >= (u64)TAKION_HEARTBEAT_MS) {
            uint8_t beat[8];
            size_t beat_len = takion_control_build_bare(TAKION_CONTROL_HEARTBEAT, beat, sizeof(beat));
            if (beat_len > 0)
                (void)takion_channel_send(&g_stream_channel, TAKION_CHANNEL_SESSION, beat, beat_len);
            last_heartbeat_ms = now_ms;
        }
        if (now_ms - last_congestion_ms >= (u64)CONGESTION_INTERVAL_MS) {
            /* 15 bytes: [0]=0x05, u16BE received at 3, u16BE lost at 5, tag at 7, key position at 0x0b.
             * Sealed with the same shared counter and the same tag+keypos-zeroed AAD as control. */
            uint8_t congestion[15];
            uint64_t key_pos;

            memset(congestion, 0, sizeof(congestion));
            long window_received = 0, window_lost = 0;

            /*
             * REPORT THE LOSS. This wrote `received` and left `lost` at zero - so every 200 ms we told
             * the console the link was perfect, no matter what was actually arriving.
             *
             * On a static home screen that is harmless: the console sends ~0.9 Mbps and nothing is lost
             * anyway. Under a real game it is the whole problem. Forza pushed roughly 2.5 Mbps, 60,066
             * units were lost against 39,177 received, and the console had no reason to back off because
             * we kept reporting zero. It stayed at a rate the link could not carry until the session
             * died - and each of the 314 IDR requests our own loss detection fired added another 30 KB
             * keyframe burst to a link already drowning.
             *
             * stream_demux has counted both numbers all along; take_packet_stats even RESETS them, so it
             * was built to be drained periodically. Nothing was draining it except the end-of-run report.
             *
             * Worth checking the .NET side for the same gap: x64 and ARM64 fail this benchmark the same
             * way, and three clients failing identically points at something shared rather than at any
             * one of them.
             */
            stream_demux_take_packet_stats(&g_demux, &window_received, &window_lost);
            g_total_received += window_received;
            g_total_lost += window_lost;

            congestion[0] = 0x05;
            congestion[3] = (uint8_t)((unsigned long)window_received >> 8);
            congestion[4] = (uint8_t)window_received;
            congestion[5] = (uint8_t)((unsigned long)window_lost >> 8);
            congestion[6] = (uint8_t)window_lost;
            key_pos = reserve_key_pos(sizeof(congestion));
            congestion[CONGESTION_KEYPOS_OFFSET + 0] = (uint8_t)(key_pos >> 24);
            congestion[CONGESTION_KEYPOS_OFFSET + 1] = (uint8_t)(key_pos >> 16);
            congestion[CONGESTION_KEYPOS_OFFSET + 2] = (uint8_t)(key_pos >> 8);
            congestion[CONGESTION_KEYPOS_OFFSET + 3] = (uint8_t)key_pos;
            (void)stream_packet_crypto_seal(&g_send_crypto, key_pos, congestion, sizeof(congestion),
                                            CONGESTION_TAG_OFFSET, 1);
            sendto(sock, congestion, sizeof(congestion), 0,
                   (struct sockaddr *)&g_stream_channel.peer, sizeof(g_stream_channel.peer));
            last_congestion_ms = now_ms;
        }

        /*
         * CONTROLLER INPUT, up the same socket.
         *
         * Two packets, both sealed exactly like congestion: the shared up-direction key position, written
         * into the header before sealing, and a GMAC that zeroes ONLY the tag. A history packet goes
         * whenever a button transitioned; a state packet on analog movement or every 200 ms otherwise.
         *
         * Sent from the outer loop rather than the drain: input is a wall-clock activity like the
         * heartbeat, and the drain's period depends on load. Two phases of this port have already been
         * spent learning that the hard way.
         */
        if (now_ms - g_last_input_poll_ms >= (u64)INPUT_POLL_INTERVAL_MS) {
            halyard_input_state in;
            circlePosition circle, cstick;
            uint8_t input_packet[HALYARD_INPUT_MAX_PACKET];
            size_t payload_len;
            uint64_t input_key_pos;
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
            if (payload_len > 0) {
                size_t total = HALYARD_INPUT_HEADER_LENGTH + payload_len;

                (void)halyard_input_write_header(0x01, g_input.history_seq++, input_packet, sizeof(input_packet));
                input_key_pos = reserve_key_pos(total);
                input_packet[4] = (uint8_t)(input_key_pos >> 24);
                input_packet[5] = (uint8_t)(input_key_pos >> 16);
                input_packet[6] = (uint8_t)(input_key_pos >> 8);
                input_packet[7] = (uint8_t)input_key_pos;
                /*
                 * ENCRYPT, THEN SEAL - and the first version did neither, which is what made input
                 * unusable.
                 *
                 * stream_packet_crypto_seal only computes the GMAC tag; it does not touch the payload.
                 * Congestion packets get away with that, but spec 6.3 is explicit that feedback payloads
                 * ARE AES-CTR encrypted. So the console was faithfully DECRYPTING our plaintext button
                 * events into noise - which is why one press arrived as D-pad movement, at an
                 * unpredictable time, having passed authentication the whole way.
                 *
                 * Order matters: the tag is computed over the packet AS IT GOES ON THE WIRE, i.e. over
                 * ciphertext. Sealing first would authenticate a payload nobody will ever see.
                 */
                stream_packet_crypto_crypt_payload(&g_send_crypto, input_key_pos,
                                                   input_packet + HALYARD_INPUT_HEADER_LENGTH,
                                                   total - HALYARD_INPUT_HEADER_LENGTH);
                (void)stream_packet_crypto_seal(&g_send_crypto, input_key_pos, input_packet, total, 8, 0);
                sendto(sock, input_packet, total, 0,
                       (struct sockaddr *)&g_stream_channel.peer, sizeof(g_stream_channel.peer));
                g_input_history_packets++;
            }

            if (memcmp(&in, &g_input.previous, sizeof(in)) != 0
                || now_ms - g_last_input_ms >= (u64)INPUT_STATE_INTERVAL_MS) {
                size_t total;

                payload_len = halyard_input_build_state_payload(&in, input_packet + HALYARD_INPUT_HEADER_LENGTH,
                                                                sizeof(input_packet) - HALYARD_INPUT_HEADER_LENGTH);
                total = HALYARD_INPUT_HEADER_LENGTH + payload_len;
                (void)halyard_input_write_header(0x06, g_input.state_seq++, input_packet, sizeof(input_packet));
                input_key_pos = reserve_key_pos(total);
                input_packet[4] = (uint8_t)(input_key_pos >> 24);
                input_packet[5] = (uint8_t)(input_key_pos >> 16);
                input_packet[6] = (uint8_t)(input_key_pos >> 8);
                input_packet[7] = (uint8_t)input_key_pos;
                /* Encrypt then seal - see the history packet above. */
                stream_packet_crypto_crypt_payload(&g_send_crypto, input_key_pos,
                                                   input_packet + HALYARD_INPUT_HEADER_LENGTH,
                                                   total - HALYARD_INPUT_HEADER_LENGTH);
                (void)stream_packet_crypto_seal(&g_send_crypto, input_key_pos, input_packet, total, 8, 0);
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
         * DRAIN UNTIL EMPTY, then sleep - never one packet per tick.
         *
         * This loop previously read a single datagram per 2 ms sleep, capping the receive rate at 500
         * packets/second and, worse, guaranteeing that any burst larger than one packet overflowed the
         * socket buffer. A keyframe is a burst of a dozen or more units, so asking for more keyframes
         * made the loss dramatically worse and looked like an IDR-amplification problem. It was not: the
         * decode-on/decode-off A/B showed identical loss (1799 vs 1743 units), which ruled out CPU cost
         * and pointed here instead.
         *
         * This is the same lesson source/linktest/main.c learned on its own first hardware run, where a
         * drain loop tied to vblank produced a fake ~2 Mbps ceiling. Bounded per tick so the control
         * channel and the button poll still get serviced under a sustained flood.
         */
        int drained = 0;
        while (drained++ < 64
               && (n = recvfrom(sock, packet, sizeof(packet), MSG_PEEK, NULL, NULL)) > 0) {
            unsigned base_type = (unsigned)(packet[0] & 0x0fu);

            if (base_type == 0u) {
                unsigned channel_id;
                const uint8_t *message;
                size_t message_length;
                (void)takion_channel_poll(&g_stream_channel, &channel_id, &message, &message_length);
            } else {
                n = recvfrom(sock, packet, sizeof(packet), 0, NULL, NULL);
                if (n > 0 && (size_t)n >= STREAM_HEADER_LENGTH) {
                    /*
                     * Authenticate it. This is the question the whole stream layer has been waiting to
                     * be asked: the A/V rule differs from control's - the key position travels IN the
                     * packet (u32 BE at offset 14) rather than being ours to choose, and only the TAG
                     * region is zeroed in the AAD, not the key position. Getting either wrong makes
                     * every packet fail, which is a far better outcome than silently decrypting noise.
                     */
                    uint64_t key_pos =
                        ((uint64_t)packet[STREAM_HEADER_KEY_POSITION_OFFSET + 0] << 24) |
                        ((uint64_t)packet[STREAM_HEADER_KEY_POSITION_OFFSET + 1] << 16) |
                        ((uint64_t)packet[STREAM_HEADER_KEY_POSITION_OFFSET + 2] << 8) |
                        ((uint64_t)packet[STREAM_HEADER_KEY_POSITION_OFFSET + 3]);
                    /*
                     * SAMPLED, not every packet. stream_demux_ingest below already verifies every
                     * packet through its crypto seam (packet_crypto_open_packet) and drops what fails -
                     * so verifying here too was computing GMAC over every byte of every packet TWICE,
                     * about 10,000 redundant times a minute at ~1,100 bytes each. That is real ARM11
                     * time on a CPU this port has already shown to be the binding constraint.
                     *
                     * The check is kept, sampled, because it is the only thing that distinguishes link
                     * corruption from a crypto fault - and one in sixteen is plenty to spot a
                     * systematic failure, which would hit every packet rather than one in a few
                     * thousand. The count below is scaled accordingly.
                     */
                    int verified = 1;

                    if ((video_packets + audio_packets) % 16 == 0) {
                        uint64_t t = rc_profile_start();
                        verified = stream_packet_crypto_verify(&g_recv_crypto, key_pos, packet,
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
                         * Log the first few individually. A bare count cannot distinguish the two
                         * hypotheses that matter: a systematic crypto error fails deterministically or
                         * in bulk, whereas a corrupted datagram on a 2.4 GHz link is isolated and
                         * unrepeatable. On 2026-08-12 exactly one packet in 3,879 failed on one run and
                         * none in the 11,448 that followed - consistent with the latter, but a count
                         * alone could not say so, and the next occurrence should not be that ambiguous.
                         * The rotation window is printed because a fault at a window boundary would be
                         * the one shape that IS a crypto bug.
                         */
                        if (verify_failures <= 4) {
                            rc_log("\x1b[33mGMAC fail #%d\x1b[0m base %u, %u bytes, keypos %u "
                                   "(rotation window %u)\n", verify_failures, base_type, (unsigned)n,
                                   (unsigned)key_pos, (unsigned)(key_pos / 16u / 45000u));
                        }
                    }

                    if (video_packets + audio_packets == 1) {
                        rc_log("\x1b[32mFIRST A/V PACKET\x1b[0m base type %u, %u bytes, keypos %u, "
                               "GMAC %s\n", base_type, (unsigned)n, (unsigned)key_pos,
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
                     * THE CONTROL CHANNEL IS SERVICED FROM INSIDE THE DRAIN TOO, and leaving it out
                     * cost two sessions.
                     *
                     * service_control ran once per OUTER iteration, and an outer iteration drains up to
                     * 64 packets. At 60 fps that is ~20 frames of decode and scale - comfortably 200 ms
                     * of work between two chances to answer a HEARTBEAT_REQ. This file's own header
                     * records what the console does about that: "session ~15-30 s after heartbeat
                     * replies stop". Both 60 fps runs went black at ~29 s with "control channel closed
                     * by console", and the picture froze because the media loop breaks out on that.
                     *
                     * It is the same shape as the frame-pacing bug fixed a few phases ago: work that
                     * must happen on a wall clock, gated behind a loop whose period depends on load.
                     * A TCP recv on an empty socket is cheap, so 1-in-16 is frequent enough to be safe
                     * and rare enough not to matter.
                     */
                    if ((drained & 15) == 0) {
                        service_control(NULL);
                        if (!g_control_alive)
                            break;
                    }

                    /*
                     * EVERY FOURTH PACKET, NOT EVERY SIXTEENTH - this interval is the frame-rate cap.
                     *
                     * At ~290 packets/s a 1-in-16 check runs about 18 times a second, and since the
                     * threaded scale needs one call to start and another to collect, that put the
                     * ceiling near 9 fps and explained why 30 fps never felt like 30. The decoder was
                     * producing 1,661 pictures and only 1,236 were ever shown. The check itself is a
                     * clock comparison and a TryWait, so running it four times as often costs nothing
                     * next to the demux it sits beside.
                     */
                    if ((drained & 3) == 0)
                        maybe_present(osGetTime());
                }
            }
        }
        /*
         * Present.
         *
         * gfxFlushBuffers() IS THE PART THAT WAS MISSING, and its absence is why decode succeeded - 16
         * pictures, zero render errors - against a blank screen. The frame is written into the
         * framebuffer by rc_mvd's ARM11 blit, i.e. by software, and libctru documents this flush as
         * required for exactly that case: without it the pixels sit in the data cache and the display
         * controller never sees them. The devkitPro example needs no flush because MVD writes the
         * framebuffer itself, through the GPU's view of memory - the moment this port stopped doing that
         * and started scaling on the CPU, it inherited the software-rendering rule and did not notice.
         *
         * (gfxSwapBuffersGpu and gfxSwapBuffers are the same function in current libctru - "formerly
         * different" per gfx.h - so the choice between them was never the issue.)
         *
         * Only when a picture actually landed: swapping at loop rate would flicker the last frame
         * against whatever is in the other buffer.
         */
        /*
         * Present. gfxFlushBuffers is required again: rc_mvd scales the decoded frame into the
         * framebuffer with the CPU, and libctru documents the flush as necessary for exactly that
         * software-rendering case - without it the pixels sit in the data cache and the display
         * controller never sees them.
         */
        /*
         * FRAME PACING: present on the display's clock, not the decoder's.
         *
         * Frames were previously swapped the instant a decode completed, at whatever rate they happened
         * to arrive. Two problems with that, and only one is cosmetic: a swap mid-scanout tears, and -
         * more importantly on this hardware - swapping more often than the display refreshes is work
         * thrown away, and this port has already measured itself at ~80% of one core.
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
        /* Per-candidate, not a session total: rc_mvd_next_candidate resets this so each geometry is
         * judged on its own. Earlier logs reported "0 picture(s) rendered" under a log full of
         * FIRST DECODED PICTURE lines purely because the last candidate had just been reset. */
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
    /*
     * MEASURED elapsed time, not a constant. Every per-second figure below divides by this. Quoting rates
     * against a window the session did not fill is exactly how "receive core 40%" got reported for a
     * 60 fps run that had actually died at 29 seconds - right arithmetic, fictional denominator.
     */
    elapsed_ms = osGetTime() - start_ms;
    if (elapsed_ms == 0)
        elapsed_ms = 1;
    elapsed_secs = (double)elapsed_ms / 1000.0;
    rc_log("\nsession ran %.1f s\n", elapsed_secs);

    rc_profile_report(&g_profile, (unsigned)elapsed_ms);
    {
        /*
         * The RUNNING totals, not another drain. take_packet_stats resets as it reads, and the congestion
         * reporter now drains it every 200 ms - so reading it again here would show only the residual
         * since the last report rather than the session.
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
         * WHAT THE CONSOLE ACTUALLY SENT - the number this whole quality investigation turned on and
         * the one the log has never printed. Every round so far it had to be reconstructed by hand from
         * unit counts, which is how it went unexamined while resolution, scalers and threading were
         * tuned around it.
         *
         * DO NOT READ bits/pixel AS A LEGIBILITY THRESHOLD. A comment here used to claim that small
         * text needs roughly 0.2-0.5 bits/pixel, which was invented rather than measured, printed in
         * the log as though established, and used to argue for several rounds that the console was
         * starving the stream. It was disproved by decoding a capture on a PC: at 0.10 bits/pixel the
         * PS5 home screen renders with every label legible. Static UI compresses far better than that
         * rule of thumb assumed.
         *
         * The figure is still worth printing as a description of what arrived. It is not a verdict.
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
             * THE RESOLUTION HERE IS THE ONE THAT ARRIVED. It used to be the one requested, which is a
             * different number whenever the console declines - and it declines silently. The figure was
             * being divided by 960x540 while 640x360 was on the wire, understating bits/pixel by 2.25x.
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
                   "the nonce derivation, or the A/V AAD rule (tag zeroed, key position NOT).\n",
                   verify_failures);
    }
    if (video_packets == 0 && audio_packets == 0) {
        rc_log("\x1b[31mFAIL\x1b[0m the console acked our STREAM_INFO_ACK and sent no media.\n");
        rc_log("        Suspect the keepalives (1 s heartbeat, 200 ms congestion) or their sealing.\n");
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
 * it actually chose. See the probe-state comment at the top for why this question matters.
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
     * TOP SCREEN IS RGB565 AND BELONGS TO MVD; the console goes on the bottom.
     *
     * Both halves matter. MVD emits 16-bit BGR565, and gfxInitDefault() gives the top screen a 24-bit
     * BGR8 framebuffer - a format mismatch the block will not write into. And consoleInit(GFX_TOP) hands
     * that same framebuffer to the text console, so the render target was owned by printf. On the first
     * hardware runs this produced 0 pictures with 15 render failures per session and ZERO process
     * errors: the bitstream decoded perfectly every time and had nowhere to go. Matches the devkitPro
     * MVD example, which does exactly this and is the only proven-working reference for the block.
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
