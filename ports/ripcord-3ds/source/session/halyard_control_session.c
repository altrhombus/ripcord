/*
 * See halyard_control_session.h. This is a faithful lift of the sequence source/session/main.c ran
 * successfully against a real PS5 - the wire behaviour is deliberately unchanged, including the header
 * set, their order, and the arm probe's timing.
 */

#include "halyard_control_session.h"

#include "../net/rc_tcp.h"
#include "../util/rc_base64.h"
#include "../util/rc_hex.h"
#include "../util/rc_log.h"
#include "halyard_control_arm.h"
#include "halyard_ctrl_message.h"
#include "halyard_sess_fields.h"
#include "halyard_sess_request.h"

#include <3ds.h>

#include <arpa/inet.h>
#include <errno.h>
#include <fcntl.h>
#include <netinet/in.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/types.h>
#include <unistd.h>

#define ARM_REPLY_WINDOW_MS 2000
#define ARM_SETTLE_MS 200
#define SESS_RESPONSE_TIMEOUT_MS 5000u

static void buffer_consume(halyard_control_session *s, size_t n)
{
    memmove(s->buffer, s->buffer + n, s->buffered - n);
    s->buffered -= n;
}

/* >0 bytes read, 0 nothing available right now (not an error), -1 real error, -2 peer closed. */
static int buffer_fill(halyard_control_session *s)
{
    ssize_t n;

    if (s->buffered >= sizeof(s->buffer))
        return -1; /* no legitimate response or frame is this large - treat it as a protocol error */

    n = rc_tcp_recv(s->sock, s->buffer + s->buffered, sizeof(s->buffer) - s->buffered);
    if (n < 0)
        return (errno == EAGAIN || errno == EWOULDBLOCK) ? 0 : -1;
    if (n == 0)
        return -2;
    s->buffered += (size_t)n;
    return (int)n;
}

/*
 * Polls until a complete /sess response has arrived, bounded by SESS_RESPONSE_TIMEOUT_MS of no progress.
 * Returns bytes consumed, or 0 on close/error/timeout. The caller must read any headers it needs out of
 * s->buffer BEFORE consuming, which invalidates them.
 */
static size_t wait_for_response(halyard_control_session *s, halyard_sess_response *out)
{
    u64 start_ms = osGetTime();

    for (;;) {
        size_t consumed = halyard_sess_response_parse((const char *)s->buffer, s->buffered, out);
        int filled;

        if (consumed > 0)
            return consumed;

        filled = buffer_fill(s);
        if (filled < 0) {
            rc_log("\x1b[31mFAIL\x1b[0m connection closed or errored while waiting for a response\n");
            return 0;
        }
        if (filled == 0) {
            if (osGetTime() - start_ms > SESS_RESPONSE_TIMEOUT_MS) {
                rc_log("\x1b[31mFAIL\x1b[0m timed out waiting for a response\n");
                return 0;
            }
            svcSleepThread(20000000); /* 20 ms - the console answers at human timescale, not packet-rate */
        }
    }
}

/*
 * The console does not hold TCP 9295 open continuously; a 4-byte UDP probe opens it briefly. Best-effort
 * by design: sending the probe is what arms it, so a lost reply is not fatal and we proceed regardless.
 * One probe covers both the /sess/init and /sess/ctrl connections.
 */
static void arm_control_listener(const char *host, int is_ps5)
{
    int sock;
    char probe[HALYARD_CONTROL_ARM_PROBE_SIZE];
    struct sockaddr_in unicast, broadcast;
    u64 start_ms;

    sock = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (sock < 0)
        return;

    {
        int enable = 1;
        setsockopt(sock, SOL_SOCKET, SO_BROADCAST, &enable, sizeof(enable));
    }
    fcntl(sock, F_SETFL, O_NONBLOCK);

    halyard_control_arm_build_probe(is_ps5, probe);

    memset(&unicast, 0, sizeof(unicast));
    unicast.sin_family = AF_INET;
    unicast.sin_port = htons(HALYARD_CONTROL_ARM_PORT);
    inet_aton(host, &unicast.sin_addr);

    broadcast = unicast;
    broadcast.sin_addr.s_addr = INADDR_BROADCAST;

    sendto(sock, probe, sizeof(probe), 0, (struct sockaddr *)&unicast, sizeof(unicast));
    sendto(sock, probe, sizeof(probe), 0, (struct sockaddr *)&broadcast, sizeof(broadcast));

    start_ms = osGetTime();
    while (osGetTime() - start_ms < ARM_REPLY_WINDOW_MS) {
        uint8_t buf[16];
        ssize_t n = recvfrom(sock, buf, sizeof(buf), 0, NULL, NULL);
        if (n > 0 && halyard_control_arm_is_reply(is_ps5, buf, (size_t)n)) {
            rc_log("control listener armed (got %s)\n", is_ps5 ? "RES3" : "RES2");
            break;
        }
        svcSleepThread(20000000); /* 20 ms */
    }

    close(sock);
    svcSleepThread((s64)ARM_SETTLE_MS * 1000000);
}

/* /sess/init: presents RP-Registkey in plaintext, returns the console's RP-Nonce. 1 on success. */
static int run_sess_init(halyard_control_session *s, const halyard_pairing_record *rec,
                         uint8_t out_nonce[HALYARD_KEY_LENGTH])
{
    halyard_sess_request req;
    halyard_sess_response resp;
    char buf[1024];
    char regist_hex[RC_HEX_ENCODED_SIZE(HALYARD_PAIRING_REGISTKEY_MAX)];
    char nonce_b64[64];
    size_t n;
    int ok = 0;

    s->sock = rc_tcp_connect(rec->host, HALYARD_CONTROL_ARM_PORT);
    if (s->sock < 0) {
        rc_log("\x1b[31mFAIL\x1b[0m TCP connect for /sess/init failed: %d\n", errno);
        s->sock = -1;
        return 0;
    }
    fcntl(s->sock, F_SETFL, O_NONBLOCK);

    rc_hex_encode(rec->registkey, rec->registkey_length, regist_hex);
    halyard_sess_request_init(&req, "GET", halyard_sess_path(rec->is_ps5, "init"));
    halyard_sess_request_add_header(&req, "Host", rec->host);
    halyard_sess_request_add_header(&req, "User-Agent", "remoteplay Windows");
    halyard_sess_request_add_header(&req, "Connection", "close");
    halyard_sess_request_add_header(&req, "RP-Registkey", regist_hex);
    halyard_sess_request_add_header(&req, "RP-Version", halyard_sess_version(rec->is_ps5));

    n = halyard_sess_request_serialize(&req, buf, sizeof(buf));
    if (n == 0 || rc_tcp_send_all(s->sock, buf, n) != 0) {
        rc_log("\x1b[31mFAIL\x1b[0m could not send /sess/init\n");
        goto done;
    }

    s->buffered = 0;
    n = wait_for_response(s, &resp);
    if (n == 0) {
        rc_log("\x1b[31mFAIL\x1b[0m /sess/init: no response\n");
        goto done;
    }
    rc_log("/sess/init -> %d\n", resp.status_code);
    if (resp.status_code < 200 || resp.status_code >= 300)
        goto done;

    if (!halyard_sess_response_header((const char *)s->buffer, &resp, "RP-Nonce",
                                      nonce_b64, sizeof(nonce_b64))) {
        rc_log("\x1b[31mFAIL\x1b[0m /sess/init carried no RP-Nonce\n");
        goto done;
    }
    if (rc_base64_decode(nonce_b64, strlen(nonce_b64), out_nonce, HALYARD_KEY_LENGTH)
        != HALYARD_KEY_LENGTH) {
        rc_log("\x1b[31mFAIL\x1b[0m RP-Nonce did not decode to 16 bytes\n");
        goto done;
    }
    ok = 1;

done:
    /* /sess/init is served Connection: close - the console has already closed its end either way. */
    close(s->sock);
    s->sock = -1;
    s->buffered = 0;
    return ok;
}

/* /sess/ctrl on a FRESH connection, carrying the five encrypted fields. Leaves the socket open. */
static int run_sess_ctrl(halyard_control_session *s, const halyard_pairing_record *rec)
{
    halyard_sess_request req;
    halyard_sess_response resp;
    char buf[1024];
    size_t n;

    s->sock = rc_tcp_connect(rec->host, HALYARD_CONTROL_ARM_PORT);
    if (s->sock < 0) {
        rc_log("\x1b[31mFAIL\x1b[0m TCP connect for /sess/ctrl failed: %d\n", errno);
        s->sock = -1;
        return 0;
    }
    fcntl(s->sock, F_SETFL, O_NONBLOCK);

    halyard_sess_request_init(&req, "GET", halyard_sess_path(rec->is_ps5, "ctrl"));
    halyard_sess_request_add_header(&req, "Host", rec->host);
    halyard_sess_request_add_header(&req, "User-Agent", "remoteplay Windows");
    halyard_sess_request_add_header(&req, "Connection", "keep-alive");
    halyard_sess_request_add_header(&req, "RP-Version", halyard_sess_version(rec->is_ps5));
    halyard_sess_request_add_header(&req, "RP-ControllerType", "0");
    halyard_sess_request_add_header(&req, "RP-ClientType", "11");
    halyard_sess_request_add_header(&req, "RP-ConPath", "1");
    halyard_sess_request_add_header(&req, "RP-PadProcNo", "2");
    halyard_sess_request_add_header(&req, "RP-SupportCmd", "060000");

    /* Each header's base64 buffer must outlive serialization, so they live in this scope. */
    {
        char auth_b64[32], did_b64[48], os_b64[32], bitrate_b64[16], streaming_b64[16];
        uint8_t plain16[16], plain32[32], plain4[4];
        char os_text[16];
        size_t os_plain_len;

        halyard_sess_field_auth_plaintext(rec->registkey, rec->registkey_length, plain16);
        halyard_control_field_encrypt(&s->ctrl, HALYARD_SESS_COUNTER_AUTH, plain16, plain16, sizeof(plain16));
        rc_base64_encode(plain16, sizeof(plain16), auth_b64, sizeof(auth_b64));
        halyard_sess_request_add_header(&req, "RP-Auth", auth_b64);

        halyard_sess_field_did_plaintext(rec->device_id, rec->device_id_length, plain32);
        halyard_control_field_encrypt(&s->ctrl, HALYARD_SESS_COUNTER_DID, plain32, plain32, sizeof(plain32));
        rc_base64_encode(plain32, sizeof(plain32), did_b64, sizeof(did_b64));
        halyard_sess_request_add_header(&req, "RP-Did", did_b64);

        os_plain_len = halyard_sess_field_os_type_plaintext(rec->os_major, rec->os_minor,
                                                           os_text, sizeof(os_text));
        halyard_control_field_encrypt(&s->ctrl, HALYARD_SESS_COUNTER_OS_TYPE,
            (const uint8_t *)os_text, (uint8_t *)os_text, os_plain_len);
        rc_base64_encode((const uint8_t *)os_text, os_plain_len, os_b64, sizeof(os_b64));
        halyard_sess_request_add_header(&req, "RP-OSType", os_b64);

        halyard_sess_field_int32le_plaintext(rec->start_bitrate, plain4);
        halyard_control_field_encrypt(&s->ctrl, HALYARD_SESS_COUNTER_START_BITRATE,
            plain4, plain4, sizeof(plain4));
        rc_base64_encode(plain4, sizeof(plain4), bitrate_b64, sizeof(bitrate_b64));
        halyard_sess_request_add_header(&req, "RP-StartBitrate", bitrate_b64);

        halyard_sess_field_int32le_plaintext(rec->streaming_type, plain4);
        halyard_control_field_encrypt(&s->ctrl, HALYARD_SESS_COUNTER_STREAMING_TYPE,
            plain4, plain4, sizeof(plain4));
        rc_base64_encode(plain4, sizeof(plain4), streaming_b64, sizeof(streaming_b64));
        halyard_sess_request_add_header(&req, "RP-StreamingType", streaming_b64);

        n = halyard_sess_request_serialize(&req, buf, sizeof(buf));
    }

    if (n == 0 || rc_tcp_send_all(s->sock, buf, n) != 0) {
        rc_log("\x1b[31mFAIL\x1b[0m could not send /sess/ctrl\n");
        close(s->sock);
        s->sock = -1;
        return 0;
    }

    s->buffered = 0;
    n = wait_for_response(s, &resp);
    if (n == 0) {
        rc_log("\x1b[31mFAIL\x1b[0m /sess/ctrl: no response\n");
        close(s->sock);
        s->sock = -1;
        return 0;
    }
    rc_log("/sess/ctrl -> %d\n", resp.status_code);
    if (resp.status_code < 200 || resp.status_code >= 300) {
        close(s->sock);
        s->sock = -1;
        return 0;
    }

    /* Whatever follows the response head is the first bytes of the binary channel. */
    buffer_consume(s, n);
    return 1;
}

int halyard_control_session_open(const halyard_pairing_record *record, halyard_control_session *out)
{
    uint8_t nonce[HALYARD_KEY_LENGTH];
    int version_selector;

    if (record == NULL || out == NULL)
        return 0;

    memset(out, 0, sizeof(*out));
    out->sock = -1;

    arm_control_listener(record->host, record->is_ps5);

    if (!run_sess_init(out, record, nonce))
        return 0;

    version_selector = record->is_ps5 ? HALYARD_VERSION_SELECTOR_PS5 : HALYARD_VERSION_SELECTOR_PS4;
    /* Codec selector 2 is the value this project's own captures resolve to; the full RP-KeyType ->
     * selector mapping is a tracked spec refinement, not something this port settles. Confirmed correct
     * against a real console on 2026-08-12 - /sess/ctrl would have rejected the fields otherwise. */
    if (halyard_control_field_init(&out->ctrl, nonce, record->companion, 2, version_selector) != 0) {
        rc_log("\x1b[31mFAIL\x1b[0m control-field KDF failed\n");
        memset(nonce, 0, sizeof(nonce));
        return 0;
    }
    memset(nonce, 0, sizeof(nonce));

    if (!run_sess_ctrl(out, record)) {
        memset(&out->ctrl, 0, sizeof(out->ctrl));
        return 0;
    }

    /* The five headers above consumed counters 0-4; anything encrypted later on this connection starts
     * here. See the header's note on why this must never be restarted. */
    out->next_counter = HALYARD_SESS_COUNTER_LOGIN_PIN_START;
    return 1;
}

int halyard_control_session_send(halyard_control_session *session, unsigned type,
                                 const uint8_t *payload, size_t payload_length)
{
    uint8_t frame[HALYARD_CTRL_HEADER_SIZE + 256];
    size_t frame_len;

    if (session == NULL || session->sock < 0)
        return 0;
    if (payload_length > sizeof(frame) - HALYARD_CTRL_HEADER_SIZE)
        return 0;

    frame_len = halyard_ctrl_message_build(type, payload, payload_length, frame, sizeof(frame));
    if (frame_len == 0)
        return 0;
    return rc_tcp_send_all(session->sock, frame, frame_len) == 0;
}

int halyard_control_session_service(halyard_control_session *session, halyard_control_event *out_event)
{
    unsigned type;
    const uint8_t *payload;
    size_t payload_length;
    size_t consumed;

    if (out_event == NULL)
        return 0;
    out_event->kind = HALYARD_CONTROL_EVENT_NONE;
    out_event->type = 0;
    out_event->payload = NULL;
    out_event->payload_length = 0;

    if (session == NULL || session->sock < 0) {
        out_event->kind = HALYARD_CONTROL_EVENT_ERROR;
        return 0;
    }

    consumed = halyard_ctrl_message_parse(session->buffer, session->buffered,
                                          &type, &payload, &payload_length);
    if (consumed == 0) {
        int filled = buffer_fill(session);
        if (filled == -2) {
            out_event->kind = HALYARD_CONTROL_EVENT_CLOSED;
            return 0;
        }
        if (filled == -1) {
            out_event->kind = HALYARD_CONTROL_EVENT_ERROR;
            return 0;
        }
        return 1; /* nothing complete yet - NONE */
    }

    out_event->type = type;
    out_event->payload = payload;
    out_event->payload_length = payload_length;

    if (type == HALYARD_CTRL_TYPE_HEARTBEAT_REQ) {
        /* Answered here rather than by the caller on purpose: forgetting it is what makes a console
         * reset the session 15-30 s in, and a caller busy with the stream bring-up is exactly the caller
         * most likely to forget. */
        (void)halyard_control_session_send(session, HALYARD_CTRL_TYPE_HEARTBEAT_REP, NULL, 0);
        out_event->kind = HALYARD_CONTROL_EVENT_MESSAGE;
    } else if (type == HALYARD_CTRL_TYPE_SESSION_ID) {
        session->session_ready = 1;
        out_event->kind = HALYARD_CONTROL_EVENT_SESSION_READY;
    } else {
        out_event->kind = HALYARD_CONTROL_EVENT_MESSAGE;
    }

    /* The payload pointer above aims into the buffer we are about to shift, so the caller must use it
     * before the next service() call - which the header states. Consuming here rather than on the next
     * call keeps the buffer from filling with already-handled frames. */
    buffer_consume(session, consumed);
    return 1;
}

void halyard_control_session_close(halyard_control_session *session)
{
    if (session == NULL)
        return;
    if (session->sock >= 0) {
        close(session->sock);
        session->sock = -1;
    }
    memset(&session->ctrl, 0, sizeof(session->ctrl));
    session->buffered = 0;
    session->session_ready = 0;
}
