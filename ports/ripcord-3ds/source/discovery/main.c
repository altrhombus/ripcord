/*
 * ripcord-3ds - Phase 3 LAN discovery, on-device.
 *
 * Broadcasts the SRCH probe for both console families (halyard_discovery.h) and lists every distinct
 * console - deduplicated by host-id - that answers within a fixed search window. Four seconds, the same
 * generous window the .NET side's own pairing UI settled on, because a resting console answers slowly.
 *
 * This is Phase 3's on-device half. tests/discovery_test.c already checks the parser without hardware,
 * against hand-transcribed examples from docs/protocol/ps5-local-discovery.md; only a real broadcast on
 * a real LAN proves the wire format still matches an actual console rather than just those examples.
 */
#include "../net/rc_soc.h"
#include "../util/rc_log.h"
#include "halyard_discovery.h"

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

#define SEARCH_WINDOW_MS 4000u
#define MAX_RESULTS 16

/* See the bind() call in run_search() for why this is a fixed port rather than 0. Deliberately clear of
 * every port this protocol uses (9295 control, 9296/9297 stream, 9302 PS5 discovery, 987 PS4). */
#define DISCOVERY_LOCAL_PORT_FIRST 9310u
#define DISCOVERY_LOCAL_PORT_TRIES 4u

static const halyard_discovery_profile *const PROFILES[] = {
    &halyard_discovery_profile_ps5,
    &halyard_discovery_profile_ps4,
};
#define PROFILE_COUNT (sizeof(PROFILES) / sizeof(PROFILES[0]))

static int already_seen(const halyard_discovered_console *results, int count, const char *host_id)
{
    int i;
    for (i = 0; i < count; i++) {
        if (strcmp(results[i].host_id, host_id) == 0)
            return 1;
    }
    return 0;
}

static int run_search(void)
{
    int sock;
    struct sockaddr_in bind_addr;
    struct sockaddr_in broadcast_addr;
    halyard_discovered_console results[MAX_RESULTS];
    int result_count = 0;
    size_t i;
    u64 start_ms;

    if (rc_soc_init() != 0) {
        rc_log("\x1b[31mFAIL\x1b[0m SOC init failed\n");
        return 1;
    }

    sock = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (sock < 0) {
        rc_log("\x1b[31mFAIL\x1b[0m socket() failed: %d\n", errno);
        rc_soc_exit();
        return 1;
    }

    /*
     * A FIXED local port, and NOT the ephemeral port 0 that the obvious port of this code would use.
     *
     * The .NET side binds IPAddress.Any:0 here (HalyardControlSearch) and is right to - on Windows and
     * Linux the stack picks a free port and everything works. On the 3DS it does not: SOC rejects a zero
     * port outright with EINVAL. Confirmed on real hardware 2026-08-12, where this call failed with
     * errno 22 for both an awake and a resting console, while source/linktest/main.c's otherwise
     * identical bind to a nonzero port had already been working on the same device since Phase 2. That
     * differential is what identified it: same socket type, same INADDR_ANY, same addrlen - only the
     * port differed.
     *
     * Any port will do, because a console answers SRCH to whatever source port the probe arrived from.
     * "Let the stack choose" is the single option unavailable. The small range exists so that a port
     * left occupied by a previous run is a retry rather than a second trip to the console.
     */
    memset(&bind_addr, 0, sizeof(bind_addr));
    bind_addr.sin_family = AF_INET;
    bind_addr.sin_addr.s_addr = INADDR_ANY;

    {
        unsigned attempt;
        int bound = 0;

        for (attempt = 0; attempt < DISCOVERY_LOCAL_PORT_TRIES; attempt++) {
            unsigned short port = (unsigned short)(DISCOVERY_LOCAL_PORT_FIRST + attempt);

            bind_addr.sin_port = htons(port);
            if (bind(sock, (struct sockaddr *)&bind_addr, sizeof(bind_addr)) == 0) {
                rc_log("bound local port %u\n", (unsigned)port);
                bound = 1;
                break;
            }
        }

        if (!bound) {
            rc_log("\x1b[31mFAIL\x1b[0m bind() failed: %d (tried ports %u-%u)\n", errno,
                DISCOVERY_LOCAL_PORT_FIRST,
                DISCOVERY_LOCAL_PORT_FIRST + DISCOVERY_LOCAL_PORT_TRIES - 1u);
            close(sock);
            rc_soc_exit();
            return 1;
        }
    }

    {
        int enable = 1;
        setsockopt(sock, SOL_SOCKET, SO_BROADCAST, &enable, sizeof(enable));
    }
    fcntl(sock, F_SETFL, O_NONBLOCK);

    /* The limited broadcast address, not a subnet-directed one: it needs no knowledge of our own IP or
     * netmask, and every console on a typical single-router home LAN - the only kind this port targets -
     * receives it. */
    memset(&broadcast_addr, 0, sizeof(broadcast_addr));
    broadcast_addr.sin_family = AF_INET;
    broadcast_addr.sin_addr.s_addr = INADDR_BROADCAST;

    rc_log("broadcasting SRCH (");
    for (i = 0; i < PROFILE_COUNT; i++) {
        char probe[128];
        size_t probe_len = halyard_discovery_build_probe(PROFILES[i], probe, sizeof(probe));

        rc_log("%s %u%s", PROFILES[i]->host_type, (unsigned)PROFILES[i]->port,
            i + 1 < PROFILE_COUNT ? " + " : "");

        broadcast_addr.sin_port = htons(PROFILES[i]->port);
        if (probe_len > 0)
            sendto(sock, probe, probe_len, 0, (struct sockaddr *)&broadcast_addr, sizeof(broadcast_addr));
    }
    rc_log(")\n\n");

    start_ms = osGetTime();
    while (osGetTime() - start_ms < (u64)SEARCH_WINDOW_MS) {
        struct sockaddr_in from;
        socklen_t from_len = sizeof(from);
        char buf[1024];
        ssize_t n = recvfrom(sock, buf, sizeof(buf), 0, (struct sockaddr *)&from, &from_len);

        if (n > 0) {
            halyard_discovered_console console;
            if (halyard_discovery_parse_response(buf, (size_t)n, inet_ntoa(from.sin_addr), &console)
                && !already_seen(results, result_count, console.host_id)) {
                if (result_count < MAX_RESULTS)
                    results[result_count++] = console;
                rc_log("  [%s] %-24s %-15s id=%s sw=%s awake=%s\n",
                    console.host_type, console.host_name, console.address,
                    console.host_id, console.system_version,
                    console.is_awake ? "yes" : "no (resting)");
            }
        }

        /* Discovery replies arrive at human timescale, not packet-rate - no reason to poll as tightly
         * as the linktest's receive loop does. */
        svcSleepThread(10000000);
    }

    if (result_count == 0)
        rc_log("no consoles responded.\n");
    else
        rc_log("\n%d console(s) found.\n", result_count);

    close(sock);
    rc_soc_exit();
    return 0;
}

int main(int argc, char **argv)
{
    /* Without this a New 3DS runs at the Old 3DS clock speed. Named directly in SETUP.md's gotcha list -
     * this program does not depend on timing the way the crypto/link tests do, but there is no reason to
     * be the one on-device entry point that forgets it. */
    osSetSpeedupEnable(true);

    gfxInitDefault();
    consoleInit(GFX_TOP, NULL);

    rc_log_open(argc > 0 ? argv[0] : NULL, "discovery.log");

    rc_log("ripcord-3ds LAN discovery (Phase 3)\n");
    rc_log("------------------------------------\n");

    run_search();

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
