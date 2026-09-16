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

#include "rc_platform.h"

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

    s->recv_calls++;
    n = rc_tcp_recv(s->sock, s->buffer + s->buffered, sizeof(s->buffer) - s->buffered);
    if (n < 0) {
        if (errno == EAGAIN || errno == EWOULDBLOCK) {
            s->recv_would_block++;
            return 0;
        }
        return -1;
    }
    if (n == 0)
        return -2;
    s->buffered += (size_t)n;
    s->recv_bytes += (long)n;
    return (int)n;
}

/*
 * Polls until a complete /sess response has arrived, bounded by SESS_RESPONSE_TIMEOUT_MS of no progress.
 * Returns bytes consumed, or 0 on close/error/timeout. The caller must read any headers it needs out of
 * s->buffer BEFORE consuming, which invalidates them.
 */
static size_t wait_for_response(halyard_control_session *s, halyard_sess_response *out)
{
    uint64_t start_ms = rc_time_ms();

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
            if (rc_time_ms() - start_ms > SESS_RESPONSE_TIMEOUT_MS) {
                rc_log("\x1b[31mFAIL\x1b[0m timed out waiting for a response\n");
                return 0;
            }
            rc_sleep_ms(20); /* 20 ms - the console answers at human timescale, not packet-rate */
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
    uint64_t start_ms;

    sock = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (sock < 0)
        return;

    {
        int enable = 1;
        setsockopt(sock, SOL_SOCKET, SO_BROADCAST, &enable, sizeof(enable));
    }
    rc_socket_set_nonblocking(sock);

    halyard_control_arm_build_probe(is_ps5, probe);

    memset(&unicast, 0, sizeof(unicast));
    unicast.sin_family = AF_INET;
    unicast.sin_port = htons(HALYARD_CONTROL_ARM_PORT);
    inet_aton(host, &unicast.sin_addr);

    broadcast = unicast;
    broadcast.sin_addr.s_addr = INADDR_BROADCAST;

    sendto(sock, probe, sizeof(probe), 0, (struct sockaddr *)&unicast, sizeof(unicast));
    sendto(sock, probe, sizeof(probe), 0, (struct sockaddr *)&broadcast, sizeof(broadcast));

    start_ms = rc_time_ms();
    while (rc_time_ms() - start_ms < ARM_REPLY_WINDOW_MS) {
        uint8_t buf[16];
        ssize_t n = recvfrom(sock, buf, sizeof(buf), 0, NULL, NULL);
        if (n > 0 && halyard_control_arm_is_reply(is_ps5, buf, (size_t)n)) {
            rc_log("control listener armed (got %s)\n", is_ps5 ? "RES3" : "RES2");
            break;
        }
        rc_sleep_ms(20); /* 20 ms */
    }

    close(sock);
    rc_sleep_ms(ARM_SETTLE_MS);
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
    rc_socket_set_nonblocking(s->sock);

    rc_hex_encode(rec->registkey, rec->registkey_length, regist_hex);
    halyard_sess_request_init(&req, "GET", halyard_sess_path(rec->is_ps5, "init"));
    halyard_sess_request_add_header(&req, "Host", rec->host);
    halyard_sess_request_add_header(&req, "User-Agent",
                                    (rec->user_agent[0] != '\0') ? rec->user_agent
                                                                 : "remoteplay Windows");
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
    rc_socket_set_nonblocking(s->sock);

    halyard_sess_request_init(&req, "GET", halyard_sess_path(rec->is_ps5, "ctrl"));
    halyard_sess_request_add_header(&req, "Host", rec->host);
    halyard_sess_request_add_header(&req, "User-Agent",
                                    (rec->user_agent[0] != '\0') ? rec->user_agent
                                                                 : "remoteplay Windows");
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

        /*
         * The override replaces the whole string, not its numbers. RP-OSType's plaintext has always
         * been "Win<major>.<minor>", and the thing worth varying is the part that is not a number -
         * the console's own remote-play list names every session this project has opened as a PC, and
         * this field is the most likely place it reads that from. [X]
         */
        if (rec->os_type[0] != '\0') {
            size_t os_len = strlen(rec->os_type);

            if (os_len + 1u <= sizeof(os_text)) {
                memcpy(os_text, rec->os_type, os_len + 1u);   /* the NUL is part of the plaintext */
                os_plain_len = os_len + 1u;
            } else {
                os_plain_len = 0u;
            }
        } else {
            os_plain_len = halyard_sess_field_os_type_plaintext(rec->os_major, rec->os_minor,
                                                               os_text, sizeof(os_text));
        }
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
    out->recv_counter = HALYARD_SESS_COUNTER_CONSOLE_START;
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

/* Newest at index 0, oldest pushed off the end. See `recent` in the header for why this is kept. */
static void record_frame(halyard_control_session *s, unsigned type, size_t payload_length, size_t consumed)
{
    int i;

    for (i = HALYARD_CONTROL_RECENT - 1; i > 0; i--)
        s->recent[i] = s->recent[i - 1];
    s->recent[0].type = type;
    s->recent[0].payload_length = payload_length;
    s->recent[0].consumed = consumed;
    if (s->recent_count < HALYARD_CONTROL_RECENT)
        s->recent_count++;
}

/*
 * Names the message that under-consumed, which is the one open question about the desync.
 *
 * Called BEFORE the recovered frame is recorded, so `recent` still holds only frames that preceded the
 * junk - recent[0] is the prime suspect. The discarded bytes go out as hex too: if they turn out to be
 * the tail of the previous message rather than the head of the next, that is visible here and nowhere
 * else. Every earlier attempt to explain this from counters alone failed; the bytes are the evidence.
 */
static void report_resync(const halyard_control_session *s, size_t discarded)
{
    size_t i;
    int k;

    rc_log("\x1b[33mctrl resync #%ld\x1b[0m discarded %u byte(s):", s->resyncs, (unsigned)discarded);
    for (i = 0; i < discarded && i < 16u; i++)
        rc_log(" %02x", s->buffer[i]);
    rc_log("%s\n", discarded > 16u ? " ..." : "");

    rc_log("  frames parsed just before, newest first:\n");
    for (k = 0; k < s->recent_count; k++) {
        rc_log("    [%d] type 0x%04x, declared payload %u, consumed %u%s\n",
               k, s->recent[k].type, (unsigned)s->recent[k].payload_length,
               (unsigned)s->recent[k].consumed,
               k == 0 ? "   <- under-consumed by 8 if the gap is constant" : "");
    }
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

    /*
     * RESYNCHRONISE RATHER THAN WAIT FOREVER FOR A FRAME THAT CANNOT EXIST.
     *
     * A desync here is fatal and silent. Hardware caught it exactly: 8 bytes of high-entropy junk at the
     * head of the buffer, followed by perfectly valid heartbeat frames -
     *
     *     <8 bytes, encrypted-frame tail> | 00 00 00 00 00 fe 00 00 | 00 00 00 00 00 fe 00 00
     *
     * (The junk is the tail of an encrypted frame and is not reproduced: it is session-keyed, and the shape
     * is what matters here, not the value.) The parser reads its first four bytes as a payload length -
     * around four gigabytes, since the high byte is large - and waits for data that will
     * never arrive, while the console's heartbeats stack up untouched behind it. We stop replying, and
     * it drops the session ~38 s later. Every earlier theory about that disconnect (a starved loop, our
     * replies failing to send, Takion stalling, the console losing interest) was downstream of this.
     *
     * The root cause is still open: something under-consumed an earlier message by exactly one header's
     * worth of bytes, and identifying which message needs a capture. But waiting forever is the wrong
     * response to an impossible length whatever produced it - the frames behind it are readable, and a
     * client that resynchronises keeps the session alive while the cause is found.
     *
     * The scan looks for the frame shape the wire actually has: reserved bytes zero at offset 6..7, and
     * a payload length small enough to be real. That is weak evidence per byte, which is why the resync
     * is LOGGED with what it discarded - a silent recovery would hide the very bug this exists to survive.
     */
    if (consumed == 0 && session->buffered >= HALYARD_CTRL_HEADER_SIZE) {
        uint32_t claimed = ((uint32_t)session->buffer[0] << 24) | ((uint32_t)session->buffer[1] << 16)
                         | ((uint32_t)session->buffer[2] << 8) | (uint32_t)session->buffer[3];

        if (claimed > HALYARD_CONTROL_SESSION_BUFFER) {
            size_t scan;

            for (scan = 1; scan + HALYARD_CTRL_HEADER_SIZE <= session->buffered; scan++) {
                uint32_t len = ((uint32_t)session->buffer[scan] << 24)
                             | ((uint32_t)session->buffer[scan + 1] << 16)
                             | ((uint32_t)session->buffer[scan + 2] << 8)
                             | (uint32_t)session->buffer[scan + 3];

                if (session->buffer[scan + 6] == 0 && session->buffer[scan + 7] == 0
                    && len <= HALYARD_CONTROL_SESSION_BUFFER) {
                    session->resyncs++;
                    session->resync_discarded += (long)scan;
                    /* Before the consume, while the junk is still in the buffer to be printed. */
                    report_resync(session, scan);
                    buffer_consume(session, scan);
                    consumed = halyard_ctrl_message_parse(session->buffer, session->buffered,
                                                          &type, &payload, &payload_length);
                    break;
                }
            }
        }
    }

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

    /*
     * SPEND THE CONSOLE'S COUNTER FOR EVERY PAYLOAD-CARRYING FRAME, decrypt where we can.
     *
     * The increment is unconditional on there being a payload, and deliberately so: the counter tracks
     * what the CONSOLE encrypted at, not what we managed to read. Skipping it for a frame we chose not to
     * decrypt - because it was too large, or because nobody wanted it - would silently shift every
     * subsequent frame onto the wrong counter, and a wrong counter does not fail loudly. It produces
     * plausible garbage.
     *
     * Empty payloads spend nothing. Heartbeats and the login prompt are both empty, and they are the two
     * most frequent frames on this channel, so getting that wrong would desynchronise almost at once.
     */
    out_event->counter = session->recv_counter;
    if (payload_length > 0u) {
        session->recv_counter++;

        if (payload_length <= sizeof(session->recv_plain)) {
            halyard_control_field_decrypt(&session->ctrl, out_event->counter,
                                          payload, session->recv_plain, payload_length);
            out_event->plaintext = session->recv_plain;
            out_event->plaintext_length = payload_length;
        }
    }

    /* Every frame, not just interesting ones - the under-consuming message is by definition one we
     * currently believe we handled correctly, so filtering here would hide it. */
    record_frame(session, type, payload_length, consumed);

    if (type == HALYARD_CTRL_TYPE_HEARTBEAT_REQ) {
        /* Answered here rather than by the caller on purpose: forgetting it is what makes a console
         * reset the session 15-30 s in, and a caller busy with the stream bring-up is exactly the caller
         * most likely to forget. */
        if (!halyard_control_session_send(session, HALYARD_CTRL_TYPE_HEARTBEAT_REP, NULL, 0))
            session->heartbeat_send_failures++;
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

int halyard_control_session_submit_login(halyard_control_session *session,
                                         const char *pin, size_t pin_length)
{
    /*
     * Sized from the field encoding rather than from the longest passcode anyone expects: the plaintext
     * builder is the authority on how much it needs, and a buffer chosen from a guess about PIN length
     * is a truncation waiting for a console that asks for more digits.
     */
    uint8_t plain[HALYARD_SESS_LOGIN_PIN_MAX];
    size_t plain_length;
    uint64_t counter;

    if (session == NULL || session->sock < 0 || pin == NULL)
        return 0;

    /*
     * Take the counter and advance it in the same breath, so a retry cannot reuse one even if the caller
     * loops. See the header: a repeated counter is a repeated IV under the same key.
     */
    counter = session->next_counter++;

    plain_length = halyard_ctrl_build_login_submit(&session->ctrl, counter, pin, pin_length,
                                                   plain, sizeof(plain));
    if (plain_length == 0u)
        return 0;

    {
        int sent = halyard_control_session_send(session, HALYARD_CTRL_TYPE_LOGIN_SUBMIT,
                                                plain, plain_length);
        /* The passcode was in this buffer in clear before it was encrypted in place. */
        memset(plain, 0, sizeof(plain));
        return sent;
    }
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
