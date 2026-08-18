#include "rc_tcp.h"

#include "rc_platform.h"

#include <arpa/inet.h>
#include <errno.h>
#include <netinet/in.h>
#include <string.h>
#include <sys/socket.h>
#include <unistd.h>

#define SEND_TIMEOUT_MS 5000u

int rc_tcp_connect(const char *host, unsigned short port)
{
    struct sockaddr_in addr;
    int sock;

    memset(&addr, 0, sizeof(addr));
    addr.sin_family = AF_INET;
    addr.sin_port = htons(port);
    if (inet_aton(host, &addr.sin_addr) == 0)
        return -1;

    sock = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (sock < 0)
        return -1;

    if (connect(sock, (struct sockaddr *)&addr, sizeof(addr)) != 0) {
        close(sock);
        return -1;
    }

    return sock;
}

int rc_tcp_send_all(int sock, const void *data, size_t length)
{
    const unsigned char *p = (const unsigned char *)data;
    size_t sent = 0;
    uint64_t start_ms = rc_time_ms();

    while (sent < length) {
        ssize_t n = send(sock, p + sent, length - sent, 0);

        if (n < 0) {
            /* A non-blocking socket's send buffer can be momentarily full even for a request this
             * small - retry rather than fail outright, bounded so a genuinely wedged connection still
             * gives up instead of hanging the caller. */
            if (errno == EAGAIN || errno == EWOULDBLOCK) {
                if (rc_time_ms() - start_ms > SEND_TIMEOUT_MS)
                    return -1;
                rc_sleep_ms(5); /* 5 ms */
                continue;
            }
            return -1;
        }
        if (n == 0)
            return -1;

        sent += (size_t)n;
    }
    return 0;
}

ssize_t rc_tcp_recv(int sock, void *buf, size_t buf_size)
{
    return recv(sock, buf, buf_size, 0);
}
