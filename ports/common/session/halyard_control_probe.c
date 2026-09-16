/*
 * ripcord - sending the console's search probe, which is the networked half of halyard_control_arm.
 *
 * SEPARATE FROM THE MAGIC BYTES ON PURPOSE. halyard_control_arm.c builds and recognises four ASCII
 * bytes and touches nothing else, which is what lets the host suite check it with no sockets, no
 * platform seam and no network. Putting a sendto() in that file would have made a pure unit test link
 * a UDP stack to verify a string comparison.
 */
#include "halyard_control_arm.h"

#include "rc_platform.h"
#include "rc_tcp.h"

#include <string.h>

#include <arpa/inet.h>
#include <netinet/in.h>
#include <sys/socket.h>
#include <sys/types.h>
#include <unistd.h>

/* See the header: sending is what arms the console, so a lost reply is not a failure. */
int halyard_control_arm_probe(const char *host, int is_ps5)
{
    int sock;
    char probe[HALYARD_CONTROL_ARM_PROBE_SIZE];
    struct sockaddr_in unicast, broadcast;
    uint64_t start_ms;
    int saw_reply = 0;

    if (host == NULL)
        return 0;

    sock = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (sock < 0)
        return 0;

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

    /* Both, because a console that has not been spoken to recently answers the broadcast when it does
     * not answer the unicast - observed, not assumed. */
    broadcast = unicast;
    broadcast.sin_addr.s_addr = INADDR_BROADCAST;

    sendto(sock, probe, sizeof(probe), 0, (struct sockaddr *)&unicast, sizeof(unicast));
    sendto(sock, probe, sizeof(probe), 0, (struct sockaddr *)&broadcast, sizeof(broadcast));

    start_ms = rc_time_ms();
    while (rc_time_ms() - start_ms < HALYARD_CONTROL_ARM_REPLY_WINDOW_MS) {
        uint8_t buf[16];
        ssize_t n = recvfrom(sock, buf, sizeof(buf), 0, NULL, NULL);

        if (n > 0 && halyard_control_arm_is_reply(is_ps5, buf, (size_t)n)) {
            saw_reply = 1;
            break;
        }
        rc_sleep_ms(20);
    }

    close(sock);
    /* The console needs a moment between being armed and being spoken to. */
    rc_sleep_ms(HALYARD_CONTROL_ARM_SETTLE_MS);
    return saw_reply;
}
