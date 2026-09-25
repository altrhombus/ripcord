/*
 * See halyard_client.h. Nothing in this file logs anything from the pairing record, and no passcode is
 * ever logged - only how many digits went out.
 *
 * HOW IT IS ORGANISED. connect() is a straight line of stage functions, each ported from one block of
 * the PS3's rc_connect() (ports/ripcord-ps3/source/connect/rc_connect.c, cited by line below) and checked
 * against the .NET reference (HalyardStreamingSession.cs, HalyardTakionStream.cs):
 *
 *   connect_control      4038-4050   ARM, /sess/init, /sess/ctrl
 *   connect_signin       4052-4213   the sign-in gate, SESSION_ID
 *   connect_senkusha     4230-4240   9297, its handshake and the two legs the console gates on
 *   connect_takion       4244-4253   9296, the stream's own association
 *   connect_keys         2697-2888   protocol version, launch spec, SESSION_REQUEST/REPLY
 *   connect_stream_info  2892-3157   sealing, verification, STREAM_INFO and its ack, the demuxer
 *
 * pump() is the PS3's hold loop (3159-3418) with its body split into drain_av (1610-1723),
 * poll_stream_control (3369-3392) and send_periodic (1410-1533). client_end() is its teardown
 * (4262-4328).
 *
 * WHAT A WAIT DOES. Every wait in connect() goes through client_tick(): it services the control session
 * (the console resets a session 15-30 s after heartbeat replies stop, and the Takion waits alone are
 * longer than that - rc_connect.c 405-414) and pulls the host's commands. Anything that must end the
 * session is recorded with end_request() and every loop checks it, so a cancel, a console that closes
 * the control session, and a console that says DISCONNECT all leave by the same road.
 *
 * THE RENDEZVOUS ROUTE (halyard_client.h, "THE RENDEZVOUS ROUTE") reuses every stage above and changes
 * three things, each ported from HalyardStreamingSession and HalyardAccountConsoleSession:
 *
 *   connect_control      over the control leg's 9303 association (halyard_dgram_session.h), no ARM probe
 *   connect_media        new: the A/V leg - its socket, STUN, the host's media negotiation (poll_media),
 *                        the prelude, and up to 8 s for SESSION_ID (StartStreamingAsync)
 *   connect_senkusha     on the A/V leg's socket rather than a socket of its own (RunSenkushaAsync)
 *   connect_stream_ready new: PROBE_REPORT, then up to 10 s for STREAM_READY (SendProbeReportAsync)
 *   connect_takion       on the A/V leg's socket, reading only the console's endpoint
 *
 * WHAT IS NOT HERE, deliberately (the PS3's own concerns, or later slices):
 *   - discovery, wake and re-addressing: the host supplies an address for an awake console
 *   - senkusha's echo and MTU probes: the declared rtt falls back to the PROTOCOL_VERSION round trip,
 *     which is the PS3's own fallback (rc_connect.c 968-975), and the MTU to the declared 1454
 *   - CORRUPT_FRAME feedback (.NET sends it on loss; the core has no builder yet)
 *   - CONNECTION_QUALITY: the PS3's trigger is a cellVdec slice count, and the units are [X]
 *   - the 720p cap and the ECDH "control experiment" (rc_connect.c 2744-2765, 2839-2886): cellVdec and a
 *     PS3 stack question respectively, not protocol
 *   - the stall policy: the host reads both clocks in halyard_client_stats and decides
 */
#include "halyard_client.h"
#include "halyard_client_policy.h"

#include "../halyard/halyard_v1.h"
#include "../net/rc_udp.h"
#include "../platform/rc_platform.h"
#include "../session/halyard_control_session.h"
#include "../session/halyard_ctrl_message.h"
#include "../session/halyard_dgram_session.h"
#include "../session/halyard_launch_spec.h"
#include "../session/halyard_wan_candidates.h"
#include "../stream/stream_demux.h"
#include "../stream/stream_header.h"
#include "../takion/takion_control_proto.h"
#include "../takion/takion_control_sealer.h"
#include "../takion/takion_data_chunk.h"
#include "../takion/takion_reliable_channel.h"
#include "../takion/takion_session_negotiator.h"
#include "../util/rc_base64.h"
#include "../util/rc_log.h"

#include <stdarg.h>
#include <stdio.h>
#include <string.h>
#include <sys/socket.h>
#include <netinet/in.h>
#include <unistd.h>

/* The two UDP ports, in the order the console requires them (rc_connect.c 366-374). */
#define CLIENT_SENKUSHA_PORT 9297u
#define CLIENT_STREAM_PORT   9296u

/*
 * Senkusha's whole budget, handshake and legs together: .NET's 8 s box (RunSenkushaAsync). The PS3
 * bounded only the handshake (10 x 300 ms) and each reply (5 s), which on a console that answers the
 * handshake and then goes quiet cost two further five-second waits before a non-fatal step gave up.
 */
#define CLIENT_SENKUSHA_BUDGET_MS 8000u

/* One control reply on a Takion channel, and STREAM_INFO, which answers nothing and arrives when the
 * console's encoder is ready (rc_connect.c 451-462). */
#define CLIENT_TAKION_REPLY_MS   5000u
#define CLIENT_STREAM_INFO_MS   10000u

/* The sign-in gate: five attempts, eight seconds each (rc_connect.c 3578-3586, and .NET's
 * MaxSignInAttempts/SignInAttemptTimeout, which the PS3 took them from). */
#define CLIENT_SIGNIN_ATTEMPTS 5
#define CLIENT_SIGNIN_WAIT_MS  8000u

/*
 * After an ACCEPTED passcode, how long SESSION_ID may take. Separate from the per-attempt wait above
 * because the two answer different questions: that one is "did the console hear the passcode", this one
 * is "how long does a signed-in console take to offer a session", and the second depends on how awake it
 * was. Measured on a PS5 over the internet on 2026-09-25: woken from rest by the account's command, it
 * sent nothing for 8.5 s after accepting, and the 8 s this used to share failed the session. The same
 * console, already awake, sent SESSION_ID 1 s after accepting. The wait ends the moment SESSION_ID
 * arrives, so a longer budget costs only a failure's time, never a success's.
 */
#define CLIENT_SESSION_AFTER_LOGIN_MS 30000u

/* The stream channel's own cadences (rc_connect.c 804-811; HalyardTakionStream HeartbeatInterval and
 * CongestionInterval). The congestion window is also the stats window. */
#define CLIENT_HEARTBEAT_MS  1000u
#define CLIENT_CONGESTION_MS  200u

/* A/V datagrams taken per drain before yielding (rc_connect.h RC_AV_DRAIN_BURST, b91), and how many
 * drain-and-poll rounds one pump may run before it returns to the host. */
#define CLIENT_DRAIN_BURST  256
#define CLIENT_PUMP_ROUNDS    8

/* Control-session events handled per service: enough to drain a burst without letting a flood own the
 * thread (signin_pump's 32, rc_connect.c 3694-3700). */
#define CLIENT_CONTROL_STEPS 32

/*
 * The longest pump() tells a host to wait. The PS3 slept 2 ms when idle (rc_connect.c 3406-3408); a host
 * waiting on halyard_client_fds() is woken by traffic anyway, and one that merely sleeps must not sleep
 * past a burst's worth of socket buffer.
 */
#define CLIENT_MAX_WAIT_MS 10u

/*
 * The declared MTU (rc_connect.c 840-846, and .NET's LinkMetrics.VendorMtu). Senkusha's MTU legs can
 * confirm it; they are a later slice, so the figure every implementation has always declared stands.
 */
#define CLIENT_DECLARED_MTU 1454

/* The PS3's pre-flight bound for the control port, now inside rc_tcp_connect (slice 0). Stated for the
 * log line only. */

/*
 * The rendezvous route's two waits on the console, both non-fatal, as HalyardStreamingSession has them:
 * SESSION_ID after the A/V prelude (measured 2.3-2.9 s across three captured sessions, so about triple
 * the longest: SessionReadyWindow), and STREAM_READY after PROBE_REPORT (0.5-1.5 s: StreamReadyWindow).
 * Neither ends the session on expiry, because a console that never sends one is better diagnosed by what
 * SESSION_REPLY then says than by a timeout that hides it.
 */
#define CLIENT_SESSION_READY_WINDOW_MS  8000u
#define CLIENT_STREAM_READY_WINDOW_MS  10000u

struct halyard_client {
    halyard_client_config config;          /* resolved; config.record points at `record` */
    halyard_client_callbacks cb;
    halyard_pairing_record record;
    halyard_client_result result;

    halyard_client_end_reason end_pending; /* the first reason to end wins */
    int connect_started;
    int in_connect;                        /* inside connect(): an end is requested, never performed */
    int ended;
    int rest_wanted;
    uint64_t phase_deadline_ms;            /* 0 = none; senkusha's box */

    /* The control plane. */
    halyard_control_session control;
    int control_open;
    int control_dead;
    int session_ready;
    int login_prompt;
    int verdict_new;                       /* a verdict arrived and is unread - see connect_signin */
    int stream_ready;                      /* STREAM_READY seen (the rendezvous route waits for it) */

    /* Takion. */
    int senkusha_sock;
    int stream_sock;
    int senkusha_up;
    int takion_up;
    int sealing_on;
    takion_session_negotiator negotiator;
    takion_control_sealer sealer;
    takion_control_verifier verifier;
    uint8_t handshake_key[16];

    /* The rendezvous route. The legs themselves are at the end, with the other large members. */
    int is_rendezvous;
    int rv_prepared;                       /* the control leg is bound */
    int rv_begun;                          /* and aimed at the console, our Init sent */
    int filter_peer;                       /* the A/V socket reads only `media_peer` */
    struct sockaddr_in media_peer;
    uint64_t stray_dropped;                /* drain_av's share; Takion counts its own */
    struct sockaddr_in stun[HALYARD_CLIENT_STUN_MAX];
    size_t stun_count;
    uint8_t bind_address[4];
    int have_bind_address;
    uint8_t local_hashed_id[HALYARD_CLIENT_HASHED_ID_LENGTH];
    char host_header[48];
    halyard_dgram_control_pipe control_pipe;

    /* Streaming. */
    int streaming;                         /* STREAM_READY reached; pump() does work */
    halyard_input_writer writer;
    halyard_client_input_cadence cadence;
    halyard_client_idr_latch idr;
    uint64_t next_input_ms, next_congestion_ms, next_heartbeat_ms;
    uint64_t window_start_ms, window_bytes;
    uint64_t last_activity_ms, last_video_ms;
    uint64_t packets_received, packets_lost;
    uint64_t video_frames, audio_frames, keyframes;

    /* Scratch, here rather than on a stack: the launch spec, its base64 and the request built from them
     * are ~8 KB together, which breaches a console port's frame limit (rc_connect.c 1069-1073). */
    char launch_spec[HALYARD_LAUNCH_SPEC_MAX];
    char launch_spec_b64[HALYARD_LAUNCH_SPEC_B64_MAX];
    uint8_t request[HALYARD_LAUNCH_SPEC_B64_MAX + 512];
    uint8_t rx[TAKION_MAX_PACKET];
    char log_line[256];

    /* The two associations (~50 KB each) and the demuxer (megabytes: its frame slots), last. */
    takion_reliable_channel senkusha;
    takion_reliable_channel stream;
    halyard_rendezvous_leg control_leg;    /* ~23 KB each: a 9303 association and its buffers */
    halyard_rendezvous_leg media_leg;
    stream_demux demux;
};

size_t halyard_client_struct_size(void)
{
    return sizeof(struct halyard_client);
}

/* ---- Small things ------------------------------------------------------------------------------------ */

#if defined(__GNUC__) || defined(__clang__)
static void client_log(halyard_client *c, int level, const char *fmt, ...) __attribute__((format(printf, 3, 4)));
#endif

static void client_log(halyard_client *c, int level, const char *fmt, ...)
{
    va_list args;

    if (c->cb.log == NULL)
        return;
    va_start(args, fmt);
    (void)vsnprintf(c->log_line, sizeof(c->log_line), fmt, args);
    va_end(args);
    c->cb.log(c->cb.user, level, c->log_line);
}

/* The core's own rc_log lines, delivered through the host's callback - see rc_log_set_sink. */
static void client_rc_log_sink(void *user, const char *line)
{
    halyard_client *c = (halyard_client *)user;

    if (c != NULL && c->cb.log != NULL)
        c->cb.log(c->cb.user, HALYARD_CLIENT_LOG_INFO, line);
}

/* The mbedtls-shaped RNG the negotiator wants: 0 on success, the inverse of rc_random_bytes. Local rather
 * than rc_random_rng_callback because that adaptor lives in each port, not in the core. */
static int client_rng(void *ctx, uint8_t *out, size_t length)
{
    (void)ctx;
    return rc_random_bytes(out, length) ? 0 : 1;
}

static void end_request(halyard_client *c, halyard_client_end_reason reason)
{
    if (c->end_pending == HALYARD_CLIENT_END_NONE)
        c->end_pending = reason;
}

static void announce(halyard_client *c, halyard_client_stage stage)
{
    if (c->cb.stage != NULL)
        c->cb.stage(c->cb.user, stage);
}

static void reach(halyard_client *c, halyard_client_stage stage)
{
    if (stage > c->result.stage)
        c->result.stage = stage;
}

static uint32_t remaining_ms(uint64_t deadline, uint64_t now)
{
    return halyard_client_timer_wait_ms(deadline, now);
}

static int stream_sendto(halyard_client *c, const uint8_t *packet, size_t length)
{
    return sendto(c->stream.sock, packet, length, 0, (const struct sockaddr *)&c->stream.peer,
                  (socklen_t)sizeof(c->stream.peer)) >= 0;
}

/* A bare ControlMessage on the session channel: HEARTBEAT, IDR_REQUEST. */
static int send_bare(halyard_client *c, uint32_t type, unsigned channel)
{
    uint8_t message[8];
    size_t length = takion_control_build_bare(type, message, sizeof(message));

    return length > 0u && takion_channel_send(&c->stream, channel, message, length);
}

/* ---- The control session, and the host's commands ---------------------------------------------------- */

/*
 * What the control session said, absorbed. Ported from signin_absorb (rc_connect.c 3596-3651), minus
 * its per-frame logging of every heartbeat.
 */
static void absorb(halyard_client *c, const halyard_control_event *ev)
{
    if (ev->kind == HALYARD_CONTROL_EVENT_SESSION_READY) {
        c->session_ready = 1;
        return;
    }
    if (ev->kind != HALYARD_CONTROL_EVENT_MESSAGE)
        return;

    /* RunCtrlKeepAliveAsync's TypeStreamReady case: the rendezvous route's A/V leg waits on this. */
    if (ev->type == HALYARD_CTRL_TYPE_STREAM_READY) {
        c->stream_ready = 1;
        return;
    }

    /* b39 confirmed the gate on hardware: one frame of type 0x0004, no heartbeats, no SESSION_ID. */
    if (ev->type == HALYARD_CTRL_TYPE_LOGIN_PROMPT) {
        c->login_prompt = 1;
        c->result.login_prompted = 1;
        return;
    }

    /*
     * The console's verdict on a passcode: one byte, which the .NET side established by controlled
     * experiment (HALYARD_CTRL_LOGIN_ACCEPTED/REJECTED). A third value is reported as unknown rather
     * than rounded to either.
     */
    if (ev->type == HALYARD_CTRL_TYPE_LOGIN && ev->plaintext_length > 0u) {
        uint8_t byte = ev->plaintext[0];

        c->result.login_verdict_byte = (int)byte;
        if (byte == HALYARD_CTRL_LOGIN_ACCEPTED)
            c->result.login_verdict = 1;
        else if (byte == HALYARD_CTRL_LOGIN_REJECTED)
            c->result.login_verdict = 0;
        else
            c->result.login_verdict = -1;
        c->verdict_new = 1;
        client_log(c, HALYARD_CLIENT_LOG_INFO, "client: the console answered the passcode (verdict 0x%02x)",
                   (unsigned)byte);
    }
}

/* Services the control session until it has nothing more to say, bounded. HEARTBEAT_REQ is answered
 * inside halyard_control_session_service. */
static void service_control(halyard_client *c)
{
    int steps;

    if (!c->control_open || c->control_dead)
        return;
    for (steps = 0; steps < CLIENT_CONTROL_STEPS; steps++) {
        halyard_control_event ev;

        memset(&ev, 0, sizeof(ev));
        if (!halyard_control_session_service(&c->control, &ev)) {
            c->control_dead = 1;
            c->result.control_error = (int)ev.kind;
            client_log(c, HALYARD_CLIENT_LOG_WARN, "client: the control session %s",
                       ev.kind == HALYARD_CONTROL_EVENT_CLOSED ? "was closed by the console" : "failed");
            end_request(c, ev.kind == HALYARD_CONTROL_EVENT_CLOSED ? HALYARD_CLIENT_END_CONSOLE_CLOSED
                                                                   : HALYARD_CLIENT_END_CHANNEL_ERROR);
            return;
        }
        if (ev.kind == HALYARD_CONTROL_EVENT_NONE)
            return;
        absorb(c, &ev);
    }
}

/* Pulls the host's commands. DISCONNECT before CANCEL, when both arrive together, because it is the
 * more specific request. */
static void poll_commands(halyard_client *c)
{
    unsigned cmd;

    if (c->cb.poll_commands == NULL)
        return;
    cmd = c->cb.poll_commands(c->cb.user);
    if (cmd == 0u)
        return;
    if (cmd & HALYARD_CLIENT_CMD_KEYFRAME)
        halyard_client_idr_arm(&c->idr);
    if (cmd & HALYARD_CLIENT_CMD_DISCONNECT) {
        if (cmd & HALYARD_CLIENT_CMD_REST_CONSOLE)
            c->rest_wanted = 1;
        end_request(c, HALYARD_CLIENT_END_USER_DISCONNECT);
    }
    if (cmd & HALYARD_CLIENT_CMD_CANCEL)
        end_request(c, HALYARD_CLIENT_END_HOST_CANCEL);
}

/* The one thing every wait in connect() does. A takion_tick_fn, so the handshakes call it too. */
static void client_tick(void *ctx)
{
    halyard_client *c = (halyard_client *)ctx;

    service_control(c);
    poll_commands(c);
}

/* A takion_abort_fn: a pending end, or the current phase's deadline. */
static int client_abort(void *ctx)
{
    halyard_client *c = (halyard_client *)ctx;

    if (c->end_pending != HALYARD_CLIENT_END_NONE)
        return 1;
    return c->phase_deadline_ms != 0u && rc_time_ms() >= c->phase_deadline_ms;
}

/*
 * Waits for one control message of `want_type` on a Takion channel, ticking throughout. Returns 1 with
 * the message (borrowed until the next poll of that channel), 0 on timeout, abort or error.
 *
 * Here rather than takion_channel_await_control because this one can be cancelled, and because on the
 * stream channel a DISCONNECT is an answer: the console hanging up with a reason (a rejected launch spec
 * does exactly that - takion_control_proto.h) should end the session with that reason, not after five
 * seconds as a timeout. On senkusha a channel error is not fatal; on the stream it is.
 */
static int await_control(halyard_client *c, takion_reliable_channel *ch, int is_stream, uint32_t want_type,
                         unsigned timeout_ms, const uint8_t **out_message, size_t *out_length)
{
    uint64_t start = rc_time_ms();

    *out_message = NULL;
    *out_length = 0u;

    while (rc_time_ms() - start < (uint64_t)timeout_ms) {
        unsigned channel = 0u;
        const uint8_t *message = NULL;
        size_t length = 0u;
        int result;

        client_tick(c);
        if (client_abort(c))
            return 0;

        result = takion_channel_poll(ch, &channel, &message, &length);
        if (result == 1) {
            uint32_t type = 0xffffffffu;

            if (!takion_control_peek_type(message, length, &type))
                continue;
            if (type == want_type) {
                *out_message = message;
                *out_length = length;
                return 1;
            }
            if (is_stream && type == TAKION_CONTROL_DISCONNECT) {
                const char *reason = NULL;
                size_t reason_length = 0u;

                if (takion_control_parse_disconnect(message, length, &reason, &reason_length)) {
                    size_t n = reason_length < sizeof(c->result.console_disconnect_reason) - 1u
                                   ? reason_length : sizeof(c->result.console_disconnect_reason) - 1u;
                    memcpy(c->result.console_disconnect_reason, reason, n);
                    c->result.console_disconnect_reason[n] = '\0';
                }
                client_log(c, HALYARD_CLIENT_LOG_WARN, "client: the console sent DISCONNECT (\"%s\")",
                           c->result.console_disconnect_reason);
                end_request(c, HALYARD_CLIENT_END_CONSOLE_CLOSED);
                return 0;
            }
            continue;   /* heartbeats and the like: taken, not wanted */
        }
        if (result == -1) {
            if (is_stream)
                end_request(c, HALYARD_CLIENT_END_CHANNEL_ERROR);
            return 0;
        }
        rc_sleep_ms(10u);
    }
    return 0;
}

/* ---- Stage 1: the control session -------------------------------------------------------------------- */

/*
 * rc_connect.c 4038-4050. The PS3's TCP pre-flight (305-344) is gone: it was a workaround for
 * rc_tcp_connect having no deadline, and that deadline now lives in rc_tcp_connect itself.
 *
 * [X] halyard_control_session_open returns only a bool, so a console refusing /sess and a socket that
 * failed are the same ending here - CHANNEL_ERROR - and its log lines are what tell them apart.
 */
static int connect_control_rendezvous(halyard_client *c);

static int connect_control(halyard_client *c)
{
    announce(c, HALYARD_CLIENT_STAGE_CONTROL_OPEN);
    if (c->is_rendezvous)
        return connect_control_rendezvous(c);
    client_log(c, HALYARD_CLIENT_LOG_INFO, "client: opening the control session (ARM, /sess/init, /sess/ctrl)");

    if (!halyard_control_session_open(&c->record, &c->control)) {
        c->control.sock = -1;
        end_request(c, HALYARD_CLIENT_END_CHANNEL_ERROR);
        return 0;
    }
    c->control_open = 1;
    reach(c, HALYARD_CLIENT_STAGE_CONTROL_OPEN);
    return 1;
}

/* ---- Stage 2: the sign-in gate ----------------------------------------------------------------------- */

/* Services until the console says something that ends the wait. verdict_new is one of those things, so
 * THE CALLER MUST CLEAR IT when it reads it, or the next wait returns at once (rc_connect.c 3671-3687). */
static void signin_wait(halyard_client *c, uint64_t until)
{
    while (rc_time_ms() < until && !c->session_ready && !c->verdict_new) {
        client_tick(c);
        if (c->end_pending != HALYARD_CLIENT_END_NONE)
            return;
        if (!c->session_ready && !c->verdict_new)
            rc_sleep_ms(10u);
    }
}

/*
 * Asks the host for a passcode, servicing the session for as long as the person takes - the PS3's
 * keyboard pump hook (rc_connect.c 3562-3575, 3694-3700) without the keyboard. No timeout of its own:
 * the console's heartbeats are answered throughout, and the host can cancel. Returns 1 with digits in
 * `out`, 0 having requested an end.
 */
static int ask_passcode(halyard_client *c, int retry, char *out, size_t out_size)
{
    if (c->cb.poll_passcode == NULL) {
        client_log(c, HALYARD_CLIENT_LOG_WARN, "client: the console wants a passcode and this host cannot ask for one");
        end_request(c, HALYARD_CLIENT_END_SIGNIN_CANCELLED);
        return 0;
    }
    for (;;) {
        int answer;

        client_tick(c);
        if (c->end_pending != HALYARD_CLIENT_END_NONE)
            return 0;

        memset(out, 0, out_size);
        answer = c->cb.poll_passcode(c->cb.user, retry, out, out_size);
        out[out_size - 1u] = '\0';
        if (answer < 0 || (answer > 0 && out[0] == '\0')) {
            end_request(c, HALYARD_CLIENT_END_SIGNIN_CANCELLED);
            return 0;
        }
        if (answer > 0)
            return 1;
        rc_sleep_ms(10u);
    }
}

/*
 * The passcode loop, rc_connect.c 4093-4208, which follows HalyardStreamingSession.EnsureSignedInAsync.
 * The stored passcode stands in for the first attempt when the record has one, so a rig that carries one
 * never asks anybody. A refusal asks the host again with `retry` counting refusals; silence re-submits
 * the same digits (the PS3's choice: "the same passcode may simply not have landed"), each submit at a
 * fresh counter, which halyard_control_session_submit_login guarantees.
 */
static int signin_passcode(halyard_client *c)
{
    char typed[HALYARD_SESS_LOGIN_PIN_MAX];
    const char *pin;
    int refusals = 0;
    int attempt;

    client_log(c, HALYARD_CLIENT_LOG_INFO, "client: the console's user is locked and it wants a passcode");

    if (c->record.login_pin[0] != '\0') {
        pin = c->record.login_pin;
        client_log(c, HALYARD_CLIENT_LOG_INFO, "client: using the passcode from the pairing record");
    } else {
        if (!ask_passcode(c, 0, typed, sizeof(typed)))
            return 0;
        pin = typed;
    }

    for (attempt = 1; attempt <= CLIENT_SIGNIN_ATTEMPTS; attempt++) {
        if (c->end_pending != HALYARD_CLIENT_END_NONE)
            return 0;

        /* Armed before the submit: on a LAN the answer comes in milliseconds (the .NET note). */
        c->verdict_new = 0;
        c->result.login_verdict = -1;
        if (!halyard_control_session_submit_login(&c->control, pin, strlen(pin))) {
            client_log(c, HALYARD_CLIENT_LOG_ERROR, "client: the passcode could not be sent (digits only)");
            end_request(c, c->control_dead ? HALYARD_CLIENT_END_CONSOLE_CLOSED : HALYARD_CLIENT_END_SIGNIN_REJECTED);
            return 0;
        }
        c->result.login_attempts = attempt;
        client_log(c, HALYARD_CLIENT_LOG_INFO, "client: %u digit(s) submitted, attempt %d",
                   (unsigned)strlen(pin), attempt);

        signin_wait(c, rc_time_ms() + CLIENT_SIGNIN_WAIT_MS);
        if (c->end_pending != HALYARD_CLIENT_END_NONE)
            return 0;
        if (c->session_ready)
            return 1;

        if (c->verdict_new) {
            int verdict = c->result.login_verdict;

            c->verdict_new = 0;
            if (verdict == 0) {
                refusals++;
                client_log(c, HALYARD_CLIENT_LOG_WARN, "client: the console refused that passcode");
                if (attempt == CLIENT_SIGNIN_ATTEMPTS)
                    break;
                if (!ask_passcode(c, refusals, typed, sizeof(typed)))
                    return 0;
                pin = typed;
                continue;
            }
            if (verdict == 1) {
                /* Accepted, and SESSION_ID does not always follow at once - about five seconds on the
                 * rendezvous route while the console renegotiates (the .NET note). */
                reach(c, HALYARD_CLIENT_STAGE_SIGNED_IN);
                announce(c, HALYARD_CLIENT_STAGE_SESSION_READY);
                signin_wait(c, rc_time_ms() + CLIENT_SESSION_AFTER_LOGIN_MS);
                return c->end_pending == HALYARD_CLIENT_END_NONE;
            }
            client_log(c, HALYARD_CLIENT_LOG_WARN, "client: a verdict byte nobody has seen - not retrying");
            end_request(c, HALYARD_CLIENT_END_SIGNIN_REJECTED);
            return 0;
        }
        client_log(c, HALYARD_CLIENT_LOG_WARN, "client: the console did not answer the passcode - sending it again");
    }

    /* Refused every time, or never answered at all: the second is a timeout, not a rejection. */
    end_request(c, refusals > 0 ? HALYARD_CLIENT_END_SIGNIN_REJECTED : HALYARD_CLIENT_END_TIMEOUT);
    return 0;
}

/*
 * rc_connect.c 4052-4213. Waits for SESSION_ID or a LOGIN_PROMPT, whichever comes first, inside
 * signin_prompt_window_ms (the PS3's 20 s: b36). A console that never asks has "signed in" by not
 * asking.
 *
 * SESSION_ID IS REQUIRED BY DEFAULT, because on hardware a console that has not sent it silently drops
 * every Takion INIT, so trying anyway turns an authorisation failure into what looks like a transport one
 * (rc_connect.c 4215-4225, the 3DS port's same finding). .NET's claim that a LAN console streams without
 * it is [X]; require_session_ready < 0 follows .NET instead.
 */
static int connect_signin(halyard_client *c)
{
    uint64_t deadline = rc_time_ms() + (uint64_t)c->config.signin_prompt_window_ms;

    announce(c, HALYARD_CLIENT_STAGE_SIGNED_IN);

    while (rc_time_ms() < deadline && !c->session_ready && !c->login_prompt) {
        client_tick(c);
        if (c->end_pending != HALYARD_CLIENT_END_NONE)
            return 0;
        if (!c->session_ready && !c->login_prompt)
            rc_sleep_ms(10u);
    }

    if (c->login_prompt && !c->session_ready) {
        if (!signin_passcode(c))
            return 0;
    }
    reach(c, HALYARD_CLIENT_STAGE_SIGNED_IN);

    /*
     * THE RENDEZVOUS ROUTE DOES NOT WAIT HERE for SESSION_ID: the console sends it only after the A/V leg's
     * prelude, which connect_media runs. The exception is a passcode, after which it does come here - about
     * five seconds later, while the console renegotiates the connection - and .NET's EnsureSignedInAsync
     * fails the session when it does not ("accepted the passcode but didn't start a session").
     */
    if (c->is_rendezvous) {
        if (c->session_ready)
            reach(c, HALYARD_CLIENT_STAGE_SESSION_READY);
        if (c->login_prompt && !c->session_ready) {
            client_log(c, HALYARD_CLIENT_LOG_WARN, "client: passcode accepted but no SESSION_ID followed");
            end_request(c, HALYARD_CLIENT_END_SIGNIN_NO_SESSION);
            return 0;
        }
        return 1;
    }

    if (c->session_ready) {
        reach(c, HALYARD_CLIENT_STAGE_SESSION_READY);
        return 1;
    }
    if (c->config.require_session_ready) {
        client_log(c, HALYARD_CLIENT_LOG_WARN, "client: no SESSION_ID - the console is not willing to stream");
        end_request(c, c->login_prompt ? HALYARD_CLIENT_END_SIGNIN_NO_SESSION : HALYARD_CLIENT_END_TIMEOUT);
        return 0;
    }
    client_log(c, HALYARD_CLIENT_LOG_WARN, "client: no SESSION_ID - opening Takion anyway, as configured [X]");
    return 1;
}

/* ---- Stage 3: senkusha ------------------------------------------------------------------------------- */

/*
 * The legs the console GATES ON (rc_connect.c 2571-2650, from ports/ripcord-3ds): PROTOCOL_VERSION, then
 * a keyless SESSION exchange whose whole purpose is to have happened. The echo and MTU legs (2652-2665)
 * are a later slice.
 *
 * The version round trip is timed: answering it asks the console for essentially no work, so it is nearly
 * all path time - the PS3's fallback RTT sample, and the one declared until the echo leg arrives.
 */
static void senkusha_legs(halyard_client *c, uint64_t box)
{
    /*
     * PROTOCOL_VERSION_REQUEST{supportedVersions=[9]} as the PS3's literal: field 31 length-delimited is
     * (31 << 3) | 2 = 250, a TWO-byte varint tag. client_test checks it against the builder.
     */
    static const uint8_t kVersionRequest[] = { 0x08, 0x1F, 0xFA, 0x01, 0x02, 0x08, 0x09 };
    static const uint8_t kZeroKey[4] = { 0, 0, 0, 0 };
    takion_session_request request;
    const uint8_t *reply;
    size_t reply_length;
    uint8_t payload[256];
    size_t payload_length;
    uint64_t asked;

    if (!takion_channel_send(&c->senkusha, TAKION_CHANNEL_PROTOCOL_VERSION, kVersionRequest, sizeof(kVersionRequest)))
        return;
    asked = rc_time_ms();
    if (await_control(c, &c->senkusha, 0, TAKION_CONTROL_PROTOCOL_VERSION_ACK,
                      remaining_ms(box, rc_time_ms()) < CLIENT_TAKION_REPLY_MS ? remaining_ms(box, rc_time_ms())
                                                                               : CLIENT_TAKION_REPLY_MS,
                      &reply, &reply_length)) {
        uint64_t rtt = rc_time_ms() - asked;

        c->result.version_rtt_ms = (int)(rtt > 1000u ? 1000u : rtt);
    }
    if (c->end_pending != HALYARD_CLIENT_END_NONE)
        return;

    memset(&request, 0, sizeof(request));
    request.client_version = 9;
    request.session_key = "";
    request.session_key_length = 0;
    request.launch_spec_json = "";
    request.launch_spec_json_length = 0;
    request.encrypted_key = kZeroKey;
    request.encrypted_key_length = sizeof(kZeroKey);

    payload_length = takion_control_build_session_request(&request, payload, sizeof(payload));
    if (payload_length == 0u
        || !takion_channel_send(&c->senkusha, TAKION_CHANNEL_SESSION, payload, payload_length))
        return;
    if (await_control(c, &c->senkusha, 0, TAKION_CONTROL_SESSION_REPLY,
                      remaining_ms(box, rc_time_ms()) < CLIENT_TAKION_REPLY_MS ? remaining_ms(box, rc_time_ms())
                                                                               : CLIENT_TAKION_REPLY_MS,
                      &reply, &reply_length))
        c->result.senkusha_ok = 1;
}

/* rc_connect.c 4226-4240 and takion_bring_up (427-446). Non-fatal throughout, as in both references: a
 * failed senkusha leaves the stream to be attempted, and its own failure is the one that ends things. */
static int connect_senkusha(halyard_client *c)
{
    struct sockaddr_in peer;
    uint64_t box = rc_time_ms() + CLIENT_SENKUSHA_BUDGET_MS;
    int ok;

    announce(c, HALYARD_CLIENT_STAGE_SENKUSHA_UP);

    if (c->is_rendezvous) {
        /*
         * On the A/V leg's own socket, as the captured client does: a first Takion association there, torn
         * down, then a second for the stream. On this route :9297 answers nobody who has not completed a
         * prelude on it, so the LAN's fresh socket would only ever time out - and skipping senkusha got a
         * SESSION_REPLY with no key at all (StartStreamingAsync). Nothing is sent to tear it down, as
         * HalyardSenkusha.DisposeAsync sends nothing; its stale datagrams fail the stream's tag check.
         */
        client_log(c, HALYARD_CLIENT_LOG_INFO, "client: senkusha bring-up on the A/V leg");
        c->phase_deadline_ms = box;
        ok = takion_channel_connect_filtered(&c->senkusha, c->stream_sock, c->media_peer,
                                             c->config.senkusha_attempts, c->config.attempt_interval_ms,
                                             client_tick, c, client_abort, c);
    } else {
        client_log(c, HALYARD_CLIENT_LOG_INFO, "client: senkusha bring-up on %u", CLIENT_SENKUSHA_PORT);

        /* No receive cushion asked for: the handshake and both legs are a handful of small datagrams. */
        c->senkusha_sock = rc_udp_open(c->record.host, CLIENT_SENKUSHA_PORT, &peer, 0);
        if (c->senkusha_sock < 0) {
            client_log(c, HALYARD_CLIENT_LOG_WARN, "client: no senkusha socket - continuing without it");
            return 1;
        }

        c->phase_deadline_ms = box;
        ok = takion_channel_connect_abortable(&c->senkusha, c->senkusha_sock, peer, c->config.senkusha_attempts,
                                              c->config.attempt_interval_ms, client_tick, c, client_abort, c);
    }
    if (ok) {
        c->senkusha_up = 1;
        reach(c, HALYARD_CLIENT_STAGE_SENKUSHA_UP);
        senkusha_legs(c, box);
    }
    c->phase_deadline_ms = 0u;

    if (c->end_pending != HALYARD_CLIENT_END_NONE)
        return 0;
    if (!c->result.senkusha_ok)
        client_log(c, HALYARD_CLIENT_LOG_WARN, "client: senkusha did not complete - continuing, as both references do");
    return 1;
}

/* ---- Stage 4: the stream's Takion -------------------------------------------------------------------- */

/* rc_connect.c 4243-4253, with .NET's budget (100 x 300 ms per phase, cap55) and receive buffer. */
static int connect_takion(halyard_client *c)
{
    struct sockaddr_in peer;

    int ok;

    announce(c, HALYARD_CLIENT_STAGE_TAKION_UP);

    if (c->is_rendezvous) {
        /* The A/V leg's socket, already bound with the receive cushion and preluded (connect_media). */
        client_log(c, HALYARD_CLIENT_LOG_INFO, "client: Takion handshake for the stream on the A/V leg");
        ok = takion_channel_connect_filtered(&c->stream, c->stream_sock, c->media_peer, c->config.stream_attempts,
                                             c->config.attempt_interval_ms, client_tick, c, client_abort, c);
    } else {
        client_log(c, HALYARD_CLIENT_LOG_INFO, "client: Takion handshake for the stream on %u", CLIENT_STREAM_PORT);

        c->stream_sock = rc_udp_open(c->record.host, CLIENT_STREAM_PORT, &peer, c->config.rcvbuf_bytes);
        if (c->stream_sock < 0) {
            end_request(c, HALYARD_CLIENT_END_CHANNEL_ERROR);
            return 0;
        }
        /* Reported, not assumed: SO_RCVBUF is a hint (rc_udp.h), and b128 found lv2 granting 124,800 of 1 MB. */
        c->result.rcvbuf_asked = c->config.rcvbuf_bytes;
        c->result.rcvbuf_granted = rc_udp_rcvbuf_actual(c->stream_sock);

        ok = takion_channel_connect_abortable(&c->stream, c->stream_sock, peer, c->config.stream_attempts,
                                              c->config.attempt_interval_ms, client_tick, c, client_abort, c);
    }
    if (!ok) {
        if (c->end_pending == HALYARD_CLIENT_END_NONE) {
            client_log(c, HALYARD_CLIENT_LOG_WARN, "client: the stream's Takion handshake was not answered");
            end_request(c, HALYARD_CLIENT_END_TIMEOUT);
        }
        return 0;
    }
    c->takion_up = 1;
    reach(c, HALYARD_CLIENT_STAGE_TAKION_UP);
    return 1;
}

/* ---- Stage 5: the key agreement ---------------------------------------------------------------------- */

/*
 * stream_session_exchange, rc_connect.c 2697-2890.
 *
 * The version is negotiated on THIS channel first and the console's choice used, because the ECDH curve
 * follows the negotiated version (b51: a key on a curve the console had not picked, whose signature
 * verified anyway). The launch spec travels encrypted under the control session's field cipher at counter
 * 0 - wire-confirmed, "do not fix the counter" (HalyardStreamingSession.BuildSessionRequest) - and the
 * handshake key inside it is what the console's ECDH signature is checked under, which is why
 * accept_reply refusing a bad signature is the check that matters.
 */
static int connect_keys(halyard_client *c)
{
    /* TakionSessionNegotiator.cs's list; 12 is absent there and here (rc_connect.c 2681-2686). */
    static const uint32_t kSupportedVersions[] = { 9u, 10u, 11u, 13u, 14u, 15u, 16u, 17u };
    halyard_launch_spec_params params;
    uint8_t version_message[64];
    const uint8_t *message;
    size_t message_length;
    size_t spec_length, b64_length, request_length, version_length;
    uint32_t version = TAKION_CLIENT_VERSION;
    int ok = 0;

    announce(c, HALYARD_CLIENT_STAGE_STREAM_KEYS);

    version_length = takion_control_build_protocol_version_request(
        kSupportedVersions, sizeof(kSupportedVersions) / sizeof(kSupportedVersions[0]),
        version_message, sizeof(version_message));
    if (version_length > 0u
        && takion_channel_send(&c->stream, TAKION_CHANNEL_PROTOCOL_VERSION, version_message, version_length)
        && await_control(c, &c->stream, 1, TAKION_CONTROL_PROTOCOL_VERSION_ACK, CLIENT_TAKION_REPLY_MS,
                         &message, &message_length)) {
        uint32_t agreed = 0u;

        /* A missing field is not an error: fall back to what was asked for, as the reference does. */
        if (takion_control_parse_protocol_version_ack(message, message_length, &agreed) && agreed != 0u)
            version = agreed;
    }
    if (c->end_pending != HALYARD_CLIENT_END_NONE)
        goto done;
    c->result.stream_version = (unsigned)version;

    if (!rc_random_bytes(c->handshake_key, sizeof(c->handshake_key))) {
        client_log(c, HALYARD_CLIENT_LOG_ERROR, "client: the platform's CSPRNG refused - no handshake key");
        end_request(c, HALYARD_CLIENT_END_CHANNEL_ERROR);
        goto done;
    }

    memset(&params, 0, sizeof(params));
    params.width = c->config.width;
    params.height = c->config.height;
    params.fps = c->config.fps;
    params.bitrate_kbps = c->config.bitrate_kbps;
    params.mtu = CLIENT_DECLARED_MTU;
    /* The PS3's fallback (rc_connect.c 971-973); .NET declares 0 until its echo probe measures. b115 is
     * why a measured figure beats 0: the console plans its rate against it. */
    params.rtt_ms = c->result.version_rtt_ms;
    params.is_hevc = c->config.allow_hevc;
    /* HDR is an HEVC profile, and "HDR" in the launch spec is itself an inference, never observed [X]. */
    params.is_hdr = c->config.hdr && c->config.allow_hevc;

    spec_length = halyard_launch_spec_build(&params, c->handshake_key, c->launch_spec, sizeof(c->launch_spec));
    if (spec_length == 0u) {
        client_log(c, HALYARD_CLIENT_LOG_ERROR, "client: the launch spec could not be built");
        end_request(c, HALYARD_CLIENT_END_CHANNEL_ERROR);
        goto done;
    }
    halyard_control_streaminfo_crypt(&c->control.ctrl, 0, (const uint8_t *)c->launch_spec,
                                     (uint8_t *)c->launch_spec, spec_length);
    b64_length = rc_base64_encode((const uint8_t *)c->launch_spec, spec_length,
                                  c->launch_spec_b64, sizeof(c->launch_spec_b64));
    if (b64_length == 0u) {
        end_request(c, HALYARD_CLIENT_END_CHANNEL_ERROR);
        goto done;
    }

    request_length = takion_session_negotiator_begin(&c->negotiator, version, c->handshake_key,
                                                     c->launch_spec_b64, b64_length, client_rng, NULL,
                                                     c->request, sizeof(c->request));
    if (request_length == 0u) {
        client_log(c, HALYARD_CLIENT_LOG_ERROR, "client: SESSION_REQUEST could not be built (no ECDH backend?)");
        end_request(c, HALYARD_CLIENT_END_CHANNEL_ERROR);
        goto done;
    }
    c->result.curve = (int)c->negotiator.curve;

    if (!takion_channel_send(&c->stream, TAKION_CHANNEL_SESSION, c->request, request_length)) {
        end_request(c, HALYARD_CLIENT_END_CHANNEL_ERROR);
        goto done;
    }
    if (!await_control(c, &c->stream, 1, TAKION_CONTROL_SESSION_REPLY, CLIENT_TAKION_REPLY_MS,
                       &message, &message_length)) {
        if (c->end_pending == HALYARD_CLIENT_END_NONE) {
            client_log(c, HALYARD_CLIENT_LOG_WARN, "client: no SESSION_REPLY");
            end_request(c, HALYARD_CLIENT_END_TIMEOUT);
        }
        goto done;
    }
    if (!takion_session_negotiator_accept_reply(&c->negotiator, message, message_length, client_rng, NULL)) {
        c->result.reply_reject_reason = c->negotiator.last_reject_reason;
        client_log(c, HALYARD_CLIENT_LOG_ERROR, "client: SESSION_REPLY refused (reason %d)",
                   c->negotiator.last_reject_reason);
        end_request(c, HALYARD_CLIENT_END_REFUSED);
        goto done;
    }
    reach(c, HALYARD_CLIENT_STAGE_STREAM_KEYS);
    ok = 1;

done:
    /* The handshake key does not outlive this function, whatever happened (rc_connect.c 3553-3558), and
     * nor does the launch spec that carried it. */
    memset(c->handshake_key, 0, sizeof(c->handshake_key));
    memset(c->launch_spec, 0, sizeof(c->launch_spec));
    memset(c->launch_spec_b64, 0, sizeof(c->launch_spec_b64));
    memset(c->request, 0, sizeof(c->request));
    return ok;
}

/* ---- Stage 6: sealing and STREAM_INFO ---------------------------------------------------------------- */

static void send_idr(halyard_client *c, uint64_t now);

/* The demuxer's sink. A buffer is borrowed for the call (stream_demux.h), and so is the host's. */
static void on_video_frame(void *userdata, const uint8_t *data, size_t length, int is_keyframe)
{
    halyard_client *c = (halyard_client *)userdata;

    c->video_frames++;
    c->last_video_ms = rc_time_ms();
    if (is_keyframe) {
        c->keyframes++;
        /* Cleared on arrival, not when the request went out: a request that produced nothing has not
         * fixed anything (rc_connect.c 2310-2315). */
        halyard_client_idr_keyframe(&c->idr);
    }
    c->cb.video_frame(c->cb.user, data, length, is_keyframe);
    if (is_keyframe && c->result.stage < HALYARD_CLIENT_STAGE_STREAMING) {
        reach(c, HALYARD_CLIENT_STAGE_STREAMING);
        announce(c, HALYARD_CLIENT_STAGE_STREAMING);
    }
}

static void on_audio_frame(void *userdata, const uint8_t *data, size_t length)
{
    halyard_client *c = (halyard_client *)userdata;

    c->audio_frames++;
    if (c->cb.audio_frame != NULL)
        c->cb.audio_frame(c->cb.user, data, length);
}

/* Loss breaks the reference chain until the next keyframe, which the console sends only when asked
 * (b90). Arm the latch and ask now, throttled (rc_connect.c 2540-2569). */
static void on_video_loss(void *userdata, int first_frame_index, int last_frame_index)
{
    halyard_client *c = (halyard_client *)userdata;

    (void)first_frame_index;
    (void)last_frame_index;
    halyard_client_idr_arm(&c->idr);
    send_idr(c, rc_time_ms());
}

/*
 * rc_connect.c 2892-3157.
 *
 * SEALING ON BEFORE ANYTHING ELSE GOES OUT: from here the console authenticates every control packet, SACKs
 * included, and the channel routes all of them through one callback, so arming it here makes the SACKs
 * right by construction. VERIFICATION ENFORCED from the start: b70 checked 14 of 14 incoming packets, and
 * the PS3 has enforced since (2936-2944). INPUT SENDABLE only now, at exactly the moment the sealer is
 * armed (2905-2913).
 *
 * STREAM_INFO is acked whether or not it parsed - the ack says "received" (3144-3155) - and the demuxer is
 * built only now, because the parameter sets arrive here and not in the video stream. It is built even
 * when STREAM_INFO did not parse, where the PS3 left it unbuilt: a stream with no parameter sets is still
 * audio, and a host that cannot decode the video can say so.
 *
 * THE ACK GOES ON CHANNEL 9, which the .NET reference records as wire-confirmed (TakionDataChunk
 * ChannelStreamInfo) and the 3DS port uses on hardware. The PS3 sends it on channel 1 and was accepted
 * too; this follows the capture.
 */
static int connect_stream_info(halyard_client *c)
{
    const uint8_t *message;
    size_t message_length;
    takion_stream_info info;
    stream_demux_sink sink;
    int parsed;

    announce(c, HALYARD_CLIENT_STAGE_STREAM_READY);

    takion_control_sealer_init(&c->sealer, c->negotiator.send_aes_key, c->negotiator.send_base_iv);
    takion_channel_enable_sealing(&c->stream, takion_control_sealer_seal, &c->sealer);
    c->sealing_on = 1;
    halyard_input_writer_init(&c->writer);
    memset(&c->cadence, 0, sizeof(c->cadence));

    takion_control_verifier_init(&c->verifier, c->negotiator.receive_aes_key, c->negotiator.receive_base_iv);
    takion_channel_enable_verification(&c->stream, takion_control_verifier_check, &c->verifier,
                                       1 /* drop what does not authenticate */);

    if (!await_control(c, &c->stream, 1, TAKION_CONTROL_STREAM_INFO, CLIENT_STREAM_INFO_MS,
                       &message, &message_length)) {
        if (c->end_pending == HALYARD_CLIENT_END_NONE) {
            client_log(c, HALYARD_CLIENT_LOG_WARN, "client: the console never described the stream");
            end_request(c, HALYARD_CLIENT_END_TIMEOUT);
        }
        return 0;
    }

    memset(&info, 0, sizeof(info));
    parsed = takion_control_parse_stream_info(message, message_length, &info) && info.has_resolution;

    memset(&sink, 0, sizeof(sink));
    sink.userdata = c;
    sink.video_frame_ready = on_video_frame;
    sink.audio_frame_ready = on_audio_frame;
    sink.video_loss_detected = on_video_loss;
    stream_demux_init(&c->demux, stream_demux_packet_crypto(&c->verifier.crypto), sink);
    if (parsed && info.video_header_length > 0u)
        stream_demux_set_video_header(&c->demux, info.video_header, info.video_header_length);

    /* Before the host's callback, so a slow host cannot delay it; the message stays valid because a send
     * does not poll the channel. */
    if (!send_bare(c, TAKION_CONTROL_STREAM_INFO_ACK, TAKION_CHANNEL_STREAM_INFO)) {
        end_request(c, HALYARD_CLIENT_END_CHANNEL_ERROR);
        return 0;
    }

    if (parsed) {
        c->result.stream_info_parsed = 1;
        c->result.stream_width = info.width;
        c->result.stream_height = info.height;
        c->result.stream_is_hevc = stream_demux_video_is_hevc(&c->demux);
        if (c->cb.stream_info != NULL) {
            halyard_client_stream_info out;

            memset(&out, 0, sizeof(out));
            out.width = info.width;
            out.height = info.height;
            out.is_hevc = c->result.stream_is_hevc;
            out.video_header = info.video_header;
            out.video_header_length = info.video_header_length;
            c->cb.stream_info(c->cb.user, &out);
        }
    } else {
        client_log(c, HALYARD_CLIENT_LOG_WARN, "client: STREAM_INFO did not parse - acked anyway");
    }

    reach(c, HALYARD_CLIENT_STAGE_STREAM_READY);
    return 1;
}

/* ---- Streaming --------------------------------------------------------------------------------------- */

static void begin_streaming(halyard_client *c)
{
    uint64_t now = rc_time_ms();

    /* ARMED AT THE START, because at the start we are blind by definition (b141, rc_connect.c 2987-2996). */
    halyard_client_idr_reset(&c->idr);
    halyard_client_idr_arm(&c->idr);

    c->next_input_ms = now;
    c->next_heartbeat_ms = now;
    c->next_congestion_ms = now + CLIENT_CONGESTION_MS;
    c->window_start_ms = now;
    c->window_bytes = 0u;
    c->last_activity_ms = now;
    c->last_video_ms = now;
    c->streaming = 1;
}

/* request_idr, rc_connect.c 2525-2538. */
static void send_idr(halyard_client *c, uint64_t now)
{
    if (!c->streaming)
        return;
    if (halyard_client_idr_due(&c->idr, now))
        (void)send_bare(c, TAKION_CONTROL_IDR_REQUEST, TAKION_CHANNEL_SESSION);
}

/*
 * send_input, rc_connect.c 1238-1408, without the PS3's in-session menu and synthesised PS press: those
 * are a front end's, and a host that wants one neutralises the state before handing it over. Nothing is
 * sent when poll_input says no controller - neutral is a position, and absence is a different statement
 * (1247-1254). The sealer encrypts and tags input together, at the sealer's shared key position.
 */
static void send_input(halyard_client *c, uint64_t now)
{
    halyard_input_state in;
    halyard_client_input_packets packets;

    memset(&in, 0, sizeof(in));
    if (!c->cb.poll_input(c->cb.user, &in))
        return;

    halyard_client_input_step(&c->cadence, &c->writer, &in, now, &packets);
    if (packets.history_length > 0u) {
        takion_control_sealer_seal_input(&c->sealer, packets.history, packets.history_length,
                                         HALYARD_INPUT_HEADER_LENGTH);
        if (stream_sendto(c, packets.history, packets.history_length))
            c->result.input_history_sent++;
    }
    if (packets.state_length > 0u) {
        takion_control_sealer_seal_input(&c->sealer, packets.state, packets.state_length,
                                         HALYARD_INPUT_HEADER_LENGTH);
        if (stream_sendto(c, packets.state, packets.state_length))
            c->result.input_state_sent++;
    }
}

static void report_stats(halyard_client *c, uint64_t now)
{
    halyard_client_stats stats;
    uint64_t window = now - c->window_start_ms;

    if (c->cb.stats != NULL) {
        memset(&stats, 0, sizeof(stats));
        stats.packets_received = c->packets_received;
        stats.packets_lost = c->packets_lost;
        stats.video_frames = c->video_frames;
        stats.audio_frames = c->audio_frames;
        stats.keyframes = c->keyframes;
        stats.kbps = halyard_client_kbps(c->window_bytes, window);
        stats.ms_since_console_activity = (uint32_t)((now - c->last_activity_ms) > UINT32_MAX
                                                         ? UINT32_MAX : (now - c->last_activity_ms));
        stats.ms_since_video_frame = (uint32_t)((now - c->last_video_ms) > UINT32_MAX
                                                    ? UINT32_MAX : (now - c->last_video_ms));
        stats.idr_requests = c->idr.requests;
        stats.window_ms = (uint32_t)(window > UINT32_MAX ? UINT32_MAX : window);
        stats.verify_dropped = (uint64_t)c->stream.verify_dropped;
        c->cb.stats(c->cb.user, &stats);
    }
    c->window_start_ms = now;
    c->window_bytes = 0u;
}

/*
 * send_periodic, rc_connect.c 1410-1533: the things owed to the console on a clock, checked wherever
 * there is a moment - including between drained packets, which is what lets the drain bound be sized
 * for a burst alone (1211-1218).
 */
static void send_periodic(halyard_client *c)
{
    uint64_t now = rc_time_ms();

    /* Input is a wall-clock activity like the heartbeat, not tied to how much video is arriving
     * (1229-1232). */
    if (c->cb.poll_input != NULL && halyard_client_timer_due(&c->next_input_ms, now, HALYARD_CLIENT_INPUT_POLL_MS))
        send_input(c, now);

    /* Congestion feedback: what arrived and what did not. take_packet_stats RESETS on read, so each
     * report covers its own window (1422-1448). The stats window rides the same tick. */
    if (halyard_client_timer_due(&c->next_congestion_ms, now, CLIENT_CONGESTION_MS)) {
        long got = 0, missed = 0;
        uint8_t feedback[TAKION_CONGESTION_PACKET_SIZE];
        size_t length;

        stream_demux_take_packet_stats(&c->demux, &got, &missed);
        if (got > 0)
            c->packets_received += (uint64_t)got;
        if (missed > 0)
            c->packets_lost += (uint64_t)missed;
        length = takion_congestion_build((unsigned long)(got < 0 ? 0 : got), (unsigned long)(missed < 0 ? 0 : missed),
                                         feedback, sizeof(feedback));
        if (length > 0u) {
            takion_control_sealer_seal_congestion(&c->sealer, feedback, length);
            if (stream_sendto(c, feedback, length))
                c->result.congestion_sent++;
        }
        report_stats(c, now);
    }

    /* Still blind? Ask again (1512-1518): the request is a demand for the repair, not an acknowledgement. */
    send_idr(c, now);

    /* Ours to SEND, unprompted; the console's own need no reply (1520-1532). */
    if (halyard_client_timer_due(&c->next_heartbeat_ms, now, CLIENT_HEARTBEAT_MS)) {
        if (send_bare(c, TAKION_CONTROL_HEARTBEAT, TAKION_CHANNEL_SESSION))
            c->result.heartbeats_sent++;
    }
}

/*
 * drain_av, rc_connect.c 1610-1723. PEEK, because one socket carries the control association and A/V
 * both: base type 0 is control and belongs to takion_channel_poll, anything else is taken here. The
 * probe's second GMAC pass is not ported (1667-1687): the demuxer verifies and decrypts through its own
 * crypto seam, and b137 measured what paying twice cost.
 */
static int drain_av(halyard_client *c)
{
    int drained = 0;

    while (drained < CLIENT_DRAIN_BURST) {
        uint8_t peek[STREAM_HEADER_LENGTH];
        ssize_t peeked;
        ssize_t n;

        send_periodic(c);

        if (c->filter_peer) {
            /* The rendezvous A/V socket: only the console's endpoint is the console (HalyardTakionStream.
             * ReceiveLoopAsync's RemoteEndPoint check). Anything else is taken and dropped, and is not
             * activity. Counted against the burst, so a flood cannot hold the pump here. */
            struct sockaddr_in from;
            socklen_t from_length = (socklen_t)sizeof(from);

            memset(&from, 0, sizeof(from));
            peeked = recvfrom(c->stream_sock, peek, sizeof(peek), MSG_PEEK, (struct sockaddr *)&from, &from_length);
            if (peeked <= 0)
                break;
            if (from.sin_family != AF_INET || from.sin_addr.s_addr != c->media_peer.sin_addr.s_addr
                || from.sin_port != c->media_peer.sin_port) {
                (void)recvfrom(c->stream_sock, c->rx, sizeof(c->rx), 0, NULL, NULL);
                c->stray_dropped++;
                drained++;
                continue;
            }
        } else {
            peeked = recvfrom(c->stream_sock, peek, sizeof(peek), MSG_PEEK, NULL, NULL);
            if (peeked <= 0)
                break;
        }
        c->last_activity_ms = rc_time_ms();
        if ((unsigned)(peek[0] & 0x0fu) == 0u)
            break;

        n = recvfrom(c->stream_sock, c->rx, sizeof(c->rx), 0, NULL, NULL);
        if (n <= 0)
            break;
        drained++;
        if (n >= (ssize_t)STREAM_HEADER_LENGTH) {
            c->window_bytes += (uint64_t)n;
            stream_demux_ingest(&c->demux, c->rx, (size_t)n);
        }
        if (c->end_pending != HALYARD_CLIENT_END_NONE)
            break;
    }
    return drained;
}

/* The control half of the hold loop, rc_connect.c 3369-3392, plus the console's DISCONNECT. Returns 1 if
 * a whole message arrived. */
static int poll_stream_control(halyard_client *c)
{
    unsigned channel = 0u;
    const uint8_t *message = NULL;
    size_t length = 0u;
    uint32_t type = 0xffffffffu;
    int result = takion_channel_poll(&c->stream, &channel, &message, &length);

    if (result == -1) {
        end_request(c, HALYARD_CLIENT_END_CHANNEL_ERROR);
        return 0;
    }
    if (result != 1)
        return 0;
    c->last_activity_ms = rc_time_ms();
    if (!takion_control_peek_type(message, length, &type))
        return 1;

    /* The console re-sends STREAM_INFO if it did not hear the ack; answering again is cheap. */
    if (type == TAKION_CONTROL_STREAM_INFO) {
        (void)send_bare(c, TAKION_CONTROL_STREAM_INFO_ACK, TAKION_CHANNEL_STREAM_INFO);
        c->result.stream_info_repeats++;
    } else if (type == TAKION_CONTROL_DISCONNECT) {
        const char *reason = NULL;
        size_t reason_length = 0u;

        if (takion_control_parse_disconnect(message, length, &reason, &reason_length)) {
            size_t n = reason_length < sizeof(c->result.console_disconnect_reason) - 1u
                           ? reason_length : sizeof(c->result.console_disconnect_reason) - 1u;
            memcpy(c->result.console_disconnect_reason, reason, n);
            c->result.console_disconnect_reason[n] = '\0';
        }
        client_log(c, HALYARD_CLIENT_LOG_INFO, "client: the console sent DISCONNECT (\"%s\")",
                   c->result.console_disconnect_reason);
        end_request(c, HALYARD_CLIENT_END_CONSOLE_CLOSED);
    }
    return 1;
}

/* ---- Ending ------------------------------------------------------------------------------------------ */

/*
 * GOODBYE, rc_connect.c 4262-4310: REST_MODE on the control channel first, and only when asked - cap52
 * isolated it as the one difference between a rest-on and a rest-off disconnect - then the Takion
 * DISCONNECT, which is byte-identical either way. Both best-effort: a console that never sees them is no
 * worse off, so nothing is checked and nothing waits. Each is sent at most once.
 */
static void say_goodbye(halyard_client *c)
{
    if (c->rest_wanted && !c->result.rest_requested && c->control_open && !c->control_dead) {
        if (halyard_control_session_send(&c->control, HALYARD_CTRL_TYPE_REST_MODE, NULL, 0)) {
            c->result.rest_requested = 1;
            client_log(c, HALYARD_CLIENT_LOG_INFO, "client: asked the console to rest");
        }
    }
    if (c->takion_up && !c->result.disconnect_sent) {
        uint8_t bye[16];
        size_t length = takion_control_build_disconnect(NULL, bye, sizeof(bye));

        if (length > 0u && takion_channel_send(&c->stream, TAKION_CHANNEL_SESSION, bye, length)) {
            c->result.disconnect_sent = 1;
            client_log(c, HALYARD_CLIENT_LOG_INFO, "client: said goodbye (Takion DISCONNECT)");
        }
    }
}

/* Teardown, once: goodbye, keys wiped, sockets closed (rc_connect.c 4312-4328), ENDED announced. */
static void client_end(halyard_client *c)
{
    if (c->ended)
        return;
    c->ended = 1;
    if (c->end_pending == HALYARD_CLIENT_END_NONE)
        c->end_pending = HALYARD_CLIENT_END_CHANNEL_ERROR;
    c->result.end_reason = c->end_pending;

    say_goodbye(c);

    if (c->sealing_on) {
        c->result.verify_checked = (uint64_t)c->verifier.checked;
        c->result.verify_failed = (uint64_t)c->verifier.failed;
    }
    takion_session_negotiator_reset(&c->negotiator);
    takion_control_sealer_reset(&c->sealer);
    takion_control_verifier_reset(&c->verifier);
    memset(c->handshake_key, 0, sizeof(c->handshake_key));

    if (c->is_rendezvous) {
        /*
         * The control connection first, while its socket is open: on datagrams its polite close is a
         * Close chunk, and a console never sent one keeps the session live and refuses further cloud
         * sessions until rebooted. The A/V socket is the media leg's, and closes with it.
         */
        if (c->control_open)
            halyard_control_session_close(&c->control);
        c->result.stray_dropped = c->stray_dropped + (uint64_t)c->stream.stray_dropped
                                  + (uint64_t)c->senkusha.stray_dropped;
        halyard_rendezvous_leg_close(&c->media_leg);
        halyard_rendezvous_leg_close(&c->control_leg);
        c->stream_sock = -1;
    }
    if (c->stream_sock >= 0)
        (void)close(c->stream_sock);
    if (c->senkusha_sock >= 0)
        (void)close(c->senkusha_sock);
    c->stream_sock = -1;
    c->senkusha_sock = -1;
    c->stream.sock = -1;
    c->senkusha.sock = -1;
    if (c->control_open)
        halyard_control_session_close(&c->control);
    c->control.sock = -1;
    c->control_open = 0;
    c->streaming = 0;
    c->takion_up = 0;

    /* The record carries the registration key and the companion. Nothing needs it after this. */
    memset(&c->record, 0, sizeof(c->record));

    client_log(c, HALYARD_CLIENT_LOG_INFO, "client: ended at stage %d, reason %d (verified %lu, failed %lu)",
               (int)c->result.stage, (int)c->result.end_reason, (unsigned long)c->result.verify_checked,
               (unsigned long)c->result.verify_failed);
    announce(c, HALYARD_CLIENT_STAGE_ENDED);
}

/* ---- The rendezvous route -------------------------------------------------------------------------- */

static void control_leg_log(void *ctx, const char *line)
{
    client_log((halyard_client *)ctx, HALYARD_CLIENT_LOG_DEBUG, "client: control 9303: %s", line);
}

static void media_leg_log(void *ctx, const char *line)
{
    client_log((halyard_client *)ctx, HALYARD_CLIENT_LOG_DEBUG, "client: A/V leg: %s", line);
}

/*
 * HalyardDatagramControlOptions for a leg, with the one thing only C needs: every blocking 9303 stage ticks
 * the client, so the control session keeps answering heartbeats while the A/V leg's prelude runs, and a
 * host's cancel ends a stage at once rather than at its deadline.
 */
static void leg_options(halyard_client *c, halyard_dgram_options *o, void (*log)(void *, const char *))
{
    halyard_dgram_options_default(o);
    o->stage_timeout_ms = c->config.dgram_stage_timeout_ms;
    o->receive_timeout_ms = c->config.dgram_receive_timeout_ms;
    o->log = log;
    o->log_ctx = c;
    o->tick = client_tick;
    o->abort = client_abort;
    o->tick_ctx = c;
}

/* What a failed 9303 stage costs the session. ABORTED means an end is already pending, and it stands. */
static void dgram_failed(halyard_client *c, halyard_dgram_channel_status status, const char *what)
{
    c->result.dgram_status = (int)status;
    if (status == HALYARD_DGRAM_CHANNEL_ABORTED)
        return;
    client_log(c, HALYARD_CLIENT_LOG_WARN, "client: %s failed (9303 status %d)", what, (int)status);
    end_request(c, status == HALYARD_DGRAM_CHANNEL_TIMEOUT ? HALYARD_CLIENT_END_TIMEOUT
                                                            : HALYARD_CLIENT_END_CHANNEL_ERROR);
}

static void describe_leg(const halyard_rendezvous_leg *leg, halyard_client_leg *out)
{
    memset(out, 0, sizeof(*out));
    out->local_port = leg->local_port;
    out->endpoint_independent = -1;
    if (leg->have_mapping) {
        out->has_reflexive = 1;
        memcpy(out->reflexive_address, leg->mapping.reflexive.address, 4);
        out->reflexive_port = leg->mapping.reflexive.port;
        out->endpoint_independent = leg->mapping.endpoint_independent;
    }
}

/* Binds a leg and asks STUN about it: the half of HalyardAccountConsoleSession that precedes an OFFER. */
static int open_leg(halyard_client *c, halyard_rendezvous_leg *leg, uint16_t port, int rcvbuf, const char *name)
{
    rc_stun_gather_status gathered;

    if (!halyard_rendezvous_leg_open(leg, c->have_bind_address ? c->bind_address : NULL, port, rcvbuf)) {
        client_log(c, HALYARD_CLIENT_LOG_ERROR, "client: could not bind the %s leg's socket", name);
        return 0;
    }
    gathered = halyard_rendezvous_leg_gather(leg, c->stun_count > 0u ? c->stun : NULL, c->stun_count,
                                             c->config.stun_attempts, c->config.stun_timeout_ms);
    if (leg->have_mapping) {
        client_log(c, HALYARD_CLIENT_LOG_INFO,
                   "client: %s leg on local port %u, reflexive %u.%u.%u.%u:%u (%s)", name,
                   (unsigned)leg->local_port, (unsigned)leg->mapping.reflexive.address[0],
                   (unsigned)leg->mapping.reflexive.address[1], (unsigned)leg->mapping.reflexive.address[2],
                   (unsigned)leg->mapping.reflexive.address[3], (unsigned)leg->mapping.reflexive.port,
                   leg->mapping.endpoint_independent == 1   ? "the NAT maps consistently"
                   : leg->mapping.endpoint_independent == 0 ? "the NAT maps per destination: a distant console "
                                                              "will NOT reach this"
                                                            : "NAT behaviour undetermined");
    } else {
        client_log(c, HALYARD_CLIENT_LOG_INFO,
                   "client: %s leg on local port %u, no reflexive address (STUN status %d); LOCAL only", name,
                   (unsigned)leg->local_port, (int)gathered);
    }
    return 1;
}

/*
 * HalyardStreamingSession.ConnectAsync's control plane, over HalyardDatagramSessionControlChannel: no ARM
 * probe (it arms a TCP listener this route does not use), RP-ConPath 3, and the request spelled as the
 * capture spells it. The prelude was begun by halyard_client_rendezvous_begin and is established by the
 * first open, which is idempotent, exactly as ConnectAsync's EstablishAsync is.
 */
static int connect_control_rendezvous(halyard_client *c)
{
    halyard_control_open_options options;

    client_log(c, HALYARD_CLIENT_LOG_INFO, "client: opening the control session over 9303 (/sess/init, /sess/ctrl)");

    halyard_dgram_control_pipe_init(&c->control_pipe, &c->control_leg.channel, c->control_leg.sock);
    memset(&options, 0, sizeof(options));
    options.pipe = &c->control_pipe.pipe;
    options.skip_arm_probe = 1;
    options.connection_path = HALYARD_ROUTE_RENDEZVOUS;
    options.host_header = c->host_header;
    options.init_version_header = "Rp-Version";

    if (!halyard_control_session_open_with(&c->record, &options, &c->control)) {
        c->control.sock = -1;
        if (c->control_pipe.last_status != HALYARD_DGRAM_CHANNEL_OK)
            dgram_failed(c, c->control_pipe.last_status, "the control association");
        end_request(c, HALYARD_CLIENT_END_CHANNEL_ERROR);
        return 0;
    }
    c->control_open = 1;
    reach(c, HALYARD_CLIENT_STAGE_CONTROL_OPEN);
    return 1;
}

/* Services until `flag` is set or `window_ms` passes. Returns how long it took, or -1 for never. */
static int wait_flag(halyard_client *c, const int *flag, unsigned window_ms)
{
    uint64_t start = rc_time_ms();

    while (!*flag && rc_time_ms() - start < (uint64_t)window_ms) {
        client_tick(c);
        if (c->end_pending != HALYARD_CLIENT_END_NONE)
            return -1;
        if (!*flag)
            rc_sleep_ms(10u);
    }
    return *flag ? (int)(rc_time_ms() - start) : -1;
}

/*
 * StartStreamingAsync's rendezvous branch, up to the senkusha probe, with PrepareStreamAsync inlined:
 *
 *   the A/V leg's own socket and STUN mapping - its own connection on its own port, so its own discovery
 *   (offering only the local candidate left the control plane working off-network while the media prelude
 *   went unanswered); then the host's media negotiation (NegotiateMediaAsync, through poll_media); then the
 *   same 88-byte prelude the control association opened with, on the socket Takion will use; then up to
 *   8 s for SESSION_ID, because the console does not serve Takion the moment the prelude finishes - every
 *   captured session sits quiet two to three seconds, sends SESSION_ID, and only then is a single Takion
 *   INIT answered. Waiting is non-fatal: a console that never sends it is diagnosed by what follows.
 */
static int connect_media(halyard_client *c)
{
    halyard_client_leg info;
    halyard_client_peer peer;
    halyard_dgram_options options;
    halyard_dgram_channel_status status;
    uint64_t asked;
    int answer = 0;

    announce(c, HALYARD_CLIENT_STAGE_SESSION_READY);

    if (!open_leg(c, &c->media_leg, c->config.media_local_port, c->config.rcvbuf_bytes, "A/V")) {
        end_request(c, HALYARD_CLIENT_END_CHANNEL_ERROR);
        return 0;
    }
    c->stream_sock = c->media_leg.sock;
    c->result.media_local_port = c->media_leg.local_port;
    c->result.rcvbuf_asked = c->config.rcvbuf_bytes;
    c->result.rcvbuf_granted = rc_udp_rcvbuf_actual(c->stream_sock);
    describe_leg(&c->media_leg, &info);

    /* The host's turn: its media OFFER and ACCEPT go out while this keeps the control session alive. */
    asked = rc_time_ms();
    memset(&peer, 0, sizeof(peer));
    for (;;) {
        client_tick(c);
        if (c->end_pending != HALYARD_CLIENT_END_NONE)
            return 0;
        answer = c->cb.poll_media(c->cb.user, &info, &peer);
        if (answer != 0)
            break;
        if (rc_time_ms() - asked >= (uint64_t)c->config.media_offer_timeout_ms)
            break;
        rc_sleep_ms(10u);
    }
    if (answer <= 0) {
        client_log(c, HALYARD_CLIENT_LOG_WARN, "client: %s - there is no A/V path",
                   answer < 0 ? "the host gave up on the media connection"
                              : "the console never offered a media connection");
        end_request(c, HALYARD_CLIENT_END_NO_MEDIA);
        return 0;
    }

    leg_options(c, &options, media_leg_log);
    status = halyard_rendezvous_leg_attach(&c->media_leg, peer.address, peer.port, c->local_hashed_id,
                                           peer.console_hashed_id, &options);
    if (status != HALYARD_DGRAM_CHANNEL_OK) {
        dgram_failed(c, status, "aiming the A/V leg");
        return 0;
    }
    c->media_peer = c->media_leg.peer;
    c->filter_peer = 1;
    client_log(c, HALYARD_CLIENT_LOG_INFO, "client: A/V leg to %u.%u.%u.%u:%u, prelude", (unsigned)peer.address[0],
               (unsigned)peer.address[1], (unsigned)peer.address[2], (unsigned)peer.address[3], (unsigned)peer.port);

    status = halyard_dgram_channel_establish(&c->media_leg.channel);
    if (status != HALYARD_DGRAM_CHANNEL_OK) {
        dgram_failed(c, status, "the A/V leg's prelude");
        return 0;
    }
    c->result.media_prelude_ok = 1;

    c->result.session_ready_waited_ms = wait_flag(c, &c->session_ready, CLIENT_SESSION_READY_WINDOW_MS);
    if (c->end_pending != HALYARD_CLIENT_END_NONE)
        return 0;
    if (c->session_ready)
        reach(c, HALYARD_CLIENT_STAGE_SESSION_READY);
    else
        client_log(c, HALYARD_CLIENT_LOG_WARN, "client: no SESSION_ID after the A/V prelude - continuing, as .NET does");
    return 1;
}

static void put_be32(uint8_t *out, uint32_t value)
{
    out[0] = (uint8_t)(value >> 24);
    out[1] = (uint8_t)(value >> 16);
    out[2] = (uint8_t)(value >> 8);
    out[3] = (uint8_t)value;
}

/*
 * SendProbeReportAsync, then the StreamReadyWindow wait. PROBE_REPORT is required on this route (without it
 * the console never sends STREAM_READY and answers SESSION_REQUEST with no key); its slots are .NET's
 * [bitrate, declared MTU, 0, rtt] and **[X]** which slot means what - the console was indifferent to all
 * values tried. The rtt here is senkusha's PROTOCOL_VERSION round trip, the figure the launch spec declares
 * too; .NET's is its echo probe's, which this core does not run yet.
 */
static int connect_stream_ready(halyard_client *c)
{
    uint8_t report[HALYARD_CTRL_PROBE_REPORT_LENGTH];
    int rtt = c->result.version_rtt_ms;

    put_be32(report, (uint32_t)c->config.bitrate_kbps);
    put_be32(report + 4, (uint32_t)CLIENT_DECLARED_MTU);
    put_be32(report + 8, 0u);
    put_be32(report + 12, (uint32_t)(rtt < 0 ? 0 : (rtt > 1000 ? 1000 : rtt)));
    if (halyard_control_session_send_field(&c->control, HALYARD_CTRL_TYPE_PROBE_REPORT, report, sizeof(report))) {
        c->result.probe_report_sent = 1;
        client_log(c, HALYARD_CLIENT_LOG_INFO, "client: probe report sent");
    } else {
        client_log(c, HALYARD_CLIENT_LOG_WARN, "client: the probe report could not be sent");
    }

    (void)wait_flag(c, &c->stream_ready, CLIENT_STREAM_READY_WINDOW_MS);
    if (c->end_pending != HALYARD_CLIENT_END_NONE)
        return 0;
    c->result.stream_ready_seen = c->stream_ready;
    if (!c->stream_ready)
        client_log(c, HALYARD_CLIENT_LOG_WARN, "client: STREAM_READY never arrived; opening the stream anyway");
    return 1;
}

int halyard_client_rendezvous_prepare(halyard_client *client, halyard_client_leg *out_control_leg)
{
    halyard_client *c = client;

    if (c == NULL || !c->is_rendezvous || c->connect_started || c->ended || c->rv_begun)
        return 0;
    if (!open_leg(c, &c->control_leg, c->config.control_local_port, 0, "control"))
        return 0;
    c->rv_prepared = 1;
    c->result.control_local_port = c->control_leg.local_port;
    if (out_control_leg != NULL)
        describe_leg(&c->control_leg, out_control_leg);
    return 1;
}

int halyard_client_rendezvous_begin(halyard_client *client,
                                    const uint8_t local_hashed_id[HALYARD_CLIENT_HASHED_ID_LENGTH],
                                    const halyard_client_peer *console)
{
    halyard_client *c = client;
    halyard_dgram_options options;
    halyard_dgram_channel_status status;

    if (c == NULL || local_hashed_id == NULL || console == NULL || !c->rv_prepared || c->rv_begun
        || c->connect_started || c->ended)
        return 0;

    memcpy(c->local_hashed_id, local_hashed_id, sizeof(c->local_hashed_id));
    /* The paired host on 9303, not the candidate: HalyardAccountConsoleSession's ControlEndpoint. */
    if (halyard_dgram_host_header(c->record.host, HALYARD_WAN_CONTROL_PORT, c->host_header, sizeof(c->host_header))
        == 0u)
        return 0;

    leg_options(c, &options, control_leg_log);
    status = halyard_rendezvous_leg_attach(&c->control_leg, console->address, console->port, local_hashed_id,
                                           console->console_hashed_id, &options);
    if (status == HALYARD_DGRAM_CHANNEL_OK)
        status = halyard_dgram_channel_begin(&c->control_leg.channel);
    if (status != HALYARD_DGRAM_CHANNEL_OK) {
        c->result.dgram_status = (int)status;
        client_log(c, HALYARD_CLIENT_LOG_ERROR, "client: could not begin the control association (9303 status %d)",
                   (int)status);
        return 0;
    }
    client_log(c, HALYARD_CLIENT_LOG_INFO, "client: control association begun toward %u.%u.%u.%u:%u",
               (unsigned)console->address[0], (unsigned)console->address[1], (unsigned)console->address[2],
               (unsigned)console->address[3], (unsigned)console->port);
    c->rv_begun = 1;
    return 1;
}

int halyard_client_rendezvous_exchange(void *user, const uint8_t *request, size_t request_length,
                                       uint8_t *response, size_t response_size, size_t *out_response_length)
{
    halyard_client *c = (halyard_client *)user;

    if (out_response_length != NULL)
        *out_response_length = 0u;
    if (c == NULL || !c->rv_begun || c->connect_started || c->ended)
        return 0;
    return halyard_dgram_regist_exchange(&c->control_leg.channel, request, request_length, response, response_size,
                                         out_response_length);
}

/* ---- The API ----------------------------------------------------------------------------------------- */

halyard_client *halyard_client_init(void *storage, size_t storage_size, const halyard_client_config *config,
                                    const halyard_client_callbacks *callbacks)
{
    halyard_client *c;
    halyard_client_config resolved;

    if (storage == NULL || storage_size < sizeof(struct halyard_client) || config == NULL
        || callbacks == NULL || callbacks->video_frame == NULL || config->record == NULL)
        return NULL;
    /* "Suitably aligned for any type": every allocator a host would use gives at least this. */
    if (((uintptr_t)storage % sizeof(uint64_t)) != 0u)
        return NULL;
    if (config->record->host[0] == '\0' || config->record->registkey_length == 0u
        || config->record->registkey_length > sizeof(config->record->registkey))
        return NULL;

    halyard_client_config_resolve(config, &resolved);
    if (resolved.route != HALYARD_ROUTE_LOCAL && resolved.route != HALYARD_ROUTE_RENDEZVOUS)
        return NULL;
    /* The media negotiation is the host's, and without it the A/V leg cannot exist. */
    if (resolved.route == HALYARD_ROUTE_RENDEZVOUS && callbacks->poll_media == NULL)
        return NULL;

    c = (halyard_client *)storage;
    /* Not the whole of storage: the demuxer is megabytes and is initialised when it is first needed. */
    memset(c, 0, offsetof(struct halyard_client, senkusha));
    c->record = *config->record;
    c->config = resolved;
    c->config.record = &c->record;
    c->cb = *callbacks;

    c->control.sock = -1;
    c->senkusha_sock = -1;
    c->stream_sock = -1;
    memset(&c->senkusha, 0, sizeof(c->senkusha));
    memset(&c->stream, 0, sizeof(c->stream));
    c->senkusha.sock = -1;
    c->stream.sock = -1;
    halyard_rendezvous_leg_init(&c->control_leg);
    halyard_rendezvous_leg_init(&c->media_leg);

    /* Copied, like the record: the config's pointers need not outlive this call. */
    c->is_rendezvous = resolved.route == HALYARD_ROUTE_RENDEZVOUS;
    c->stun_count = resolved.stun_server_count;
    if (c->stun_count > 0u)
        memcpy(c->stun, resolved.stun_servers, c->stun_count * sizeof(c->stun[0]));
    c->config.stun_servers = NULL;
    if (resolved.bind_address != NULL) {
        memcpy(c->bind_address, resolved.bind_address, sizeof(c->bind_address));
        c->have_bind_address = 1;
    }
    c->config.bind_address = NULL;
    c->result.session_ready_waited_ms = -1;

    c->result.stage = HALYARD_CLIENT_STAGE_IDLE;
    c->result.end_reason = HALYARD_CLIENT_END_NONE;
    c->result.login_verdict = -1;
    c->result.login_verdict_byte = -1;

    if (c->cb.log != NULL)
        rc_log_set_sink(client_rc_log_sink, c);
    return c;
}

halyard_client_stage halyard_client_connect(halyard_client *client)
{
    halyard_client *c = client;

    if (c == NULL)
        return HALYARD_CLIENT_STAGE_IDLE;
    if (c->connect_started || c->ended)
        return c->result.stage;
    c->connect_started = 1;

    /* A cancel that arrived before anything opened costs nothing to honour. */
    c->in_connect = 1;
    poll_commands(c);
    if (c->is_rendezvous && c->end_pending == HALYARD_CLIENT_END_NONE && !c->rv_begun) {
        client_log(c, HALYARD_CLIENT_LOG_ERROR,
                   "client: RENDEZVOUS needs halyard_client_rendezvous_prepare and _begin before connect");
        end_request(c, HALYARD_CLIENT_END_CHANNEL_ERROR);
    }
    if (c->end_pending == HALYARD_CLIENT_END_NONE
        && connect_control(c) && connect_signin(c)
        && (!c->is_rendezvous || connect_media(c))
        && connect_senkusha(c)
        && (!c->is_rendezvous || connect_stream_ready(c))
        && connect_takion(c)
        && connect_keys(c) && connect_stream_info(c)) {
        begin_streaming(c);
        c->in_connect = 0;
        return c->result.stage;
    }
    c->in_connect = 0;
    client_end(c);
    return c->result.stage;
}

int halyard_client_pump(halyard_client *client, uint32_t *next_deadline_ms)
{
    halyard_client *c = client;
    int active = 0;
    int round;

    if (next_deadline_ms != NULL)
        *next_deadline_ms = 0u;
    if (c == NULL || c->ended)
        return 0;
    if (!c->streaming) {
        /* Nothing is running: either connect() has not succeeded, or a disconnect is waiting to finish. */
        if (c->end_pending != HALYARD_CLIENT_END_NONE)
            client_end(c);
        return 0;
    }

    poll_commands(c);
    service_control(c);

    for (round = 0; round < CLIENT_PUMP_ROUNDS && c->end_pending == HALYARD_CLIENT_END_NONE; round++) {
        int drained = drain_av(c);
        int message = (c->end_pending == HALYARD_CLIENT_END_NONE) ? poll_stream_control(c) : 0;

        if (drained > 0 || message)
            active = 1;
        if (drained == 0 && !message)
            break;
    }
    if (c->end_pending == HALYARD_CLIENT_END_NONE)
        send_periodic(c);

    if (c->end_pending != HALYARD_CLIENT_END_NONE) {
        client_end(c);
        return 0;
    }

    if (next_deadline_ms != NULL && !active) {
        uint64_t now = rc_time_ms();
        uint32_t wait = CLIENT_MAX_WAIT_MS;
        uint32_t w;

        w = halyard_client_timer_wait_ms(c->next_heartbeat_ms, now);
        if (w < wait)
            wait = w;
        w = halyard_client_timer_wait_ms(c->next_congestion_ms, now);
        if (w < wait)
            wait = w;
        if (c->cb.poll_input != NULL) {
            w = halyard_client_timer_wait_ms(c->next_input_ms, now);
            if (w < wait)
                wait = w;
        }
        w = halyard_client_idr_wait_ms(&c->idr, now);
        if (w < wait)
            wait = w;
        *next_deadline_ms = wait;
    }
    return 1;
}

int halyard_client_fds(const halyard_client *client, int *fds, int max_fds)
{
    int n = 0;

    if (client == NULL || fds == NULL || max_fds <= 0 || client->ended)
        return 0;
    if (client->control_open && client->control.sock >= 0 && n < max_fds)
        fds[n++] = client->control.sock;
    if (client->stream_sock >= 0 && n < max_fds)
        fds[n++] = client->stream_sock;
    return n;
}

void halyard_client_disconnect(halyard_client *client, int rest_console)
{
    halyard_client *c = client;

    if (c == NULL || c->ended)
        return;
    if (rest_console)
        c->rest_wanted = 1;
    say_goodbye(c);
    end_request(c, HALYARD_CLIENT_END_USER_DISCONNECT);
    /* A session that is not running has nothing for a pump to finish - unless this was called from a
     * callback inside connect(), which is still using the sockets and will end it on its way out. */
    if (!c->streaming && !c->in_connect)
        client_end(c);
}

const halyard_client_result *halyard_client_result_get(const halyard_client *client)
{
    return (client != NULL) ? &client->result : NULL;
}

void halyard_client_destroy(halyard_client *client)
{
    halyard_client *c = client;

    if (c == NULL)
        return;
    if (!c->ended && !c->in_connect) {
        end_request(c, HALYARD_CLIENT_END_USER_DISCONNECT);
        client_end(c);
    }
    if (rc_log_sink_user() == (void *)c)
        rc_log_set_sink(NULL, NULL);
}
