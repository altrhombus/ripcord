/*
 * ripcord host tests - the control session's byte-pipe seam (halyard_control_session.h), on both pipes.
 *
 * WHAT THIS HOLDS IN PLACE.
 *
 *   1. THE TCP PATH DID NOT MOVE. halyard_control_session.c is hardware-validated on the 3DS and the PS3,
 *      and the seam put its four socket calls behind a pointer. So a loopback console (a thread, 127.0.0.1
 *      only) takes /sess/init and /sess/ctrl from it and the bytes are compared, whole, with the request
 *      this file assembles independently from the primitives - the header set, its order, the five fields
 *      at counters 0-4. Then the binary channel: a heartbeat answered, a passcode at counter 5, a field
 *      frame at counter 6, the console's close seen as CLOSED. When 127.0.0.1:9295 is free the unmodified
 *      halyard_control_session_open runs too, so the default options are covered, not only the port knob.
 *   2. THE DATAGRAM PIPE behaves as HalyardDatagramSessionControlChannel does, against a scripted console
 *      in-process (fake_dgram_console.h): no ARM probe, init and ctrl on two connections over one prelude,
 *      "Rp-Version" and the padded Host on init, RP-ConPath 3, heartbeats and fields over the association,
 *      a polite Close on close, and the console's Close reported once the data before it is read.
 *   3. THE REGISTRATION ADAPTER: halyard_dgram_regist_exchange is ExchangeAsync, and halyard_account_regist_run
 *      runs over it.
 *   4. BYTE FOR BYTE WITH .NET: vectors/rendezvous-control.kat holds the /sess/init and /sess/ctrl requests
 *      HalyardStreamingSession itself sent on the rendezvous route (ProtocolLab's LabRendezvousVectors, a
 *      PS5 case and a PS4 one), and the datagram pipe must send exactly those bytes from the same inputs.
 *
 * NOTHING LEAVES LOOPBACK. The ARM probe - which halyard_control_session_open sends to the broadcast
 * address as well as the console (halyard_control_probe.c) - is replaced at link time by a stub below
 * that records the call, which is also how the datagram route is shown to skip it.
 */
#include "fake_dgram_console.h"

#include "../halyard/halyard_v1.h"
#include "../platform/rc_platform.h"
#include "../session/halyard_account_regist_flow.h"
#include "../session/halyard_control_arm.h"
#include "../session/halyard_control_session.h"
#include "../session/halyard_ctrl_message.h"
#include "../session/halyard_dgram_session.h"
#include "../session/halyard_sess_fields.h"
#include "../session/halyard_sess_request.h"
#include "../util/rc_base64.h"
#include "../util/rc_hex.h"

#include <pthread.h>
#include <stdio.h>
#include <string.h>

#include <arpa/inet.h>
#include <netinet/in.h>
#include <sys/socket.h>
#include <sys/time.h>
#include <sys/types.h>
#include <unistd.h>

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

/* The host seam has no CSPRNG on purpose (see stun_test.c); a counter keeps every run the same. */
static uint8_t g_random_next = 0x40;

int rc_random_bytes(uint8_t *out, size_t length)
{
    size_t i;

    for (i = 0; i < length; i++)
        out[i] = g_random_next++;
    return 1;
}

/* The ARM probe, at link time: counted, and never sent. */
static int g_probe_calls;
static char g_probe_host[64];

int halyard_control_arm_probe(const char *host, int is_ps5)
{
    (void)is_ps5;
    g_probe_calls++;
    snprintf(g_probe_host, sizeof(g_probe_host), "%s", host);
    return 0;
}

static const uint8_t k_nonce[16] = { 0x10, 0x21, 0x32, 0x43, 0x54, 0x65, 0x76, 0x87,
                                     0x98, 0xA9, 0xBA, 0xCB, 0xDC, 0xED, 0xFE, 0x0F };

static const uint8_t k_companion[16] = { 0xC0, 0xC1, 0xC2, 0xC3, 0xC4, 0xC5, 0xC6, 0xC7,
                                         0xC8, 0xC9, 0xCA, 0xCB, 0xCC, 0xCD, 0xCE, 0xCF };

static void make_record(halyard_pairing_record *rec, int is_ps5)
{
    int i;

    memset(rec, 0, sizeof(*rec));
    snprintf(rec->host, sizeof(rec->host), "127.0.0.1");
    rec->is_ps5 = is_ps5;
    memcpy(rec->registkey, "1a2b3c4d", 8);
    rec->registkey_length = 8;
    memcpy(rec->companion, k_companion, sizeof(k_companion));
    for (i = 0; i < 16; i++)
        rec->device_id[i] = (uint8_t)(0x30 + i);
    rec->device_id_length = 16;
    rec->os_major = 10;
    rec->os_minor = 0;
    rec->start_bitrate = 15000;
    rec->streaming_type = 0;
}

/* ---- the requests, assembled independently of halyard_control_session.c ---- */

static size_t expected_init(const halyard_pairing_record *rec, const char *host, const char *version_name,
                            char *out, size_t out_size)
{
    char hex[64];

    rc_hex_encode(rec->registkey, rec->registkey_length, hex);
    return (size_t)snprintf(out, out_size,
                            "GET /sie/%s/rp/sess/init HTTP/1.1\r\n"
                            "Host: %s\r\n"
                            "User-Agent: remoteplay Windows\r\n"
                            "Connection: close\r\n"
                            "RP-Registkey: %s\r\n"
                            "%s: %s\r\n"
                            "Content-Length: 0\r\n\r\n",
                            rec->is_ps5 ? "ps5" : "ps4", host, hex, version_name, rec->is_ps5 ? "1.0" : "10.0");
}

static size_t expected_ctrl(const halyard_pairing_record *rec, const char *host, int conpath, char *out,
                            size_t out_size)
{
    halyard_control_field f;
    uint8_t auth[16], did[32], bitrate[4], streaming[4];
    char os[16];
    size_t os_length;
    char auth_b64[32], did_b64[48], os_b64[32], bitrate_b64[16], streaming_b64[16];
    int n;

    if (halyard_control_field_init(&f, k_nonce, rec->companion, 2,
                                   rec->is_ps5 ? HALYARD_VERSION_SELECTOR_PS5 : HALYARD_VERSION_SELECTOR_PS4) != 0)
        return 0;
    halyard_sess_field_auth_plaintext(rec->registkey, rec->registkey_length, auth);
    halyard_control_field_encrypt(&f, 0, auth, auth, sizeof(auth));
    rc_base64_encode(auth, sizeof(auth), auth_b64, sizeof(auth_b64));
    halyard_sess_field_did_plaintext(rec->device_id, rec->device_id_length, did);
    halyard_control_field_encrypt(&f, 1, did, did, sizeof(did));
    rc_base64_encode(did, sizeof(did), did_b64, sizeof(did_b64));
    os_length = halyard_sess_field_os_type_plaintext(rec->os_major, rec->os_minor, os, sizeof(os));
    halyard_control_field_encrypt(&f, 2, (const uint8_t *)os, (uint8_t *)os, os_length);
    rc_base64_encode((const uint8_t *)os, os_length, os_b64, sizeof(os_b64));
    halyard_sess_field_int32le_plaintext(rec->start_bitrate, bitrate);
    halyard_control_field_encrypt(&f, 3, bitrate, bitrate, sizeof(bitrate));
    rc_base64_encode(bitrate, sizeof(bitrate), bitrate_b64, sizeof(bitrate_b64));
    halyard_sess_field_int32le_plaintext(rec->streaming_type, streaming);
    halyard_control_field_encrypt(&f, 4, streaming, streaming, sizeof(streaming));
    rc_base64_encode(streaming, sizeof(streaming), streaming_b64, sizeof(streaming_b64));

    n = snprintf(out, out_size,
                 "GET /sie/%s/rp/sess/ctrl HTTP/1.1\r\n"
                 "Host: %s\r\n"
                 "User-Agent: remoteplay Windows\r\n"
                 "Connection: keep-alive\r\n"
                 "RP-Version: %s\r\n"
                 "RP-ControllerType: 0\r\n"
                 "RP-ClientType: 11\r\n"
                 "RP-ConPath: %d\r\n"
                 "RP-PadProcNo: 2\r\n"
                 "RP-SupportCmd: 060000\r\n"
                 "RP-Auth: %s\r\n"
                 "RP-Did: %s\r\n"
                 "RP-OSType: %s\r\n"
                 "RP-StartBitrate: %s\r\n"
                 "%s%s%s"
                 "Content-Length: 0\r\n\r\n",
                 rec->is_ps5 ? "ps5" : "ps4", host, rec->is_ps5 ? "1.0" : "10.0", conpath, auth_b64, did_b64,
                 os_b64, bitrate_b64, rec->is_ps5 ? "RP-StreamingType: " : "", rec->is_ps5 ? streaming_b64 : "",
                 rec->is_ps5 ? "\r\n" : "");
    return n < 0 ? 0u : (size_t)n;
}

/* Decrypts one binary frame's payload at `counter` and compares it with `plain`. */
static int frame_decrypts(const uint8_t *frame, size_t frame_length, unsigned type, uint64_t counter,
                          const uint8_t *plain, size_t plain_length)
{
    halyard_control_field f;
    unsigned got_type = 0;
    const uint8_t *payload = NULL;
    size_t payload_length = 0;
    uint8_t out[256];

    if (halyard_ctrl_message_parse(frame, frame_length, &got_type, &payload, &payload_length) != frame_length
        || got_type != type || payload_length != plain_length || plain_length > sizeof(out))
        return 0;
    if (halyard_control_field_init(&f, k_nonce, k_companion, 2, HALYARD_VERSION_SELECTOR_PS5) != 0)
        return 0;
    halyard_control_field_decrypt(&f, counter, payload, out, payload_length);
    return memcmp(out, plain, plain_length) == 0;
}

static const uint8_t k_heartbeat_req[8] = { 0, 0, 0, 0, 0x00, 0xFE, 0, 0 };
static const uint8_t k_heartbeat_rep[8] = { 0, 0, 0, 0, 0x01, 0xFE, 0, 0 };
static const uint8_t k_report[16] = { 0, 0, 0x27, 0x10, 0, 0, 0x05, 0xAE, 0, 0, 0, 0, 0, 0, 0, 7 };

/* Services until `want` (a frame type, or the CLOSED event when want is 0), at most `ms`. */
static int service_until(halyard_control_session *s, unsigned want, unsigned ms, int *out_closed)
{
    uint64_t start = rc_time_ms();

    *out_closed = 0;
    while (rc_time_ms() - start < (uint64_t)ms) {
        halyard_control_event ev;

        memset(&ev, 0, sizeof(ev));
        if (!halyard_control_session_service(s, &ev)) {
            *out_closed = ev.kind == HALYARD_CONTROL_EVENT_CLOSED ? 1 : -1;
            return want == 0u;
        }
        if (ev.kind == HALYARD_CONTROL_EVENT_MESSAGE && want != 0u && ev.type == want)
            return 1;
        if (ev.kind == HALYARD_CONTROL_EVENT_NONE)
            rc_sleep_ms(2u);
    }
    return 0;
}

/* ==== 1. the TCP pipe, against a loopback console ===================================================== */

typedef struct {
    int listener;
    char nonce_b64[32];
    char init[2048];
    size_t init_length;
    char ctrl[2048];
    size_t ctrl_length;
    uint8_t after[1024];
    size_t after_length;
    size_t expect_after;
} tcp_console;

static size_t read_head(int sock, char *out, size_t capacity)
{
    size_t got = 0;

    while (got + 1 < capacity) {
        ssize_t n = recv(sock, out + got, 1, 0);

        if (n <= 0)
            break;
        got++;
        if (got >= 4 && memcmp(out + got - 4, "\r\n\r\n", 4) == 0)
            break;
    }
    out[got] = '\0';
    return got;
}

static void set_timeout(int sock, int ms)
{
    struct timeval tv;

    tv.tv_sec = ms / 1000;
    tv.tv_usec = (ms % 1000) * 1000;
    setsockopt(sock, SOL_SOCKET, SO_RCVTIMEO, &tv, (socklen_t)sizeof(tv));
}

static void *tcp_console_main(void *arg)
{
    tcp_console *tc = (tcp_console *)arg;
    char reply[256];
    uint8_t burst[128];
    int c1, c2;
    int n;

    c1 = accept(tc->listener, NULL, NULL);
    if (c1 < 0)
        return NULL;
    set_timeout(c1, 3000);
    tc->init_length = read_head(c1, tc->init, sizeof(tc->init));
    n = snprintf(reply, sizeof(reply), "HTTP/1.1 200 OK\r\nRP-Nonce: %s\r\nContent-Length: 0\r\n\r\n", tc->nonce_b64);
    send(c1, reply, (size_t)n, 0);
    close(c1);

    c2 = accept(tc->listener, NULL, NULL);
    if (c2 < 0)
        return NULL;
    set_timeout(c2, 3000);
    tc->ctrl_length = read_head(c2, tc->ctrl, sizeof(tc->ctrl));
    /* The head and the first binary frame in one write: whatever follows the head is the channel's. */
    n = snprintf((char *)burst, sizeof(burst), "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");
    memcpy(burst + n, k_heartbeat_req, sizeof(k_heartbeat_req));
    send(c2, burst, (size_t)n + sizeof(k_heartbeat_req), 0);

    while (tc->after_length < tc->expect_after) {
        ssize_t got = recv(c2, tc->after + tc->after_length, sizeof(tc->after) - tc->after_length, 0);

        if (got <= 0)
            break;
        tc->after_length += (size_t)got;
    }
    close(c2);
    return NULL;
}

static int listen_on(unsigned short port, unsigned short *out_port)
{
    struct sockaddr_in addr;
    socklen_t len = (socklen_t)sizeof(addr);
    int s = socket(AF_INET, SOCK_STREAM, 0);
    int one = 1;

    if (s < 0)
        return -1;
    setsockopt(s, SOL_SOCKET, SO_REUSEADDR, &one, (socklen_t)sizeof(one));
    memset(&addr, 0, sizeof(addr));
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    addr.sin_port = htons(port);
    if (bind(s, (struct sockaddr *)&addr, sizeof(addr)) != 0 || listen(s, 4) != 0
        || getsockname(s, (struct sockaddr *)&addr, &len) != 0) {
        close(s);
        return -1;
    }
    *out_port = ntohs(addr.sin_port);
    return s;
}

/* One whole TCP session. `use_default` runs the unmodified halyard_control_session_open (port 9295). */
static void run_tcp_session(int listener, unsigned short port, int use_default, const char *label)
{
    static tcp_console tc;
    static halyard_control_session s;
    halyard_pairing_record rec;
    halyard_control_open_options options;
    pthread_t thread;
    char want[2048];
    size_t want_length;
    uint8_t login_plain[8];
    int closed = 0;
    int opened;

    make_record(&rec, 1);
    memset(&tc, 0, sizeof(tc));
    tc.listener = listener;
    rc_base64_encode(k_nonce, sizeof(k_nonce), tc.nonce_b64, sizeof(tc.nonce_b64));
    /* heartbeat reply 8, login 8+4, field 8+16 */
    tc.expect_after = 8u + 12u + 24u;
    g_probe_calls = 0;

    if (pthread_create(&thread, NULL, tcp_console_main, &tc) != 0) {
        CHECK(0, "%s: console thread", label);
        return;
    }

    memset(&options, 0, sizeof(options));
    options.port = port;
    opened = use_default ? halyard_control_session_open(&rec, &s) : halyard_control_session_open_with(&rec, &options, &s);
    CHECK(opened, "%s: /sess/init and /sess/ctrl over TCP", label);
    CHECK(g_probe_calls == 1 && strcmp(g_probe_host, "127.0.0.1") == 0,
          "%s: the ARM probe is still sent first, once, to the record's host (%d)", label, g_probe_calls);
    CHECK(s.pipe == &halyard_control_pipe_tcp, "%s: and the session is on the TCP pipe", label);
    CHECK(s.next_counter == 5u && s.recv_counter == 1u, "%s: counters positioned as before", label);

    if (opened) {
        CHECK(service_until(&s, HALYARD_CTRL_TYPE_HEARTBEAT_REQ, 2000, &closed),
              "%s: the frame that shared the response's segment is read, and answered", label);
        CHECK(halyard_control_session_submit_login(&s, "1234", 4), "%s: a passcode goes out", label);
        CHECK(halyard_control_session_send_field(&s, HALYARD_CTRL_TYPE_PROBE_REPORT, k_report, sizeof(k_report)),
              "%s: a field frame goes out", label);
        CHECK(service_until(&s, 0, 3000, &closed) && closed == 1, "%s: the console's close is CLOSED", label);
        halyard_control_session_close(&s);
    }
    pthread_join(thread, NULL);

    want_length = expected_init(&rec, "127.0.0.1", "RP-Version", want, sizeof(want));
    CHECK(tc.init_length == want_length && memcmp(tc.init, want, want_length) == 0,
          "%s: /sess/init is byte-for-byte the request it always was:\n%s", label, tc.init);
    want_length = expected_ctrl(&rec, "127.0.0.1", 1, want, sizeof(want));
    CHECK(tc.ctrl_length == want_length && memcmp(tc.ctrl, want, want_length) == 0,
          "%s: /sess/ctrl is byte-for-byte the request it always was:\n%s", label, tc.ctrl);

    halyard_sess_field_login_pin_plaintext("1234", 4, login_plain, sizeof(login_plain));
    CHECK(tc.after_length == tc.expect_after && memcmp(tc.after, k_heartbeat_rep, 8) == 0,
          "%s: the heartbeat reply is the first thing after the head (%lu bytes)", label,
          (unsigned long)tc.after_length);
    CHECK(frame_decrypts(tc.after + 8, 12, HALYARD_CTRL_TYPE_LOGIN_SUBMIT, 5, login_plain, 4),
          "%s: the passcode is at counter 5", label);
    CHECK(frame_decrypts(tc.after + 20, 24, HALYARD_CTRL_TYPE_PROBE_REPORT, 6, k_report, sizeof(k_report)),
          "%s: and the field frame after it at counter 6", label);
}

static void test_tcp_pipe(void)
{
    unsigned short port = 0;
    int listener = listen_on(0, &port);

    CHECK(listener >= 0, "a loopback listener");
    if (listener >= 0) {
        run_tcp_session(listener, port, 0, "tcp");
        close(listener);
    }

    listener = listen_on(HALYARD_CONTROL_ARM_PORT, &port);
    if (listener >= 0) {
        run_tcp_session(listener, port, 1, "tcp default");
        close(listener);
    } else {
        printf("control_pipe_test: 127.0.0.1:%u is taken; the default-options run is skipped\n",
               (unsigned)HALYARD_CONTROL_ARM_PORT);
    }
}

/* ==== 2. the datagram pipe, against a scripted console ================================================ */

#define QUEUE_MAX 64

typedef struct {
    size_t count;
    size_t head;
    size_t length[QUEUE_MAX];
    uint8_t data[QUEUE_MAX][1300];
} queue;

typedef struct {
    fake_console console;
    queue to_client;
} scripted;

static void q_emit(void *ctx, const uint8_t *datagram, size_t length)
{
    queue *q = (queue *)ctx;
    size_t slot = (q->head + q->count) % QUEUE_MAX;

    if (q->count < QUEUE_MAX && length <= sizeof(q->data[0])) {
        memcpy(q->data[slot], datagram, length);
        q->length[slot] = length;
        q->count++;
    }
}

static int s_send(void *ctx, const uint8_t *datagram, size_t length)
{
    fake_console_on_datagram(&((scripted *)ctx)->console, datagram, length);
    return 1;
}

static long s_receive(void *ctx, uint8_t *buffer, size_t capacity)
{
    queue *q = &((scripted *)ctx)->to_client;
    size_t length;

    if (q->count == 0)
        return 0;
    length = q->length[q->head] < capacity ? q->length[q->head] : capacity;
    memcpy(buffer, q->data[q->head], length);
    q->head = (q->head + 1) % QUEUE_MAX;
    q->count--;
    return (long)length;
}

static void scripted_channel(scripted *sc, halyard_dgram_channel *ch, const char *init_reply)
{
    static const uint8_t address[4] = { 127, 0, 0, 1 };
    halyard_dgram_transport t;
    halyard_dgram_options o;

    memset(sc, 0, sizeof(*sc));
    fake_console_init(&sc->console, q_emit, &sc->to_client);
    sc->console.init_reply = init_reply;
    t.send = s_send;
    t.receive = s_receive;
    t.ctx = sc;
    halyard_dgram_options_default(&o);
    o.receive_timeout_ms = 20;
    o.stage_timeout_ms = 2000;
    halyard_dgram_channel_init(ch, &t, address, 9303, fake_client_id(), fake_console_id(), &o);
}

static void test_dgram_pipe(void)
{
    static scripted sc;
    static halyard_dgram_channel ch;
    static halyard_dgram_control_pipe pipe;
    static halyard_control_session s;
    halyard_pairing_record rec;
    halyard_control_open_options options;
    char init_reply[160];
    char nonce_b64[32];
    char host[48];
    char want[2048];
    size_t want_length;
    int closed = 0;
    int opened;

    make_record(&rec, 1);
    rc_base64_encode(k_nonce, sizeof(k_nonce), nonce_b64, sizeof(nonce_b64));
    snprintf(init_reply, sizeof(init_reply), "HTTP/1.1 200 OK\r\nRP-Nonce: %s\r\nContent-Length: 0\r\n\r\n", nonce_b64);

    CHECK(halyard_dgram_host_header("127.0.0.1", 9303, host, sizeof(host)) == 20 && strcmp(host, "127.  0.  0.  1:9303") == 0,
          "the Host header is right-aligned in three columns, as .NET's HostHeader (%s)", host);
    CHECK(halyard_dgram_host_header("192.0.2.104", 9303, host, sizeof(host)) > 0
              && strcmp(host, "192.  0.  2.104:9303") == 0,
          "and pads each octet as the captured requests do (%s)", host);
    CHECK(halyard_dgram_host_header("console.local", 9303, host, sizeof(host)) > 0
              && strcmp(host, "console.local:9303") == 0,
          "a name is written as is");
    CHECK(halyard_dgram_host_header("127.0.0.1", 9303, host, 8) == 0, "and a buffer too small is refused");
    halyard_dgram_host_header("127.0.0.1", 9303, host, sizeof(host));

    scripted_channel(&sc, &ch, init_reply);
    halyard_dgram_control_pipe_init(&pipe, &ch, 5);
    memset(&options, 0, sizeof(options));
    options.pipe = &pipe.pipe;
    options.skip_arm_probe = 1;
    options.connection_path = 3;
    options.host_header = host;
    options.init_version_header = "Rp-Version";
    g_probe_calls = 0;

    opened = halyard_control_session_open_with(&rec, &options, &s);
    CHECK(opened && s.sock == 5, "/sess/init and /sess/ctrl over the association");
    CHECK(g_probe_calls == 0, "with no ARM probe");
    CHECK(sc.console.inits == 1 && sc.console.hellos == 2 && sc.console.requests == 2,
          "as two connections over one prelude (inits %d, hellos %d, requests %d)", sc.console.inits,
          sc.console.hellos, sc.console.requests);
    want_length = expected_init(&rec, host, "Rp-Version", want, sizeof(want));
    CHECK(sc.console.request_length[0] == want_length && memcmp(sc.console.request[0], want, want_length) == 0,
          "/sess/init carries the padded Host and \"Rp-Version\", as .NET's SendInitAsync:\n%s", sc.console.request[0]);
    want_length = expected_ctrl(&rec, host, 3, want, sizeof(want));
    CHECK(sc.console.request_length[1] == want_length && memcmp(sc.console.request[1], want, want_length) == 0,
          "/sess/ctrl carries RP-ConPath 3 and the same fields:\n%s", sc.console.request[1]);
    if (!opened)
        return;

    fake_console_push(&sc.console, k_heartbeat_req, sizeof(k_heartbeat_req));
    CHECK(service_until(&s, HALYARD_CTRL_TYPE_HEARTBEAT_REQ, 1000, &closed), "a heartbeat arrives over the association");
    CHECK(sc.console.frames == 1 && sc.console.frame_length[0] == 8 && memcmp(sc.console.frame[0], k_heartbeat_rep, 8) == 0,
          "and is answered on it");
    CHECK(halyard_control_session_send_field(&s, HALYARD_CTRL_TYPE_PROBE_REPORT, k_report, sizeof(k_report))
              && sc.console.frames == 2
              && frame_decrypts(sc.console.frame[1], sc.console.frame_length[1], HALYARD_CTRL_TYPE_PROBE_REPORT, 5,
                                k_report, sizeof(k_report)),
          "a field frame goes out at the next counter (5, with no passcode before it)");

    /* A frame and the console's Close in the same poll: the frame first, the close on the next read. */
    fake_console_push(&sc.console, k_heartbeat_req, sizeof(k_heartbeat_req));
    fake_console_close(&sc.console);
    CHECK(service_until(&s, HALYARD_CTRL_TYPE_HEARTBEAT_REQ, 1000, &closed), "data ahead of a Close is still delivered");
    CHECK(service_until(&s, 0, 1000, &closed) && closed == 1, "and the Close is then reported, not forgotten");
    halyard_control_session_close(&s);

    /* Closing our side is a Close chunk: the goodbye a datagram console needs. */
    scripted_channel(&sc, &ch, init_reply);
    halyard_dgram_control_pipe_init(&pipe, &ch, 5);
    if (halyard_control_session_open_with(&rec, &options, &s)) {
        int before = sc.console.closes_received;

        halyard_control_session_close(&s);
        CHECK(sc.console.closes_received == before + 1 && s.sock == -1, "close says goodbye with a Close chunk");
    } else {
        CHECK(0, "a second open");
    }

    /* An /sess/init answered without RP-Nonce fails, and leaves nothing open. */
    scripted_channel(&sc, &ch, NULL);
    halyard_dgram_control_pipe_init(&pipe, &ch, 5);
    CHECK(!halyard_control_session_open_with(&rec, &options, &s) && s.sock == -1 && sc.console.requests == 1,
          "an /sess/init with no RP-Nonce fails before /sess/ctrl is sent");
}

/* ==== 3. the registration adapter ===================================================================== */

static void test_regist_adapter(void)
{
    static scripted sc;
    static halyard_dgram_channel ch;
    static const char rgst[] = "POST /sie/ps5/rp/sess/rgst HTTP/1.1\r\nContent-Length: 0\r\n\r\n";
    static const char reply[] = "HTTP/1.1 200 OK\r\nContent-Length: 4\r\n\r\nabcd";
    uint8_t response[256];
    size_t response_length = 99;
    halyard_account_regist_params params;
    halyard_account_regist_result result;

    scripted_channel(&sc, &ch, NULL);
    sc.console.other_reply = reply;
    CHECK(halyard_dgram_regist_exchange(&ch, (const uint8_t *)rgst, sizeof(rgst) - 1, response, sizeof(response),
                                        &response_length)
              && response_length == sizeof(reply) - 1 && memcmp(response, reply, response_length) == 0,
          "the adapter returns the whole reply, as ExchangeAsync does");
    CHECK(sc.console.requests == 1 && memcmp(sc.console.request[0], rgst, sizeof(rgst) - 1) == 0,
          "having sent the request unchanged");

    scripted_channel(&sc, &ch, NULL);
    sc.console.close_before_answering = 1;
    CHECK(!halyard_dgram_regist_exchange(&ch, (const uint8_t *)rgst, sizeof(rgst) - 1, response, sizeof(response),
                                         &response_length)
              && response_length == 0,
          "a console that closes before answering is a failed exchange");

    /* The whole flow over it: a refusal comes back as the console's own words, not as a transport error. */
    scripted_channel(&sc, &ch, NULL);
    sc.console.other_reply = "HTTP/1.1 403 Forbidden\r\nRP-Application-Reason: 80108b09\r\nContent-Length: 0\r\n\r\n";
    memset(&params, 0, sizeof(params));
    params.is_ps5 = 1;
    snprintf(params.account_id, sizeof(params.account_id), "1234567890123456");
    snprintf(params.client_ip, sizeof(params.client_ip), "127.0.0.1");
    halyard_account_regist_run(&params, halyard_dgram_regist_exchange, &ch, &result);
    CHECK(result.status == HALYARD_ACCOUNT_REGIST_ERR_REFUSED && result.http_status == 403
              && strcmp(result.console_reason, "80108b09") == 0,
          "halyard_account_regist_run runs over the adapter (status %d, http %d)", (int)result.status,
          result.http_status);
    CHECK(sc.console.requests == 1 && memcmp(sc.console.request[0], "POST /sie/ps5/rp/sess/rgst ", 27) == 0,
          "and its POST reached the console over the association");
}

/* ==== 4. against the .NET session's own requests ===================================================== */

static char g_line[16384];

static int hex_field(const char *text, uint8_t *out, size_t capacity, size_t *out_length)
{
    size_t length = strlen(text);

    if (length % 2u != 0u || length / 2u > capacity)
        return 0;
    *out_length = rc_hex_decode(text, out, capacity);
    return *out_length == length / 2u;
}

static void run_vector_case(const halyard_pairing_record *rec, const uint8_t nonce[16], const uint8_t *init,
                            size_t init_length, const uint8_t *ctrl, size_t ctrl_length)
{
    static scripted sc;
    static halyard_dgram_channel ch;
    static halyard_dgram_control_pipe pipe;
    static halyard_control_session s;
    halyard_control_open_options options;
    char init_reply[160];
    char nonce_b64[32];
    char host[48];

    rc_base64_encode(nonce, 16, nonce_b64, sizeof(nonce_b64));
    snprintf(init_reply, sizeof(init_reply), "HTTP/1.1 200 OK\r\nRP-Nonce: %s\r\nContent-Length: 0\r\n\r\n", nonce_b64);
    halyard_dgram_host_header(rec->host, 9303, host, sizeof(host));

    scripted_channel(&sc, &ch, init_reply);
    halyard_dgram_control_pipe_init(&pipe, &ch, 5);
    memset(&options, 0, sizeof(options));
    options.pipe = &pipe.pipe;
    options.skip_arm_probe = 1;
    options.connection_path = 3;
    options.host_header = host;
    options.init_version_header = "Rp-Version";

    CHECK(halyard_control_session_open_with(rec, &options, &s), "%s: the vector's session opens", rec->is_ps5 ? "ps5" : "ps4");
    CHECK(sc.console.requests == 2 && sc.console.request_length[0] == init_length
              && memcmp(sc.console.request[0], init, init_length) == 0,
          "%s: /sess/init is byte-for-byte .NET's:\n%s", rec->is_ps5 ? "ps5" : "ps4", sc.console.request[0]);
    CHECK(sc.console.requests == 2 && sc.console.request_length[1] == ctrl_length
              && memcmp(sc.console.request[1], ctrl, ctrl_length) == 0,
          "%s: /sess/ctrl is byte-for-byte .NET's:\n%s", rec->is_ps5 ? "ps5" : "ps4", sc.console.request[1]);
    halyard_control_session_close(&s);
}

static void test_dotnet_vectors(const char *path)
{
    static uint8_t init[4096], ctrl[4096];
    FILE *f = fopen(path, "r");
    halyard_pairing_record rec;
    uint8_t nonce[16];
    size_t init_length = 0, ctrl_length = 0, n;
    int have_case = 0, cases = 0;

    if (f == NULL) {
        printf("control_pipe_test: %s is missing; generate it from the repository root with:\n"
               "    dotnet run --project tools/Ripcord.ProtocolLab -- vectors\n", path);
        g_failed++;
        return;
    }
    while (fgets(g_line, sizeof(g_line), f) != NULL) {
        char kind[8], platform[8], host[64], key[64], companion[64], device[128], nonce_hex[64], blob[8192];
        int os_major, os_minor, bitrate;

        g_line[strcspn(g_line, "\r\n")] = '\0';
        if (sscanf(g_line, "case %7s %63s %63s %63s %127s %63s %d %d %d", platform, host, key, companion, device,
                   nonce_hex, &os_major, &os_minor, &bitrate) == 9) {
            memset(&rec, 0, sizeof(rec));
            snprintf(rec.host, sizeof(rec.host), "%s", host);
            rec.is_ps5 = strcmp(platform, "ps5") == 0;
            have_case = hex_field(key, rec.registkey, sizeof(rec.registkey), &rec.registkey_length)
                        && hex_field(companion, rec.companion, sizeof(rec.companion), &n) && n == 16u
                        && hex_field(device, rec.device_id, sizeof(rec.device_id), &rec.device_id_length)
                        && hex_field(nonce_hex, nonce, sizeof(nonce), &n) && n == 16u;
            rec.os_major = os_major;
            rec.os_minor = os_minor;
            rec.start_bitrate = bitrate;
            rec.streaming_type = 0;
            init_length = ctrl_length = 0;
            CHECK(have_case, "a vector case parses");
            continue;
        }
        if (sscanf(g_line, "%7s %8191s", kind, blob) != 2 || !have_case)
            continue;
        if (strcmp(kind, "init") == 0)
            hex_field(blob, init, sizeof(init), &init_length);
        else if (strcmp(kind, "ctrl") == 0 && hex_field(blob, ctrl, sizeof(ctrl), &ctrl_length) && init_length > 0u) {
            run_vector_case(&rec, nonce, init, init_length, ctrl, ctrl_length);
            have_case = 0;
            cases++;
        }
    }
    fclose(f);
    CHECK(cases == 2, "both .NET cases ran (%d)", cases);
}

int main(int argc, char **argv)
{
    test_tcp_pipe();
    test_dgram_pipe();
    test_regist_adapter();
    test_dotnet_vectors(argc > 1 ? argv[1] : "vectors/rendezvous-control.kat");

    printf("control_pipe_test: %d passed, %d failed\n", g_passed, g_failed);
    return g_failed == 0 ? 0 : 1;
}
