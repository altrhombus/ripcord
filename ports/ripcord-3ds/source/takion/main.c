/*
 * ripcord-3ds - Phase 5 Takion transport probe.
 *
 * Drives just the SCTP handshake, then (once established) exchanges a synthetic
 * PROTOCOL_VERSION_REQUEST-shaped message, against a UDP host:port read from a small config file next
 * to this .3dsx. This is EXPLORATORY, not the real connect flow: a real session gates the Takion INIT on
 * the binary control channel's session-ready frame (Phase 4) and a keyless senkusha probe first (both
 * out of scope - see SETUP.md), neither of which this program does. It was written to answer "does the
 * handshake complete against a real peer, and does its SACK/DATA behaviour match tests/takion_test.c's
 * captured vectors" - a question it turns out to be structurally unable to ask a console at all.
 *
 * THIS PROGRAM CANNOT SUCCEED ON ITS OWN, and hardware confirmed it (2026-08-12): five INITs to
 * <console-ip>:9297 went unanswered. That is not a transport bug, it is the design of this probe. The
 * console opens its UDP listeners only between /sess/ctrl and the stream, so with no live session there
 * is nothing listening to answer an INIT - and the run that proved it had already ended its session and
 * put the console into rest mode. The default port compounds it: 9297 is the SENKUSHA port, one above
 * the A/V stream port 9296 (HalyardStreamingSession.SenkushaPort, wire-confirmed).
 *
 * Keep this program for driving the transport against a peer you control (a host-side responder, a
 * second 3DS), which is what it is actually good for. Testing Takion against a console needs the
 * connect flow instead: hold the Phase 4 control channel open past session-ready, run senkusha on 9297,
 * then INIT the stream on 9296. That is a single combined probe, not this one plus a port number.
 *
 * takion.txt (next to this .3dsx, plain key=value text):
 *   host=192.168.1.42   (required)
 *   port=9297            (optional, default shown - see above; neither 9296 nor 9297 is open on a
 *                         console without a live session, so no value here makes this probe work)
 */
#include "../net/rc_soc.h"
#include "util/rc_log.h"
#include "util/rc_program_dir.h"
#include "takion/takion_data_chunk.h"
#include "takion/takion_reliable_channel.h"

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

#define DEFAULT_PORT 9297u
#define HANDSHAKE_MAX_ATTEMPTS 5u
#define HANDSHAKE_ATTEMPT_TIMEOUT_MS 1000u
#define POLL_WINDOW_MS 10000u

static int load_config(const char *argv0, char *host, size_t host_size, unsigned short *port)
{
    char path[512];
    FILE *f;
    char line[256];
    int have_host = 0;

    rc_program_dir(argv0, path, sizeof(path));
    strncat(path, "takion.txt", sizeof(path) - strlen(path) - 1);

    *port = (unsigned short)DEFAULT_PORT;
    f = fopen(path, "r");
    if (f == NULL) {
        rc_log("\x1b[31mFAIL\x1b[0m could not open %s\n", path);
        return 0;
    }

    while (fgets(line, sizeof(line), f) != NULL) {
        char *eq = strchr(line, '=');
        char *value;
        char *trail;

        if (eq == NULL)
            continue;
        *eq = '\0';
        value = eq + 1;
        trail = strpbrk(value, "\r\n");
        if (trail != NULL)
            *trail = '\0';

        if (strcmp(line, "host") == 0) {
            strncpy(host, value, host_size - 1);
            have_host = (host[0] != '\0');
        } else if (strcmp(line, "port") == 0) {
            *port = (unsigned short)atoi(value);
        }
    }
    fclose(f);

    if (!have_host) {
        rc_log("\x1b[31mFAIL\x1b[0m %s is missing host\n", path);
        return 0;
    }
    return 1;
}

static int run_takion(const char *host, unsigned short port)
{
    int sock;
    struct sockaddr_in peer;
    /*
     * static, NOT a local: this struct is 49.6 KB and libctru gives the whole main thread a 32 KB stack,
     * so as an ordinary local it overflowed in this function's prologue - a data abort before the first
     * rc_log line, which is exactly how it presented on hardware on 2026-08-12 (see HARDWARE-PROBES.md).
     * Safe because this program drives exactly one channel and never re-enters.
     */
    static takion_reliable_channel channel;

    sock = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (sock < 0) {
        rc_log("\x1b[31mFAIL\x1b[0m socket() failed: %d\n", errno);
        return 1;
    }
    fcntl(sock, F_SETFL, O_NONBLOCK);

    memset(&peer, 0, sizeof(peer));
    peer.sin_family = AF_INET;
    peer.sin_port = htons(port);
    inet_aton(host, &peer.sin_addr);

    rc_log("handshake: sending INIT to %s:%u\n", host, (unsigned)port);
    if (!takion_channel_connect(&channel, sock, peer, HANDSHAKE_MAX_ATTEMPTS, HANDSHAKE_ATTEMPT_TIMEOUT_MS)) {
        rc_log("\x1b[31mFAIL\x1b[0m handshake did not complete (%u attempts x %u ms)\n",
            HANDSHAKE_MAX_ATTEMPTS, HANDSHAKE_ATTEMPT_TIMEOUT_MS);
        close(sock);
        return 1;
    }
    rc_log("handshake ESTABLISHED - local tag 0x%08x, peer tag 0x%08x\n",
        (unsigned)channel.local_tag, (unsigned)channel.peer_tag);

    /* A synthetic PROTOCOL_VERSION_REQUEST-shaped payload - not a real protobuf encode (building the
     * actual ControlMessage protobuf is later-phase work), just enough bytes to see whether anything on
     * the other end answers on the reliable channel at all. */
    {
        static const uint8_t probe_payload[4] = { 0x08, 0x11, 0x08, 0x09 };
        if (takion_channel_send(&channel, TAKION_CHANNEL_PROTOCOL_VERSION, probe_payload, sizeof(probe_payload)))
            rc_log("sent a probe message on channel 0x%04x\n", TAKION_CHANNEL_PROTOCOL_VERSION);
        else
            rc_log("\x1b[33mNOTE\x1b[0m could not send the probe message\n");
    }

    {
        int frames = 0;
        u64 start_ms = osGetTime();

        while (aptMainLoop() && osGetTime() - start_ms < POLL_WINDOW_MS) {
            unsigned channel_id;
            const uint8_t *message;
            size_t message_length;
            int result;
            u32 kdown;

            hidScanInput();
            kdown = hidKeysDown();
            if (kdown & KEY_START)
                break;

            result = takion_channel_poll(&channel, &channel_id, &message, &message_length);
            if (result == 1) {
                frames++;
                rc_log("reliable message #%d on channel 0x%04x, %u bytes\n",
                    frames, channel_id, (unsigned)message_length);
            } else if (result == -1) {
                rc_log("\x1b[31mFAIL\x1b[0m poll() reported a socket error: %d\n", errno);
                break;
            }

            gfxFlushBuffers();
            gfxSwapBuffers();
            gspWaitForVBlank();
        }
        rc_log("\n%d reliable message(s) received in the %u s window.\n", frames, POLL_WINDOW_MS / 1000u);
    }

    close(sock);
    return 0;
}

int main(int argc, char **argv)
{
    char host[64];
    unsigned short port;

    osSetSpeedupEnable(true);

    gfxInitDefault();
    consoleInit(GFX_TOP, NULL);

    rc_log_open(argc > 0 ? argv[0] : NULL, "takion.log");

    rc_log("ripcord-3ds Takion transport probe (Phase 5)\n");
    rc_log("---------------------------------------------\n");

    if (rc_soc_init() != 0) {
        rc_log("\x1b[31mFAIL\x1b[0m SOC init failed\n");
    } else if (load_config(argc > 0 ? argv[0] : NULL, host, sizeof(host), &port)) {
        run_takion(host, port);
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
