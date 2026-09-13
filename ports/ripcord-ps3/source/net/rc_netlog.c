/* See rc_netlog.h for why this exists and what it is for. */
#include "rc_netlog.h"

#include <string.h>

#include <unistd.h>

#include <net/net.h>
#include <net/socket.h>
#include <netinet/in.h>
#include <arpa/inet.h>

static int s_sock = -1;
static int s_net_up;
static struct sockaddr_in s_dest;

int rc_netlog_open(const char *ip, uint16_t port)
{
    if (s_sock >= 0)
        return 1;

    /*
     * [X] netInitialize() before any socket call. rc_platform.h flagged this as the one thing the socket
     * investigation did NOT establish - "whether sceNetInit()'s memory pool must be brought up before the
     * POSIX names work. Almost certainly yes (the 3DS's socInit() is the same shape), and it is each
     * port's own bring-up file's job - not this seam's." This is that file, doing that job.
     */
    if (netInitialize() < 0)
        return 0;

    s_net_up = 1;

    /* PF_INET/IPPROTO_UDP spelled out rather than AF_INET/0, matching PSL1GHT's networktest sample. The
     * zero-protocol form is correct on every other platform this project builds for; there is no reason
     * to find out here whether it is correct on this one. */
    s_sock = socket(PF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (s_sock < 0) {
        netDeinitialize();
        s_net_up = 0;
        return 0;
    }

    /*
     * No bind(). The seam's notes single out bind() to port 0 as the specific idiom worth testing on a
     * device before assuming - the 3DS rejects it outright - and a sending-only datagram socket does not
     * need one, so this deliberately does not answer that question rather than answering it by accident
     * in a program whose job is something else.
     */
    memset(&s_dest, 0, sizeof(s_dest));

    /*
     * *** sin_len IS NOT OPTIONAL HERE, AND THIS IS THE TRAP rc_platform.h PREDICTED. ***
     *
     * The PS3's sockaddr_in carries a leading length byte, in the original BSD style. Linux and the 3DS
     * both dropped it, so code written against either compiles here unchanged and leaves it zero -
     * which is exactly the shape of failure the seam's notes warn about: "socket idioms are exactly
     * where a second platform bites, and 'it compiles' is not 'it works'."
     *
     * PSL1GHT's own samples/network/networktest sets it on every sockaddr it builds. Copied from there
     * rather than deduced, because a struct field that only matters at runtime is not something to
     * reason about when a worked example is available.
     */
    s_dest.sin_len = (uint8_t)sizeof(s_dest);
    s_dest.sin_family = AF_INET;
    s_dest.sin_port = htons(port);

    /* inet_pton, not inet_addr, for the same reason: it is what the sample uses, and it reports failure
     * rather than folding it into a valid-looking address the way inet_addr's INADDR_NONE does. */
    if (inet_pton(AF_INET, ip, &s_dest.sin_addr) != 1) {
        (void)close(s_sock);
        s_sock = -1;
        (void)netDeinitialize();
        s_net_up = 0;
        return 0;
    }

    return 1;
}

void rc_netlog_write(const char *text)
{
    size_t len;

    if (s_sock < 0 || text == NULL)
        return;

    len = strlen(text);
    if (len == 0u)
        return;

    /* Result ignored on purpose - see the header. There is nothing useful this could do about a failure
     * that would not be worse than the failure. */
    (void)sendto(s_sock, text, len, 0, (const struct sockaddr *)&s_dest, (socklen_t)sizeof(s_dest));
}

int rc_netlog_is_open(void)
{
    return s_sock >= 0;
}

void rc_netlog_close(void)
{
    if (s_sock >= 0) {
        (void)close(s_sock);
        s_sock = -1;
    }
    if (s_net_up) {
        (void)netDeinitialize();
        s_net_up = 0;
    }
}
