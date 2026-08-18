/*
 * ripcord-3ds - Phase 4 on-device probe: the /sess/init -> /sess/ctrl exchange against a real console.
 *
 * Orchestrates, in order, the same sequence as HalyardStreamingSession.ConnectAsync: arm the console's
 * control listener (halyard_control_arm), connect over TCP,
 * GET /sess/init (plaintext RP-Registkey), derive the control key from the returned RP-Nonce plus the
 * pairing record's companion (halyard_control_field_init - the first end-to-end use of the crypto from
 * Phase 0), reconnect fresh, GET /sess/ctrl with the five encrypted RP-* fields, then answer the
 * console's HEARTBEAT_REQ on the persistent binary channel that follows - missing that is what makes a
 * real console RST the session ~15-30s in.
 *
 * PAIRING RECORD: read from "pairing.txt" beside this .3dsx - see halyard_pairing_file.h for the format,
 * and for why that loader is shared rather than living here.
 *
 * RUN AND CONFIRMED against a real PS5 on 2026-08-12: /sess/init -> 200, /sess/ctrl -> 200, session
 * ready (17-byte session id), and rest mode round-tripped. This program is the validated half of the
 * connect sequence and is kept as-is for exactly that reason - it is the known-good reference for the
 * control plane. source/connect/main.c carries the same sequence forward into the stream plane; when the
 * two disagree about the control plane, this one is right.
 */
#include "../net/rc_soc.h"
#include "net/rc_tcp.h"
#include "halyard/halyard_v1.h"
#include "util/rc_base64.h"
#include "util/rc_hex.h"
#include "util/rc_log.h"
#include "util/rc_program_dir.h"
#include "session/halyard_control_arm.h"
#include "session/halyard_pairing_file.h"
#include "session/halyard_ctrl_message.h"
#include "session/halyard_sess_fields.h"
#include "session/halyard_sess_request.h"

#include <3ds.h>

#include <arpa/inet.h>
#include <errno.h>
#include <fcntl.h>
#include <netinet/in.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/types.h>
#include <unistd.h>

#define RECV_BUFFER_SIZE 4096
#define ARM_REPLY_WINDOW_MS 2000
#define ARM_SETTLE_MS 200

typedef struct {
    uint8_t data[RECV_BUFFER_SIZE];
    size_t length;
} recv_buffer;

static void recv_buffer_consume(recv_buffer *rb, size_t n)
{
    memmove(rb->data, rb->data + n, rb->length - n);
    rb->length -= n;
}

/* Returns bytes read (>0), 0 if nothing is available right now (non-blocking socket, not an error), -1
 * on a real error, or -2 if the peer closed the connection. */
static int recv_buffer_fill(recv_buffer *rb, int sock)
{
    ssize_t n;

    if (rb->length >= sizeof(rb->data))
        return -1; /* a real response/frame should never be this large - treat it as a protocol error */

    n = rc_tcp_recv(sock, rb->data + rb->length, sizeof(rb->data) - rb->length);
    if (n < 0)
        return (errno == EAGAIN || errno == EWOULDBLOCK) ? 0 : -1;
    if (n == 0)
        return -2;
    rb->length += (size_t)n;
    return (int)n;
}

#define SESS_RESPONSE_TIMEOUT_MS 5000u

/*
 * Polls (the socket is non-blocking - see rc_tcp_connect() callers below) until a complete /sess
 * response has arrived, up to SESS_RESPONSE_TIMEOUT_MS of no progress. Returns the bytes consumed (the
 * caller must read whatever headers it needs from rb->data BEFORE calling recv_buffer_consume(), which
 * invalidates them), or 0 on a closed connection, a real error, or the timeout - a console that never
 * answers gets a bounded failure instead of hanging the program.
 */
static size_t wait_for_sess_response(int sock, recv_buffer *rb, halyard_sess_response *out)
{
    u64 start_ms = osGetTime();

    for (;;) {
        size_t consumed = halyard_sess_response_parse((const char *)rb->data, rb->length, out);
        int filled;

        if (consumed > 0)
            return consumed;

        filled = recv_buffer_fill(rb, sock);
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

    /* Best-effort: sending the probe is what arms the console, so a lost reply is not fatal - proceed
     * to the TCP connect after the window regardless. */
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

static int run_session(const halyard_pairing_record *rec)
{
    int sock;
    recv_buffer rb;
    halyard_sess_request req;
    halyard_sess_response resp;
    char buf[1024];
    char regist_hex[RC_HEX_ENCODED_SIZE(sizeof(rec->registkey))];
    size_t n;
    halyard_control_field ctrl;
    int version_selector;
    int have_control = 0;

    arm_control_listener(rec->host, rec->is_ps5);

    /* ---- /sess/init: presents RP-Registkey in plaintext, gets RP-Nonce back. ---- */
    sock = rc_tcp_connect(rec->host, HALYARD_CONTROL_ARM_PORT);
    if (sock < 0) {
        rc_log("\x1b[31mFAIL\x1b[0m TCP connect for /sess/init failed: %d\n", errno);
        return 1;
    }
    fcntl(sock, F_SETFL, O_NONBLOCK);

    rc_hex_encode(rec->registkey, rec->registkey_length, regist_hex);
    halyard_sess_request_init(&req, "GET", halyard_sess_path(rec->is_ps5, "init"));
    halyard_sess_request_add_header(&req, "Host", rec->host);
    halyard_sess_request_add_header(&req, "User-Agent", "remoteplay Windows");
    halyard_sess_request_add_header(&req, "Connection", "close");
    halyard_sess_request_add_header(&req, "RP-Registkey", regist_hex);
    halyard_sess_request_add_header(&req, "RP-Version", halyard_sess_version(rec->is_ps5));

    n = halyard_sess_request_serialize(&req, buf, sizeof(buf));
    if (n == 0 || rc_tcp_send_all(sock, buf, n) != 0) {
        rc_log("\x1b[31mFAIL\x1b[0m could not send /sess/init\n");
        close(sock);
        return 1;
    }

    memset(&rb, 0, sizeof(rb));
    n = wait_for_sess_response(sock, &rb, &resp);
    if (n == 0) {
        rc_log("\x1b[31mFAIL\x1b[0m /sess/init: connection closed before a response arrived\n");
        close(sock);
        return 1;
    }
    rc_log("/sess/init -> %d\n", resp.status_code);

    if (resp.status_code >= 200 && resp.status_code < 300) {
        char nonce_b64[64];
        if (halyard_sess_response_header((const char *)rb.data, &resp, "RP-Nonce", nonce_b64, sizeof(nonce_b64))) {
            uint8_t nonce[16];
            size_t nonce_len = rc_base64_decode(nonce_b64, strlen(nonce_b64), nonce, sizeof(nonce));
            if (nonce_len == sizeof(nonce)) {
                version_selector = rec->is_ps5 ? HALYARD_VERSION_SELECTOR_PS5 : HALYARD_VERSION_SELECTOR_PS4;
                /* Codec selector 2: the value this project's own captures resolve to (see
                 * HalyardSessCtrlFields's .NET counterpart) - pinning the full RP-KeyType -> selector
                 * mapping is a refinement tracked in the spec, not something this port adds to. */
                if (halyard_control_field_init(&ctrl, nonce, rec->companion, 2, version_selector) == 0)
                    have_control = 1;
            }
        }
    }
    close(sock); /* /sess/init is served Connection: close - the console has already closed its end */

    if (!have_control) {
        rc_log("\x1b[31mFAIL\x1b[0m /sess/init did not yield a usable RP-Nonce; stopping before /sess/ctrl\n");
        return 1;
    }

    /* ---- /sess/ctrl: a FRESH connection (the single arm probe already covers this window too). ---- */
    sock = rc_tcp_connect(rec->host, HALYARD_CONTROL_ARM_PORT);
    if (sock < 0) {
        rc_log("\x1b[31mFAIL\x1b[0m TCP connect for /sess/ctrl failed: %d\n", errno);
        return 1;
    }
    fcntl(sock, F_SETFL, O_NONBLOCK);

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

    /* The five encrypted fields. Each header's base64 buffer must outlive request serialization, so they
     * are declared here rather than in a throwaway inner scope. */
    {
        char auth_b64[32], did_b64[48], os_b64[32], bitrate_b64[16], streaming_b64[16];
        uint8_t plain16[16], plain32[32], plain4[4];
        size_t os_plain_len;

        halyard_sess_field_auth_plaintext(rec->registkey, rec->registkey_length, plain16);
        halyard_control_field_encrypt(&ctrl, HALYARD_SESS_COUNTER_AUTH, plain16, plain16, sizeof(plain16));
        rc_base64_encode(plain16, sizeof(plain16), auth_b64, sizeof(auth_b64));
        halyard_sess_request_add_header(&req, "RP-Auth", auth_b64);

        halyard_sess_field_did_plaintext(rec->device_id, rec->device_id_length, plain32);
        halyard_control_field_encrypt(&ctrl, HALYARD_SESS_COUNTER_DID, plain32, plain32, sizeof(plain32));
        rc_base64_encode(plain32, sizeof(plain32), did_b64, sizeof(did_b64));
        halyard_sess_request_add_header(&req, "RP-Did", did_b64);

        {
            char os_text[16];
            os_plain_len = halyard_sess_field_os_type_plaintext(rec->os_major, rec->os_minor, os_text, sizeof(os_text));
            halyard_control_field_encrypt(&ctrl, HALYARD_SESS_COUNTER_OS_TYPE,
                (const uint8_t *)os_text, (uint8_t *)os_text, os_plain_len);
            rc_base64_encode((const uint8_t *)os_text, os_plain_len, os_b64, sizeof(os_b64));
            halyard_sess_request_add_header(&req, "RP-OSType", os_b64);
        }

        halyard_sess_field_int32le_plaintext(rec->start_bitrate, plain4);
        halyard_control_field_encrypt(&ctrl, HALYARD_SESS_COUNTER_START_BITRATE, plain4, plain4, sizeof(plain4));
        rc_base64_encode(plain4, sizeof(plain4), bitrate_b64, sizeof(bitrate_b64));
        halyard_sess_request_add_header(&req, "RP-StartBitrate", bitrate_b64);

        halyard_sess_field_int32le_plaintext(rec->streaming_type, plain4);
        halyard_control_field_encrypt(&ctrl, HALYARD_SESS_COUNTER_STREAMING_TYPE, plain4, plain4, sizeof(plain4));
        rc_base64_encode(plain4, sizeof(plain4), streaming_b64, sizeof(streaming_b64));
        halyard_sess_request_add_header(&req, "RP-StreamingType", streaming_b64);

        n = halyard_sess_request_serialize(&req, buf, sizeof(buf));
    }

    if (n == 0 || rc_tcp_send_all(sock, buf, n) != 0) {
        rc_log("\x1b[31mFAIL\x1b[0m could not send /sess/ctrl\n");
        close(sock);
        return 1;
    }

    memset(&rb, 0, sizeof(rb));
    n = wait_for_sess_response(sock, &rb, &resp);
    if (n == 0) {
        rc_log("\x1b[31mFAIL\x1b[0m /sess/ctrl: connection closed before a response arrived\n");
        close(sock);
        return 1;
    }
    rc_log("/sess/ctrl -> %d\n", resp.status_code);
    if (resp.status_code < 200 || resp.status_code >= 300) {
        close(sock);
        return 1;
    }
    recv_buffer_consume(&rb, n);

    rc_log("entering the binary control channel - Y requests rest mode on exit, START exits\n");

    {
        int rest_requested = 0;

        while (aptMainLoop()) {
            unsigned type;
            const uint8_t *payload;
            size_t payload_length;
            size_t consumed;
            u32 kdown;

            hidScanInput();
            kdown = hidKeysDown();
            if (kdown & KEY_START)
                break;
            if ((kdown & KEY_Y) && !rest_requested) {
                uint8_t frame[HALYARD_CTRL_HEADER_SIZE];
                size_t frame_len = halyard_ctrl_message_build(HALYARD_CTRL_TYPE_REST_MODE, NULL, 0, frame, sizeof(frame));
                if (frame_len > 0)
                    rc_tcp_send_all(sock, frame, frame_len);
                rest_requested = 1;
                rc_log("requested rest mode\n");
            }

            consumed = halyard_ctrl_message_parse(rb.data, rb.length, &type, &payload, &payload_length);
            if (consumed == 0) {
                int filled = recv_buffer_fill(&rb, sock);
                if (filled == -2) {
                    rc_log("ctrl connection closed by console\n");
                    break;
                }
                if (filled == -1) {
                    rc_log("\x1b[31mFAIL\x1b[0m ctrl channel recv error: %d\n", errno);
                    break;
                }
            } else {
                if (type == HALYARD_CTRL_TYPE_HEARTBEAT_REQ) {
                    uint8_t frame[HALYARD_CTRL_HEADER_SIZE];
                    size_t frame_len = halyard_ctrl_message_build(HALYARD_CTRL_TYPE_HEARTBEAT_REP, NULL, 0,
                        frame, sizeof(frame));
                    if (frame_len > 0)
                        rc_tcp_send_all(sock, frame, frame_len);
                } else if (type == HALYARD_CTRL_TYPE_SESSION_ID) {
                    rc_log("session ready (%u-byte session id)\n", (unsigned)payload_length);
                } else if (type == HALYARD_CTRL_TYPE_LOGIN_PROMPT) {
                    rc_log("console requests sign-in - not supported by this build yet\n");
                } else if (type == HALYARD_CTRL_TYPE_REST_MODE_ACK) {
                    rc_log("rest mode acknowledged\n");
                } else {
                    rc_log("ctrl message type 0x%04x, %u bytes\n", type, (unsigned)payload_length);
                }
                recv_buffer_consume(&rb, consumed);
            }

            gfxFlushBuffers();
            gfxSwapBuffers();
            gspWaitForVBlank();
        }
    }

    close(sock);
    return 0;
}

int main(int argc, char **argv)
{
    halyard_pairing_record rec;

    osSetSpeedupEnable(true);

    gfxInitDefault();
    consoleInit(GFX_TOP, NULL);

    rc_log_open(argc > 0 ? argv[0] : NULL, "session.log");

    rc_log("ripcord-3ds session probe (Phase 4)\n");
    rc_log("------------------------------------\n");

    if (rc_soc_init() != 0) {
        rc_log("\x1b[31mFAIL\x1b[0m SOC init failed\n");
    } else if (halyard_pairing_file_load(argc > 0 ? argv[0] : NULL, &rec)) {
        rc_log("connecting to %s (%s)\n", rec.host, rec.is_ps5 ? "PS5" : "PS4");
        run_session(&rec);
        rc_soc_exit();
    } else {
        rc_soc_exit();
    }

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
