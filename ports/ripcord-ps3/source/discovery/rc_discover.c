/* See rc_discover.h for what this establishes and why bind() to port 0 is asked separately. */
#include "rc_discover.h"

#include "platform/rc_platform.h"

#include <errno.h>
#include <stdio.h>
#include <string.h>
#include <unistd.h>

#include <net/net.h>
#include <net/socket.h>
#include <netinet/in.h>
#include <arpa/inet.h>

/*
 * sin_len is set on every sockaddr here. The PS3's sockaddr_in carries a leading length byte in the
 * original BSD style that Linux and the 3DS both dropped, so code written against either compiles
 * unchanged and leaves it zero - source/net/rc_netlog.c found that the hard way and says so at length.
 */
static void fill_addr(struct sockaddr_in *a, const char *ip, unsigned short port)
{
    memset(a, 0, sizeof(*a));
    a->sin_len = (uint8_t)sizeof(*a);
    a->sin_family = AF_INET;
    a->sin_port = htons(port);
    if (ip == NULL)
        a->sin_addr.s_addr = INADDR_ANY;
    else
        (void)inet_pton(AF_INET, ip, &a->sin_addr);
}

int rc_discover(unsigned timeout_ms, rc_discover_result *out)
{
    const halyard_discovery_profile *profile = &halyard_discovery_profile_ps5;
    char probe[128];
    size_t probe_len;
    struct sockaddr_in local, bcast;
    int sock = -1;
    int on = 1;
    uint64_t deadline;
    int found = 0;

    memset(out, 0, sizeof(*out));

    probe_len = halyard_discovery_build_probe(profile, probe, sizeof(probe));
    if (probe_len == 0u)
        return 0;

    sock = socket(PF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (sock < 0)
        return 0;

    if (setsockopt(sock, SOL_SOCKET, SO_BROADCAST, &on, (socklen_t)sizeof(on)) < 0) {
        (void)close(sock);
        return 0;
    }
    out->socket_ok = 1;

    /*
     * THE QUESTION rc_platform.h LEFT OPEN. Port 0 means "any free port", which is what a client that
     * only needs to receive replies to its own request should ask for - and it is the exact idiom the
     * 3DS rejects outright. Asked here, recorded, and not treated as fatal: if this platform refuses
     * it, discovery can still work from a fixed port, and knowing which of those is true is worth more
     * than a run that quietly picked the safe option.
     */
    fill_addr(&local, NULL, 0);
    if (bind(sock, (struct sockaddr *)&local, (socklen_t)sizeof(local)) < 0) {
        out->bind_port0_ok = 0;
        out->bind_errno = errno;

        /* Fall back to the discovery port itself, which a console is willing to talk to. */
        fill_addr(&local, NULL, profile->port);
        if (bind(sock, (struct sockaddr *)&local, (socklen_t)sizeof(local)) < 0) {
            (void)close(sock);
            return 0;
        }
    } else {
        out->bind_port0_ok = 1;
    }

    fill_addr(&bcast, "255.255.255.255", profile->port);

    if (sendto(sock, probe, probe_len, 0, (struct sockaddr *)&bcast, (socklen_t)sizeof(bcast)) < 0) {
        (void)close(sock);
        return 0;
    }
    out->probes_sent = 1;

    /*
     * Poll rather than block. rc_time_ms is the seam's monotonic clock - the one rc_platform_ps3.c
     * derives from the time base precisely so a deadline cannot be moved by the console's wall clock -
     * and a non-blocking recvfrom in a timed loop needs nothing from the platform that has not already
     * been confirmed on this hardware.
     */
    deadline = rc_time_ms() + (uint64_t)timeout_ms;

    while (rc_time_ms() < deadline && found < RC_DISCOVER_MAX) {
        char buf[1024];
        struct sockaddr_in from;
        socklen_t from_len = (socklen_t)sizeof(from);
        ssize_t n;

        memset(&from, 0, sizeof(from));
        n = recvfrom(sock, buf, sizeof(buf) - 1u, MSG_DONTWAIT,
                     (struct sockaddr *)&from, &from_len);
        if (n <= 0) {
            rc_sleep_ms(20u);
            continue;
        }

        out->datagrams_rx++;
        buf[n] = '\0';

        {
            char ip[HALYARD_DISCOVERY_ADDRESS_MAX];
            const char *src = inet_ntop(AF_INET, &from.sin_addr, ip, (socklen_t)sizeof(ip));

            if (halyard_discovery_parse_response(buf, (size_t)n, src, &out->console[found])) {
                out->parsed++;
                found++;
            }
        }
    }

    (void)close(sock);
    return found;
}
