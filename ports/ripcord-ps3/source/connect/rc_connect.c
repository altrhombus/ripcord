/* See rc_connect.h. Nothing in this file logs or returns anything from the pairing record. */
#include "rc_connect.h"

#include <sys/thread.h>
#include <sys/mutex.h>
#include <sys/cond.h>
#include <lv2/thread.h>

#include "halyard_discovery.h"
#include "halyard_wake.h"
#include "halyard_pairing_file.h"
#include "halyard_control_session.h"
#include "takion_reliable_channel.h"
#include "rc_udp.h"
#include "rc_ecdh.h"
#include "rc_stack_ps3.h"
#include "takion_control_sealer.h"
#include "stream_header.h"
#include "stream_demux.h"
#include "senkusha_echo.h"
#include "rc_decode_probe.h"
#include "rc_decode_vdec.h"
#include "rc_audio_ps3.h"
#include "rc_spu_yuv.h"
#include "rc_video_ps3.h"
#include "rc_pad_ps3.h"
#include "rc_session_state.h"
#include "rc_status_screen.h"
#include "halyard_input.h"
#include "rc_overlay.h"
#include "rc_sysfont.h"
#include <stdio.h>
#include "rc_build_id.h"
#include "takion_control_proto.h"
#include "takion_session_negotiator.h"
#include "takion_data_chunk.h"
#include "halyard_launch_spec.h"
#include "rc_base64.h"
#include "rc_random.h"
#include "halyard_control_arm.h"
#include "halyard_ctrl_message.h"
#include "platform/rc_platform.h"

#include <fcntl.h>
#include <string.h>
#include <unistd.h>
#include <sys/select.h>
#include <sys/time.h>

#include <net/net.h>
#include <net/socket.h>
#include <netinet/in.h>
#include <arpa/inet.h>

/*
 * A DIRECTORY, NOT A FILENAME, and the distinction is the whole point of this comment.
 *
 * halyard_pairing_file_load takes a path, runs rc_program_dir over it to get the directory part, and
 * then appends the fixed name "pairing.txt" - the same shape rc_log_open has, because on the 3DS both
 * are handed argv[0] and asked to find something beside the executable. A path ending in '/' names
 * itself as its own directory, which is what makes this work.
 *
 * An earlier revision defined this as "/dev_hdd0/ripcord-pairing.txt" and called it a PATH. The loader
 * would have taken the directory part and looked for /dev_hdd0/pairing.txt, found nothing, and the
 * probe would have reported "no pairing record - skipping" with the record sitting on the console. The
 * constant's own name was the misleading part; it is a directory and now says so.
 */
#ifndef RC_CONNECT_PAIRING_DIR
#define RC_CONNECT_PAIRING_DIR "/dev_hdd0/"
#endif

/* sin_len is set because PSL1GHT's samples do. It is NOT required - rc_discover.c asked the console
 * directly and a zeroed field was accepted - so this is consistency, not necessity. */
static void fill_addr(struct sockaddr_in *a, const char *ip, unsigned short port)
{
    memset(a, 0, sizeof(*a));
    a->sin_len = (uint8_t)sizeof(*a);
    a->sin_family = AF_INET;
    a->sin_port = htons(port);
    if (ip == NULL)
        a->sin_addr.s_addr = INADDR_ANY;
    else
        (void)inet_pton(AF_INET, ip, &a->sin_addr);
}

/*
 * One SRCH to a known address, unicast. Discovery broadcasts to find anything; here the address is
 * already known from the record, so asking it directly avoids depending on broadcast reaching it and
 * avoids waking the question of which console replied.
 */
/*
 * Broadcast SRCH, used only to tell two failures apart: a console that is off, and a console that has
 * moved since the record was written. A DHCP lease outliving a pairing record is ordinary - this
 * project's own PS3 changed address twice in a week - and the symptom is identical to an absent console
 * unless something asks the wider question.
 *
 * `found_addr` receives the address that answered, so the caller can compare it with the record's
 * WITHOUT either of them being logged.
 */
static int broadcast_find(char *found_addr, size_t addr_size, unsigned timeout_ms)
{
    const halyard_discovery_profile *profile = &halyard_discovery_profile_ps5;
    char probe[128];
    size_t probe_len;
    struct sockaddr_in local, bcast;
    int sock, on = 1, got = 0;
    uint64_t deadline;

    probe_len = halyard_discovery_build_probe(profile, probe, sizeof(probe));
    if (probe_len == 0u)
        return 0;

    sock = socket(PF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (sock < 0)
        return 0;
    if (setsockopt(sock, SOL_SOCKET, SO_BROADCAST, &on, (socklen_t)sizeof(on)) < 0) {
        (void)close(sock);
        return 0;
    }

    fill_addr(&local, NULL, 0);
    (void)bind(sock, (struct sockaddr *)&local, (socklen_t)sizeof(local));

    fill_addr(&bcast, "255.255.255.255", profile->port);
    if (sendto(sock, probe, probe_len, 0, (struct sockaddr *)&bcast, (socklen_t)sizeof(bcast)) < 0) {
        (void)close(sock);
        return 0;
    }

    deadline = rc_time_ms() + (uint64_t)timeout_ms;
    while (rc_time_ms() < deadline) {
        char buf[1024];
        struct sockaddr_in from;
        socklen_t from_len = (socklen_t)sizeof(from);
        halyard_discovered_console found;
        ssize_t n;

        memset(&from, 0, sizeof(from));
        n = recvfrom(sock, buf, sizeof(buf) - 1u, MSG_DONTWAIT, (struct sockaddr *)&from, &from_len);
        if (n <= 0) {
            rc_sleep_ms(20u);
            continue;
        }
        buf[n] = '\0';
        if (halyard_discovery_parse_response(buf, (size_t)n, NULL, &found)) {
            if (inet_ntop(AF_INET, &from.sin_addr, found_addr, (socklen_t)addr_size) != NULL)
                got = 1;
            break;
        }
    }

    (void)close(sock);
    return got;
}

static int probe_once(const char *host, unsigned short src_port, int *is_awake, unsigned timeout_ms)
{
    const halyard_discovery_profile *profile = &halyard_discovery_profile_ps5;
    char probe[128];
    size_t probe_len;
    struct sockaddr_in local, peer;
    int sock;
    uint64_t deadline;
    int got = 0;

    probe_len = halyard_discovery_build_probe(profile, probe, sizeof(probe));
    if (probe_len == 0u)
        return 0;

    sock = socket(PF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (sock < 0)
        return 0;

    /* The spec names a source port for the post-wake poll. Bind it where possible and fall back to
     * ephemeral rather than fail - halyard_discovery.h: "a wake from the wrong port is more likely to
     * work than no wake at all", and the same reasoning applies to the poll that follows it. */
    fill_addr(&local, NULL, src_port);
    if (bind(sock, (struct sockaddr *)&local, (socklen_t)sizeof(local)) < 0) {
        fill_addr(&local, NULL, 0);
        (void)bind(sock, (struct sockaddr *)&local, (socklen_t)sizeof(local));
    }

    fill_addr(&peer, host, profile->port);
    if (sendto(sock, probe, probe_len, 0, (struct sockaddr *)&peer, (socklen_t)sizeof(peer)) < 0) {
        (void)close(sock);
        return 0;
    }

    deadline = rc_time_ms() + (uint64_t)timeout_ms;
    while (rc_time_ms() < deadline) {
        char buf[1024];
        halyard_discovered_console found;
        ssize_t n = recvfrom(sock, buf, sizeof(buf) - 1u, MSG_DONTWAIT, NULL, NULL);

        if (n <= 0) {
            rc_sleep_ms(20u);
            continue;
        }
        buf[n] = '\0';
        if (halyard_discovery_parse_response(buf, (size_t)n, NULL, &found)) {
            *is_awake = found.is_awake;
            got = 1;
            break;
        }
    }

    (void)close(sock);
    return got;
}

static int send_wakeup(const halyard_pairing_record *rec, rc_connect_result *out)
{
    const halyard_discovery_profile *profile = &halyard_discovery_profile_ps5;
    char credential[HALYARD_WAKE_CREDENTIAL_MAX];
    char payload[256];
    size_t payload_len;
    struct sockaddr_in local, peer;
    int sock;
    int sent;

    if (!halyard_wake_credential(rec->registkey, rec->registkey_length,
                                 credential, sizeof(credential)))
        return 0;
    out->credential_ok = 1;

    payload_len = halyard_wake_build_payload(profile, credential, payload, sizeof(payload));
    if (payload_len == 0u)
        return 0;

    sock = socket(PF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (sock < 0)
        return 0;

    /* A sleeping console may only honour a wake that originates on the vendor's own source port. */
    fill_addr(&local, NULL, profile->wake_source_port);
    if (bind(sock, (struct sockaddr *)&local, (socklen_t)sizeof(local)) == 0) {
        out->wake_source_bound = 1;
    } else {
        fill_addr(&local, NULL, 0);
        (void)bind(sock, (struct sockaddr *)&local, (socklen_t)sizeof(local));
    }

    fill_addr(&peer, rec->host, profile->port);
    sent = (sendto(sock, payload, payload_len, 0,
                   (struct sockaddr *)&peer, (socklen_t)sizeof(peer)) >= 0);

    /* Nothing ever acknowledges a WAKEUP. Closing immediately is correct; waiting here would wait
     * forever, which halyard_wake.h warns about explicitly. */
    (void)close(sock);
    return sent;
}

/*
 * A TCP CONNECT WITH A DEADLINE, which ports/common's does not have.
 *
 * rc_tcp_connect calls connect() on a BLOCKING socket and only sets O_NONBLOCK afterwards, so a console
 * that does not accept on the control port leaves halyard_control_session_open stuck inside it with no
 * way out. That is what locked the console on b31. The core is otherwise careful - it polls everywhere
 * and uses timed windows - and this one call predates that discipline.
 *
 * Rather than change a file every port shares on the strength of one hardware run, this pre-flights the
 * same port from the port layer: non-blocking connect, select with a deadline, then close. If it does
 * not accept, open() is never entered and the probe reports why instead of hanging. Fixing rc_tcp.c
 * itself is the better long-term answer and belongs with evidence from more than one platform.
 */
static int tcp_port_accepts(const char *host, unsigned short port, unsigned timeout_ms)
{
    struct sockaddr_in addr;
    struct timeval tv;
    fd_set wr;
    int sock;
    int ok = 0;

    sock = socket(PF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (sock < 0)
        return 0;

    fill_addr(&addr, host, port);
    (void)fcntl(sock, F_SETFL, O_NONBLOCK);

    if (connect(sock, (struct sockaddr *)&addr, (socklen_t)sizeof(addr)) == 0) {
        ok = 1;
    } else {
        FD_ZERO(&wr);
        /* FD_SET's macros convert the descriptor through the fd_set mask type, which this port's
         * -Wconversion objects to inside the SDK's own header. The conversion is theirs. */
#pragma GCC diagnostic push
#pragma GCC diagnostic ignored "-Wsign-conversion"
#pragma GCC diagnostic ignored "-Wconversion"
        FD_SET(sock, &wr);
#pragma GCC diagnostic pop
        tv.tv_sec = (long)(timeout_ms / 1000u);
        tv.tv_usec = (long)((timeout_ms % 1000u) * 1000u);

        if (select(sock + 1, NULL, &wr, NULL, &tv) > 0) {
            int err = 0;
            socklen_t len = (socklen_t)sizeof(err);
            if (getsockopt(sock, SOL_SOCKET, SO_ERROR, &err, &len) == 0 && err == 0)
                ok = 1;
        }
    }

    (void)close(sock);
    return ok;
}

const char *rc_connect_stage_name(rc_connect_stage stage)
{
    switch (stage) {
    case RC_CONNECT_NO_RECORD:     return "no pairing record - skipped";
    case RC_CONNECT_BAD_RECORD:    return "the pairing record is unusable";
    case RC_CONNECT_NO_CONSOLE:    return "no console answered at the recorded address";
    case RC_CONNECT_WAKE_SENT:     return "WAKEUP sent, but it did not wake in time";
    case RC_CONNECT_AWAKE:         return "awake";
    case RC_CONNECT_SESSION_OPEN:  return "control session open";
    case RC_CONNECT_SESSION_READY: return "SESSION_ID seen - willing to stream";
    case RC_CONNECT_SENKUSHA_UP:   return "senkusha channel up, stream channel did not follow";
    case RC_CONNECT_TAKION_UP:     return "Takion established on the stream channel";
    case RC_CONNECT_STREAM_KEYS:   return "stream keys derived - the session is negotiated";
    case RC_CONNECT_STREAM_READY:  return "sealed, and the console has described the stream";
    default:                       return "unknown";
    }
}

#define SAY(text) do { if (log != NULL) log(text); } while (0)

/*
 * TAKION, and the two UDP ports it needs, in the order the console requires them.
 *
 * Senkusha (9297) first, because the console gates on it: it will not answer the stream channel's
 * SESSION exchange unless a senkusha bring-up has happened. The stream's own Takion handshake is 9296 -
 * a different port, and confusing the two is the obvious mistake to make.
 */
#define RC_SENKUSHA_PORT      9297u
#define RC_STREAM_PORT        9296u
#define RC_SENKUSHA_ATTEMPTS  10u
#define RC_SENKUSHA_ATTEMPT_MS 300u
#define RC_STREAM_ATTEMPTS    20u
#define RC_STREAM_ATTEMPT_MS  300u

/*
 * The receive cushion, which is not about this probe at all - the handshake's datagrams are tiny. It is
 * set now because the socket this opens is the one the A/V stream will later arrive on, and a keyframe
 * arrives as a burst of ~20 back-to-back datagrams that the stack drops the tail of if the buffer is
 * smaller than the burst.
 */
/*
 * The receive cushion. Raised from 256 KB after real content showed 12.9% unit loss where a static
 * screen showed none: a keyframe under motion is ~50 KB arriving back-to-back, IDR requests make those
 * frequent, and anything the socket cannot hold while the PPE is inside an 18.9 ms decode is gone before
 * any amount of draining can reach it. Loss that does not respond to CPU is not caused by CPU.
 */
#define RC_UDP_RCVBUF (1024 * 1024)

/*
 * Static rather than automatic, and for the same reason rc_log's format buffer is: these are large, the
 * main thread's stack is finite, and rc_connect is already deep in the call graph by the time it gets
 * here. The 3DS port reached the same conclusion for the same structures.
 *
 * Single-threaded, so no lock. If a second thread ever drives Takion, this needs revisiting rather than
 * copying.
 */
static takion_reliable_channel g_senkusha_channel;
static takion_reliable_channel g_stream_channel;

/*
 * THE HEARTBEAT PUMP, and the reason this whole flow is shaped around a callback.
 *
 * The console resets the session 15-30 s after heartbeat replies stop, and the Takion handshakes below
 * wait far longer than that. Single-threaded, so nothing services the control channel unless this does -
 * takion_channel_connect_ticked calls it roughly every 20 ms while it waits.
 *
 * halyard_control_session_service answers HEARTBEAT_REQ internally, so this deliberately does nothing
 * with the event: the point is the call, not the result. It must not block, and it does not.
 */
static void service_control_tick(void *ctx)
{
    halyard_control_session *session = (halyard_control_session *)ctx;
    halyard_control_event ev;

    if (session == NULL)
        return;
    memset(&ev, 0, sizeof(ev));
    (void)halyard_control_session_service(session, &ev);
}

/* Brings up one Takion channel. Returns 1 if the four-way handshake completed. */
static int takion_bring_up(takion_reliable_channel *channel, const char *host, unsigned port,
                           unsigned attempts, unsigned attempt_ms,
                           halyard_control_session *session, int *out_sock)
{
    struct sockaddr_in peer;
    int sock = rc_udp_open(host, port, &peer, RC_UDP_RCVBUF);

    *out_sock = -1;
    if (sock < 0)
        return 0;

    memset(channel, 0, sizeof(*channel));
    if (!takion_channel_connect_ticked(channel, sock, peer, attempts, attempt_ms,
                                       service_control_tick, session)) {
        (void)close(sock);
        return 0;
    }
    *out_sock = sock;
    return 1;
}

/* Reported rather than assumed - see rc_udp_rcvbuf_actual. */
static int g_stream_rcvbuf;

/*
 * How long to wait for each control reply on a Takion channel. The console answers these promptly when it
 * answers at all; the generous figure is for the reply that has to cross a fragmented request.
 */
#define RC_TAKION_REPLY_MS 5000u

/*
 * How long to wait for STREAM_INFO after sealing comes on. Longer than a control reply because this is
 * not an answer to anything we sent - the console produces it when its encoder is ready, and a figure
 * sized for a round trip would report a console that is merely still setting up as one that never spoke.
 */
#define RC_STREAM_INFO_WAIT_MS 10000u

/*
 * How long to hold the negotiated session open, watching. Long enough that the console's own cadence
 * shows through - it heartbeats and re-sends on its own timers - and short enough that a bring-up probe
 * still finishes. Not a streaming duration; a sampling one.
 */
/*
 * Long enough to WATCH, not just to measure.
 *
 * Six seconds produced ~146 good frames and a clean set of numbers, and on a television it read as a
 * glimpse. The measurement was never the constraint here - the console streams for as long as it is
 * asked to, and the cost of asking for longer is only that the probe takes longer.
 */
#define RC_STREAM_HOLD_MS 30000u

/* What the hold ACTUALLY ran for - the constant above is only the default when the pairing record does
 * not say. Reported rather than assumed, because every rate in the summary divides by it. */
static unsigned g_hold_ms = RC_STREAM_HOLD_MS;

/*
 * WHAT THE PERSON IN FRONT OF THE TELEVISION IS TOLD.
 *
 * Kept here because this file is the one that knows why things stopped. Every phase change is drawn
 * immediately rather than queued: these are rare, and the whole point of them is to appear at the
 * moment the thing they describe happens.
 */
static rc_session_state g_session;

static void say(rc_phase phase, const char *headline, const char *detail, const char *hint)
{
    unsigned before = g_session.revision;

    rc_session_set(&g_session, phase, headline, detail, hint);
    if (g_session.revision != before && rc_status_screen_wants_draw(&g_session))
        rc_status_screen_draw(&g_session);
}

const rc_session_state *rc_connect_session_state(void)
{
    return &g_session;
}

/*
 * THE SAME OUTCOME, FOR SOMEONE WHO IS NOT READING A LOG.
 *
 * rc_connect_stage_name says what happened and is written for whoever is debugging this. That is the
 * wrong register for a television: "senkusha channel up, stream channel did not follow" is precise and
 * tells a viewer nothing they can act on.
 *
 * So each stage also gets a headline, a detail and - the part that matters - a HINT. Where a stage has
 * no useful hint that is said plainly rather than filled with advice that sounds helpful and is not;
 * "try again" on a fault nobody can influence is worse than silence, because it implies the fault is
 * the viewer's to fix.
 */
void rc_connect_report_outcome(rc_connect_stage stage, int stalled)
{
    if (stalled)
        return;             /* the stall already said something more specific than any of this */

    switch (stage) {
    case RC_CONNECT_NO_RECORD:
        say(RC_PHASE_FAILED, "Not paired with a console",
            "No pairing record was found",
            "Pair with the console first");
        break;
    case RC_CONNECT_BAD_RECORD:
        say(RC_PHASE_FAILED, "The pairing is unusable",
            "The record is present but malformed",
            "Pair with the console again");
        break;
    case RC_CONNECT_NO_CONSOLE:
        say(RC_PHASE_FAILED, "The console did not answer",
            "Nothing replied at the recorded address",
            "Check the console is on the same network and its address has not changed");
        break;
    case RC_CONNECT_WAKE_SENT:
        say(RC_PHASE_FAILED, "The console did not wake",
            "A wake request was sent and went unanswered",
            "Turn the console on, or enable waking from rest mode in its settings");
        break;
    case RC_CONNECT_AWAKE:
    case RC_CONNECT_SESSION_OPEN:
        say(RC_PHASE_FAILED, "The console refused the session",
            "It answered, then would not start Remote Play",
            "Check Remote Play is enabled on the console and no one else is using it");
        break;
    case RC_CONNECT_SESSION_READY:
    case RC_CONNECT_SENKUSHA_UP:
    case RC_CONNECT_TAKION_UP:
    case RC_CONNECT_STREAM_KEYS:
        say(RC_PHASE_FAILED, "The stream did not start",
            "The session was agreed but no video followed",
            "Try again; if it repeats, restart the console's Remote Play");
        break;
    case RC_CONNECT_STREAM_READY:
        say(RC_PHASE_ENDED, "Session ended", NULL, NULL);
        break;
    default:
        say(RC_PHASE_FAILED, "Something went wrong", rc_connect_stage_name(stage), NULL);
        break;
    }
}


/* Set once the parameter sets are classified - see where it is assigned for why this is a drop rather
 * than a disconnection. */
static int g_stream_is_hevc;

/*
 * Input cadences, and the two are answering different questions.
 *
 * THE POLL is 4 ms - 250 Hz - which is deliberately FASTER than any pad reports. A gate of G ms adds
 * G/2 of latency on average and G at worst, entirely on top of whatever the pad and the network cost,
 * and it buys nothing once it is quicker than the source: a poll that finds nothing new is two
 * syscalls. Sampling above the pad's rate means the only delay left is the pad's own, which is the
 * definition of as fast as this can go. It is cheap to make it faster and it is not cheap to notice
 * that it was not.
 *
 * THE KEEPALIVE is 200 ms and matches HalyardInputPacketWriter.cs. It is not a latency figure: a state
 * packet goes out the moment anything actually moves, and this only covers a controller that is
 * perfectly still, so the console keeps hearing that it is still there.
 */
/*
 * How long without a picture counts as the stream having stopped. Generous enough that a slow keyframe
 * or a congested second cannot trip it - the frame queue is eight deep and a bad second still delivers
 * something - and short enough that a viewer is told rather than left looking at a still frame.
 */
#define RC_STREAM_STALL_MS          4000u

#define RC_INPUT_POLL_INTERVAL_MS   4u
#define RC_INPUT_STATE_INTERVAL_MS  200u

static halyard_input_writer g_input;
/* When the last picture reached the screen. A stream that stops does not close the socket, so this is
 * the only thing that notices - see the stall check in the hold loop. */
static uint64_t g_last_picture_ms;

static uint64_t g_next_input_poll;
static uint64_t g_last_input_state_ms;
static unsigned g_chord_edges_seen;
static int g_stream_channel_ready;

/* Defined below, between its two callers - the YUV and the packed-RGB present paths. */
static void draw_overlay(void);

/*
 * What the overlay reports that is not already a live counter: the asks, which are settled once when
 * the stream opens, and the clock the rates are measured against. Copied here rather than read from
 * rc_connect_result because that struct is filled at the END of a session and the overlay runs during
 * one.
 */
static uint64_t g_overlay_t0;

/*
 * A THIRTY-SECOND WINDOW, because a running total stops being information.
 *
 * The first overlay showed cumulative frames, bytes and audio frames since the session began. Twenty
 * seconds in, every one of those is a large number that changes slowly and says nothing about now -
 * and "now" is the entire reason to put numbers on the screen rather than in the log. The .NET client
 * reports its live figures as `latest / peak` over the last thirty seconds and this matches it, so a
 * fault described against one client reads the same way on the other.
 *
 * One bucket a second, thirty of them, oldest overwritten. The totals are kept too: cumulative is the
 * right shape for FAULTS - an IDR request or an overrun that happened and stopped mattering is still
 * something you want to know happened - and the wrong shape for rates.
 */
#define RC_OVERLAY_WINDOW 30u

typedef struct {
    unsigned frames;        /* blitted in this second   */
    unsigned long bytes;    /* video bytes in this second */
    unsigned lost;          /* units lost in this second  */
} rc_overlay_bucket;

static rc_overlay_bucket g_win[RC_OVERLAY_WINDOW];
static unsigned g_win_slot;
static uint64_t g_win_next;
static unsigned long g_win_last_bytes;
static unsigned long g_win_last_lost;

/* Rolls the window on if a second has passed. Called from the present path, so it must be cheap and
 * must not care about being called at an irregular rate. */
static void overlay_tick(void)
{
    uint64_t now = rc_time_ms();

    if (g_win_next == 0u) {
        g_win_next = now + 1000u;
        return;
    }
    while (now >= g_win_next) {
        g_win_slot = (g_win_slot + 1u) % RC_OVERLAY_WINDOW;
        g_win[g_win_slot].frames = 0u;
        g_win[g_win_slot].bytes = 0ul;
        g_win[g_win_slot].lost = 0u;
        g_win_next += 1000u;
    }
}

/* The most recently COMPLETED second, not the one in progress - a bucket still filling always reads
 * low, and an fps that dips every time you look at it is worse than no fps at all. */
static const rc_overlay_bucket *overlay_latest(void)
{
    return &g_win[(g_win_slot + RC_OVERLAY_WINDOW - 1u) % RC_OVERLAY_WINDOW];
}
static int g_overlay_asked_w, g_overlay_asked_h, g_overlay_asked_fps, g_overlay_asked_kbps;
static int g_overlay_hw_scale;
static unsigned long g_overlay_units_lost;

/* Declared with the overlay's other state because draw_overlay reads it and sits above where the IDR
 * logic defines the rest of its own. */
static unsigned g_idr_requests;

/* The client's own heartbeat cadence on the stream channel, from the reference's one second. */
#define RC_STREAM_HEARTBEAT_MS 1000u

/*
 * How often the console is told what arrived. The reference's interval, and it is the rate controller's
 * sampling period rather than an arbitrary tick - reporting less often gives it less to adapt to.
 */
#define RC_CONGESTION_INTERVAL_MS 200u

/*
 * How often a CONNECTION_QUALITY report may go out, and how far it steps the ask down.
 *
 * Two seconds because this is not congestion control - the console already gets loss every 200 ms. It is
 * a statement about what this CLIENT can consume, which changes slowly and only when the console changes
 * how it slices. A step rather than a jump so the console can settle: overshooting downward costs
 * picture quality nobody asked to lose.
 */
#define RC_CONNQUALITY_INTERVAL_MS 2000u
#define RC_CONNQUALITY_STEP_PERCENT 75u
#define RC_CONNQUALITY_FLOOR_KBPS 4000u

/* See the CONNECTION_QUALITY block in send_periodic for why this policy lives in the port. */
static int g_connquality_enabled;
static unsigned g_connquality_target;
static uint64_t g_next_connquality;


/* The reference's own throttle. One IDR repairs the entire reference chain, so asking again while the
 * answer is still in flight buys nothing and costs upstream bandwidth. */
#define RC_IDR_REQUEST_MIN_MS 200u

/* RC_AV_DRAIN_BURST now lives in rc_connect.h, so the run's report cannot disagree with it. */

/* PROTOCOL_VERSION_ACK's message type. Named because a bare 32 in a comparison says nothing. */
#define RC_TAKION_PROTOCOL_VERSION_ACK 32u

/*
 * The declared MTU, which is a protocol figure rather than the interface's. Senkusha's MTU legs can
 * confirm it; this port does not run them yet, so it declares the default the launch spec has always
 * used. Declaring an unverified figure is the status quo; declaring one a probe had just disproved
 * would be worse than not probing.
 */
#define RC_DECLARED_MTU 1454

/*
 * How long to wait for one echo, and for an MTU-sized datagram.
 *
 * The echo window is short on purpose: a reply that takes 80 ms on a LAN is not a slow round trip worth
 * recording, it is a lost ping, and waiting longer would let one bad sample stretch the whole probe. The
 * MTU window is long because the console has to prepare and send a full-sized datagram, which is a
 * different kind of wait. Both figures are ports/ripcord-3ds'.
 */
#define RC_SENKUSHA_ECHO_TIMEOUT_MS 80u
#define RC_SENKUSHA_MTU_TIMEOUT_MS 600u

/*
 * WHOLE-PROBE BACKSTOPS, from the reference. Ten pings on a LAN take a few milliseconds and ten misses
 * cost under a second, so these are not targets - they bound the case where something is wrong in a way
 * the per-reply timeouts do not catch, and this port has already lost runs to a loop with an inner bound
 * and no outer one.
 */
#define RC_SENKUSHA_ECHO_BUDGET_MS 3000u
#define RC_SENKUSHA_MTU_BUDGET_MS 3000u

/* Measured by the legs below; 0 until they succeed, which is what the launch spec falls back on. */
static int g_measured_rtt_ms;
static int g_measured_mtu;

/* The PROTOCOL_VERSION round trip, used as a fallback RTT sample - see senkusha_echo_probe. */
static int g_version_rtt_ms;

/*
 * THE RTT PROBE. Ten pings, each timed against its own echo, and the MINIMUM taken.
 *
 * The minimum rather than the mean, because a sample can only be inflated by delay and never deflated:
 * the smallest round trip observed is the closest thing to the true path time, and an average would
 * report the queue rather than the link.
 *
 * A MAJORITY IS ENOUGH, and that is not defensive padding. The 3DS port's captures show a real loss and
 * retry - one ping unanswered for ~200 ms, eleven sent for ten echoes - so a probe demanding all ten
 * would fail a perfectly healthy session. Five of ten and the result stands.
 *
 * Each echo is matched to ITS OWN ping by sequence. Crediting a late echo of an earlier ping to a later
 * one would report a round trip shorter than it was, which is the one direction this must not be wrong in.
 */
static void senkusha_echo_probe(halyard_control_session *session, rc_connect_result *out)
{
    static uint8_t ping[SENKUSHA_ECHO_PAYLOAD + 64];
    static uint8_t rx[SENKUSHA_ECHO_PAYLOAD + 64];
    uint8_t payload[256];
    unsigned samples = 0u;
    uint64_t best_ms = 0u;
    uint64_t budget_end = rc_time_ms() + RC_SENKUSHA_ECHO_BUDGET_MS;
    size_t n;
    uint8_t seq;

    /*
     * SEEDED WITH THE VERSION HANDSHAKE, which the reference calls the purest sample available: answering
     * a version request asks the console to do essentially no work, so it is nearly all path time. Since
     * the result is a MINIMUM, a sample that is slow can only be ignored and never inflate the figure -
     * which is what makes including a handshake round trip safe at all.
     *
     * It is a FALLBACK, not a substitute. The reference's own documentation warns that a non-null RTT
     * therefore does not mean the echo probe succeeded; this port reports the echo count separately so
     * that ambiguity does not exist here.
     */
    if (g_version_rtt_ms > 0) {
        best_ms = (uint64_t)g_version_rtt_ms;
        out->handshake_rtt_ms = g_version_rtt_ms;
    }

    /* Arm echo mode. */
    n = takion_control_build_echo_command(1, payload, sizeof(payload));
    if (n == 0u || !takion_channel_send(&g_senkusha_channel, TAKION_CHANNEL_BANDWIDTH, payload, n))
        return;

    for (seq = 0u; seq < (uint8_t)SENKUSHA_PING_COUNT; seq++) {
        uint64_t sent_ms = rc_time_ms();
        uint64_t deadline;

        if (sent_ms > budget_end)
            break;   /* the backstop - see RC_SENKUSHA_ECHO_BUDGET_MS */

        n = senkusha_echo_build(seq, sent_ms * 1000u, SENKUSHA_ECHO_PAYLOAD, 0x00u,
                                ping, sizeof(ping));
        if (n == 0u)
            break;
        if (sendto(g_senkusha_channel.sock, ping, n, 0,
                   (struct sockaddr *)&g_senkusha_channel.peer,
                   sizeof(g_senkusha_channel.peer)) < 0)
            continue;

        deadline = sent_ms + RC_SENKUSHA_ECHO_TIMEOUT_MS;
        while (rc_time_ms() < deadline) {
            uint8_t got;
            ssize_t r;

            service_control_tick(session);
            r = recvfrom(g_senkusha_channel.sock, rx, sizeof(rx), 0, NULL, NULL);
            if (r > 0 && senkusha_echo_is_echo(rx, (size_t)r, &got) && got == seq) {
                uint64_t rtt = rc_time_ms() - sent_ms;

                if (best_ms == 0u || rtt < best_ms)
                    best_ms = rtt;
                samples++;
                break;
            }
            if (r <= 0)
                rc_sleep_ms(1u);
        }
    }

    /* Disarm, whatever happened. */
    n = takion_control_build_echo_command(0, payload, sizeof(payload));
    if (n > 0u)
        (void)takion_channel_send(&g_senkusha_channel, TAKION_CHANNEL_BANDWIDTH, payload, n);

    out->echo_samples = samples;

    /*
     * A MAJORITY OF ECHOES, or fall back to the handshake sample. The reference's captures show a real
     * loss and retry - one ping unanswered for ~200 ms, eleven sent for ten echoes - so demanding all ten
     * would fail a healthy session.
     */
    if (samples * 2u >= SENKUSHA_PING_COUNT) {
        g_measured_rtt_ms = (int)best_ms;
        out->rtt_from_echo = 1;
    } else if (g_version_rtt_ms > 0) {
        g_measured_rtt_ms = g_version_rtt_ms;
        out->rtt_from_echo = 0;
    }
    out->measured_rtt_ms = g_measured_rtt_ms;
}

/*
 * THE MTU LEGS, downstream then upstream, in the capture's order.
 *
 * DOWNSTREAM: ask the console for one datagram of the candidate size. ITS ARRIVAL IS THE ENTIRE RESULT -
 * a datagram that size reaching us intact is what "this MTU works" means, so the contents are never
 * inspected. The console's reply reports what it SENT, which is a different question.
 *
 * UPSTREAM: put the console into echo mode for one large packet and send it, padded with 0x47 rather
 * than zeros. A zero-filled payload is trivially compressible, and a link doing compression would let
 * this pass at a size the path cannot actually carry.
 *
 * THE CLOSE IS UNCONDITIONAL. Past the open the console IS in client-MTU mode with no other way of being
 * cleared, so state=false goes out on every exit path including failure. The .NET side records that this
 * was once a plain sequential send a timeout could unwind past, leaving the console stuck - a mistake
 * worth not repeating in a third implementation.
 *
 * Only the value we intend to DECLARE is tested. The vendor probes a smaller size upstream than
 * downstream, but it has reason to know its own uplink and this port does not, so verifying the figure
 * that will go in the launch spec is more useful than reproducing theirs.
 */
static void senkusha_mtu_probe(halyard_control_session *session, rc_connect_result *out)
{
    static uint8_t probe[1600];
    static uint8_t rx[1600];
    uint8_t payload[256];
    size_t probe_len = (size_t)RC_DECLARED_MTU - SENKUSHA_IP_UDP_OVERHEAD;
    uint64_t budget_end = rc_time_ms() + RC_SENKUSHA_MTU_BUDGET_MS;
    uint64_t deadline;
    size_t n;
    int downstream = 0;
    int upstream = 0;

    n = takion_control_build_mtu_command(1u, (uint32_t)RC_DECLARED_MTU, 1u, payload, sizeof(payload));
    if (n > 0u && takion_channel_send(&g_senkusha_channel, TAKION_CHANNEL_BANDWIDTH, payload, n)) {
        deadline = rc_time_ms() + RC_SENKUSHA_MTU_TIMEOUT_MS;
        if (deadline > budget_end)
            deadline = budget_end;
        while (rc_time_ms() < deadline && !downstream) {
            ssize_t r;

            service_control_tick(session);
            r = recvfrom(g_senkusha_channel.sock, rx, sizeof(rx), 0, NULL, NULL);
            if (r > 0 && (rx[0] & 0x0fu) == 0x02u)
                downstream = 1;     /* base type 2 - the console's MTU datagram, arrived intact */
            else if (r <= 0)
                rc_sleep_ms(1u);
        }
    }

    if (downstream) {
        n = takion_control_build_client_mtu_command(1u, (uint32_t)RC_DECLARED_MTU, 1,
                                                    payload, sizeof(payload));
        if (n > 0u && takion_channel_send(&g_senkusha_channel, TAKION_CHANNEL_BANDWIDTH, payload, n)) {
            size_t m = senkusha_echo_build(0u, rc_time_ms() * 1000u, probe_len, 0x47u,
                                           probe, sizeof(probe));

            if (m > 0u && sendto(g_senkusha_channel.sock, probe, m, 0,
                                 (struct sockaddr *)&g_senkusha_channel.peer,
                                 sizeof(g_senkusha_channel.peer)) >= 0) {
                deadline = rc_time_ms() + RC_SENKUSHA_MTU_TIMEOUT_MS;
                if (deadline > budget_end)
                    deadline = budget_end;
                while (rc_time_ms() < deadline && !upstream) {
                    uint8_t got;
                    ssize_t r;

                    service_control_tick(session);
                    r = recvfrom(g_senkusha_channel.sock, rx, sizeof(rx), 0, NULL, NULL);
                    if (r > 0 && senkusha_echo_is_echo(rx, (size_t)r, &got) && got == 0u)
                        upstream = 1;
                    else if (r <= 0)
                        rc_sleep_ms(1u);
                }
            }
        }

        /* Unconditional - see above. */
        n = takion_control_build_client_mtu_command(2u, (uint32_t)RC_DECLARED_MTU, 0,
                                                    payload, sizeof(payload));
        if (n > 0u)
            (void)takion_channel_send(&g_senkusha_channel, TAKION_CHANNEL_BANDWIDTH, payload, n);
    }

    out->mtu_downstream = downstream;
    out->mtu_upstream = upstream;
    if (downstream && upstream) {
        g_measured_mtu = RC_DECLARED_MTU;
        out->measured_mtu = g_measured_mtu;
    }
}

/*
 * Static for the frame-size reason again, and here it is not a nicety: the launch spec is 2 KB, its
 * base64 form is another 2.7 KB, and the SESSION_REQUEST built from them is larger still. The port
 * compiles with -Wframe-larger-than=8192 and these three together would breach it on their own.
 */
static char g_launch_spec[HALYARD_LAUNCH_SPEC_MAX];
static char g_launch_spec_b64[HALYARD_LAUNCH_SPEC_B64_MAX];
static uint8_t g_session_request[HALYARD_LAUNCH_SPEC_B64_MAX + 512];
static takion_session_negotiator g_negotiator;
static takion_control_sealer g_sealer;
static takion_control_verifier g_verifier;

/*
 * The demuxer, and a tally of what comes out of it. Static for the same frame-size reason as everything
 * else here: its assembly buffer alone is FEC_MAX_TOTAL_UNITS * 4 KB, which is orders of magnitude past
 * this port's 8 KB frame limit.
 */
static stream_demux g_demux;

typedef struct {
    unsigned video_frames;
    unsigned keyframes;
    unsigned audio_frames;
    unsigned long video_bytes;
    unsigned long audio_bytes;
    unsigned corrupt_events;
    unsigned largest_frame;
} rc_demux_tally;

static rc_demux_tally g_tally;

static rc_decode_live_stats g_live_stats;
static int g_live_open;

/*
 * A FRAME QUEUE, so that decoding does not happen inside the receive loop.
 *
 * b128 measured what the receive buffer actually is: 124800 bytes, where 1048576 was asked for and lv2
 * capped it. That is about half a second of this stream on average and HALF OF ONE KEYFRAME at the
 * 57 KB they now reach - so the average was never the problem, the stalls were.
 *
 * Decoding used to happen in the demuxer's callback, inside the drain loop. A pass that completed five
 * frames stopped reading the socket for five decodes - around 133 ms - and at 232 KB/s that is 31 KB
 * arriving with nowhere to go, on top of whatever burst provoked it. The loss looked like the network
 * twice over: first because poll was eating packets, and then because the buffer was overrunning while
 * the PPE was busy.
 *
 * So the sink COPIES and returns, and the outer loop decodes one frame per pass. The copy is a few
 * microseconds against a 22 ms decode, and the longest the socket goes unread becomes one decode rather
 * than however many frames happened to complete together.
 *
 * Sized in whole frames rather than bytes: eight is enough to ride out a keyframe followed by a burst of
 * inter-frames, and a slot has to hold the largest frame seen with room to spare.
 */
#define RC_FRAME_QUEUE_SLOTS 8
/*
 * A WHOLE FRAME, SIZED FOR 1080p. 96 KB was comfortable for 720p, whose largest frame measured 51 KB -
 * but a 1080p keyframe carries 2.25x the pixels and there is no headroom in that. The demuxer can
 * assemble about a megabyte (STREAM_DEMUX_ASSEMBLY_CAPACITY), so this was the first thing that would
 * have clipped, and it would have clipped SILENTLY: the guard below simply skipped anything larger.
 */
#define RC_FRAME_SLOT_BYTES (256 * 1024)

static uint8_t g_frame_queue[RC_FRAME_QUEUE_SLOTS][RC_FRAME_SLOT_BYTES];
static size_t g_frame_length[RC_FRAME_QUEUE_SLOTS];
static int g_frame_head;
static int g_frame_count;
static unsigned g_frames_queued;
static unsigned g_frames_overrun;
static unsigned g_frames_oversized;
static unsigned g_queue_worst;

/*
 * THE DECODER RUNS ON ITS OWN THREAD, AND THE REASON IS b142.
 *
 * Decoding inline in the receive loop was a deliberate choice once, and the note above the queue records
 * why: doing the work where the buffer is valid avoids a copy at every layer. The crypto rewrite changed
 * the arithmetic underneath that choice. With ingest down to 84 us per packet, decode is the only large
 * cost left - 21,774 us per call at 720p - and b142 measured the consequence directly: a 270 ms decode
 * run, a 94 ms gap between socket reads, a drain burst of 118 packets where b141 saw 6, and 157 units
 * lost to a 124,800-byte socket buffer that nobody was emptying. The frame queue sat pinned at 8 of 8.
 *
 * None of that is the decoder being slow. It is the decoder and the socket sharing a thread.
 *
 * THREE CHOICES HERE ARE DELIBERATE:
 *
 * The consumer COPIES the frame out of the ring while holding the lock, then decodes outside it. The
 * alternative - decoding in place - lets the producer evict and overwrite the slot mid-decode once the
 * ring wraps. The copy averages ~8 KB against a 21,774 us decode, which is not a cost worth a race.
 *
 * The wait has a TIMEOUT rather than being infinite. The thread therefore notices the quit flag by
 * itself within 50 ms and cannot be wedged by a signal that arrives at the wrong moment. This port has
 * already lost three sessions to shutdown lockups; a teardown path that depends on a wakeup being
 * delivered is not one to write a fourth time.
 *
 * The priority is READ from the receive thread rather than assumed, and set one step below it. Draining
 * the socket promptly is the whole point of the change, so the thread that does it must win. A hard-coded
 * number would be a guess about a scheduler this port has never measured.
 */
#define RC_DECODE_THREAD_STACK (256u * 1024u)
#define RC_DECODE_WAIT_US      50000ull

static sys_ppu_thread_t g_decode_thread;
static sys_mutex_t g_decode_mutex;
static sys_cond_t g_decode_cond;
static int g_decode_thread_up;
static volatile int g_decode_quit;
static int g_decode_priority;
static int g_decode_receive_priority;
static uint8_t g_decode_buf[RC_FRAME_SLOT_BYTES];

/*
 * The picture sink: a decoded frame goes straight onto the screen.
 *
 * In the decoder's callback, which is inside the demuxer's callback, which is inside the receive loop.
 * Deep, and deliberately so - every layer's contract says its buffer is valid for the call only, so
 * doing the work here is what avoids a copy at each level. The cost of that decision is that a slow
 * blit stalls the receive loop, which is why it is measured rather than assumed.
 */
/* Longer than a 60 Hz vsync with margin, short enough that a stalled display cannot wedge the decode
 * thread. A picture still waiting past this is stale anyway - the next one is already decoded. */
#define RC_BLIT_WAIT_MS 25u
static unsigned g_blit_worst_wait_ms;
static unsigned g_pictures_dropped;
static unsigned g_rgb_scale_failed;
static int g_decoder_rgb;
static unsigned g_blit_us_total;
static unsigned g_blit_worst_us;
static unsigned g_blits;

/*
 * WE ARE BLIND UNTIL A KEYFRAME ARRIVES, and this remembers that. Defined here, above both the periodic
 * loop and the loss callback, because both of them need it - see the fuller note at on_video_loss.
 */
static const char *g_decode_backend = "none";
static int g_decode_backend_id;
static int g_awaiting_keyframe;
static void request_idr(uint64_t now);

/*
 * The two things this client owes the console on a timer, checked wherever there is a moment.
 *
 * They used to sit only at the top of the hold loop, which made the drain bound a compromise: large
 * enough to clear a burst, small enough that one burst could not starve a heartbeat. Calling this from
 * inside the drain as well costs two comparisons per packet and removes the tension, so the bound can be
 * sized for the burst alone.
 */
/*
 * CONTROLLER INPUT, UP THE STREAM CHANNEL.
 *
 * Two packets and two cadences. A HISTORY packet (type 1) goes whenever a button transitioned, and
 * carries the last several transitions rather than only the new one - they are CUMULATIVE and the
 * console tracks button state from them, so a dropped packet would otherwise lose a transition
 * permanently and leave a button held forever. A STATE packet (type 6) is the analog snapshot, sent
 * when anything moved or every 200 ms regardless, because the console wants to keep hearing from a
 * controller that is merely still.
 *
 * SENT FROM THE PERIODIC TICK, NOT THE DRAIN, and ripcord-3ds spent two phases learning why: input is
 * a wall-clock activity like the heartbeat, while the drain's period depends on how much video is
 * arriving. Tying the poll to the drain makes the controller laggy exactly when the picture is busy,
 * which is exactly when it is being used.
 *
 * The sealer does the encryption and the tag together - see takion_control_sealer_seal_input for why
 * those cannot be separated, and for why input must share the sealer's key position rather than count
 * its own.
 */
static void send_input(rc_connect_result *out)
{
    halyard_input_state in;
    uint8_t packet[HALYARD_INPUT_MAX_PACKET];
    size_t payload_len;
    uint64_t now;

    if (!g_stream_channel_ready)
        return;
    if (!rc_pad_read(&in)) {
        /*
         * Nothing sent when no pad is connected. Neutral is a position - sticks centred, nothing
         * pressed - and telling the console that repeatedly is a different statement from saying
         * nothing, which is what an absent controller means.
         */
        return;
    }

    /*
     * The chord is watched for a CHANGE in its completion count rather than for a held state, so a long
     * hold toggles once and this loop does not have to track when a press stops being new.
     */
    {
        unsigned edges = rc_pad_chord_edges();

        if (edges != g_chord_edges_seen) {
            g_chord_edges_seen = edges;
            rc_overlay_show(!rc_overlay_shown());
            out->overlay_toggles++;
        }
    }

    now = rc_time_ms();

    payload_len = halyard_input_build_history_payload(&g_input, &in,
                                                      packet + HALYARD_INPUT_HEADER_LENGTH,
                                                      sizeof(packet) - HALYARD_INPUT_HEADER_LENGTH);
    if (payload_len > 0u) {
        size_t total = HALYARD_INPUT_HEADER_LENGTH + payload_len;

        (void)halyard_input_write_header(0x01u, g_input.history_seq++, packet, sizeof(packet));
        takion_control_sealer_seal_input(&g_sealer, packet, total, HALYARD_INPUT_HEADER_LENGTH);
        if (sendto(g_stream_channel.sock, packet, total, 0,
                   (struct sockaddr *)&g_stream_channel.peer,
                   sizeof(g_stream_channel.peer)) >= 0)
            out->input_history_packets++;
    }

    if (memcmp(&in, &g_input.previous, sizeof(in)) != 0
        || now - g_last_input_state_ms >= RC_INPUT_STATE_INTERVAL_MS) {
        size_t total;

        payload_len = halyard_input_build_state_payload(&in, packet + HALYARD_INPUT_HEADER_LENGTH,
                                                        sizeof(packet) - HALYARD_INPUT_HEADER_LENGTH);
        total = HALYARD_INPUT_HEADER_LENGTH + payload_len;
        (void)halyard_input_write_header(0x06u, g_input.state_seq++, packet, sizeof(packet));
        takion_control_sealer_seal_input(&g_sealer, packet, total, HALYARD_INPUT_HEADER_LENGTH);
        if (sendto(g_stream_channel.sock, packet, total, 0,
                   (struct sockaddr *)&g_stream_channel.peer,
                   sizeof(g_stream_channel.peer)) >= 0)
            out->input_state_packets++;
        g_last_input_state_ms = now;
    }

    /* Updated once both payloads are built, not between them: the history diff and the state's
     * change test are both against the same previous frame. */
    g_input.previous = in;
    g_input.have_previous = 1;
}

static void send_periodic(halyard_control_session *session, rc_connect_result *out,
                          uint64_t *next_heartbeat, uint64_t *next_congestion)
{
    uint64_t now = rc_time_ms();

    (void)session;

    if (now >= g_next_input_poll) {
        send_input(out);
        g_next_input_poll = now + RC_INPUT_POLL_INTERVAL_MS;
    }

    /*
     * CONGESTION FEEDBACK: what arrived and what did not. The console's rate controller adapts to it,
     * and the counts come from the demuxer, which has been computing them all along.
     * stream_demux_take_packet_stats RESETS on read, so each report covers its own window.
     */
    if (out->demux_ready && now >= *next_congestion) {
        long got = 0, missed = 0;
        uint8_t feedback[TAKION_CONGESTION_PACKET_SIZE];
        size_t fn;

        stream_demux_take_packet_stats(&g_demux, &got, &missed);
        out->units_received += got;
        out->units_lost += missed;
        g_overlay_units_lost = (unsigned long)out->units_lost;

        fn = takion_congestion_build((unsigned long)(got < 0 ? 0 : got),
                                     (unsigned long)(missed < 0 ? 0 : missed),
                                     feedback, sizeof(feedback));
        if (fn > 0u) {
            takion_control_sealer_seal_congestion(&g_sealer, feedback, fn);
            if (sendto(g_stream_channel.sock, feedback, fn, 0,
                       (struct sockaddr *)&g_stream_channel.peer,
                       sizeof(g_stream_channel.peer)) >= 0)
                out->congestion_sent++;
        }
        *next_congestion = now + RC_CONGESTION_INTERVAL_MS;
    }

    /*
     * ASK FOR LESS WHEN THE PICTURES ARE TOO FINELY SLICED FOR THIS DECODER.
     *
     * The protocol half of this lives in ports/common; the POLICY is here, because what triggers it is a
     * property of cellVdec and not of Halyard. 65 slices in a 720p picture decode on this hardware and
     * 136 do not, and the console slices so that each network unit stands alone - so slice count follows
     * the bitrate it is sending. Another port would throttle for entirely different reasons, or not at
     * all, which is exactly why this does not belong in the shared layer.
     *
     * Congestion feedback cannot express this. It reports what was lost, and nothing is being lost: the
     * link is fine and the decoder simply cannot use what arrives. That gap is what CONNECTION_QUALITY
     * is for.
     */
    if (g_connquality_enabled && out->demux_ready && now >= g_next_connquality) {
        unsigned slices = rc_decode_vdec_au_max_slices();

        if (slices >= RC_VDEC_SLICES_WARN && g_connquality_target > RC_CONNQUALITY_FLOOR_KBPS) {
            uint8_t msg[48];
            size_t mn;

            g_connquality_target = (g_connquality_target * RC_CONNQUALITY_STEP_PERCENT) / 100u;
            if (g_connquality_target < RC_CONNQUALITY_FLOOR_KBPS)
                g_connquality_target = RC_CONNQUALITY_FLOOR_KBPS;

            mn = takion_control_build_connection_quality(g_connquality_target,
                                                         (double)out->measured_rtt_ms,
                                                         0.0, msg, sizeof(msg));
            if (mn > 0u
                && takion_channel_send(&g_stream_channel, TAKION_CHANNEL_SESSION, msg, mn)) {
                out->connquality_sent++;
                out->connquality_target = (int)g_connquality_target;

                /*
                 * NO KEYFRAME REQUEST HERE, and b177 is why it was tried and removed.
                 *
                 * The reasoning was sound - a stream that has changed shape needs a fresh reference -
                 * and the implementation was not. Setting g_awaiting_keyframe hands the flag to the
                 * "still blind? ask again" loop above, which repeats every 200 ms until a keyframe
                 * arrives; eight throttle steps became 79 IDR requests and 16 keyframes in thirty
                 * seconds. Asking for a keyframe once is a reasonable thing to want, and this flag is
                 * not the way to ask for it once.
                 */
            }
        }
        g_next_connquality = now + RC_CONNQUALITY_INTERVAL_MS;
    }

    /*
     * A FRAME THE DECODER NEVER SAW BREAKS THE CHAIN, exactly as a lost packet does.
     *
     * b180 at 720p60 decoded 1,208 pictures of 1,779 with 489 errors and FOUR units lost on the network -
     * so nearly all of it was frames this port dropped before the decoder, its access-unit ring being a
     * 30 fps size. Every one of those breaks the reference chain, and nothing asked for a keyframe: three
     * keyframes in thirty seconds, one IDR request, and a picture that went blocky and stayed blocky.
     *
     * This is what g_awaiting_keyframe is for, and it is worth distinguishing from b177's misuse of it.
     * There the flag was set on every bitrate step, which is not a broken chain and produced 79 requests.
     * Here a frame really is missing, which is the condition the flag names.
     */
    if (rc_decode_vdec_take_chain_broken())
        g_awaiting_keyframe = 1;

    /*
     * STILL BLIND? ASK AGAIN. See g_awaiting_keyframe: the request is a demand for the thing that
     * repairs the stream, not an acknowledgement that something broke, so one unanswered request is a
     * reason to repeat it rather than to wait for the next loss.
     */
    if (g_awaiting_keyframe)
        request_idr(now);

    /*
     * The stream channel's heartbeat, which is ours to SEND rather than to answer - the console's own
     * need no reply. Easy to get backwards: on the control session the console asks and we reply.
     */
    if (now >= *next_heartbeat) {
        uint8_t beat[8];
        size_t beat_len = takion_control_build_bare(TAKION_CONTROL_HEARTBEAT, beat, sizeof(beat));

        if (beat_len > 0u
            && takion_channel_send(&g_stream_channel, TAKION_CHANNEL_SESSION, beat, beat_len))
            out->heartbeats_sent++;
        *next_heartbeat = now + RC_STREAM_HEARTBEAT_MS;
    }
}

/*
 * Takes whatever A/V is waiting, up to the bound, and feeds it to the demuxer. Returns how many
 * datagrams it took.
 *
 * A FUNCTION because it has to be called from more than one place. The receive loop calls it, and so
 * does the gap between decodes - one decode is 19 to 23 ms and the socket holds 122 KB, so going more
 * than one decode without reading is how b130 put loss back to 6.6% after b129 had it at 1.3%.
 */
/*
 * THE LONGEST THE SOCKET GOES UNREAD, measured rather than reasoned about.
 *
 * Four builds have now moved work across this boundary on the theory that some unit of it was too long
 * to sit between two reads - decode inside the drain, then one decode per pass, then four with only the
 * timers between, then four with a drain between each. Loss went 11.9%, 1.3%, 6.6%, 8.6%. That is not a
 * sequence converging on an answer, it is a sequence of guesses, and the thing every one of them was
 * guessing at is a number this loop can simply report.
 *
 * A 122 KB buffer at ~300 KB/s is full in about 400 ms. If the worst gap is nowhere near that, the
 * overflow theory is wrong and the loss is somewhere else entirely.
 */
static uint64_t g_last_drain_ms;
static unsigned g_worst_read_gap_ms;

/*
 * WHICH PHASE OWNS THE GAP. b136 measured the longest interval between socket reads at 372 ms against a
 * buffer that fills in about 400 - so the overflow is real and sitting on the edge. What it does not say
 * is what spends that time, and the arithmetic does not obviously account for it: a bounded drain is
 * tens of milliseconds, four decodes are sixty, and the blit is four.
 *
 * Something in this loop costs far more than its parts suggest, and guessing which has already cost four
 * builds. Each phase is timed and its worst reported.
 */
static unsigned g_worst_drain_ms;
static unsigned g_worst_decode_ms;
static unsigned g_worst_other_ms;
static uint64_t g_ingest_ticks;
static unsigned g_ingest_calls;

/*
 * SPLIT THE 829 MICROSECONDS. b139 measured stream_demux_ingest at 829 us per packet, which at 3.2 GHz
 * is about 1900 cycles for every byte of a 1400-byte datagram. A naive software AES is 50 to 100 cycles
 * per byte, so the cipher cannot be most of this - and removing an entire redundant GMAC pass in b138
 * moved the drain by a tenth, which said the same thing.
 *
 * The port supplies the demuxer's crypto seam, so it can wrap it: this callback does exactly what
 * stream_demux_packet_crypto's does and times it. Whatever is left of the 829 is header parsing, FEC
 * bookkeeping and the assembly copy - and one of those is then the thing to attack.
 *
 * Wrapping rather than editing ports/common, because this is a question about this platform's
 * performance and not a change to how the demuxer works.
 */
static uint64_t g_crypto_ticks;
static unsigned g_crypto_calls;

static int timed_open_packet(void *ctx, uint8_t *packet, size_t packet_length,
                             uint32_t key_position, int payload_offset)
{
    stream_packet_crypto *crypto = (stream_packet_crypto *)ctx;
    uint64_t t0 = rc_tick();
    int ok;

    if (!stream_packet_crypto_verify(crypto, key_position, packet, packet_length,
                                     STREAM_HEADER_TAG_OFFSET, 0)) {
        ok = 0;
    } else {
        stream_packet_crypto_crypt_payload(crypto, key_position, packet + payload_offset,
                                           packet_length - (size_t)payload_offset);
        ok = 1;
    }

    g_crypto_ticks += rc_tick() - t0;
    g_crypto_calls++;
    return ok;
}

static int drain_av(halyard_control_session *session, rc_connect_result *out,
                    uint64_t *next_heartbeat, uint64_t *next_congestion)
{
    int drained;

    uint64_t drain_started;

    {
        uint64_t now = rc_time_ms();

        if (g_last_drain_ms != 0u) {
            uint64_t gap = now - g_last_drain_ms;

            if (gap > (uint64_t)g_worst_read_gap_ms)
                g_worst_read_gap_ms = (unsigned)gap;
        }
        drain_started = now;
    }

    drained = 0;
    while (drained < RC_AV_DRAIN_BURST) {
        /*
         * THE TIMERS ARE CHECKED IN HERE TOO, which is what lets the bound be generous.
         *
         * They used to live only in the outer loop, so the bound was a compromise: large enough
         * to drain a burst, small enough that heartbeats and congestion reports were not starved
         * by one. Checking them per drained packet costs two comparisons and removes the
         * tension, so the bound can be sized for the burst alone.
         */
        send_periodic(session, out, next_heartbeat, next_congestion);

        uint8_t peek[STREAM_HEADER_LENGTH];
        ssize_t peeked = recvfrom(g_stream_channel.sock, peek, sizeof(peek), MSG_PEEK,
                                  NULL, NULL);

        if (peeked <= 0)
            break;                                   /* nothing waiting */
        if ((unsigned)(peek[0] & 0x0fu) == 0u)
            break;                                   /* control - takion_channel_poll owns it */
        drained++;
        {
            /* Not the control association - take it and account for it here. */
            uint8_t packet[TAKION_MAX_PACKET];
            ssize_t n = recvfrom(g_stream_channel.sock, packet, sizeof(packet), 0, NULL, NULL);

            if (n >= (ssize_t)STREAM_HEADER_LENGTH) {
                unsigned base = (unsigned)(packet[0] & 0x0fu);

                out->av_packets++;
                out->av_bytes += (unsigned long)n;
                if (base == STREAM_HEADER_TYPE_VIDEO)
                    out->av_video++;
                else if (base == STREAM_HEADER_TYPE_AUDIO)
                    out->av_audio++;
                else
                    out->av_other++;

                /*
                 * THE PROBE'S OWN VERIFY IS GONE, and its removal is the point.
                 *
                 * It was kept deliberately: it answered "does the A/V authentication rule hold"
                 * independently of "does reassembly work", and collapsing the two would have made one
                 * failure look like the other. That measurement has been taken many times over - 1123 of
                 * 1123, then 6287 of 6287, then every run since.
                 *
                 * What it costs is a SECOND GMAC over ~1400 bytes for every packet, on a 3.2 GHz PPE
                 * with no AES instructions, on the one thread that has to empty a 122 KB socket buffer.
                 * b137 timed a bounded 256-packet drain at 192 ms - three quarters of a millisecond per
                 * packet - while the demuxer was already verifying and decrypting each one through its
                 * own crypto seam. Paying twice for an answer we have is a diagnostic that outlived its
                 * question and became the fault.
                 *
                 * The A/V rule itself is unchanged and still enforced, inside the demuxer: tag at offset
                 * 10, key position read from the packet at 14, and only the tag zeroed in the AAD - not
                 * the control rule. av_verified now counts what the DEMUXER accepted, which is the
                 * demuxer's own accounting - units received against units lost - is the number that
                 * matters from here, and it already reports exactly this.
                 */
                if (out->demux_ready) {
                    /*
                     * TIMED, because removing one of the two GMAC passes moved the drain from 192 ms to
                     * 173 - about a tenth - which says the crypto was not what it cost. A bounded drain
                     * is two syscalls and this call per packet, and at ~0.95 ms per packet something
                     * here is an order of magnitude more expensive than it looks.
                     *
                     * stream_demux_ingest parses the header, verifies a GMAC, CTR-decrypts the payload,
                     * does FEC bookkeeping and copies into the assembly buffer. Which of those dominates
                     * is worth knowing before anything is moved to an SPE on the assumption it is the
                     * crypto.
                     */
                    uint64_t t0 = rc_tick();

                    stream_demux_ingest(&g_demux, packet, (size_t)n);
                    g_ingest_ticks += rc_tick() - t0;
                    g_ingest_calls++;
                    out->av_ingested++;
                }
            }
        }
    }
    if ((unsigned)drained > out->av_worst_burst)
        out->av_worst_burst = (unsigned)drained;

    {
        uint64_t now = rc_time_ms();
        uint64_t spent = now - drain_started;

        if (spent > (uint64_t)g_worst_drain_ms)
            g_worst_drain_ms = (unsigned)spent;
        /* The gap is measured from the END of a drain: that is when the socket was last emptied. */
        g_last_drain_ms = now;
    }
    return drained;
}

static void on_picture(void *ctx, const unsigned char *y, const unsigned char *u,
                       const unsigned char *v, int y_stride, int uv_stride, int width, int height)
{
    unsigned us;

    (void)ctx;

    /*
     * ASK BEFORE CONVERTING. If the previous flip has not landed, this picture is dropped whole - no
     * conversion, no wait. b87 did the opposite and paid for it: it converted and vsync-waited on every
     * frame, on the thread draining the socket, and lost 90 units where the run before lost none.
     *
     * A dropped frame costs one frame. A waited-on frame costs every packet that arrives during the
     * wait, and those losses cascade - the decoder then errors on the gaps, which is where b87's 81
     * decoder errors came from.
     */
    /*
     * WAIT BRIEFLY FOR THE DISPLAY RATHER THAN DROPPING THE PICTURE - and the reasoning above is kept
     * because it was right when it was written and is not any more.
     *
     * It describes this running on the thread that drains the socket, where a wait cost every packet
     * arriving during it. Since b144 the decode and blit have had their OWN thread; the receive loop
     * keeps draining regardless, which is the whole point of that split. What remains of the old cost is
     * a decode thread that pauses until the flip completes, and the picture ring in front of it exists
     * exactly to absorb that.
     *
     * b186 is what dropping costs now: 1,777 pictures decoded without an error and 649 thrown away here,
     * which is 37 fps on screen from a 60 fps stream. A flip completes every vsync, so a bounded wait
     * turns those drops into paced blits.
     */
    {
        unsigned waited = 0u;

        while (!rc_video_present_ready()) {
            if (waited >= RC_BLIT_WAIT_MS) {
                g_pictures_dropped++;
                return;
            }
            rc_sleep_ms(1u);
            waited++;
        }
        if (waited > g_blit_worst_wait_ms)
            g_blit_worst_wait_ms = waited;
    }

    us = rc_video_blit_yuv420(y, u, v, y_stride, uv_stride, width, height);
    if (us == 0u)
        return;   /* the display is not open, or the picture does not fit - not an error here */

    draw_overlay();

    rc_video_flip();
    g_blits++;
    g_last_picture_ms = rc_time_ms();
    if (g_session.phase != RC_PHASE_STREAMING)
        say(RC_PHASE_STREAMING, "Streaming", NULL, NULL);
    g_blit_us_total += us;
    if (us > g_blit_worst_us)
        g_blit_worst_us = us;
}

/*
 * The packed-RGB sink. Same shape as on_picture without the conversion: the decoder has already done it,
 * which is the point - b185 measured the SPE spending 16,444 us a frame converting and scaling against
 * 1,667 us waiting for the MFC, so the conversion is what there is to remove.
 *
 * If the SPE scale returns 0 the picture is dropped rather than converted on the PPE, because there is
 * no PPE scaler. That is counted, and a run where it is non-zero is a run that should go back to YUV.
 */
/*
 * WHAT THE OVERLAY SAYS, and it is deliberately the same set of questions the .NET client's diagnostics
 * report answers - what was asked for, what arrived, and what is happening now - so that a fault
 * described on one client is recognisable on the other.
 *
 * Everything here is read from counters this port already keeps. Nothing is computed for the overlay
 * alone, and nothing here touches the picture: the numbers are all in main memory, which matters
 * because the buffer being drawn ON is RSX memory and must only be written.
 *
 * The rates are over the whole session rather than a sliding window. A window would read better and
 * would need a history buffer per metric; this is the version that exists, and the log still carries
 * the peaks.
 */
/*
 * THE PANEL.
 *
 * Laid out here rather than in rc_overlay.c because what belongs on it is a question about this port's
 * pipeline, while how to put a pixel down is not. The overlay file owns the bitmap and the primitives;
 * this owns the answer to "what would someone want to know at the moment the picture looks wrong".
 *
 * The shape follows the .NET client's diagnostics report - what was asked for, what arrived, what is
 * happening now - so a fault described against one client is recognisable on the other.
 *
 * TWO COLUMNS AND RIGHT-ALIGNED NUMBERS. The labels sit at one x and the values at another, and the
 * numbers end on a fixed edge rather than starting at one, so a frame rate going 59 -> 9 does not shift
 * everything after it. A number that moves while you read it is a number you read twice.
 */
/*
 * DESIGN PIXELS, against a 1920x1080 screen. rc_overlay_px converts each to whatever the television
 * actually negotiated - see the note on it, and note that a fixed pixel layout is wider than a
 * 720x480 screen, at which point the overlay silently is not drawn at all.
 */
#define OV_PAD      rc_overlay_px(20)
#define OV_HEAD_H   rc_overlay_px(48)
#define OV_LABEL_X  rc_overlay_px(24)
#define OV_VALUE_X  rc_overlay_px(186)
#define OV_NUM_R    rc_overlay_px(330)     /* right edge of the value column */
#define OV_SPARK_X  rc_overlay_px(354)
#define OV_SPARK_W  rc_overlay_px(240)
#define OV_ROW_H    rc_overlay_px(34)

/*
 * A BITRATE IN THE UNIT SOMEONE WOULD SAY IT IN.
 *
 * The launch spec carries kbps because that is what the wire field is, and 20000 on a panel next to a
 * live figure reading 11.9 invites the reader to do the conversion themselves and to get it wrong.
 * Both are the same quantity and both should be in the same unit.
 *
 * Writes into `out` and returns the unit, so the number can be set as a figure - tabular, right
 * aligned - and the unit as a word beside it, which is the split the rest of the panel uses.
 */
static const char *bitrate_text(int kbps, char *out, size_t out_size)
{
    if (kbps >= 1000) {
        int whole = kbps / 1000;
        int tenth = (kbps % 1000) / 100;

        /* No trailing .0 - "20" reads as a round number and "20.0" reads as a measurement. */
        if (tenth == 0)
            snprintf(out, out_size, "%d", whole);
        else
            snprintf(out, out_size, "%d.%d", whole, tenth);
        return " Mbit/s asked";
    }
    snprintf(out, out_size, "%d", kbps);
    return " kbit/s asked";
}

static void draw_overlay(void)
{
    const rc_overlay_bucket *now;
    unsigned peak_fps = 0u, low_fps = 0xffffffffu, peak_lost = 0u;
    unsigned long peak_bytes = 0ul;
    unsigned fps_series[RC_OVERLAY_WINDOW];
    unsigned mbps_series[RC_OVERLAY_WINDOW];
    unsigned lost_series[RC_OVERLAY_WINDOW];
    unsigned series_n = 0u;
    unsigned i;
    int y;
    int small_drop;

    if (!rc_overlay_on())
        return;

    /* Fed every frame; the bitmap behind it is only rebuilt a few times a second. */
    overlay_tick();
    g_win[g_win_slot].frames++;
    g_win[g_win_slot].bytes += (unsigned long)(g_tally.video_bytes - g_win_last_bytes);
    g_win_last_bytes = (unsigned long)g_tally.video_bytes;
    g_win[g_win_slot].lost += (unsigned)(g_overlay_units_lost - g_win_last_lost);
    g_win_last_lost = g_overlay_units_lost;

    /*
     * BEGIN CAN DECLINE AND END STILL HAS TO RUN. The text is rebuilt a few times a second and the
     * COPY is queued every frame, because the picture blit overwrites the back buffer each time -
     * returning early here would show the overlay on one frame in fifteen, which reads as flicker
     * rather than as a bug.
     */
    if (!rc_overlay_begin()) {
        rc_overlay_end();
        return;
    }

    now = overlay_latest();

    /*
     * How far to drop the small text so it shares a baseline with the large figure beside it. The rows
     * used a hand-chosen +5, which is the same top-versus-baseline mistake the header had: two runs
     * whose boxes line up do not have their letters on the same line.
     */
    small_drop = rc_overlay_ascent(2) - rc_overlay_ascent(1);

    /*
     * OLDEST TO NEWEST, SKIPPING THE ONE IN PROGRESS. The ring's order is not the reading order, and a
     * sparkline drawn in ring order is a plausible-looking lie with a discontinuity wherever the write
     * head happens to be. The bucket still filling is left out of the series and the extremes alike:
     * it always reads low, and an fps that dips every time you look at it would be the first thing
     * anyone chased.
     */
    for (i = 1u; i <= RC_OVERLAY_WINDOW; i++) {
        unsigned at = (g_win_slot + i) % RC_OVERLAY_WINDOW;

        if (at == g_win_slot)
            continue;
        fps_series[series_n] = g_win[at].frames;
        mbps_series[series_n] = (unsigned)((g_win[at].bytes * 8ul) / 100000ul);   /* tenths of a Mbps */
        lost_series[series_n] = g_win[at].lost;
        series_n++;

        if (g_win[at].frames > peak_fps)
            peak_fps = g_win[at].frames;
        if (g_win[at].frames < low_fps)
            low_fps = g_win[at].frames;
        if (g_win[at].bytes > peak_bytes)
            peak_bytes = g_win[at].bytes;
        if (g_win[at].lost > peak_lost)
            peak_lost = g_win[at].lost;
    }
    if (low_fps == 0xffffffffu)
        low_fps = 0u;

    /* ---- panel ------------------------------------------------------------------------------- */
    rc_overlay_rect(0, 0, rc_overlay_width(), rc_overlay_height(), RC_OV_PANEL);
    rc_overlay_rect(0, 0, rc_overlay_width(), OV_HEAD_H, RC_OV_HEADER);
    rc_overlay_rect(0, 0, rc_overlay_px(7), OV_HEAD_H, RC_OV_ACCENT);
    rc_overlay_rect(0, OV_HEAD_H, rc_overlay_width(), 1, RC_OV_EDGE);
    /* A one-pixel edge on all four sides, so the panel has a boundary over a bright picture as well as
     * over a dark one. */
    rc_overlay_rect(0, 0, rc_overlay_width(), 1, RC_OV_EDGE);
    rc_overlay_rect(0, rc_overlay_height() - 1, rc_overlay_width(), 1, RC_OV_EDGE);
    rc_overlay_rect(0, 0, 1, rc_overlay_height(), RC_OV_EDGE);
    rc_overlay_rect(rc_overlay_width() - 1, 0, 1, rc_overlay_height(), RC_OV_EDGE);

    /*
     * ONE BASELINE, TWO SIZES. These were placed by their top edges with hand-chosen y values, so the
     * boxes lined up and the letters did not - the name sat a few pixels above the build id beside it.
     * Each run is now placed at (baseline - its own ascent), which is what "on the same line" means.
     */
    {
        int base = rc_overlay_px(34);
        int big = rc_overlay_ascent(2);
        int small = rc_overlay_ascent(1);
        int at = OV_PAD;

        at += rc_overlay_text(at, base - big, 2, RC_OV_TEXT, "Ripcord");
        rc_overlay_num(at + rc_overlay_px(12), base - small, 1, RC_OV_LABEL, RC_PS3_BUILD_ID);
        rc_overlay_text_right(rc_overlay_width() - OV_PAD, base - small, 1, RC_OV_LABEL,
                              "PlayStation 3");
    }

    /* ---- what the stream is ------------------------------------------------------------------ */
    y = OV_HEAD_H + rc_overlay_px(14);
    rc_overlay_text(OV_LABEL_X, y + 2, 1, RC_OV_LABEL, "Stream");
    if (g_stream_is_hevc) {
        rc_overlay_text(OV_VALUE_X, y, 1, RC_OV_BAD, "HEVC - this decoder is H.264 only");
    } else {
        /* The geometry is monospaced because it is read as a value, and it changes if the console ever
         * renegotiates; the words around it are not. */
        int at = OV_VALUE_X;

        at += rc_overlay_num(at, y, 1, RC_OV_TEXT, "%dx%d @%d",
                             g_live_stats.width, g_live_stats.height, g_overlay_asked_fps);
        rc_overlay_text(at + rc_overlay_px(20), y, 1, RC_OV_TEXT, "H.264 hardware");
    }

    y += OV_ROW_H;
    rc_overlay_text(OV_LABEL_X, y + 2, 1, RC_OV_LABEL, "Path");
    {
        int at = OV_VALUE_X;

        /*
         * Measured and shortened rather than trusted to fit. "asked for 20000 kbps" spelled out ran
         * past the right edge at 1080p and would have run further at every smaller size, because the
         * panel shrinks with the screen and the sentence does not.
         */
        at += rc_overlay_text(at, y, 1, RC_OV_LABEL, "%s scaler, %s, ",
                              g_overlay_hw_scale ? "RSX" : "SPE",
                              rc_decode_vdec_picture_in_vram() ? "zero copy" : "one copy");
        {
            char rate[16];
            const char *unit = bitrate_text(g_overlay_asked_kbps, rate, sizeof(rate));

            at += rc_overlay_num(at, y, 1, RC_OV_LABEL, "%s", rate);
            rc_overlay_text(at + rc_overlay_px(4), y, 1, RC_OV_LABEL, "%s", unit);
        }
    }

    y += OV_ROW_H - rc_overlay_px(2);
    rc_overlay_rect(OV_PAD, y, rc_overlay_width() - OV_PAD * 2, 1, RC_OV_EDGE);

    /* ---- the live figures -------------------------------------------------------------------- */
    y += rc_overlay_px(12);
    rc_overlay_text(OV_LABEL_X, y + small_drop, 1, RC_OV_LABEL, "Frames/s");
    rc_overlay_num_right(OV_NUM_R, y, 2,
                         now->frames >= 55u ? RC_OV_GOOD
                                            : (now->frames >= 40u ? RC_OV_WARN : RC_OV_BAD),
                         "%u", now->frames);
    rc_overlay_bars(OV_SPARK_X, y, OV_SPARK_W, rc_overlay_px(20), fps_series, series_n,
                    (peak_fps > 60u) ? peak_fps : 60u, RC_OV_ACCENT);
    /*
     * The label is placed from the NUMBER'S measured width rather than from a guess at it. These three
     * lines are where the hand-measured offsets showed: "low", "peak" and "pk" each sat a fixed
     * distance from the right edge, the figure beside them grew a digit, and the two met.
     */
    {
        int w = rc_overlay_num_width(1, "%u", low_fps);

        rc_overlay_text_right(rc_overlay_width() - OV_PAD - w - rc_overlay_px(10), y + small_drop, 1, RC_OV_LABEL, "low");
        rc_overlay_num_right(rc_overlay_width() - OV_PAD, y + small_drop, 1, RC_OV_LABEL, "%u", low_fps);
    }

    y += OV_ROW_H + rc_overlay_px(4);
    rc_overlay_text(OV_LABEL_X, y + small_drop, 1, RC_OV_LABEL, "Mbit/s");
    rc_overlay_num_right(OV_NUM_R, y, 2, RC_OV_TEXT, "%lu.%lu",
                         (now->bytes * 8ul) / 1000000ul, ((now->bytes * 8ul) / 100000ul) % 10ul);
    rc_overlay_bars(OV_SPARK_X, y, OV_SPARK_W, rc_overlay_px(20), mbps_series, series_n,
                    (unsigned)((peak_bytes * 8ul) / 100000ul), RC_OV_GOOD);
    {
        unsigned long whole = (peak_bytes * 8ul) / 1000000ul;
        unsigned long tenth = ((peak_bytes * 8ul) / 100000ul) % 10ul;
        int w = rc_overlay_num_width(1, "%lu.%lu", whole, tenth);

        rc_overlay_text_right(rc_overlay_width() - OV_PAD - w - rc_overlay_px(10), y + small_drop, 1, RC_OV_LABEL, "peak");
        rc_overlay_num_right(rc_overlay_width() - OV_PAD, y + small_drop, 1, RC_OV_LABEL, "%lu.%lu",
                             whole, tenth);
    }

    y += OV_ROW_H + rc_overlay_px(4);
    rc_overlay_text(OV_LABEL_X, y + small_drop, 1, RC_OV_LABEL, "Lost/s");
    rc_overlay_num_right(OV_NUM_R, y, 2, peak_lost > 0u ? RC_OV_WARN : RC_OV_TEXT, "%u", now->lost);
    /*
     * THREE ROWS, THREE SPARKLINES. Two of three having a graph made the eye ask what was different
     * about the third, and the answer was nothing - the data was already in the window.
     *
     * A flat empty line is the point rather than a waste of it: loss is normally zero, and thirty
     * seconds of visibly nothing is a stronger statement than a 0 that could have been a 4 a moment
     * ago. The bars keep their track so a clean second reads as low rather than as missing.
     *
     * Scaled against its own peak, not a fixed ceiling, because there is no natural maximum for loss
     * the way 60 is for frames - two lost units matter if the last thirty seconds lost none.
     */
    rc_overlay_bars(OV_SPARK_X, y, OV_SPARK_W, rc_overlay_px(20), lost_series, series_n,
                    (peak_lost > 0u) ? peak_lost : 1u,
                    peak_lost > 0u ? RC_OV_WARN : RC_OV_TRACK);
    {
        int w = rc_overlay_num_width(1, "%u", peak_lost);

        rc_overlay_text_right(rc_overlay_width() - OV_PAD - w - rc_overlay_px(10), y + small_drop, 1, RC_OV_LABEL, "peak");
        rc_overlay_num_right(rc_overlay_width() - OV_PAD, y + small_drop, 1, RC_OV_LABEL, "%u", peak_lost);
    }

    /* ---- timing and faults ------------------------------------------------------------------- */
    y += OV_ROW_H + rc_overlay_px(2);
    rc_overlay_rect(OV_PAD, y, rc_overlay_width() - OV_PAD * 2, 1, RC_OV_EDGE);
    y += rc_overlay_px(14);

    {
        unsigned long hz = (unsigned long)rc_tick_hz();
        unsigned decode_us = (hz > 0ul && g_live_stats.frames_in > 0)
            ? (unsigned)(((g_live_stats.decode_ticks * 1000000ull) / hz)
                         / (unsigned long long)g_live_stats.frames_in)
            : 0u;

        int at = OV_VALUE_X;

        rc_overlay_text(OV_LABEL_X, y, 1, RC_OV_LABEL, "Time");
        at += rc_overlay_num(at, y, 1, RC_OV_TEXT, "%u", decode_us);
        at += rc_overlay_text(at + rc_overlay_px(6), y, 1, RC_OV_LABEL, " us decode") + rc_overlay_px(6);
        at += rc_overlay_num(at + rc_overlay_px(20), y, 1, RC_OV_TEXT, "%u",
                             g_blits ? (unsigned)(g_blit_us_total / g_blits) : 0u) + rc_overlay_px(20);
        rc_overlay_text(at + rc_overlay_px(6), y, 1, RC_OV_LABEL, " us present");
    }

    /*
     * FAULTS STAY CUMULATIVE, and that is the deliberate other half of the window. An IDR request or
     * an overrun that happened thirty seconds ago and stopped is still worth knowing happened - it is
     * the difference between a stream that is healthy and one that recovered.
     */
    y += OV_ROW_H;
    {
        rc_audio_stats a;
        int bad;

        rc_audio_stats_get(&a);
        bad = (g_idr_requests > 8u || g_frames_overrun > 0u
               || a.decode_errors > 0u || a.silence_written > 0u);

        int at = OV_VALUE_X;
        uint32_t c = bad ? RC_OV_WARN : RC_OV_LABEL;

        rc_overlay_text(OV_LABEL_X, y, 1, RC_OV_LABEL, "Since start");
        at += rc_overlay_num(at, y, 1, c, "%u", g_idr_requests);
        at += rc_overlay_text(at + rc_overlay_px(6), y, 1, RC_OV_LABEL, " keyframes,") + rc_overlay_px(6);
        at += rc_overlay_num(at + rc_overlay_px(12), y, 1, c, "%u", g_frames_overrun) + rc_overlay_px(12);
        at += rc_overlay_text(at + rc_overlay_px(6), y, 1, RC_OV_LABEL, " overrun,") + rc_overlay_px(6);
        at += rc_overlay_num(at + rc_overlay_px(12), y, 1, c, "%u",
                             a.decode_errors + a.silence_written) + rc_overlay_px(12);
        rc_overlay_text(at + rc_overlay_px(6), y, 1, RC_OV_LABEL, " audio");
    }

    rc_overlay_end();
}

static void on_picture_rgb(void *ctx, const unsigned char *argb, int stride, int width, int height)
{
    unsigned us;

    (void)ctx;

    if (!g_live_open)
        return;

    {
        unsigned waited = 0u;

        while (!rc_video_present_ready()) {
            if (waited >= RC_BLIT_WAIT_MS) {
                g_pictures_dropped++;
                return;
            }
            rc_sleep_ms(1u);
            waited++;
        }
        if (waited > g_blit_worst_wait_ms)
            g_blit_worst_wait_ms = waited;
    }

    /*
     * NO COPY AT ALL when the decoder wrote its picture where the RSX can read it. The scaled blit is
     * then the entire present path: no SPE, no staging buffer, and nothing on this core touching a
     * pixel. Falls back to the copying path on a zero, which is what it answers when the RSX scaler is
     * not selected.
     */
    us = 0u;
    if (rc_decode_vdec_picture_in_vram()) {
        uint32_t off = rc_decode_vdec_delivered_offset();

        if (off != 0u)
            us = rc_video_blit_rsx_offset(off, width, height);
    }
    if (us == 0u)
        us = rc_video_blit_argb32(argb, stride, width, height);
    if (us == 0u) {
        g_rgb_scale_failed++;
        return;
    }

    draw_overlay();

    rc_video_flip();
    g_blits++;
    g_last_picture_ms = rc_time_ms();
    if (g_session.phase != RC_PHASE_STREAMING)
        say(RC_PHASE_STREAMING, "Streaming", NULL, NULL);
    g_blit_us_total += us;
    if (us > g_blit_worst_us)
        g_blit_worst_us = us;
}

static void on_video_frame(void *userdata, const uint8_t *data, size_t length, int is_keyframe)
{
    (void)userdata;
    g_tally.video_frames++;
    if (is_keyframe) {
        g_tally.keyframes++;
        /* The thing we were asking for. Cleared here rather than when the request went out, because a
         * request that produced nothing has not fixed anything. */
        g_awaiting_keyframe = 0;
    }
    g_tally.video_bytes += (unsigned long)length;
    if (length > (size_t)g_tally.largest_frame)
        g_tally.largest_frame = (unsigned)length;

    /*
     * Counted and dropped, never fed. cellVdec would take these and return success - see the note where
     * this is classified. The tally keeps running so the report can say how much arrived, which is the
     * difference between "the stream is HEVC" and "the stream stopped".
     */
    if (g_stream_is_hevc)
        return;

    /*
     * COPIED, NOT DECODED. The reasoning that put the decode here was sound and the conclusion was
     * wrong: avoiding a copy per frame is worth a few microseconds, and paying for it with a 22 ms
     * stall in the loop that drains a 122 KB socket buffer is not. See the frame queue above.
     *
     * The demuxer's contract says these bytes live only for this call, which is exactly why the copy is
     * the price of decoding anywhere else.
     */
    /*
     * COUNTED, NOT JUST SKIPPED. A frame too large for a slot used to fall out of this `if` and vanish -
     * no counter, no log line, and at 1080p that would have looked exactly like a decoder that drops
     * keyframes. This session has already spent five builds on a fault that every counter reported as
     * success; a silent drop is the same mistake waiting to happen.
     */
    if (length > RC_FRAME_SLOT_BYTES)
        g_frames_oversized++;

    if (g_live_open && length > 0u && length <= RC_FRAME_SLOT_BYTES) {
        int slot;

        /* The decoder thread is the other user of this ring - see the note by g_decode_thread. */
        if (g_decode_thread_up)
            (void)sysMutexLock(g_decode_mutex, 0);

        if (g_frame_count == RC_FRAME_QUEUE_SLOTS) {
            /*
             * Behind. The OLDEST goes, not the newest: the newest is the one closest to live, and
             * dropping it would throw away the frame the viewer is waiting for to keep one they have
             * already missed. Either way the reference chain is broken, so ask for a keyframe.
             */
            g_frame_head = (g_frame_head + 1) % RC_FRAME_QUEUE_SLOTS;
            g_frame_count--;
            g_frames_overrun++;
            g_awaiting_keyframe = 1;
        }

        slot = (g_frame_head + g_frame_count) % RC_FRAME_QUEUE_SLOTS;
        memcpy(g_frame_queue[slot], data, length);
        g_frame_length[slot] = length;
        g_frame_count++;
        g_frames_queued++;
        if ((unsigned)g_frame_count > g_queue_worst)
            g_queue_worst = (unsigned)g_frame_count;

        if (g_decode_thread_up) {
            (void)sysMutexUnlock(g_decode_mutex);
            /* Signalled outside the lock: the woken thread would only block re-acquiring it. */
            (void)sysCondSignal(g_decode_cond);
        }
    }
}

/*
 * The consumer. Takes one frame per turn, copies it out under the lock, and decodes with the lock
 * released so the receive thread is never kept waiting on a 21 ms decode.
 */
static void decode_thread_entry(void *arg)
{
    (void)arg;

    for (;;) {
        size_t length = 0u;
        uint64_t started;

        (void)sysMutexLock(g_decode_mutex, 0);
        while (g_frame_count == 0 && !g_decode_quit)
            (void)sysCondWait(g_decode_cond, RC_DECODE_WAIT_US);

        if (g_frame_count > 0) {
            int slot = g_frame_head;

            length = g_frame_length[slot];
            if (length > sizeof(g_decode_buf))
                length = 0u;          /* cannot happen - the producer bounds it - but do not trust it */
            else
                memcpy(g_decode_buf, g_frame_queue[slot], length);

            g_frame_head = (g_frame_head + 1) % RC_FRAME_QUEUE_SLOTS;
            g_frame_count--;
        }
        (void)sysMutexUnlock(g_decode_mutex);

        if (length == 0u) {
            if (g_decode_quit)
                break;              /* asked to stop, and the queue is empty */
            continue;
        }

        started = rc_time_ms();
        (void)rc_decode_live_feed(g_decode_buf, length, &g_live_stats);
        {
            unsigned spent = (unsigned)(rc_time_ms() - started);

            if (spent > g_worst_decode_ms)
                g_worst_decode_ms = spent;
        }
    }

    sysThreadExit(0);
}

static int decode_thread_start(void)
{
    sys_mutex_attr_t mattr;
    sys_cond_attr_t cattr;
    sys_ppu_thread_t self;
    s32 priority = 1001;
    static char name[] = "rc_decode";

    g_decode_quit = 0;

    /* One step below whoever is draining the socket, measured rather than assumed - a larger number is
     * a lower priority on this scheduler. */
    if (sysThreadGetId(&self) == 0) {
        s32 mine = 0;

        if (sysThreadGetPriority(self, &mine) == 0) {
            priority = (mine < 3000) ? mine + 1 : mine;
            g_decode_receive_priority = (int)mine;
        }
    }

    sysMutexAttrInitialize(mattr);
    if (sysMutexCreate(&g_decode_mutex, &mattr) != 0)
        return 0;

    sysCondAttrInitialize(cattr);
    if (sysCondCreate(&g_decode_cond, g_decode_mutex, &cattr) != 0) {
        (void)sysMutexDestroy(g_decode_mutex);
        return 0;
    }

    if (sysThreadCreate(&g_decode_thread, decode_thread_entry, NULL, priority,
                        RC_DECODE_THREAD_STACK, THREAD_JOINABLE, name) != 0) {
        (void)sysCondDestroy(g_decode_cond);
        (void)sysMutexDestroy(g_decode_mutex);
        return 0;
    }

    g_decode_thread_up = 1;
    g_decode_priority = (int)priority;
    return 1;
}

static void decode_thread_stop(void)
{
    u64 retval = 0;

    if (!g_decode_thread_up)
        return;

    /* Set under the lock so a consumer about to wait sees it; the timeout means a missed signal costs
     * 50 ms rather than the session. */
    (void)sysMutexLock(g_decode_mutex, 0);
    g_decode_quit = 1;
    (void)sysMutexUnlock(g_decode_mutex);
    (void)sysCondSignal(g_decode_cond);

    (void)sysThreadJoin(g_decode_thread, &retval);
    (void)sysCondDestroy(g_decode_cond);
    (void)sysMutexDestroy(g_decode_mutex);
    g_decode_thread_up = 0;
}

static void on_audio_frame(void *userdata, const uint8_t *data, size_t length)
{
    (void)userdata;
    g_tally.audio_frames++;
    g_tally.audio_bytes += (unsigned long)length;

    /*
     * Decoded here, on the receive thread, which is where this callback already runs. Opus is about 1%
     * of a frame's work - it does not need a thread, and this port has spent three console lockups
     * learning what a casually added one costs. See rc_audio_ps3.h.
     */
    rc_audio_submit(data, length);
}

static uint64_t g_last_idr_request_ms;


/*
 * Why g_awaiting_keyframe exists, recorded where the loss is handled.
 *
 * Loss breaks the reference chain, and every frame after it is undecodable until the next keyframe -
 * which the console only sends when asked. Asking ONCE per loss event is what b124 did: 4 requests for
 * 36 events, 2 keyframes in thirty seconds, and 835 of 857 frames undecodable. Fewer loss events than
 * the run before it, and one seventeenth the pictures.
 *
 * The request is not an acknowledgement that something broke, it is a demand for the thing that repairs
 * it. An unanswered demand is a reason to repeat it.
 */

/*
 * Asks for a keyframe, throttled. One IDR repairs the whole reference chain, so asking again while the
 * answer is still in flight spends upstream bandwidth on a repair already on its way.
 */
static void request_idr(uint64_t now)
{
    uint8_t request[8];
    size_t request_len;

    if (now - g_last_idr_request_ms < RC_IDR_REQUEST_MIN_MS)
        return;
    g_last_idr_request_ms = now;

    request_len = takion_control_build_bare(TAKION_CONTROL_IDR_REQUEST, request, sizeof(request));
    if (request_len > 0u
        && takion_channel_send(&g_stream_channel, TAKION_CHANNEL_SESSION, request, request_len))
        g_idr_requests++;
}

static void on_video_loss(void *userdata, int first_frame_index, int last_frame_index)
{
    uint64_t now;

    (void)userdata;
    (void)first_frame_index;
    (void)last_frame_index;
    g_tally.corrupt_events++;

    /*
     * ASK FOR A FRESH IDR, which this used to merely count.
     *
     * The old comment said a recovery path that could not be verified would obscure the measurement,
     * and while reassembly was the open question that was right. b90 made it wrong: over thirty seconds
     * the console sent 893 video frames and exactly ONE keyframe, seven loss events cost the decoder its
     * reference chain, and 686 of those frames failed with openh264's 0x12 - dsNoParamSets | dsRefLost.
     * The picture froze while a perfectly healthy stream kept arriving.
     *
     * Inter-frames reference the pictures before them, so a gap is not one lost frame - it is every
     * frame until the next keyframe, and the console only sends another when asked. Counting the loss
     * and not asking is the one response guaranteed not to recover.
     *
     * Throttled to the reference's interval. One IDR repairs the whole chain, so requesting on every
     * lost slice while a burst is still arriving would spend upstream bandwidth on repairs already in
     * flight.
     */
    g_awaiting_keyframe = 1;
    now = rc_time_ms();
    request_idr(now);
}

/*
 * The senkusha legs the console GATES ON, as opposed to the ones that merely tune the stream.
 *
 * A completed senkusha handshake is not enough by itself: the console wants the PROTOCOL_VERSION
 * exchange and a keyless SESSION exchange to have happened on that channel before it will answer the
 * stream channel's real SESSION_REQUEST. ports/ripcord-3ds established which legs those are, and this
 * follows it rather than re-deriving it.
 *
 * NOT DONE HERE: the echo and MTU measurement legs. Those produce the rtt and mtu the launch spec
 * declares, and the 3DS port's note is worth repeating - it omitted the echo leg for five phases on the
 * reasoning that it "tunes bitrate, it does not unlock anything", and both halves of that were true while
 * the conclusion still cost it, because rtt 0 is an input the console uses. This port declares the same
 * defaults it always has, which is the status quo rather than a regression, and the measurement is
 * unfinished business rather than a decision.  [X]
 */
static int senkusha_legs(halyard_control_session *session, rc_connect_result *out)
{
    uint8_t payload[256];
    size_t payload_len;

    /*
     * PROTOCOL_VERSION_REQUEST{supportedVersions=[9]}, as a literal, because building two nested protobuf
     * fields to emit seven constant bytes is more code to be wrong in than the bytes are.
     *
     * The tag arithmetic is worth reading rather than trusting: field 31 length-delimited is
     * (31 << 3) | 2 = 250, which is >= 0x80 and therefore a TWO-byte varint tag - 0xFA 0x01, not the
     * single 0xFA that writing it out by hand produces.
     */
    {
        static const uint8_t kVersionRequest[] = {
            0x08, 0x1F,                   /* field 1 (type) varint = 31, PROTOCOL_VERSION_REQUEST */
            0xFA, 0x01, 0x02, 0x08, 0x09  /* field 31, length 2: { field 1 varint = 9 }           */
        };
        memcpy(payload, kVersionRequest, sizeof(kVersionRequest));
        payload_len = sizeof(kVersionRequest);
    }

    if (!takion_channel_send(&g_senkusha_channel, TAKION_CHANNEL_PROTOCOL_VERSION, payload, payload_len))
        return 0;
    {
        /* Timed: answering a version request asks the console to do essentially no work, so this is very
         * nearly all path time - the reference calls it the purest sample available. */
        uint64_t asked_ms = rc_time_ms();

        if (takion_channel_await_control(&g_senkusha_channel, RC_TAKION_PROTOCOL_VERSION_ACK,
                                         RC_TAKION_REPLY_MS, service_control_tick, session, NULL, NULL)) {
            out->senkusha_version_ack = 1;
            g_version_rtt_ms = (int)(rc_time_ms() - asked_ms);
        }
    }

    /*
     * The KEYLESS SESSION_REQUEST: client version 9, empty session key and launch spec, four zero
     * encryptedKey bytes, and no ECDH fields at all. This exchange carries nothing anyone needs - its
     * entire purpose is to have happened.
     */
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
        if (payload_len == 0u)
            return 0;
        if (!takion_channel_send(&g_senkusha_channel, TAKION_CHANNEL_SESSION, payload, payload_len))
            return 0;
        if (!takion_channel_await_control(&g_senkusha_channel, TAKION_CONTROL_SESSION_REPLY,
                                          RC_TAKION_REPLY_MS, service_control_tick, session, NULL, NULL))
            return 0;
    }

    out->senkusha_complete = 1;

    /*
     * THE MEASUREMENT LEGS, after the exchange the console gates on and before the launch spec is built.
     *
     * These do not unlock anything, which is exactly why they were skipped - and the 3DS port's note
     * says what that cost: it omitted the echo leg for five phases reasoning that it "only tunes
     * bitrate", and both halves of that were true while the conclusion was not. The declared RTT is an
     * input the console's rate controller uses, and declaring 0 asks it to plan against a figure nobody
     * measured. b115 asked for 8000 kbps at 720p and was sent about 1400.
     *
     * Best-effort throughout: a failed probe leaves the declared defaults in place, which is the status
     * quo rather than a regression.
     */
    senkusha_echo_probe(session, out);
    senkusha_mtu_probe(session, out);
    return 1;
}

/*
 * SESSION_REQUEST -> SESSION_REPLY -> the four per-direction stream keys.
 *
 * The launch spec travels encrypted under the CONTROL session's field cipher at counter 0 and base64'd,
 * and the 16-byte handshake key embedded in its plaintext is the same key the console signs its ECDH
 * public point under. That signature check is the one that matters: without it, anything able to inject a
 * DATA chunk on this channel could substitute its own public key and read the whole session.
 * takion_session_negotiator_accept_reply performs it and refuses the reply if it fails.
 */
static int stream_session_exchange(const halyard_pairing_record *rec,
                                   halyard_control_session *session, rc_connect_result *out)
{
    /*
     * The versions this client is willing to speak, taken from the .NET reference
     * (src/Ripcord.Protocol.Halyard.Takion/TakionSessionNegotiator.cs). 12 is absent there and absent
     * here; the gap is the reference's and is reproduced rather than tidied.
     */
    static const uint32_t kSupportedVersions[] = { 9u, 10u, 11u, 13u, 14u, 15u, 16u, 17u };

    uint8_t handshake_key[16];
    halyard_launch_spec_params params;
    uint8_t version_msg[64];
    size_t spec_len, b64_len, request_len, version_len;
    const uint8_t *reply = NULL;
    size_t reply_len = 0u;
    uint32_t version = TAKION_CLIENT_VERSION;
    int ok = 0;

    /*
     * NEGOTIATE THE VERSION ON THIS CHANNEL FIRST, and use what the console picks.
     *
     * b51 found this the expensive way. The ECDH CURVE is chosen from the negotiated version, not from
     * the one we would like - so assuming our own top version generates a key on a curve the console may
     * not have chosen. The failure surfaces nowhere near the cause: the console's reply arrives, its
     * ecdhSignature VERIFIES (an HMAC over whatever bytes it sent, which says nothing about the curve),
     * and only then is its public point rejected for being on a different curve. It reads like a crypto
     * fault and is a negotiation one.
     *
     * The senkusha channel's version exchange does not count for this. That one is part of senkusha's
     * own bring-up; this is the stream channel, and the reference asks again here.
     */
    version_len = takion_control_build_protocol_version_request(
        kSupportedVersions, sizeof(kSupportedVersions) / sizeof(kSupportedVersions[0]),
        version_msg, sizeof(version_msg));
    if (version_len > 0u
        && takion_channel_send(&g_stream_channel, TAKION_CHANNEL_PROTOCOL_VERSION,
                               version_msg, version_len)) {
        const uint8_t *ack = NULL;
        size_t ack_len = 0u;

        if (takion_channel_await_control(&g_stream_channel, TAKION_CONTROL_PROTOCOL_VERSION_ACK,
                                         RC_TAKION_REPLY_MS, service_control_tick, session,
                                         &ack, &ack_len)) {
            uint32_t agreed = 0u;

            out->stream_version_acked = 1;
            /*
             * A missing version field is not an error - fall back to what we asked for, which is what
             * the reference does. Deferring to a console that names one costs nothing and is the only
             * thing that keeps the curve choice honest if it ever picks lower.
             */
            if (takion_control_parse_protocol_version_ack(ack, ack_len, &agreed) && agreed != 0u)
                version = agreed;
        }
    }
    out->stream_version = (unsigned)version;

    if (!rc_random_bytes(handshake_key, sizeof(handshake_key)))
        return 0;
    out->stream_build_step = RC_STREAM_STEP_RANDOM;

    memset(&params, 0, sizeof(params));
    params.width = (rec->stream_width > 0) ? rec->stream_width : 640;
    params.height = (rec->stream_height > 0) ? rec->stream_height : 360;

    /*
     * NEVER ASK FOR MORE THAN 720p, AND THE REASON IS THE REFERENCE BUFFER RATHER THAN THE PIXEL RATE.
     *
     * cellVdec can be opened at H.264 level 4.2 and no higher - b145 swept vdecQueryAttr and it accepts
     * exactly 10..42. The console encodes with NINE reference frames, and level bounds the decoded
     * picture buffer: 4.2 allows 34,816 macroblocks, nine 720p frames need 32,400 and fit, nine 1080p
     * frames need 73,440 and do not. So the console declares level 5.0 for 1080p because it genuinely
     * needs it, and a decoder that cannot be configured that high accepts every access unit, reports no
     * error, and produces uniformly black pictures. b169 through b171 is that, three times.
     *
     * FRAME RATE IS NOT WHAT IS BEING CAPPED. 720p60 is 216,000 macroblocks a second against 4.2's
     * ceiling of 522,240, and the reference buffer does not grow with frame rate - so 60 fps at this
     * resolution should be inside the same level. It is untested here, and capping it would forgo
     * something that may simply work. The cap is on size alone.
     */
    if (params.height > 720 || params.width > 1280) {
        out->resolution_capped_from_width = params.width;
        out->resolution_capped_from_height = params.height;
        params.width = 1280;
        params.height = 720;
    }
    params.fps = (rec->fps == 60) ? 60 : 30;
    params.bitrate_kbps = (rec->stream_bitrate_kbps > 0) ? rec->stream_bitrate_kbps : 2000;
    out->declared_bitrate_kbps = params.bitrate_kbps;
    params.mtu = (g_measured_mtu > 0) ? g_measured_mtu : RC_DECLARED_MTU;
    params.rtt_ms = g_measured_rtt_ms;   /* 0 if the echo probe did not get a majority */
    params.is_hevc = 0;  /* H.264 only: the decoder this port proved is openh264   */
    params.is_hdr = 0;

    out->asked_width = params.width;
    out->asked_height = params.height;
    out->asked_fps = params.fps;

    spec_len = halyard_launch_spec_build(&params, handshake_key, g_launch_spec, sizeof(g_launch_spec));
    if (spec_len == 0u)
        goto done;
    out->launch_spec_bytes = (unsigned)spec_len;
    out->stream_build_step = RC_STREAM_STEP_SPEC;

    /* AES-128-OFB under the control session's field context at counter 0. In place: OFB is symmetric and
     * the plaintext is not wanted again. */
    halyard_control_streaminfo_crypt(&session->ctrl, 0,
                                     (const uint8_t *)g_launch_spec, (uint8_t *)g_launch_spec, spec_len);

    b64_len = rc_base64_encode((const uint8_t *)g_launch_spec, spec_len,
                               g_launch_spec_b64, sizeof(g_launch_spec_b64));
    if (b64_len == 0u)
        goto done;
    out->launch_spec_b64_bytes = (unsigned)b64_len;
    out->stream_build_step = RC_STREAM_STEP_B64;

    request_len = takion_session_negotiator_begin(&g_negotiator, version, handshake_key,
                                                  g_launch_spec_b64, b64_len,
                                                  rc_random_rng_callback, NULL,
                                                  g_session_request, sizeof(g_session_request));
    if (request_len == 0u)
        goto done;

    out->stream_build_step = RC_STREAM_STEP_REQUEST;
    out->session_request_bytes = (unsigned)request_len;
    out->curve_p521 = (g_negotiator.curve == RC_ECDH_CURVE_P521);

    if (!takion_channel_send(&g_stream_channel, TAKION_CHANNEL_SESSION, g_session_request, request_len))
        goto done;
    out->stream_build_step = RC_STREAM_STEP_SENT;

    if (!takion_channel_await_control(&g_stream_channel, TAKION_CONTROL_SESSION_REPLY,
                                      RC_TAKION_REPLY_MS, service_control_tick, session,
                                      &reply, &reply_len))
        goto done;

    out->stream_build_step = RC_STREAM_STEP_REPLY;
    out->session_reply_bytes = (unsigned)reply_len;
    /* Taken immediately before the call that has been failing, which is the only place the number
     * means anything. */
    rc_stack_probe(&out->stack_size, &out->stack_used, &out->stack_headroom);

    if (!takion_session_negotiator_accept_reply(&g_negotiator, reply, reply_len,
                                                rc_random_rng_callback, NULL)) {
        out->reply_reject_reason = g_negotiator.last_reject_reason;
        out->peer_key_length = (unsigned)g_negotiator.last_peer_key_length;
        out->peer_key_prefix = (unsigned)g_negotiator.last_peer_key_prefix;
        /*
         * Taken from the negotiator, which copied the bytes while it still held them. Re-parsing the
         * reply here instead produced 133 zeros beside a recorded prefix of 0x04 - the pointer aimed
         * into channel storage that had moved on, and the dump disagreed with the field next to it.
         */
        if (g_negotiator.last_peer_key_length > 0u
            && g_negotiator.last_peer_key_length <= sizeof(out->peer_key)) {
            memcpy(out->peer_key, g_negotiator.last_peer_key, g_negotiator.last_peer_key_length);
        }
        out->ecdh_step = g_negotiator.last_ecdh_step;
        out->ecdh_code = g_negotiator.last_ecdh_code;

        /*
         * THE CONTROL. Run a KNOWN-GOOD P-521 point through the identical validator, right here, at the
         * same call depth, in the same run.
         *
         * The console's point has been checked off-console against the curve equation by integer
         * arithmetic and it IS on P-521; the same bytes are accepted by the same code built for the
         * host. Yet this machine refuses them, while four P-521 vectors sitting on its own disk pass -
         * two of which have the same high-byte shape as this point, so it is not the 521st bit.
         *
         * Those facts cannot all be about the point, so this asks whether they are about the PLACE. If
         * the control point fails here too, the environment at this call site is the subject and the
         * console's key is innocent. If the control passes and the console's does not, the reverse.
         *
         * The control is one of the vectors from ports/common/tests/vectors/session-crypto.kat, which
         * this project generates from its own .NET side - generic test material, tied to no console and
         * no account.
         */
        {
            static const unsigned char kControlPoint[133] = {
                0x04,0x01,0x0d,0x81,0xe0,0x7f,0xf3,0x60,0xff,0xb5,0x90,0x14,0xfc,0xed,0x66,0x84,
                0x93,0xaf,0x0f,0xbd,0x78,0xc2,0xb9,0xc1,0xb4,0xe4,0x7e,0xc6,0xc9,0x39,0x47,0xb1,
                0x09,0x34,0xaf,0xf3,0x12,0xc0,0xdf,0xb8,0x3c,0xbf,0xb1,0xf3,0xa5,0xa7,0x79,0xa7,
                0x68,0x09,0x89,0x44,0x4c,0x01,0x86,0x91,0xdf,0xfe,0x5b,0xd9,0x73,0xb9,0x22,0x68,
                0x1b,0x51,0x59,0x01,0xcd,0x23,0x66,0x79,0x46,0x86,0xd2,0x48,0x70,0x7b,0x65,0x6d,
                0xd8,0xc9,0x83,0xa3,0xc4,0xfc,0xff,0xf3,0xdd,0x83,0x72,0x93,0x4a,0x8a,0x95,0x3c,
                0xc1,0x87,0x59,0xb2,0x35,0x36,0x2b,0x2c,0x69,0x32,0x01,0x35,0xc3,0x91,0xf5,0x07,
                0xd3,0xde,0xba,0xf0,0x96,0xb7,0xe4,0x90,0x29,0xf9,0xc5,0xd7,0x3f,0xe9,0xbf,0x02,
                0x8f,0x26,0x80,0xfa,0xca
            };

            out->control_point_ok = rc_ecdh_check_peer_point(RC_ECDH_CURVE_P521, kControlPoint,
                                                             sizeof(kControlPoint));
            out->control_step = rc_ecdh_last_error_step();
            out->control_code = rc_ecdh_last_error_code();

            /* And the console's own point through the same entry point, so both answers come from one
             * function rather than one from derive_shared and one from here. */
            out->peer_point_ok = rc_ecdh_check_peer_point(RC_ECDH_CURVE_P521, out->peer_key,
                                                          (size_t)out->peer_key_length);

            out->precheck_ok = g_negotiator.last_precheck_ok;
            out->precheck_step = g_negotiator.last_precheck_step;
            out->precheck_code = g_negotiator.last_precheck_code;
            out->derive_fingerprint = g_negotiator.last_derive_fingerprint;
            out->copy_fingerprint = rc_ecdh_fingerprint(out->peer_key, (size_t)out->peer_key_length);
            out->derive_private_length = (unsigned)g_negotiator.last_derive_private_length;
            out->derive_curve = g_negotiator.last_derive_curve;
        }
        goto done;
    }

    out->stream_keys_derived = 1;

    /*
     * SEALING ON, IMMEDIATELY, AND BEFORE ANYTHING ELSE GOES OUT.
     *
     * From here the console authenticates every control packet it receives, so the first unsealed one is
     * the last one it listens to. That includes SACKs, which is the trap this is arranged to avoid: the
     * channel routes every outgoing control packet through one callback, so arming it here makes the
     * SACKs correct by construction rather than by anybody remembering them.
     *
     * The send key seals; the receive key is not needed yet, because GMAC authenticates and does not
     * encrypt - an incoming STREAM_INFO can be parsed as it stands. Verifying what the console sends is
     * a separate job and is not done here [X].
     */
    takion_control_sealer_init(&g_sealer, g_negotiator.send_aes_key, g_negotiator.send_base_iv);
    /*
     * Input becomes sendable at exactly the moment the sealer is armed, and not before: an input
     * packet sent unsealed would be dropped by the console, and one sent with a key position from
     * before agreement would be sealed under the wrong key.
     */
    halyard_input_writer_init(&g_input);
    g_next_input_poll = 0u;
    g_last_input_state_ms = 0u;
    g_stream_channel_ready = 1;
    takion_channel_enable_sealing(&g_stream_channel, takion_control_sealer_seal, &g_sealer);
    out->sealing_on = 1;

    /*
     * AND THE RECEIVE HALF, in COUNTING mode for now.
     *
     * Sealing without verifying is half a mechanism - GMAC authenticates without encrypting, so a
     * control message from anyone parses perfectly well, and until now this port read whatever arrived
     * without asking who wrote it.
     *
     * Counting rather than enforcing on the first hardware run, deliberately. Enforcing a check that has
     * never been observed to pass turns any mistake in the receive key schedule into a session that goes
     * quiet, which is indistinguishable from the console losing interest and is the hardest failure
     * shape to diagnose - this port has already spent a dozen runs on one of those. The run reports the
     * proportion instead, and "0 of N failed" is what earns enforcement.
     */
    takion_control_verifier_init(&g_verifier, g_negotiator.receive_aes_key,
                                 g_negotiator.receive_base_iv);
    /*
     * ENFORCING NOW, and the sample is what changed. b70 held the session open and checked 14 incoming
     * packets with 0 failures, where b69 had exactly one. A single passing 32-bit tag already settled the
     * key schedule - chance is about one in four billion - but it said nothing about whether ALL console
     * traffic is authenticated, and that is the question enforcement actually turns on. Fourteen for
     * fourteen, across heartbeats and SACKs as well as STREAM_INFO, answers it.
     */
    takion_channel_enable_verification(&g_stream_channel, takion_control_verifier_check, &g_verifier,
                                       1 /* drop what does not authenticate */);

    /*
     * STREAM_INFO is the console's answer to the whole negotiation: the resolution it actually chose,
     * and the SPS/PPS the first IDR will be undecodable without, since they are not carried in the video
     * stream itself. It arrives unprompted once sealing is live, and it wants an ack.
     */
    {
        const uint8_t *info_msg = NULL;
        size_t info_len = 0u;

        if (takion_channel_await_control(&g_stream_channel, TAKION_CONTROL_STREAM_INFO,
                                         RC_STREAM_INFO_WAIT_MS, service_control_tick, session,
                                         &info_msg, &info_len)) {
            takion_stream_info info;

            out->stream_info_bytes = (unsigned)info_len;
            memset(&info, 0, sizeof(info));
            if (takion_control_parse_stream_info(info_msg, info_len, &info) && info.has_resolution) {
                /*
                 * The demuxer is built HERE and not earlier, because the video header is what makes it
                 * useful: the SPS/PPS arrive in STREAM_INFO and not in the video stream, so a demuxer
                 * started before this one would emit frames no decoder could open.
                 */
                {
                    stream_demux_sink sink;

                    memset(&sink, 0, sizeof(sink));
                    sink.userdata = NULL;
                    sink.video_frame_ready = on_video_frame;
                    sink.audio_frame_ready = on_audio_frame;
                    sink.video_loss_detected = on_video_loss;

                    memset(&g_tally, 0, sizeof(g_tally));
                    memset(&g_live_stats, 0, sizeof(g_live_stats));
                    g_blit_us_total = 0u;
                    g_blit_worst_us = 0u;
                    g_blits = 0u;
                    g_pictures_dropped = 0u;
                    g_blit_worst_wait_ms = 0u;
                    g_rgb_scale_failed = 0u;
                    g_idr_requests = 0u;
                    g_last_idr_request_ms = 0u;
                    /*
                     * ARMED AT THE START, because at the start we are blind by definition: nothing has
                     * been decoded, so there is no reference chain, and the console sends an IDR when
                     * asked rather than on a timer. b141 is what the 0 here looked like - the crypto
                     * rewrite took loss from 288 units to 4, which removed the only thing that had ever
                     * set this flag. 890 frames arrived, not one of them a keyframe, no IDR was ever
                     * requested and the decoder rejected every frame. The loss had been accidentally
                     * standing in for a request nobody was making.
                     */
                    g_awaiting_keyframe = 1;
                    /*
                     * Starts at what the launch spec declared, since that is what the console is sizing
                     * the stream against; every step down is relative to the claim that produced the
                     * slicing.
                     */
                    g_connquality_enabled = (rec->connection_quality != 0);
                    g_connquality_target = (unsigned)((rec->stream_bitrate_kbps > 0)
                                                      ? rec->stream_bitrate_kbps : 10000);
                    g_next_connquality = rc_time_ms() + RC_CONNQUALITY_INTERVAL_MS;
                    out->connquality_enabled = g_connquality_enabled;
                    g_frame_head = 0;
                    g_frame_count = 0;
                    g_frames_queued = 0u;
                    g_frames_overrun = 0u;
                    g_frames_oversized = 0u;
                    g_queue_worst = 0u;
                    g_last_drain_ms = 0u;
                    g_worst_read_gap_ms = 0u;
                    g_worst_drain_ms = 0u;
                    g_worst_decode_ms = 0u;
                    g_worst_other_ms = 0u;
                    g_ingest_ticks = 0u;
                    g_ingest_calls = 0u;
                    g_crypto_ticks = 0u;
                    g_crypto_calls = 0u;
                    /* The stream's size decides which H.264 level the hardware decoder opens at,
                     * and therefore how much memory it reserves. */
                    /* Audio is optional: if the port will not open the stream continues without it,
                     * which is better than failing a session over sound. */
                    out->audio_ready = rc_audio_init();
                    /* Not fatal if it refuses: a stream you cannot steer is worth more than no
                     * stream, and the report says which happened. */
                    (void)rc_pad_open();
                    rc_decode_live_hint((int)info.width, (int)info.height);
                    /*
                     * BEFORE THE OPEN, because that is where the picture slots are fixed. This sat
                     * after it for one edit and would have shipped as "asked for RSX memory, got main
                     * memory, measured no difference" - a silent fallback reporting a working system.
                     *
                     * ONLY WITH RGB OUTPUT, and that condition is hard rather than cautious: the YUV
                     * path has the SPEs READ the picture to convert it, and a Cell read from RSX memory
                     * is roughly two orders of magnitude slower than a write. Planes there would be the
                     * per-frame PPE sampler mistake again, three million times a frame.
                     */
                    rc_decode_vdec_want_vram(rec->hardware_scale && rec->decoder_rgb);
                    g_live_open = rc_decode_live_open();
                    /*
                     * RECORDED AT OPEN, not at report time. b160 read this in the reporting block, which
                     * runs after rc_decode_live_close() has reset it, so a run decoded entirely by
                     * cellVdec reported "decoded by none" - and the offline probe, gated on the same
                     * stale read, then opened a second decoder for no reason.
                     */
                    g_decode_backend = rc_decode_live_backend_name();
                    g_decode_backend_id = rc_decode_live_backend();
                    if (g_live_open) {
                        rc_decode_live_set_sink(on_picture, NULL);
                        /*
                         * Asking the decoder for RGB removes the SPE's colour pass; it is opt-in while
                         * it is new, since the YUV path is the one with a thousand frames behind it.
                         */
                        g_decoder_rgb = rec->decoder_rgb;
                        /*
                         * WHOEVER SCALES OWNS THE FILTER. With the RSX scaling, the SPE pass is a 1:1
                         * copy and interpolating it would be a null operation at full price - b207 did
                         * exactly that and charged 6,957 us a frame to copy a picture the SPEs had been
                         * converting AND scaling for 3,013. The kernel now refuses it at 1:1 as well;
                         * this says the intent rather than relying on that.
                         */
                        rc_spu_yuv_set_bilinear(rec->hardware_scale ? 0 : rec->bilinear_upscale);
                        out->bilinear_upscale = rec->bilinear_upscale;
                        /*
                         * Both scalers read `bilinear`, and on the RSX any non-zero value means the same
                         * thing: its interpolator has two settings, not three. The SPE's cheaper
                         * row-only mode exists only because full interpolation did not fit a 60 fps
                         * budget in software, and that is not a problem the hardware has.
                         */
                        rc_video_set_rsx_scale(rec->hardware_scale, rec->bilinear_upscale != 0);
                        out->hardware_scale = rec->hardware_scale;

                        rc_overlay_set_system_font(rec->system_font);
                        /*
                         * PREPARED ALWAYS, SHOWN ON REQUEST. The atlas and the panel are built here
                         * whether or not the overlay starts visible, because building them when the
                         * toggle is pressed would do it on the decode thread mid-stream - which is the
                         * b256 stall, and would strike exactly when someone wants the numbers.
                         */
                        rc_overlay_set(1);
                        rc_overlay_show(rec->diagnostics);
                        out->diagnostics = rec->diagnostics;
                        out->overlay_system_font = rc_overlay_using_system_font();
                        snprintf(out->overlay_font_status, sizeof(out->overlay_font_status), "%s",
                                 rc_sysfont_status());
                        g_overlay_t0 = rc_time_ms();
                        g_overlay_asked_w = out->asked_width;
                        g_overlay_asked_h = out->asked_height;
                        g_overlay_asked_fps = out->asked_fps;
                        g_overlay_asked_kbps = out->declared_bitrate_kbps;
                        g_overlay_hw_scale = rec->hardware_scale;
                        if (g_decoder_rgb)
                            rc_decode_live_set_rgb_sink(on_picture_rgb, NULL);
                        /* Started only after the decoder is open and its sink is set: the thread calls
                         * straight into both. */
                        if (!decode_thread_start())
                            out->decode_thread_failed = 1;
                        /* Black, in every buffer, before the first picture lands - otherwise the
                         * stream appears inside a frame of leftover test pattern. */
                        rc_video_clear_all(0x00000000u);
                    }
                    {
                        stream_demux_crypto timed;

                        timed.open_packet = timed_open_packet;
                        timed.ctx = &g_verifier.crypto;
                        stream_demux_init(&g_demux, timed, sink);
                    }
                    if (info.video_header_length > 0u)
                        stream_demux_set_video_header(&g_demux, info.video_header,
                                                      info.video_header_length);
                    out->demux_ready = 1;

                    /*
                     * HEVC IS REFUSED LOUDLY RATHER THAN DECODED BADLY.
                     *
                     * The launch spec asks for "avc" and both console families honour it, so this has
                     * never fired. It exists because of what the failure would look like if it ever
                     * did: cellVdec decodes H.264 and nothing else, and handing it HEVC would not make
                     * it complain - b169 through b171 are three runs of a decoder that accepted every
                     * access unit, reported no error and produced uniformly black pictures, and that
                     * cost days. The demuxer already classifies the stream from the parameter sets,
                     * which is the one place the two codecs cannot be confused, so the answer is free.
                     *
                     * The stream is NOT torn down. Audio decodes independently and a session with
                     * sound and a diagnosis on the screen is worth more than a disconnection.
                     */
                    out->stream_is_hevc = stream_demux_video_is_hevc(&g_demux);
                    g_stream_is_hevc = out->stream_is_hevc;
                }

                out->given_width = (int)info.width;
                out->given_height = (int)info.height;
                out->video_header_bytes = (unsigned)info.video_header_length;
                out->audio_header_bytes = (unsigned)info.audio_header_length;
                out->stream_info_parsed = 1;
            }

            /*
             * Acked whether or not the payload parsed. The ack says "received", and withholding it
             * because this build could not read a field would stall a console that did nothing wrong.
             */
            {
                uint8_t ack[8];
                size_t ack_len = takion_control_build_bare(TAKION_CONTROL_STREAM_INFO_ACK,
                                                           ack, sizeof(ack));
                if (ack_len > 0u
                    && takion_channel_send(&g_stream_channel, TAKION_CHANNEL_SESSION, ack, ack_len))
                    out->stream_info_acked = 1;
            }
        }
    }

    /*
     * HOLD THE SESSION OPEN AND WATCH, rather than ending the moment STREAM_INFO is acked.
     *
     * b69 armed incoming verification and came back with "1 checked, 0 failed". One passing 32-bit tag
     * is strong evidence about the KEY SCHEDULE - chance would be about one in four billion - but it is
     * a thin sample for the different question of whether everything the console sends is authenticated,
     * and the probe was ending before anything else could arrive. A sample of one cannot distinguish
     * "all console traffic is sealed" from "the one packet we happened to see was".
     *
     * So this keeps both channels serviced for a few seconds and tallies what turns up. It is also the
     * beginning of the streaming loop rather than scaffolding: holding a session open while pumping
     * heartbeats and draining the stream channel is exactly what the A/V leg has to do, and doing it
     * here first means the next step inherits something that has run on hardware.
     */
    {
        uint64_t deadline;
        uint64_t next_heartbeat;
        uint64_t next_congestion;
        uint64_t hold_ms;

        /*
         * BOTH THERMAL SAMPLES ARE TAKEN OUTSIDE THE HOLD, and b202 is why. Sampling once a second from
         * inside the loop cost 28 ms a reading - syscall 383 is a hypervisor round trip to a hardware
         * sensor, not a register read - which stalled the drain for 4.4 seconds, lost 3,896 units of
         * 4,175 and put 4 fps on the screen.
         *
         * Two samples still answer the question. Temperature under a sustained load rises towards a
         * steady state and does not come back down inside a run, so the reading after the hold IS the
         * peak, and the one before it is the baseline to subtract. What was lost with the per-second
         * sampling is the shape of the curve, which no fan responds to anyway.
         */
        rc_thermal_sample(&out->thermal);

        /*
         * holdseconds in the pairing record, because the two things measured across a hold settle at
         * very different rates: frame rate and loss are steady within seconds, while a fan responds
         * over minutes, so 30 seconds reports the beginning of a thermal curve and calls it a result.
         * Clamped to an hour so a typo cannot hang the console in a loop with no way out but the power
         * switch.
         */
        hold_ms = (rec->hold_seconds > 0)
                      ? (uint64_t)(rec->hold_seconds > 3600 ? 3600 : rec->hold_seconds) * 1000ULL
                      : (uint64_t)RC_STREAM_HOLD_MS;
        g_hold_ms = (unsigned)hold_ms;

        deadline = rc_time_ms() + hold_ms;
        next_heartbeat = rc_time_ms();
        next_congestion = rc_time_ms() + RC_CONGESTION_INTERVAL_MS;
        g_last_picture_ms = rc_time_ms();

        say(RC_PHASE_CONNECTING, "Connecting", "Waiting for the console to send video", NULL);

        while (rc_time_ms() < deadline) {
            /*
             * THE STREAM CHANNEL'S OWN HEARTBEAT, which is ours to send rather than to answer.
             *
             * Two separate mechanisms are easy to confuse here. On the control session the CONSOLE asks
             * and we reply; on this channel the client sends unprompted once a second and the console's
             * own heartbeats need no answer at all - HalyardTakionStream.cs is explicit that incoming
             * ones require nothing. b70 saw six console messages in six seconds, the last of them type
             * 0x0003, and this port was answering none of them and sending none of its own.
             *
             * Nothing has failed for want of it yet because six seconds is short. A stream is not.
             */
            unsigned channel_id = 0u;
            const uint8_t *message = NULL;
            size_t message_length = 0u;
            int result;
            int drained;
            uint64_t other_started;

            send_periodic(session, out, &next_heartbeat, &next_congestion);

            /*
             * A STREAM THAT STOPS DOES NOT CLOSE THE SOCKET, which is why nothing noticed it before.
             * Quitting the game on the console, or the console going to sleep, simply ends the flow of
             * pictures - the association stays up, the heartbeats keep being answered, and this loop
             * would run out its whole hold showing the last frame or a black screen with no explanation
             * anywhere the viewer can see.
             *
             * Counted from the last PICTURE rather than the last packet, because the packets do not
             * stop: audio and control keep arriving from a console that has stopped sending video.
             */
            if (g_session.phase == RC_PHASE_STREAMING
                && rc_time_ms() - g_last_picture_ms > RC_STREAM_STALL_MS) {
                say(RC_PHASE_FAILED, "The stream stopped",
                    "The console stopped sending video",
                    "Check the console is awake and Remote Play is still running");
                out->stream_stalled = 1;
                break;
            }
            service_control_tick(session);

            /*
             * PEEK BEFORE READING, and this is not an optimisation - it is the only way A/V can be seen
             * at all.
             *
             * One UDP socket carries both the control association and the A/V stream, and
             * takion_channel_poll does an unconditional recvfrom: every packet it does not recognise is
             * consumed and discarded. So a console that has been sending video since STREAM_INFO was
             * acked would look exactly like a console sending nothing, which is what the last two runs
             * reported. MSG_PEEK reads the base type without taking the datagram, so each one can be
             * routed to whichever reader owns it.
             *
             * The base type is the LOW NIBBLE of the first byte. 0 is the control association; 2 and 3
             * are video and audio.
             */
            /*
             * DRAIN A BURST, DO NOT SLEEP BETWEEN PACKETS.
             *
             * This used to handle one A/V packet and then sleep a millisecond. At the ~190 packets a
             * second this stream runs at, that is 190 ms of every second spent asleep with data
             * waiting - on top of 24 ms per frame of decode and colour conversion. Per 33 ms frame
             * period roughly 30 of those 33 ms were committed, so any jitter became a backlog, and the
             * backlog became the 71 lost units and 10 loss events b91 reported where the six-second run
             * had none. The loss was self-inflicted pacing, not the network: 5702 of 5704 packets
             * authenticated.
             *
             * Bounded rather than unbounded, and that bound is not timidity either - the control
             * channel's heartbeats and the console's own messages are serviced by the loop outside
             * this one, and a sustained flood that never yields would starve them.
             */
            drained = drain_av(session, out, &next_heartbeat, &next_congestion);
            /*
             * A negative peek is treated as "nothing waiting", not as an error, and deliberately so.
             * On this platform sockets are lv2 descriptors and errors surface through net_errno rather
             * than errno - a distinction this port has already been caught by once, when
             * fcntl(F_SETFL) returned -1 with nothing useful in errno. Rather than depend on a mapping
             * nobody here has verified, the peek stays advisory: takion_channel_poll below does its own
             * recvfrom and reports a genuine socket failure through its own return.
             */

            /*
             * THE DECODE THAT USED TO BE HERE NOW RUNS ON ITS OWN THREAD - see decode_thread_entry.
             *
             * The history is worth keeping, because each step was right about the problem in front of it
             * and the last one is a different problem. One decode per pass tied the decode rate to the
             * loop rate (b129: 28.5 frames arriving, 21.5 dequeued, 212 dropped). Four per pass with only
             * the timers serviced between left the socket unread for 76 ms and loss went from 1.3% to
             * 6.6% (b130). Draining after every decode fixed that, and b142 shows what is left of it: a
             * 270 ms decode run and a 94 ms gap between reads, because a 21,774 us decode on this thread
             * is 21,774 us during which nothing empties a 124,800-byte buffer.
             *
             * Bounding the work per pass was always a way of sharing one thread between two jobs that
             * both want it continuously. Two threads is the answer that does not need tuning.
             */

            other_started = rc_time_ms();
            result = takion_channel_poll(&g_stream_channel, &channel_id, &message, &message_length);
            if (result == 1) {
                uint32_t type = 0xffffffffu;

                out->held_messages++;
                if (takion_control_peek_type(message, message_length, &type)) {
                    out->held_last_type = (unsigned)type;
                    /* The console re-sends STREAM_INFO if it did not hear our ack. Answering again is
                     * cheap and silence here is expensive. */
                    if (type == TAKION_CONTROL_STREAM_INFO) {
                        uint8_t ack[8];
                        size_t ack_len = takion_control_build_bare(TAKION_CONTROL_STREAM_INFO_ACK,
                                                                   ack, sizeof(ack));
                        if (ack_len > 0u)
                            (void)takion_channel_send(&g_stream_channel, TAKION_CHANNEL_SESSION,
                                                      ack, ack_len);
                        out->held_stream_info_repeats++;
                    }
                }
            } else if (result == -1) {
                out->held_channel_error = 1;
                break;
            }
            {
                unsigned spent = (unsigned)(rc_time_ms() - other_started);

                if (spent > g_worst_other_ms)
                    g_worst_other_ms = spent;
            }

            /*
             * The hardware's block ring holds 42 ms and this loop comes round far faster, so topping it
             * up here needs no pacing of its own - see rc_audio_ps3.h for why there is no audio thread.
             */
            rc_audio_service();

            /* Only when nothing arrived at all. Sleeping with data waiting is what caused the loss. */
            if (drained == 0 && result == 0)
                rc_sleep_ms(2u);
        }
    }

    out->video_frames = g_tally.video_frames;
    out->keyframes = g_tally.keyframes;
    out->audio_frames = g_tally.audio_frames;
    out->video_frame_bytes = g_tally.video_bytes;
    out->audio_frame_bytes = g_tally.audio_bytes;
    out->corrupt_events = g_tally.corrupt_events;
    out->largest_frame = g_tally.largest_frame;
    {
        /*
         * Whatever is left in the last, partial window. ADDED rather than assigned: the congestion loop
         * has been draining these every 200 ms, and take_packet_stats resets on read - assigning here
         * would report only the final fragment and throw away thirty seconds of counting.
         */
        long got = 0, missed = 0;

        stream_demux_take_packet_stats(&g_demux, &got, &missed);
        out->units_received += got;
        out->units_lost += missed;
        g_overlay_units_lost = (unsigned long)out->units_lost;
    }

    if (g_live_open) {
        /* Before the decoder closes, because the thread is inside it. */
        {
            rc_audio_stats a;

            rc_audio_stats_get(&a);
            out->audio_frames_decoded = a.frames_decoded;
            out->audio_decode_errors = a.decode_errors;
            out->audio_blocks = a.blocks_written;
            out->audio_silence = a.silence_written;
            out->audio_overflows = a.ring_overflows;
            out->audio_worst_ring = a.worst_ring;
            out->audio_trims = a.trims;
            out->audio_trimmed = a.trimmed_samples;
            out->audio_last_error = a.last_error;
            out->audio_index_is_address = a.read_index_is_address;
        }
        rc_audio_shutdown();
        decode_thread_stop();
        rc_decode_live_close();
        g_live_open = 0;
    }
    out->decoded_pictures = g_live_stats.pictures_out;
    out->decoded_fed = g_live_stats.frames_in;
    out->decoded_errors = g_live_stats.errors;
    out->decoded_last_error = g_live_stats.last_error;
    out->decoded_width = g_live_stats.width;
    out->decoded_height = g_live_stats.height;
    out->decode_ticks = g_live_stats.decode_ticks;
    out->blits = g_blits;
    out->blit_avg_us = (g_blits > 0u) ? (g_blit_us_total / g_blits) : 0u;
    out->blit_worst_us = g_blit_worst_us;
    out->frames_queued = g_frames_queued;
    out->frames_overrun = g_frames_overrun;
    out->frames_oversized = g_frames_oversized;
    out->queue_worst = g_queue_worst;
    out->worst_read_gap_ms = g_worst_read_gap_ms;
    out->worst_drain_ms = g_worst_drain_ms;
    out->worst_decode_ms = g_worst_decode_ms;
    out->worst_other_ms = g_worst_other_ms;
    {
        uint64_t hz = rc_tick_hz();

        out->ingest_avg_us = (hz > 0u && g_ingest_calls > 0u)
            ? (unsigned)((g_ingest_ticks * 1000000u) / hz / g_ingest_calls)
            : 0u;
        out->crypto_avg_us = (hz > 0u && g_crypto_calls > 0u)
            ? (unsigned)((g_crypto_ticks * 1000000u) / hz / g_crypto_calls)
            : 0u;
    }
    rc_video_scale_info(&out->scaled_width, &out->scaled_height,
                        &out->display_width, &out->display_height);
    out->pictures_dropped = g_pictures_dropped;
    out->blit_worst_wait_ms = g_blit_worst_wait_ms;
    out->rgb_scale_failed = g_rgb_scale_failed;
    out->decoder_rgb = g_decoder_rgb;
    out->frames_too_many_units = g_demux.stat_frames_too_many_units;
    out->decode_backend = g_decode_backend;
    out->decode_backend_id = g_decode_backend_id;
    out->luma_min = (int)g_live_stats.luma_min;
    out->luma_max = (int)g_live_stats.luma_max;
    out->callback_luma_max = (int)rc_decode_vdec_callback_luma_max();
    out->callback_pictures = (int)rc_decode_vdec_callback_pictures();
    out->picture_addr = rc_decode_vdec_picture_addr();
    out->decode_level = rc_decode_vdec_level();
    out->decode_spus = rc_decode_vdec_num_spus();
    out->sps_profile = rc_decode_vdec_sps_profile();
    out->sps_level = rc_decode_vdec_sps_level();
    out->sps_max_ref = rc_decode_vdec_sps_max_ref();
    out->level_clamped = rc_decode_vdec_level_clamped();
    out->clamp_safe = rc_decode_vdec_clamp_safe();
    out->au_bad_start = rc_decode_vdec_au_bad_start();
    out->au_largest = rc_decode_vdec_au_largest();
    out->au_max_nals = rc_decode_vdec_au_max_nals();
    out->au_max_slices = rc_decode_vdec_au_max_slices();
    out->au_last_slices = rc_decode_vdec_au_last_slices();
    out->drop_ring_full = rc_decode_vdec_drop_ring_full();
    out->drop_submit = rc_decode_vdec_drop_submit();
    out->drop_submit_error = rc_decode_vdec_drop_submit_error();
    out->submit_waits = rc_decode_vdec_submit_waits();
    out->drop_collect = rc_decode_vdec_drop_collect();
    out->pictures_overwritten = rc_decode_vdec_pictures_overwritten();
    out->first_nal_types = rc_decode_vdec_first_nal_types();
    out->decode_mem_size = rc_decode_vdec_mem_size();
    out->first_au_len = rc_decode_vdec_first_au(out->first_au);
    out->decode_thread_priority = g_decode_priority;
    out->decode_receive_priority = g_decode_receive_priority;
    out->hold_ms = g_hold_ms;
    rc_video_rsx_scale_stats(&out->rsx_scale_available, &out->rsx_blits, &out->rsx_refused);
    {
        rc_video_info vi;

        if (rc_video_info_get(&vi)) {
            out->display_aspect = vi.aspect;
            out->display_scan_mode = vi.scan_mode;
            out->display_refresh = vi.refresh_rates;
        }
    }
    out->picture_in_vram = rc_decode_vdec_picture_in_vram();
    rc_pad_stats(&out->pad_connected, &out->pad_reads, &out->pad_fresh, &out->pad_changes);
    out->pad_analog_triggers = rc_pad_analog_triggers_seen();
    rc_thermal_sample(&out->thermal);   /* the closing sample - see the note where the hold opens */
    out->idr_requests = g_idr_requests;

    out->verify_checked = g_verifier.checked;
    out->verify_failed = g_verifier.failed;
    out->verify_dropped = g_stream_channel.verify_dropped;
    ok = 1;

done:
    /*
     * The handshake key does not outlive this function whatever happened. It is the key the console's
     * signature is verified under, and there is no reason for it to sit in .bss afterwards.
     */
    memset(handshake_key, 0, sizeof(handshake_key));
    return ok;
}

rc_connect_stage rc_connect(unsigned wake_timeout_ms, rc_connect_log_fn log,
                            const char *const *dirs, int dir_count, rc_connect_result *out)
{
    halyard_pairing_record rec;
    halyard_control_session session;
    int awake = 0;
    uint64_t started;

    memset(out, 0, sizeof(*out));
    memset(&rec, 0, sizeof(rec));
    out->login_verdict = -1;  /* "never arrived" is the honest default, and 0 already means accepted */

    SAY("loading the pairing record");

    /*
     * TRY THE SAME DIRECTORIES THE LOG TRIES, in the same order, rather than the single hard-coded one
     * this used to read.
     *
     * b40 failed here with the record sitting on the console: the log resolves a writable directory by
     * trying four and picking the first that works - which is the install directory - while this looked
     * only in /dev_hdd0/. Two different answers to "where does this program keep its files" in one
     * program, and the person putting the record on the console has no way to know which one applies.
     * Worse, the advice given at the time named the log's directory, so following it moved the record
     * out of the only place this would look.
     *
     * The caller passes the list so it cannot drift from the log's. A NULL list falls back to the
     * compile-time constant, which is what a caller with no opinion gets.
     */
    {
        int loaded = 0;
        int i;

        if (dirs == NULL || dir_count <= 0) {
            loaded = halyard_pairing_file_load(RC_CONNECT_PAIRING_DIR, &rec);
        } else {
            for (i = 0; i < dir_count && !loaded; i++) {
                /* The loader appends "pairing.txt" to the directory part of what it is given. */
                loaded = halyard_pairing_file_load(dirs[i], &rec);
                if (loaded)
                    out->record_dir = dirs[i];
            }
        }

        if (!loaded) {
            out->stage = RC_CONNECT_NO_RECORD;
            return out->stage;
        }
    }
    out->had_record = 1;

    if (rec.registkey_length == 0u) {
        out->stage = RC_CONNECT_BAD_RECORD;
        return out->stage;
    }

    /*
     * THE inet_aton QUESTION, asked before anything can hang on the answer. See rc_connect.h: the core
     * parses every address with inet_aton and this port never has, and the consequence of a wrong
     * result is not an error but a blocking connect to nowhere.
     */
    {
        struct in_addr by_pton, by_aton;

        memset(&by_pton, 0, sizeof(by_pton));
        memset(&by_aton, 0, sizeof(by_aton));

        out->pton_ok = (inet_pton(AF_INET, rec.host, &by_pton) == 1);
        out->aton_ok = (inet_aton(rec.host, &by_aton) != 0);
        out->aton_matches_pton =
            (out->pton_ok && out->aton_ok &&
             memcmp(&by_pton, &by_aton, sizeof(by_pton)) == 0);

        out->host_parsed = out->pton_ok;
    }

    /*
     * DOES fcntl ACTUALLY SET A PS3 SOCKET NON-BLOCKING? Tested with calls that cannot block, because
     * the way to find out by doing I/O is to hang the console, which this has now done twice.
     *
     * fcntl is asked to set O_NONBLOCK and then asked to read the flags back. If sockets are not newlib
     * file descriptors here - which is what SO_NBIO existing suggests - the readback will not carry the
     * flag, and every poll loop in ports/common that relies on it has been running against a blocking
     * socket.
     */
    {
        int probe = socket(PF_INET, SOCK_DGRAM, IPPROTO_UDP);
        if (probe >= 0) {
            int set_rc = fcntl(probe, F_SETFL, O_NONBLOCK);
            int flags = fcntl(probe, F_GETFL, 0);
            int on = 1;
            int nbio_rc = setsockopt(probe, SOL_SOCKET, SO_NBIO, &on, (socklen_t)sizeof(on));

            out->fcntl_set_rc = set_rc;
            out->fcntl_readback_nonblock = (flags != -1) && ((flags & O_NONBLOCK) != 0);
            out->so_nbio_ok = (nbio_rc == 0);
            (void)close(probe);
        }
    }

    SAY(out->aton_matches_pton
        ? "inet_aton agrees with inet_pton"
        : "inet_aton DISAGREES with inet_pton - the core parses addresses with inet_aton");

    SAY("unicast SRCH to the recorded address");

    if (probe_once(rec.host, halyard_discovery_profile_ps5.wake_search_source_port, &awake, 1500u)) {
        out->unicast_replied = 1;
    } else {
        /*
         * The recorded address said nothing. Ask the network at large before concluding the console is
         * absent: if something answers a broadcast and it is NOT the recorded address, the record is
         * stale rather than the console missing, and those need completely different fixes.
         */
        char seen[HALYARD_DISCOVERY_ADDRESS_MAX];

        if (broadcast_find(seen, sizeof(seen), 2500u)) {
            out->broadcast_found = 1;
            out->broadcast_matches = (strcmp(seen, rec.host) == 0);
        }
        out->stage = RC_CONNECT_NO_CONSOLE;
        return out->stage;
    }

    /* Only meaningful once something actually answered. */
    out->was_asleep = !awake;

    if (!awake) {
        started = rc_time_ms();

        SAY("sending WAKEUP");

        if (send_wakeup(&rec, out))
            out->wakeups_sent++;

        out->stage = RC_CONNECT_WAKE_SENT;

        /* Poll SRCH until is_awake flips. Readiness is observed, never acknowledged. */
        while (rc_time_ms() - started < (uint64_t)wake_timeout_ms) {
            rc_sleep_ms(500u);
            out->polls++;
            if (probe_once(rec.host, halyard_discovery_profile_ps5.wake_search_source_port,
                           &awake, 800u) && awake) {
                out->woke_after_ms = (unsigned)(rc_time_ms() - started);
                break;
            }
        }

        if (!awake)
            return out->stage;
    }

    out->stage = RC_CONNECT_AWAKE;

    /*
     * PRE-FLIGHT BEFORE THE BLOCKING CALL. See tcp_port_accepts: the core's connect has no deadline, so
     * entering open() against a port that will not accept is what hung b31.
     */
    SAY("pre-flighting the control port with a bounded TCP connect");

    if (!tcp_port_accepts(rec.host, HALYARD_CONTROL_ARM_PORT, 4000u)) {
        SAY("the control port did not accept - not entering the session open, which cannot time out");
        return out->stage;
    }
    out->tcp_preflight_ok = 1;

    /*
     * REFUSE TO ENTER open() IF THE CORE WOULD PARSE THE ADDRESS DIFFERENTLY. rc_tcp_connect's connect()
     * is blocking and unbounded, so a disagreement here is the difference between a report and a locked
     * console. Better to stop with a finding than to reproduce the hang for a third time.
     */
    if (!out->aton_matches_pton) {
        SAY("refusing to open the session: inet_aton would hand the core a different address,");
        SAY("and its connect() is blocking with no timeout. This is the hang, not a symptom of it.");
        return out->stage;
    }

    /*
     * The control plane: ARM, /sess/init, /sess/ctrl, then the persistent binary channel. All of it is
     * ports/common's, proven against a real PS5 from the 3DS - this contributes nothing but the call.
     */
    SAY("opening the control session (ARM, /sess/init, /sess/ctrl)");

    memset(&session, 0, sizeof(session));
    if (!halyard_control_session_open(&rec, &session))
        return out->stage;

    out->stage = RC_CONNECT_SESSION_OPEN;
    SAY("session open - waiting for SESSION_ID");

    {
        /*
         * Twenty seconds, not eight. b36 watched this console take 12.3 s merely to answer a SRCH after
         * waking, so a window that would have been generous for an already-awake console is not
         * necessarily generous here - and reporting "not willing to stream" about one that is still
         * waking up is a wrong answer rather than a slow one.
         */
        uint64_t deadline = rc_time_ms() + 20000u;

        while (rc_time_ms() < deadline) {
            halyard_control_event ev;
            memset(&ev, 0, sizeof(ev));

            if (!halyard_control_session_service(&session, &ev)) {
                out->session_error = (int)ev.kind;
                break;
            }

            if (ev.kind == HALYARD_CONTROL_EVENT_MESSAGE) {
                out->frames_seen++;
                if (out->first_type == 0u)
                    out->first_type = ev.type;
                out->last_type = ev.type;

                if (ev.type == HALYARD_CTRL_TYPE_HEARTBEAT_REQ)
                    out->heartbeats++;

                /*
                 * THE CONSOLE'S VERDICT ON THE PASSCODE, which b41 could not read and so had to report
                 * a wrong passcode and a silent console as the same thing.
                 *
                 * ports/common now decrypts the console's direction, so the one byte is here. The .NET
                 * side reads plaintext[0] the same way, and established both values by controlled
                 * experiment - see HALYARD_CTRL_LOGIN_ACCEPTED.
                 */
                if (ev.type == HALYARD_CTRL_TYPE_LOGIN && ev.plaintext_length > 0u) {
                    if (ev.plaintext[0] == HALYARD_CTRL_LOGIN_ACCEPTED)
                        out->login_verdict = 0;
                    else if (ev.plaintext[0] == HALYARD_CTRL_LOGIN_REJECTED)
                        out->login_verdict = 1;
                    else
                        out->login_verdict = 2;  /* a third value nobody has seen - say so, don't round it */

                    /* A rejected passcode will not become accepted by waiting out the deadline. */
                    if (out->login_verdict != 0)
                        break;
                }

                /*
                 * THE SIGN-IN GATE, and the reference implementation ANSWERS IT rather than giving up.
                 *
                 * src/Ripcord.Protocol.Halyard/Session/HalyardStreamingSession.cs is the source of truth
                 * here, and on TypeLoginPrompt it says "this user is locked, send the passcode" and
                 * completes a gate that another task is waiting on. ports/ripcord-3ds treats the same
                 * message as fatal - "this build cannot answer one" - which is that port's limitation
                 * rather than the protocol's, and copying it here would have carried a restriction the
                 * reference does not have.
                 *
                 * b39 confirmed this is the gate on hardware: one control frame, type 0x0004, no
                 * heartbeats, no SESSION_ID. So the probe now answers it when a passcode was supplied
                 * out of band, and still merely reports it when none was.
                 */
                if (ev.type == HALYARD_CTRL_TYPE_LOGIN_PROMPT) {
                    out->login_prompt = 1;

                    if (rec.login_pin[0] == '\0') {
                        /* Nothing to answer with - report the gate rather than sit out the deadline. */
                        break;
                    }

                    /*
                     * ONE attempt, where the reference allows five. Its retries exist because a person is
                     * typing and can correct a typo; this passcode came from a file, so re-sending the same
                     * digits would only burn a counter and ask the console the same question twice.
                     *
                     * The counter discipline still matters and lives in ports/common: the submit takes the
                     * next unused value (5 on a fresh session, matching the reference's "first attempt is
                     * 5") and advances it in the same breath, so no IV is ever reused under the session key.
                     */
                    if (!out->login_submitted) {
                        out->login_submitted =
                            halyard_control_session_submit_login(&session, rec.login_pin,
                                                                 strlen(rec.login_pin));
                        SAY(out->login_submitted ? "passcode submitted - waiting for SESSION_ID"
                                                 : "the passcode could not be encoded - not sent");
                        if (!out->login_submitted)
                            break;
                    }

                    /*
                     * Keep waiting on the SAME deadline rather than extending it. The reference measures
                     * the console's session-ready at ~2.3 s after a LAN submit, and the twenty seconds this
                     * loop already had was sized for a console still waking up - so there is room, and
                     * granting more would only slow down the "it never answered" case.
                     *
                     * The console's verdict arrives as LOGIN (0x0005) carrying one byte, and is handled
                     * above - ports/common decrypts the console's direction now, so a wrong passcode is
                     * no longer indistinguishable from silence.
                     */
                    continue;
                }
            }

            if (ev.kind == HALYARD_CONTROL_EVENT_SESSION_READY) {
                out->stage = RC_CONNECT_SESSION_READY;
                break;
            }
            rc_sleep_ms(10u);
        }
    }

    /*
     * TAKION, only if the console said it is willing to stream.
     *
     * Attempting it earlier is not merely premature, it is misleading: a console that has not answered
     * the sign-in gate SILENTLY DROPS every Takion INIT, so the handshake would time out and look like a
     * transport fault rather than the authorisation fault it is. The 3DS port records the same finding.
     *
     * The control session stays OPEN throughout and is serviced by the tick callback - see
     * service_control_tick. Closing it first would cost the session ~15-30 s later, in the middle of
     * exactly the wait this is trying to measure.
     */
    if (out->stage == RC_CONNECT_SESSION_READY) {
        int senkusha_sock = -1;
        int stream_sock = -1;

        SAY("senkusha bring-up on its own UDP port");
        if (takion_bring_up(&g_senkusha_channel, rec.host, RC_SENKUSHA_PORT,
                            RC_SENKUSHA_ATTEMPTS, RC_SENKUSHA_ATTEMPT_MS, &session, &senkusha_sock)) {
            out->senkusha_up = 1;
            out->senkusha_local_tag = (unsigned)g_senkusha_channel.local_tag;
            out->senkusha_peer_tag = (unsigned)g_senkusha_channel.peer_tag;
            out->stage = RC_CONNECT_SENKUSHA_UP;

            SAY("senkusha's gating legs (protocol version, keyless session)");
            (void)senkusha_legs(&session, out);

            SAY("Takion handshake for the stream channel");
            if (takion_bring_up(&g_stream_channel, rec.host, RC_STREAM_PORT,
                                RC_STREAM_ATTEMPTS, RC_STREAM_ATTEMPT_MS, &session, &stream_sock)) {
                out->takion_up = 1;
                out->takion_local_tag = (unsigned)g_stream_channel.local_tag;
                out->takion_peer_tag = (unsigned)g_stream_channel.peer_tag;
                out->stage = RC_CONNECT_TAKION_UP;
                g_stream_rcvbuf = rc_udp_rcvbuf_actual(stream_sock);
                out->stream_rcvbuf = g_stream_rcvbuf;
                out->stream_rcvbuf_asked = RC_UDP_RCVBUF;

                if (stream_session_exchange(&rec, &session, out)) {
                    out->stage = RC_CONNECT_STREAM_KEYS;
                    if (out->stream_info_acked)
                        out->stage = RC_CONNECT_STREAM_READY;
                }
            }
        }

        /*
         * Both sockets close here. This probe stops once the transport is proven, and a channel left
         * open past the end of the run is a channel the console is still counting on.
         */
        /* The negotiator holds four live stream keys. This probe stops here, so they are wiped
         * rather than left in .bss for the remainder of the run. */
        takion_session_negotiator_reset(&g_negotiator);
        takion_control_sealer_reset(&g_sealer);
        takion_control_verifier_reset(&g_verifier);

        if (stream_sock >= 0)
            (void)close(stream_sock);
        if (senkusha_sock >= 0)
            (void)close(senkusha_sock);
    }

    halyard_control_session_close(&session);
    return out->stage;
}
