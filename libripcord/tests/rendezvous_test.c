/*
 * ripcord host tests - the rendezvous route through halyard_client, as far as it runs without a console.
 *
 * A fake console runs in a thread on 127.0.0.1 with two UDP sockets - the control leg's 9303 association
 * and the A/V leg - plus a third that plays a stranger. It answers what a console answers
 * (fake_dgram_console.h), and sends the three control frames the route waits on at the moments a console
 * sends them: a heartbeat once /sess/ctrl is up, SESSION_ID once the A/V leg's prelude has arrived, and
 * STREAM_READY once PROBE_REPORT has. It never answers Takion, so every session here ends at the stream's
 * Takion handshake with TIMEOUT - which is exactly far enough to watch the ORDER .NET's
 * StartStreamingAsync imposes:
 *
 *   /sess/init, /sess/ctrl over the association, no ARM probe  ->  poll_media, with the A/V leg's port
 *   ->  the A/V prelude  ->  SESSION_ID  ->  senkusha's Takion INITs on the A/V socket  ->  PROBE_REPORT at
 *   the next field counter  ->  STREAM_READY  ->  the stream's Takion INITs, on the same socket
 *
 * and that the stream's handshake reads nothing from the stranger. Also here: prepare/begin's refusals, a
 * host that gives up on the media connection, and the polite Close at the end.
 *
 * NOTHING LEAVES LOOPBACK: both legs are bound to 127.0.0.1 through the config's bind_address, no STUN
 * server is configured, and every peer is 127.0.0.1.
 */
#include "fake_dgram_console.h"

#include "../client/halyard_client.h"
#include "../halyard/halyard_v1.h"
#include "../platform/rc_platform.h"
#include "../session/halyard_ctrl_message.h"
#include "../util/rc_base64.h"

#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <arpa/inet.h>
#include <netinet/in.h>
#include <sys/socket.h>
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

/* No host CSPRNG, deliberately (stun_test.c says why); a counter keeps runs identical. */
static uint8_t g_random_next = 0x40;

int rc_random_bytes(uint8_t *out, size_t length)
{
    size_t i;

    for (i = 0; i < length; i++)
        out[i] = g_random_next++;
    return 1;
}

/* The ARM probe, at link time (halyard_control_probe.c is not linked): the route must never send one,
 * and the real one would broadcast. */
static int g_probe_calls;

int halyard_control_arm_probe(const char *host, int is_ps5)
{
    (void)host;
    (void)is_ps5;
    g_probe_calls++;
    return 0;
}

static const uint8_t k_loopback[4] = { 127, 0, 0, 1 };
static const uint8_t k_nonce[16] = { 0x10, 0x21, 0x32, 0x43, 0x54, 0x65, 0x76, 0x87,
                                     0x98, 0xA9, 0xBA, 0xCB, 0xDC, 0xED, 0xFE, 0x0F };
static const uint8_t k_companion[16] = { 0xC0, 0xC1, 0xC2, 0xC3, 0xC4, 0xC5, 0xC6, 0xC7,
                                         0xC8, 0xC9, 0xCA, 0xCB, 0xCC, 0xCD, 0xCE, 0xCF };

/* ==== the fake console ================================================================================= */

typedef struct {
    int ctrl_sock, media_sock, stray_sock;
    uint16_t ctrl_port, media_port;
    struct sockaddr_in ctrl_client, media_client;
    int have_ctrl_client, have_media_client;
    fake_console ctrl, media;
    char init_reply[160];

    volatile int stop;
    int heartbeat_sent, session_id_sent, stream_ready_sent;
    int media_preludes;                 /* Inits on the A/V socket */
    int takion_before_report, takion_after_report;
    int report_seen, report_frame;      /* which ctrl frame index PROBE_REPORT was */
    int strays_sent;
    uint16_t media_client_port;         /* where the A/V leg's traffic came from */
} console_state;

static void ctrl_emit(void *ctx, const uint8_t *datagram, size_t length)
{
    console_state *cs = (console_state *)ctx;

    if (cs->have_ctrl_client)
        (void)sendto(cs->ctrl_sock, datagram, length, 0, (const struct sockaddr *)&cs->ctrl_client,
                     (socklen_t)sizeof(cs->ctrl_client));
}

static void media_emit(void *ctx, const uint8_t *datagram, size_t length)
{
    console_state *cs = (console_state *)ctx;

    if (cs->have_media_client)
        (void)sendto(cs->media_sock, datagram, length, 0, (const struct sockaddr *)&cs->media_client,
                     (socklen_t)sizeof(cs->media_client));
}

static int udp_loopback(uint16_t *out_port)
{
    struct sockaddr_in a;
    socklen_t len = (socklen_t)sizeof(a);
    int s = socket(AF_INET, SOCK_DGRAM, 0);

    if (s < 0)
        return -1;
    memset(&a, 0, sizeof(a));
    a.sin_family = AF_INET;
    a.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    if (bind(s, (struct sockaddr *)&a, sizeof(a)) != 0 || getsockname(s, (struct sockaddr *)&a, &len) != 0) {
        close(s);
        return -1;
    }
    *out_port = ntohs(a.sin_port);
    return s;
}

static void console_frames(console_state *cs)
{
    static const uint8_t heartbeat[8] = { 0, 0, 0, 0, 0x00, 0xFE, 0, 0 };
    uint8_t frame[8];

    if (cs->ctrl.ctrl_open && !cs->heartbeat_sent) {
        fake_console_push(&cs->ctrl, heartbeat, sizeof(heartbeat));
        cs->heartbeat_sent = 1;
    }
    if (cs->media_preludes > 0 && !cs->session_id_sent) {
        halyard_ctrl_message_build(HALYARD_CTRL_TYPE_SESSION_ID, NULL, 0, frame, sizeof(frame));
        fake_console_push(&cs->ctrl, frame, sizeof(frame));
        cs->session_id_sent = 1;
    }
    if (!cs->report_seen) {
        int i;

        for (i = 0; i < cs->ctrl.frames; i++) {
            if (cs->ctrl.frame_length[i] >= 8u && cs->ctrl.frame[i][4] == 0x00 && cs->ctrl.frame[i][5] == 0x0D) {
                cs->report_seen = 1;
                cs->report_frame = i;
            }
        }
        if (cs->report_seen && !cs->stream_ready_sent) {
            halyard_ctrl_message_build(HALYARD_CTRL_TYPE_STREAM_READY, NULL, 0, frame, sizeof(frame));
            fake_console_push(&cs->ctrl, frame, sizeof(frame));
            cs->stream_ready_sent = 1;
        }
    }
}

static void *console_main(void *arg)
{
    console_state *cs = (console_state *)arg;
    uint64_t start = rc_time_ms();

    while (!cs->stop && rc_time_ms() - start < 60000u) {
        uint8_t buf[2048];
        struct sockaddr_in from;
        socklen_t from_length = (socklen_t)sizeof(from);
        ssize_t n;
        int busy = 0;

        n = recvfrom(cs->ctrl_sock, buf, sizeof(buf), MSG_DONTWAIT, (struct sockaddr *)&from, &from_length);
        if (n > 0) {
            cs->ctrl_client = from;
            cs->have_ctrl_client = 1;
            fake_console_on_datagram(&cs->ctrl, buf, (size_t)n);
            busy = 1;
        }

        from_length = (socklen_t)sizeof(from);
        n = recvfrom(cs->media_sock, buf, sizeof(buf), MSG_DONTWAIT, (struct sockaddr *)&from, &from_length);
        if (n > 0) {
            halyard_dgram_prelude p;

            cs->media_client = from;
            cs->have_media_client = 1;
            cs->media_client_port = ntohs(from.sin_port);
            if (halyard_dgram_prelude_parse(buf, (size_t)n, &p)) {
                if (p.type == HALYARD_DGRAM_PRELUDE_INIT)
                    cs->media_preludes++;
                fake_console_on_datagram(&cs->media, buf, (size_t)n);
            } else if (buf[0] == 0x00) {
                /* A Takion packet: base type 0. The fake never answers one. */
                if (cs->report_seen) {
                    cs->takion_after_report++;
                    if (cs->strays_sent < 3) {
                        /* A stranger writes to the A/V socket, Takion-shaped, while the stream handshake waits. */
                        uint8_t stray[24];

                        memset(stray, 0, sizeof(stray));
                        (void)sendto(cs->stray_sock, stray, sizeof(stray), 0, (const struct sockaddr *)&from,
                                     (socklen_t)sizeof(from));
                        cs->strays_sent++;
                    }
                } else {
                    cs->takion_before_report++;
                }
            }
            busy = 1;
        }

        console_frames(cs);
        if (!busy)
            rc_sleep_ms(1u);
    }
    return NULL;
}

static int console_start(console_state *cs, pthread_t *thread)
{
    uint16_t stray_port = 0;
    char nonce_b64[32];

    memset(cs, 0, sizeof(*cs));
    cs->ctrl_sock = udp_loopback(&cs->ctrl_port);
    cs->media_sock = udp_loopback(&cs->media_port);
    cs->stray_sock = udp_loopback(&stray_port);
    if (cs->ctrl_sock < 0 || cs->media_sock < 0 || cs->stray_sock < 0)
        return 0;
    fake_console_init(&cs->ctrl, ctrl_emit, cs);
    fake_console_init(&cs->media, media_emit, cs);
    rc_base64_encode(k_nonce, sizeof(k_nonce), nonce_b64, sizeof(nonce_b64));
    snprintf(cs->init_reply, sizeof(cs->init_reply), "HTTP/1.1 200 OK\r\nRP-Nonce: %s\r\nContent-Length: 0\r\n\r\n",
             nonce_b64);
    cs->ctrl.init_reply = cs->init_reply;
    return pthread_create(thread, NULL, console_main, cs) == 0;
}

static void console_stop(console_state *cs, pthread_t thread)
{
    /* A moment for the client's last datagrams (the Close) to be read before the thread stops. */
    rc_sleep_ms(50u);
    cs->stop = 1;
    pthread_join(thread, NULL);
    close(cs->ctrl_sock);
    close(cs->media_sock);
    close(cs->stray_sock);
}

/* ==== the client's host side =========================================================================== */

typedef struct {
    int stages[32];
    int stage_count;
    int media_polls;
    int give_up;
    halyard_client_leg media_leg;
    uint16_t media_port;
    uint8_t console_id[20];
} host_state;

static void on_stage(void *user, halyard_client_stage stage)
{
    host_state *h = (host_state *)user;

    if (h->stage_count < 32)
        h->stages[h->stage_count++] = (int)stage;
}

static void on_video(void *user, const uint8_t *data, size_t length, int is_keyframe)
{
    (void)user;
    (void)data;
    (void)length;
    (void)is_keyframe;
}

static void on_log(void *user, int level, const char *line)
{
    (void)user;
    (void)level;
    if (getenv("RENDEZVOUS_TEST_VERBOSE") != NULL)
        printf("  | %s\n", line);
}

/* The host's media negotiation: "not yet" twice, as a signaling round trip would, then the answer. */
static int on_media(void *user, const halyard_client_leg *media, halyard_client_peer *out)
{
    host_state *h = (host_state *)user;

    h->media_polls++;
    h->media_leg = *media;
    if (h->give_up)
        return -1;
    if (h->media_polls < 3)
        return 0;
    memcpy(out->address, k_loopback, 4);
    out->port = h->media_port;
    memcpy(out->console_hashed_id, h->console_id, 20);
    return 1;
}

static halyard_pairing_record g_record;

static void make_record(void)
{
    int i;

    memset(&g_record, 0, sizeof(g_record));
    snprintf(g_record.host, sizeof(g_record.host), "127.0.0.1");
    g_record.is_ps5 = 1;
    memcpy(g_record.registkey, "1a2b3c4d", 8);
    g_record.registkey_length = 8;
    memcpy(g_record.companion, k_companion, 16);
    for (i = 0; i < 16; i++)
        g_record.device_id[i] = (uint8_t)(0x30 + i);
    g_record.device_id_length = 16;
    g_record.os_major = 10;
    g_record.start_bitrate = 15000;
}

static void *g_storage;
static size_t g_storage_size;

static halyard_client *make_client(halyard_client_callbacks *cb, host_state *h, halyard_route route)
{
    halyard_client_config config;

    make_record();
    memset(cb, 0, sizeof(*cb));
    cb->user = h;
    cb->stage = on_stage;
    cb->video_frame = on_video;
    cb->log = on_log;
    cb->poll_media = on_media;

    memset(&config, 0, sizeof(config));
    config.record = &g_record;
    config.route = route;
    config.bind_address = k_loopback;
    config.senkusha_attempts = 2;
    config.stream_attempts = 3;
    config.attempt_interval_ms = 50;
    config.dgram_stage_timeout_ms = 3000;
    config.dgram_receive_timeout_ms = 100;
    config.media_offer_timeout_ms = 3000;
    return halyard_client_init(g_storage, g_storage_size, &config, cb);
}

/* Decrypts ctrl frame `i` at `counter` against `plain`. */
static int report_decrypts(const console_state *cs, int i, uint64_t counter, const uint8_t *plain, size_t length)
{
    halyard_control_field f;
    unsigned type = 0;
    const uint8_t *payload = NULL;
    size_t payload_length = 0;
    uint8_t out[64];

    if (halyard_ctrl_message_parse(cs->ctrl.frame[i], cs->ctrl.frame_length[i], &type, &payload, &payload_length) == 0
        || type != HALYARD_CTRL_TYPE_PROBE_REPORT || payload_length != length || length > sizeof(out))
        return 0;
    if (halyard_control_field_init(&f, k_nonce, k_companion, 2, HALYARD_VERSION_SELECTOR_PS5) != 0)
        return 0;
    halyard_control_field_decrypt(&f, counter, payload, out, length);
    return memcmp(out, plain, length) == 0;
}

static int contains(const char *haystack, const char *needle)
{
    return strstr(haystack, needle) != NULL;
}

static void test_the_whole_route(void)
{
    static console_state cs;
    pthread_t thread;
    halyard_client_callbacks cb;
    host_state h;
    halyard_client *c;
    halyard_client_leg leg;
    halyard_client_peer peer;
    const halyard_client_result *r;
    halyard_client_stage reached;
    static const uint8_t want_report[16] = { 0, 0, 0x27, 0x10, 0, 0, 0x05, 0xAE, 0, 0, 0, 0, 0, 0, 0, 0 };
    static const int want_stages[] = { HALYARD_CLIENT_STAGE_CONTROL_OPEN, HALYARD_CLIENT_STAGE_SIGNED_IN,
                                       HALYARD_CLIENT_STAGE_SESSION_READY, HALYARD_CLIENT_STAGE_SENKUSHA_UP,
                                       HALYARD_CLIENT_STAGE_TAKION_UP, HALYARD_CLIENT_STAGE_ENDED };
    int i, stages_ok;

    if (!console_start(&cs, &thread)) {
        CHECK(0, "the fake console starts");
        return;
    }
    memset(&h, 0, sizeof(h));
    h.media_port = cs.media_port;
    memcpy(h.console_id, fake_console_id(), 20);
    c = make_client(&cb, &h, HALYARD_ROUTE_RENDEZVOUS);
    CHECK(c != NULL, "a RENDEZVOUS client initialises with poll_media");
    if (c == NULL) {
        console_stop(&cs, thread);
        return;
    }

    memset(&peer, 0, sizeof(peer));
    CHECK(halyard_client_rendezvous_begin(c, fake_client_id(), &peer) == 0, "begin before prepare is refused");
    memset(&leg, 0, sizeof(leg));
    CHECK(halyard_client_rendezvous_prepare(c, &leg) && leg.local_port != 0u && !leg.has_reflexive
              && leg.endpoint_independent == -1,
          "prepare binds the control leg and reports its port, and no mapping with no STUN (port %u)",
          (unsigned)leg.local_port);

    memset(&peer, 0, sizeof(peer));
    memcpy(peer.address, k_loopback, 4);
    peer.port = cs.ctrl_port;
    memcpy(peer.console_hashed_id, fake_console_id(), 20);
    CHECK(halyard_client_rendezvous_begin(c, fake_client_id(), &peer), "begin aims the leg and sends our Init");
    CHECK(halyard_client_rendezvous_begin(c, fake_client_id(), &peer) == 0, "and only once");

    reached = halyard_client_connect(c);
    r = halyard_client_result_get(c);

    CHECK(reached == HALYARD_CLIENT_STAGE_SESSION_READY && r->end_reason == HALYARD_CLIENT_END_TIMEOUT,
          "a console that never answers Takion ends at the stream handshake (stage %d, reason %d)", (int)reached,
          (int)r->end_reason);
    stages_ok = h.stage_count == (int)(sizeof(want_stages) / sizeof(want_stages[0]));
    for (i = 0; stages_ok && i < h.stage_count; i++)
        stages_ok = h.stages[i] == want_stages[i];
    CHECK(stages_ok, "the stages are announced in the LAN's order, the A/V leg inside SESSION_READY (%d announced)",
          h.stage_count);

    CHECK(g_probe_calls == 0, "no ARM probe on this route");
    CHECK(cs.ctrl.requests == 2 && cs.ctrl.inits >= 1 && cs.ctrl.hellos == 2,
          "/sess/init and /sess/ctrl went over the control association (requests %d)", cs.ctrl.requests);
    CHECK(contains(cs.ctrl.request[0], "GET /sie/ps5/rp/sess/init HTTP/1.1\r\n")
              && contains(cs.ctrl.request[0], "\r\nHost: 127.  0.  0.  1:9303\r\n")
              && contains(cs.ctrl.request[0], "\r\nRp-Version: 1.0\r\n"),
          "init is spelled as the capture spells it:\n%s", cs.ctrl.request[0]);
    CHECK(contains(cs.ctrl.request[1], "\r\nRP-ConPath: 3\r\n") && contains(cs.ctrl.request[1], "\r\nRP-Version: 1.0\r\n"),
          "ctrl says RP-ConPath 3");
    CHECK(cs.heartbeat_sent && cs.ctrl.frames >= 1 && cs.ctrl.frame[0][5] == 0xFE && cs.ctrl.frame[0][4] == 0x01,
          "the console's heartbeat was answered over the association");

    CHECK(h.media_polls == 3 && h.media_leg.local_port != 0u && h.media_leg.local_port != leg.local_port,
          "poll_media was asked until it answered, with the A/V leg's own port (%u)", (unsigned)h.media_leg.local_port);
    CHECK(cs.media_preludes >= 1 && cs.media_client_port == h.media_leg.local_port && r->media_prelude_ok,
          "the A/V prelude ran from that port");
    CHECK(r->session_ready_waited_ms >= 0 && cs.session_id_sent, "SESSION_ID after the prelude was waited for");

    CHECK(cs.takion_before_report == 2, "senkusha's Takion ran on the A/V socket before the probe report (%d INITs)",
          cs.takion_before_report);
    CHECK(r->probe_report_sent && cs.report_seen
              && report_decrypts(&cs, cs.report_frame, 5, want_report, sizeof(want_report)),
          "PROBE_REPORT went out at field counter 5 with [10000 kbps, 1454, 0, rtt 0]");
    CHECK(r->stream_ready_seen, "STREAM_READY was seen");
    CHECK(cs.takion_after_report == 3, "the stream's Takion followed it, on the same socket (%d INITs)",
          cs.takion_after_report);
    CHECK(cs.strays_sent > 0 && r->stray_dropped >= 1u,
          "a stranger's datagrams on the A/V socket were dropped, not read (%lu)", (unsigned long)r->stray_dropped);
    CHECK(r->control_local_port == leg.local_port && r->media_local_port == h.media_leg.local_port,
          "the result names both legs' ports");

    halyard_client_destroy(c);
    console_stop(&cs, thread);
    CHECK(cs.ctrl.closes_received >= 1, "and the control connection was closed politely (%d)", cs.ctrl.closes_received);
}

static void test_refusals(void)
{
    static console_state cs;
    pthread_t thread;
    halyard_client_callbacks cb;
    host_state h;
    halyard_client *c;
    halyard_client_leg leg;
    halyard_client_peer peer;
    const halyard_client_result *r;

    /* A LAN client has no legs. */
    memset(&h, 0, sizeof(h));
    c = make_client(&cb, &h, HALYARD_ROUTE_LOCAL);
    CHECK(c != NULL && halyard_client_rendezvous_prepare(c, &leg) == 0, "prepare is refused on LOCAL");
    halyard_client_destroy(c);

    /* connect() without begin ends at once, and says why in the log. */
    memset(&h, 0, sizeof(h));
    c = make_client(&cb, &h, HALYARD_ROUTE_RENDEZVOUS);
    if (c != NULL) {
        uint64_t start = rc_time_ms();

        CHECK(halyard_client_connect(c) == HALYARD_CLIENT_STAGE_IDLE
                  && halyard_client_result_get(c)->end_reason == HALYARD_CLIENT_END_CHANNEL_ERROR
                  && rc_time_ms() - start < 500u,
              "connect without prepare and begin ends at once");
        halyard_client_destroy(c);
    }

    /* The host gives up on the media connection: END_NO_MEDIA, after the control plane came up. */
    if (!console_start(&cs, &thread)) {
        CHECK(0, "the fake console starts");
        return;
    }
    memset(&h, 0, sizeof(h));
    h.give_up = 1;
    c = make_client(&cb, &h, HALYARD_ROUTE_RENDEZVOUS);
    if (c != NULL) {
        memset(&peer, 0, sizeof(peer));
        memcpy(peer.address, k_loopback, 4);
        peer.port = cs.ctrl_port;
        memcpy(peer.console_hashed_id, fake_console_id(), 20);
        (void)halyard_client_rendezvous_prepare(c, &leg);
        (void)halyard_client_rendezvous_begin(c, fake_client_id(), &peer);
        (void)halyard_client_connect(c);
        r = halyard_client_result_get(c);
        CHECK(r->end_reason == HALYARD_CLIENT_END_NO_MEDIA && r->stage == HALYARD_CLIENT_STAGE_SIGNED_IN
                  && h.media_polls == 1 && cs.media_preludes == 0,
              "a host that gives up on the media connection ends the session with NO_MEDIA (reason %d, stage %d)",
              (int)r->end_reason, (int)r->stage);
        halyard_client_destroy(c);
    }
    console_stop(&cs, thread);
}

int main(void)
{
    g_storage_size = halyard_client_struct_size();
    g_storage = malloc(g_storage_size);
    if (g_storage == NULL)
        return 1;

    test_the_whole_route();
    test_refusals();

    free(g_storage);
    printf("rendezvous_test: %d passed, %d failed\n", g_passed, g_failed);
    return g_failed == 0 ? 0 : 1;
}
