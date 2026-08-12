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
#include "../takion/takion_data_chunk.h"
#include "../takion/takion_reliable_channel.h"
#include "../takion/takion_session_negotiator.h"
#include "../util/rc_base64.h"
#include "../util/rc_log.h"
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

/* The launch spec's declared MTU when senkusha's measurement legs are not run. LinkMetrics.VendorMtu. */
#define DEFAULT_DECLARED_MTU 1454

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

/*
 * The heartbeat pump. Called from the main loop AND from inside takion_channel_connect's wait loops, so
 * that a 6-second UDP handshake does not cost us the control connection. Deliberately does one unit of
 * work and returns.
 */
static void service_control(void *ctx)
{
    halyard_control_event event;

    (void)ctx;
    if (!g_control_alive)
        return;
    if (!halyard_control_session_service(&g_control, &event)) {
        g_control_alive = 0;
        rc_log("\x1b[31mFAIL\x1b[0m control channel %s\n",
            event.kind == HALYARD_CONTROL_EVENT_CLOSED ? "closed by console" : "errored");
        return;
    }
    if (event.kind == HALYARD_CONTROL_EVENT_SESSION_READY)
        g_control.session_ready = 1;
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
 * The senkusha bring-up, reduced to the part the console gates on: handshake, PROTOCOL_VERSION_REQUEST,
 * and a KEYLESS SESSION_REQUEST (no ECDH fields, clientVersion 9, empty strings). The echo/MTU
 * measurement legs are deliberately omitted - they tune bitrate, they do not unlock anything, and every
 * extra second here is a second the control channel spends unattended.
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
    params.width = 640;   /* the bottom rung - the 3DS screen is 400x240 and everything downscales */
    params.height = 360;
    params.fps = 30;
    params.bitrate_kbps = rec->start_bitrate;
    params.mtu = DEFAULT_DECLARED_MTU;  /* senkusha's MTU leg is not run - see this file's header */
    params.rtt_ms = 0;
    params.is_hevc = 0;   /* MVD decodes H.264 only; HEVC must never be requested from this hardware */
    params.is_hdr = 0;

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

    close(sock);
    return derived;
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

int main(int argc, char **argv)
{
    halyard_pairing_record rec;

    osSetSpeedupEnable(true);

    gfxInitDefault();
    consoleInit(GFX_TOP, NULL);

    rc_log_open(argc > 0 ? argv[0] : NULL, "connect.log");

    rc_log("ripcord-3ds connect flow (control -> senkusha -> Takion -> stream keys)\n");
    rc_log("-----------------------------------------------------------------------\n");

    if (!rc_random_init()) {
        rc_log("\x1b[31mFAIL\x1b[0m no entropy service (ps:ps); cannot negotiate safely\n");
    } else if (rc_soc_init() != 0) {
        rc_log("\x1b[31mFAIL\x1b[0m SOC init failed\n");
    } else {
        if (halyard_pairing_file_load(argc > 0 ? argv[0] : NULL, &rec)) {
            rc_log("connecting to %s (%s)\n", rec.host, rec.is_ps5 ? "PS5" : "PS4");
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
