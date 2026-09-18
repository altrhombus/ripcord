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

/*
 * OPEN, PUMP, CLOSE - so a caller with a screen to keep drawing can ask without stopping.
 *
 * rc_discover blocks for its whole window, which is right for a bring-up probe and wrong for a menu:
 * the shell's console cards used to be asked once at launch and then never again, so a console woken
 * from another room went on saying "standby" while somebody watched it. Refreshing with the blocking
 * call would freeze the shell for a second and a half every time.
 *
 * The receive loop was already non-blocking - MSG_DONTWAIT and a sleep - so the split costs nothing but
 * moving the socket's lifetime into the caller's hands. rc_discover is now these three in a loop, which
 * is also the check that they behave: the bring-up path exercises them on every run.
 */
/*
 * BOTH FAMILIES, because a search that only asks one of them can only ever find one of them.
 *
 * The two differ in the port they listen on and in the protocol version they expect echoed back - 9302
 * and 00030010 for a PS5, 987 and 00020020 for a PS4 - so a PS5 probe is not merely unanswered by a
 * PS4, it never reaches it. This port asked only the first, and a PS4 on the same network was
 * indistinguishable from no PS4 at all.
 *
 * ONE SOCKET IS ENOUGH for both. The replies come back to the source address and port of the probe, not
 * to the port it was sent to, so a single bound socket receives both and the parser does not need to be
 * told which it is reading - the reply says so itself, in the host type this port now carries through
 * to pairing.
 */
static const halyard_discovery_profile *const kProfiles[] = {
    &halyard_discovery_profile_ps5,
    &halyard_discovery_profile_ps4,
};
#define RC_DISCOVER_PROFILES ((int)(sizeof(kProfiles) / sizeof(kProfiles[0])))

int rc_discover_open(rc_discover_result *out)
{
    const halyard_discovery_profile *profile = &halyard_discovery_profile_ps5;
    char probe[128];
    size_t probe_len;
    struct sockaddr_in local, bcast;
    int sock = -1;
    int on = 1;
    int p;


    memset(out, 0, sizeof(*out));

    probe_len = halyard_discovery_build_probe(profile, probe, sizeof(probe));
    if (probe_len == 0u)
        return -1;

    sock = socket(PF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (sock < 0)
        return -1;

    if (setsockopt(sock, SOL_SOCKET, SO_BROADCAST, &on, (socklen_t)sizeof(on)) < 0) {
        (void)close(sock);
        return -1;
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

    /*
     * THE sin_len EXPERIMENT. See rc_discover.h: whether this field is required decides whether
     * ports/common needs changing, and it was copied from a sample rather than established. The zeroed
     * attempt goes first so that if it works, the console has already seen a probe by the time the
     * second one goes out - either way the reply handling below is unchanged.
     */
    {
        struct sockaddr_in zeroed = bcast;
        zeroed.sin_len = 0;
        out->sinlen_zero_send_ok =
            (sendto(sock, probe, probe_len, 0, (struct sockaddr *)&zeroed,
                    (socklen_t)sizeof(zeroed)) >= 0) ? 1 : 0;
    }

    if (sendto(sock, probe, probe_len, 0, (struct sockaddr *)&bcast, (socklen_t)sizeof(bcast)) < 0) {
        (void)close(sock);
        return -1;
    }
    out->sinlen_set_send_ok = 1;
    out->probes_sent = out->sinlen_zero_send_ok ? 2 : 1;

    /*
     * AND THE REST OF THE FAMILIES. The first is already out - it carried the sin_len experiment above,
     * which wants one probe and not a loop around it - so this sends every profile after it. A send
     * that fails is not fatal: one family answering is a better outcome than neither, and a network
     * that refuses a broadcast to one port has already been reported by the send that did go.
     */
    for (p = 1; p < RC_DISCOVER_PROFILES; p++) {
        char other[128];
        size_t other_len = halyard_discovery_build_probe(kProfiles[p], other, sizeof(other));

        if (other_len == 0u)
            continue;
        fill_addr(&bcast, "255.255.255.255", kProfiles[p]->port);
        if (sendto(sock, other, other_len, 0, (struct sockaddr *)&bcast,
                   (socklen_t)sizeof(bcast)) >= 0)
            out->probes_sent++;
    }

    /*
     * Poll rather than block. rc_time_ms is the seam's monotonic clock - the one rc_platform_ps3.c
     * derives from the time base precisely so a deadline cannot be moved by the console's wall clock -
     * and a non-blocking recvfrom in a timed loop needs nothing from the platform that has not already
     * been confirmed on this hardware.
     */
    return sock;
}

int rc_discover_pump(int sock, rc_discover_result *out, int found)
{
    if (sock < 0 || out == NULL)
        return found;

    /* One pass of whatever has arrived, and no waiting. The caller decides how long to keep asking. */
    while (found < RC_DISCOVER_MAX) {
        char buf[1024];
        struct sockaddr_in from;
        socklen_t from_len = (socklen_t)sizeof(from);
        ssize_t n;

        memset(&from, 0, sizeof(from));
        n = recvfrom(sock, buf, sizeof(buf) - 1u, MSG_DONTWAIT,
                     (struct sockaddr *)&from, &from_len);
        if (n <= 0)
            break;

        out->datagrams_rx++;
        buf[n] = '\0';

        {
            char ip[HALYARD_DISCOVERY_ADDRESS_MAX];
            const char *src = inet_ntop(AF_INET, &from.sin_addr, ip, (socklen_t)sizeof(ip));

            if (halyard_discovery_parse_response(buf, (size_t)n, src, &out->console[found])) {
                int dup = 0;
                int j;

                out->parsed++;

                /*
                 * DEDUPE BY host-id. Two probes go out - see the sin_len experiment above - so one
                 * console answers twice and the first version of this counted it as two consoles.
                 * That was cosmetic here and would not be in a client: a console can reply more than
                 * once to a single broadcast, and a list that grows an entry per datagram is a list
                 * that shows the same machine repeatedly.
                 */
                for (j = 0; j < found; j++)
                    if (strcmp(out->console[j].host_id, out->console[found].host_id) == 0) {
                        dup = 1;
                        break;
                    }

                if (!dup)
                    found++;
            }
        }
    }

    return found;
}

void rc_discover_close(int sock)
{
    if (sock >= 0)
        (void)close(sock);
}

/*
 * The blocking form, now built from the three above - which is what keeps them honest, since every
 * bring-up run drives this path.
 */
int rc_discover(unsigned timeout_ms, rc_discover_result *out)
{
    uint64_t deadline;
    int sock, found = 0;

    sock = rc_discover_open(out);
    if (sock < 0)
        return 0;

    deadline = rc_time_ms() + (uint64_t)timeout_ms;
    while (rc_time_ms() < deadline && found < RC_DISCOVER_MAX) {
        int before = found;

        found = rc_discover_pump(sock, out, found);
        if (found == before)
            rc_sleep_ms(20u);
    }

    rc_discover_close(sock);
    return found;
}
