#include "rc_udp.h"
#include "rc_tcp.h"

#include <string.h>

#include <sys/socket.h>
#include <netinet/in.h>
#include <arpa/inet.h>
#include <unistd.h>

int rc_udp_open(const char *host, unsigned port, struct sockaddr_in *out_peer, int rcvbuf_bytes)
{
    int sock;

    if (host == NULL || out_peer == NULL)
        return -1;

    sock = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (sock < 0)
        return -1;

    /* See the header: this is the line that differs per platform, and getting it wrong hangs rather
     * than fails. rc_socket_set_nonblocking tries SO_NBIO before fcntl for exactly that reason. */
    if (!rc_socket_set_nonblocking(sock)) {
        (void)close(sock);
        return -1;
    }

    if (rcvbuf_bytes > 0) {
        /* Best-effort: a platform that refuses the hint still gives a working socket, just a smaller
         * cushion, and that is a performance fact rather than a correctness one. */
        (void)setsockopt(sock, SOL_SOCKET, SO_RCVBUF, &rcvbuf_bytes, (socklen_t)sizeof(rcvbuf_bytes));
    }

    memset(out_peer, 0, sizeof(*out_peer));
    out_peer->sin_family = AF_INET;
    out_peer->sin_port = htons((unsigned short)port);
    if (inet_aton(host, &out_peer->sin_addr) == 0) {
        (void)close(sock);
        return -1;
    }
    return sock;
}

int rc_udp_open_bound(const uint8_t bind_address[4], unsigned short port, int rcvbuf_bytes,
                      unsigned short *out_port)
{
    struct sockaddr_in local;
    socklen_t length = (socklen_t)sizeof(local);
    int sock;

    sock = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (sock < 0)
        return -1;
    if (!rc_socket_set_nonblocking(sock)) {
        (void)close(sock);
        return -1;
    }
    if (rcvbuf_bytes > 0)
        (void)setsockopt(sock, SOL_SOCKET, SO_RCVBUF, &rcvbuf_bytes, (socklen_t)sizeof(rcvbuf_bytes));

    memset(&local, 0, sizeof(local));
    local.sin_family = AF_INET;
    local.sin_port = htons(port);
    if (bind_address != NULL)
        memcpy(&local.sin_addr.s_addr, bind_address, 4);
    else
        local.sin_addr.s_addr = htonl(INADDR_ANY);
    if (bind(sock, (const struct sockaddr *)&local, (socklen_t)sizeof(local)) != 0) {
        (void)close(sock);
        return -1;
    }

    /* What was bound is what an OFFER must advertise, so it is read back rather than assumed. */
    memset(&local, 0, sizeof(local));
    if (getsockname(sock, (struct sockaddr *)&local, &length) != 0) {
        (void)close(sock);
        return -1;
    }
    if (out_port != NULL)
        *out_port = ntohs(local.sin_port);
    return sock;
}

int rc_udp_rcvbuf_actual(int sock)
{
    int value = 0;
    socklen_t len = (socklen_t)sizeof(value);

    if (sock < 0)
        return 0;
    if (getsockopt(sock, SOL_SOCKET, SO_RCVBUF, &value, &len) != 0)
        return 0;
    return value;
}
