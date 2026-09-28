/*
 * ripcord - the connect sequence (libripcord/client), on a host, with no console.
 *
 * WHAT CAN BE CHECKED WITHOUT A CONSOLE, and that is more than it sounds. halyard_client.c is a sequence
 * of network exchanges, and none of those can run here: nothing on this machine answers /sess/init, and
 * this suite never sends anything beyond loopback. But every decision it makes on a clock was factored
 * into halyard_client_policy.c so it could be held in place here, each with a hardware finding behind it:
 *
 *   the IDR latch      armed at the start (b141), re-requested every 200 ms until a keyframe (b124)
 *   the input cadence  history on a transition only, state on change or every 200 ms
 *   the defaults       every zero in the config, and the one flag where zero could not mean "default"
 *
 * The API around them is checked where it does not need a console: argument validation, the struct size
 * claim, the stage/end-reason bookkeeping, and teardown on an instance that never connected. Not a
 * connect() against loopback: see test_log_routing for why that would not stay on this machine.
 *
 * Slice 0's two core fixes are here too: rc_tcp_connect's deadline (against loopback listeners) and the
 * rc_log sink.
 */
#include "../client/halyard_client.h"
#include "../client/halyard_client_policy.h"
#include "../net/rc_tcp.h"
#include "../stream/stream_demux.h"
#include "../takion/takion_control_proto.h"
#include "../takion/takion_reliable_channel.h"
#include "../util/rc_log.h"
#include "../platform/rc_platform.h"

#include <arpa/inet.h>
#include <fcntl.h>
#include <netinet/in.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <unistd.h>

/* The host seam has no CSPRNG on purpose (see stun_test.c); nothing here derives a key. */
int rc_random_bytes(uint8_t *out, size_t length)
{
    size_t i;

    for (i = 0; i < length; i++)
        out[i] = (uint8_t)(i * 31u + 17u);
    return 1;
}

static int g_passed;
static int g_failed;

#define CHECK(cond, ...) do { \
    if (cond) { \
        g_passed++; \
    } else { \
        g_failed++; \
        printf("FAIL %s:%d: ", __FILE__, __LINE__); \
        printf(__VA_ARGS__); \
        printf("\n"); \
    } \
} while (0)

/* ---- Callbacks that record what they were told ------------------------------------------------------ */

typedef struct {
    int stages[32];
    int stage_count;
    int ended_count;
    unsigned command;          /* returned by poll_commands, once */
    int lines;
} recorder;

static void rec_stage(void *user, halyard_client_stage stage)
{
    recorder *r = (recorder *)user;

    if (r->stage_count < 32)
        r->stages[r->stage_count++] = (int)stage;
    if (stage == HALYARD_CLIENT_STAGE_ENDED)
        r->ended_count++;
}

static void rec_video(void *user, const uint8_t *data, size_t length, int is_keyframe)
{
    (void)user;
    (void)data;
    (void)length;
    (void)is_keyframe;
}

static unsigned rec_commands(void *user)
{
    recorder *r = (recorder *)user;
    unsigned cmd = r->command;

    r->command = 0u;
    return cmd;
}

static void rec_log(void *user, int level, const char *line)
{
    recorder *r = (recorder *)user;

    (void)level;
    (void)line;
    r->lines++;
}

static halyard_pairing_record g_record;

static void make_record(const char *host)
{
    memset(&g_record, 0, sizeof(g_record));
    snprintf(g_record.host, sizeof(g_record.host), "%s", host);
    g_record.is_ps5 = 1;
    g_record.registkey_length = 8u;
    memset(g_record.registkey, 0x11, 8u);
}

static void make_callbacks(halyard_client_callbacks *cb, recorder *r)
{
    memset(cb, 0, sizeof(*cb));
    memset(r, 0, sizeof(*r));
    cb->user = r;
    cb->stage = rec_stage;
    cb->video_frame = rec_video;
    cb->poll_commands = rec_commands;
    cb->log = rec_log;
}

static void *g_storage;
static size_t g_storage_size;

/* ---- The struct ------------------------------------------------------------------------------------- */

static void test_struct_size(void)
{
    size_t size = halyard_client_struct_size();

    /* The header's claim: large, because the demuxer is inside it. */
    CHECK(size > stream_demux_struct_size(), "the instance holds a demuxer (%zu vs %zu)", size,
          stream_demux_struct_size());
    CHECK(size > stream_demux_struct_size() + 2u * sizeof(takion_reliable_channel),
          "and two Takion associations besides");
    g_storage_size = size;
    g_storage = malloc(size);
    CHECK(g_storage != NULL, "a host can allocate it");
}

/* ---- init ------------------------------------------------------------------------------------------- */

static void test_init_validation(void)
{
    halyard_client_config config;
    halyard_client_callbacks cb;
    recorder r;
    halyard_client *c;

    make_record("192.0.2.1");
    make_callbacks(&cb, &r);
    memset(&config, 0, sizeof(config));
    config.record = &g_record;

    CHECK(halyard_client_init(NULL, g_storage_size, &config, &cb) == NULL, "no storage");
    CHECK(halyard_client_init(g_storage, g_storage_size - 1u, &config, &cb) == NULL, "storage one byte short");
    CHECK(halyard_client_init(g_storage, g_storage_size, NULL, &cb) == NULL, "no config");
    CHECK(halyard_client_init(g_storage, g_storage_size, &config, NULL) == NULL, "no callbacks");
    CHECK(halyard_client_init((uint8_t *)g_storage + 1, g_storage_size - 1u, &config, &cb) == NULL,
          "misaligned storage");

    cb.video_frame = NULL;
    CHECK(halyard_client_init(g_storage, g_storage_size, &config, &cb) == NULL,
          "video_frame is the one callback that may not be NULL");
    cb.video_frame = rec_video;

    config.record = NULL;
    CHECK(halyard_client_init(g_storage, g_storage_size, &config, &cb) == NULL, "no record");
    config.record = &g_record;

    g_record.registkey_length = 0u;
    CHECK(halyard_client_init(g_storage, g_storage_size, &config, &cb) == NULL, "a record with no key");
    g_record.registkey_length = 8u;

    g_record.host[0] = '\0';
    CHECK(halyard_client_init(g_storage, g_storage_size, &config, &cb) == NULL, "a record with no address");
    make_record("192.0.2.1");

    config.route = HALYARD_ROUTE_RENDEZVOUS;
    CHECK(halyard_client_init(g_storage, g_storage_size, &config, &cb) == NULL,
          "RENDEZVOUS without poll_media is refused: the A/V leg cannot exist without it");
    config.route = (halyard_route)2;
    CHECK(halyard_client_init(g_storage, g_storage_size, &config, &cb) == NULL,
          "an RP-ConPath nobody has seen (2) is refused rather than sent");
    config.route = HALYARD_ROUTE_LOCAL;

    c = halyard_client_init(g_storage, g_storage_size, &config, &cb);
    CHECK(c != NULL, "a valid config initialises");
    if (c != NULL) {
        const halyard_client_result *result = halyard_client_result_get(c);
        int fds[4];

        CHECK(result->stage == HALYARD_CLIENT_STAGE_IDLE, "an initialised client is IDLE");
        CHECK(result->end_reason == HALYARD_CLIENT_END_NONE, "and has not ended");
        CHECK(result->login_verdict == -1, "no verdict is 'unknown', not 'refused'");
        CHECK(halyard_client_fds(c, fds, 4) == 0, "and has no sockets");
        CHECK(r.stage_count == 0, "and has announced nothing");
        halyard_client_destroy(c);
    }
}

/* ---- The defaults ----------------------------------------------------------------------------------- */

static void test_config_defaults(void)
{
    halyard_client_config in, out;

    memset(&in, 0, sizeof(in));
    halyard_client_config_resolve(&in, &out);
    CHECK(out.route == HALYARD_ROUTE_LOCAL, "route 0 is LOCAL");
    CHECK(out.width == 1280 && out.height == 720, "the .NET default resolution");
    CHECK(out.fps == 60, "the .NET default rate");
    CHECK(out.bitrate_kbps == 10000, "the .NET default bitrate");
    CHECK(out.signin_prompt_window_ms == 20000u, "the PS3's sign-in window (b36)");
    CHECK(out.senkusha_attempts == 20u, ".NET's senkusha attempts");
    CHECK(out.stream_attempts == 100u, ".NET's stream attempts (cap55)");
    CHECK(out.attempt_interval_ms == 300u, "300 ms between attempts");
    CHECK(out.rcvbuf_bytes == 4 * 1024 * 1024, ".NET's receive-buffer ask");
    CHECK(out.require_session_ready == 1, "a zero-initialised config waits for SESSION_ID");
    CHECK(out.allow_hevc == 0 && out.hdr == 0, "H.264 and SDR unless asked");

    in.require_session_ready = -1;
    in.rcvbuf_bytes = -1;
    in.fps = 45;
    in.width = 1920;               /* half a resolution is not one */
    in.allow_hevc = 7;
    in.stream_attempts = 3u;
    halyard_client_config_resolve(&in, &out);
    CHECK(out.require_session_ready == 0, "a negative flag is the explicit 'do not wait'");
    CHECK(out.rcvbuf_bytes == 0, "a negative buffer asks for nothing");
    CHECK(out.fps == 60, "45 fps rounds to 60");
    CHECK(out.width == 1280 && out.height == 720, "a width without a height takes the default pair");
    CHECK(out.allow_hevc == 1, "flags are normalised to 0/1");
    CHECK(out.stream_attempts == 3u, "an explicit budget is kept");

    in.fps = 24;
    in.require_session_ready = 1;
    in.width = 1920;
    in.height = 1080;
    halyard_client_config_resolve(&in, &out);
    CHECK(out.fps == 30, "24 fps rounds to 30");
    CHECK(out.require_session_ready == 1, "1 stays 1");
    CHECK(out.width == 1920 && out.height == 1080, "no 720p cap: that was the PS3's decoder");

    halyard_client_config_resolve(&in, &in);
    CHECK(in.width == 1920 && in.fps == 30, "resolving in place is allowed");
}

/* ---- The IDR latch ---------------------------------------------------------------------------------- */

static void test_idr_latch(void)
{
    halyard_client_idr_latch latch;
    const uint64_t t = 5000u;

    halyard_client_idr_reset(&latch);
    CHECK(!halyard_client_idr_due(&latch, t), "an unarmed latch asks for nothing");
    CHECK(halyard_client_idr_wait_ms(&latch, t) == UINT32_MAX, "and wants no wake-up");

    halyard_client_idr_arm(&latch);
    CHECK(halyard_client_idr_wait_ms(&latch, t) == 0u, "armed: due now");
    CHECK(halyard_client_idr_due(&latch, t), "the first request goes at once");
    CHECK(!halyard_client_idr_due(&latch, t + 1u), "then it is throttled");
    CHECK(!halyard_client_idr_due(&latch, t + 199u), "for 200 ms");
    CHECK(halyard_client_idr_wait_ms(&latch, t + 150u) == 50u, "and says how long is left");
    CHECK(halyard_client_idr_due(&latch, t + 200u), "unanswered, it asks again (b124)");
    CHECK(halyard_client_idr_due(&latch, t + 400u), "and again");

    /* Loss re-arming an armed latch changes nothing about the throttle: one IDR repairs the chain. */
    halyard_client_idr_arm(&latch);
    CHECK(!halyard_client_idr_due(&latch, t + 450u), "a loss inside the window does not bypass it");

    halyard_client_idr_keyframe(&latch);
    CHECK(!halyard_client_idr_due(&latch, t + 1000u), "a keyframe clears it");
    CHECK(latch.requests == 3u, "three requests went out (%u)", (unsigned)latch.requests);

    /* The first request is not throttled even on a clock that starts at 0 - which the PS3's did not
     * need to care about and a host's monotonic clock may. */
    halyard_client_idr_reset(&latch);
    halyard_client_idr_arm(&latch);
    CHECK(halyard_client_idr_due(&latch, 0u), "armed at t=0, the first request still goes");
}

/* ---- Timers ----------------------------------------------------------------------------------------- */

static void test_timers(void)
{
    uint64_t next = 1000u;

    CHECK(!halyard_client_timer_due(&next, 999u, 200u), "not before its deadline");
    CHECK(halyard_client_timer_wait_ms(next, 990u) == 10u, "ten to go");
    CHECK(halyard_client_timer_due(&next, 1000u, 200u) && next == 1200u, "on it, and rearmed");
    CHECK(halyard_client_timer_due(&next, 1700u, 200u) && next == 1900u,
          "late: rearmed from now, so a stalled pump is not followed by a burst of catch-up sends");
    CHECK(!halyard_client_timer_due(&next, 1800u, 200u), "and only once");
    CHECK(halyard_client_timer_wait_ms(next, 5000u) == 0u, "overdue is 0, not a wrap");

    CHECK(halyard_client_kbps(0u, 200u) == 0u, "nothing is 0 kbps");
    CHECK(halyard_client_kbps(25000u, 200u) == 1000u, "25 KB in 200 ms is 1000 kbps");
    CHECK(halyard_client_kbps(1000u, 0u) == 0u, "an empty window is 0, not a division by zero");
}

/* ---- Input cadence ---------------------------------------------------------------------------------- */

static void test_input_cadence(void)
{
    halyard_client_input_cadence cadence;
    halyard_input_writer writer;
    halyard_client_input_packets out;
    halyard_input_state s;
    uint64_t t = 10000u;

    memset(&cadence, 0, sizeof(cadence));
    halyard_input_writer_init(&writer);
    memset(&s, 0, sizeof(s));

    halyard_client_input_step(&cadence, &writer, &s, t, &out);
    CHECK(out.state_length == HALYARD_INPUT_HEADER_LENGTH + HALYARD_INPUT_STATE_PAYLOAD,
          "the first poll sends a state packet (%zu)", out.state_length);
    CHECK(out.history_length == 0u, "and no history: there is nothing to have transitioned from");
    CHECK(writer.have_previous == 1, "the caller-side previous is kept, as rc_connect.c 1406-1407 does");
    CHECK(writer.state_seq == 1u, "one state sequence consumed");

    halyard_client_input_step(&cadence, &writer, &s, t + 4u, &out);
    CHECK(out.state_length == 0u && out.history_length == 0u, "an unchanged pad 4 ms later: nothing");

    halyard_client_input_step(&cadence, &writer, &s, t + 199u, &out);
    CHECK(out.state_length == 0u, "still nothing at 199 ms");

    halyard_client_input_step(&cadence, &writer, &s, t + 200u, &out);
    CHECK(out.state_length > 0u && out.history_length == 0u,
          "at 200 ms a still controller is reported anyway, with no history");

    s.buttons = HALYARD_PAD_CROSS;
    halyard_client_input_step(&cadence, &writer, &s, t + 204u, &out);
    CHECK(out.history_length > HALYARD_INPUT_HEADER_LENGTH, "a press is a transition: history");
    CHECK(out.state_length > 0u, "and a change: state, without waiting for the 200 ms");
    CHECK(writer.history_seq == 1u, "one history sequence consumed");

    halyard_client_input_step(&cadence, &writer, &s, t + 208u, &out);
    CHECK(out.history_length == 0u && out.state_length == 0u, "holding it: nothing new");

    s.left_x = 1200;
    halyard_client_input_step(&cadence, &writer, &s, t + 212u, &out);
    CHECK(out.history_length == 0u, "a stick is not a transition");
    CHECK(out.state_length > 0u, "but it is a change");

    s.buttons = 0u;
    halyard_client_input_step(&cadence, &writer, &s, t + 216u, &out);
    CHECK(out.history_length > 0u, "a release is a transition too");
    CHECK(writer.history_seq == 2u && writer.state_seq == 5u, "sequences advance only for packets built (%u, %u)",
          (unsigned)writer.history_seq, (unsigned)writer.state_seq);

    s.right_trigger = 128u;
    halyard_client_input_step(&cadence, &writer, &s, t + 220u, &out);
    CHECK(out.history_length > 0u && out.state_length > 0u, "an analog shoulder moving is both");
}

/* ---- Bookkeeping on instances that never reach a console -------------------------------------------- */

static halyard_client *fresh(halyard_client_callbacks *cb, recorder *r, const char *host)
{
    halyard_client_config config;

    make_record(host);
    make_callbacks(cb, r);
    memset(&config, 0, sizeof(config));
    config.record = &g_record;
    return halyard_client_init(g_storage, g_storage_size, &config, cb);
}

static void test_teardown_never_connected(void)
{
    halyard_client_callbacks cb;
    recorder r;
    halyard_client *c = fresh(&cb, &r, "192.0.2.1");
    const halyard_client_result *result;
    uint32_t deadline = 123u;

    if (c == NULL) {
        CHECK(0, "init");
        return;
    }
    CHECK(halyard_client_pump(c, &deadline) == 0, "pumping a client that never connected: not alive");
    CHECK(deadline == 0u, "and no deadline");
    halyard_client_destroy(c);
    result = halyard_client_result_get(c);
    CHECK(result->stage == HALYARD_CLIENT_STAGE_IDLE, "destroyed at IDLE");
    CHECK(result->end_reason == HALYARD_CLIENT_END_USER_DISCONNECT, "the host ended it");
    CHECK(result->disconnect_sent == 0 && result->rest_requested == 0, "and nothing was said to anybody");
    CHECK(r.ended_count == 1, "ENDED announced once");
    halyard_client_destroy(c);
    CHECK(r.ended_count == 1, "and destroy is idempotent");
    CHECK(halyard_client_connect(c) == HALYARD_CLIENT_STAGE_IDLE, "an ended client does not connect");
    CHECK(r.stage_count == 1, "and announces nothing more");
}

static void test_disconnect_before_connect(void)
{
    halyard_client_callbacks cb;
    recorder r;
    halyard_client *c = fresh(&cb, &r, "192.0.2.1");
    const halyard_client_result *result;

    if (c == NULL) {
        CHECK(0, "init");
        return;
    }
    halyard_client_disconnect(c, 1);
    result = halyard_client_result_get(c);
    CHECK(result->end_reason == HALYARD_CLIENT_END_USER_DISCONNECT, "a disconnect before connect ends it");
    CHECK(result->rest_requested == 0, "no console was rested: there was no control session to ask");
    CHECK(halyard_client_connect(c) == HALYARD_CLIENT_STAGE_IDLE, "and connect then does nothing");
    CHECK(r.ended_count == 1, "ENDED once");
    halyard_client_destroy(c);
    CHECK(r.ended_count == 1, "destroy after it adds nothing");
}

static void test_cancel_before_anything_opens(void)
{
    halyard_client_callbacks cb;
    recorder r;
    halyard_client *c = fresh(&cb, &r, "192.0.2.1");
    const halyard_client_result *result;

    if (c == NULL) {
        CHECK(0, "init");
        return;
    }
    r.command = HALYARD_CLIENT_CMD_CANCEL;
    CHECK(halyard_client_connect(c) == HALYARD_CLIENT_STAGE_IDLE, "cancelled before the first stage");
    result = halyard_client_result_get(c);
    CHECK(result->end_reason == HALYARD_CLIENT_END_HOST_CANCEL, "and it says why");
    CHECK(r.stage_count == 1 && r.stages[0] == HALYARD_CLIENT_STAGE_ENDED,
          "nothing was begun, so only ENDED was announced");
    halyard_client_destroy(c);
}

/*
 * The core's own log lines reach the host's callback while a client exists, and stop when it is gone.
 *
 * NOT a connect against loopback, though that would exercise more: halyard_control_session_open sends
 * its ARM probe to the BROADCAST address as well as the unicast one (halyard_control_probe.c - a console
 * that has not been spoken to recently answers only the broadcast), so it cannot be run without a
 * datagram leaving the machine. This suite sends nothing beyond loopback.
 */
static void test_log_routing(void)
{
    halyard_client_callbacks cb;
    recorder r;
    halyard_client *c = fresh(&cb, &r, "192.0.2.1");

    if (c == NULL) {
        CHECK(0, "init");
        return;
    }
    CHECK(rc_log_sink_user() == (void *)c, "a client with a log callback owns the core's sink");
    rc_log("a line from inside the core\n");
    CHECK(r.lines == 1, "and the core's line reached the host (%d)", r.lines);
    halyard_client_destroy(c);
    CHECK(rc_log_sink_user() == NULL, "destroy returned the sink");
}

/* ---- Slice 0: rc_tcp_connect's deadline -------------------------------------------------------------- */

static int listen_loopback(unsigned short *out_port)
{
    struct sockaddr_in addr;
    socklen_t len = (socklen_t)sizeof(addr);
    int s = socket(AF_INET, SOCK_STREAM, 0);

    if (s < 0)
        return -1;
    memset(&addr, 0, sizeof(addr));
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    addr.sin_port = 0;
    if (bind(s, (struct sockaddr *)&addr, sizeof(addr)) != 0 || listen(s, 4) != 0
        || getsockname(s, (struct sockaddr *)&addr, &len) != 0) {
        close(s);
        return -1;
    }
    *out_port = ntohs(addr.sin_port);
    return s;
}

static void test_tcp_connect_deadline(void)
{
    unsigned short port = 0;
    int listener = listen_loopback(&port);
    int sock;
    uint64_t started;

    CHECK(listener >= 0, "a loopback listener");
    if (listener < 0)
        return;

    sock = rc_tcp_connect("127.0.0.1", port);
    CHECK(sock >= 0, "connects to a listening port");
    if (sock >= 0) {
        int flags = fcntl(sock, F_GETFL, 0);

        CHECK(flags != -1 && (flags & O_NONBLOCK) == 0,
              "and hands back a BLOCKING socket, as every existing caller expects");
        close(sock);
    }
    close(listener);

    /* The port is closed now: refused, promptly, rather than waited on. */
    started = rc_time_ms();
    sock = rc_tcp_connect_timeout("127.0.0.1", port, 2000u);
    CHECK(sock < 0, "a closed port is refused");
    CHECK(rc_time_ms() - started < 1000u, "without waiting out the deadline");
    if (sock >= 0)
        close(sock);

    CHECK(rc_tcp_connect("not-an-address", port) < 0, "a host that is not a dotted quad is refused");
}

/* ---- Slice 0: the log sink -------------------------------------------------------------------------- */

static char g_sink_lines[4][64];
static int g_sink_count;

static void capture(void *user, const char *line)
{
    (void)user;
    if (g_sink_count < 4)
        snprintf(g_sink_lines[g_sink_count], sizeof(g_sink_lines[0]), "%s", line);
    g_sink_count++;
}

static void test_log_sink(void)
{
    int marker = 0;

    g_sink_count = 0;
    rc_log_set_sink(capture, &marker);
    CHECK(rc_log_sink_user() == &marker, "the sink's user is reported back");
    rc_log("\x1b[31mFAIL\x1b[0m one\npart");
    rc_log("ial two\n");
    CHECK(g_sink_count == 2, "two complete lines, one of them split across calls (%d)", g_sink_count);
    CHECK(strcmp(g_sink_lines[0], "FAIL one") == 0, "colour codes stripped, newline dropped (\"%s\")",
          g_sink_lines[0]);
    CHECK(strcmp(g_sink_lines[1], "partial two") == 0, "a split line arrives whole (\"%s\")", g_sink_lines[1]);
    rc_log_set_sink(NULL, &marker);
    CHECK(rc_log_sink_user() == NULL, "clearing it clears its user");
}

/* ---- The senkusha literal --------------------------------------------------------------------------- */

static void test_senkusha_version_literal(void)
{
    /* halyard_client.c sends the PS3's hardware-proven literal; the builder must agree with it, or one
     * of them encodes protobuf wrongly. */
    static const uint8_t kLiteral[] = { 0x08, 0x1F, 0xFA, 0x01, 0x02, 0x08, 0x09 };
    static const uint32_t kNine[] = { 9u };
    uint8_t built[32];
    size_t n = takion_control_build_protocol_version_request(kNine, 1u, built, sizeof(built));

    CHECK(takion_control_validate(kLiteral, sizeof(kLiteral)), "the literal is well-formed protobuf");
    CHECK(n == sizeof(kLiteral) && memcmp(built, kLiteral, n) == 0,
          "and it is what the builder produces for {9} (%zu bytes)", n);
}

int main(void)
{
    printf("client: the connect sequence, without a console\n");
    test_struct_size();
    if (g_storage == NULL)
        return 1;
    test_init_validation();
    test_config_defaults();
    test_idr_latch();
    test_timers();
    test_input_cadence();
    test_teardown_never_connected();
    test_disconnect_before_connect();
    test_cancel_before_anything_opens();
    test_log_routing();
    test_tcp_connect_deadline();
    test_log_sink();
    test_senkusha_version_literal();
    free(g_storage);

    printf("client: %d passed, %d failed\n", g_passed, g_failed);
    return g_failed == 0 ? 0 : 1;
}
