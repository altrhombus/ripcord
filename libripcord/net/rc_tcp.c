#include "rc_tcp.h"

#include "rc_platform.h"

#include <arpa/inet.h>
#include <fcntl.h>
#include <errno.h>
#include <netinet/in.h>
#include <string.h>
#include <sys/select.h>
#include <sys/socket.h>
#include <sys/time.h>
#include <unistd.h>

#define SEND_TIMEOUT_MS 5000u

/*
 * Undoes rc_socket_set_nonblocking, by the same two mechanisms in the same order - see that function for
 * why SO_NBIO comes first. Returns 1 on success.
 */
static int set_blocking(int sock)
{
#ifdef SO_NBIO
    {
        int off = 0;
        if (setsockopt(sock, SOL_SOCKET, SO_NBIO, &off, (socklen_t)sizeof(off)) == 0)
            return 1;
    }
#endif

#if defined(O_NONBLOCK) && defined(F_GETFL) && defined(F_SETFL)
    {
        int flags = fcntl(sock, F_GETFL, 0);
        if (flags == -1)
            return 0;
        return fcntl(sock, F_SETFL, flags & ~O_NONBLOCK) != -1;
    }
#else
    (void)sock;
    return 0;
#endif
}

/*
 * Waits for a non-blocking connect to finish. Returns 1 connected, 0 refused or out of time.
 *
 * select() can return early (EINTR), so the deadline is re-derived from the clock on every pass rather
 * than trusted to a single timeval - the same rule every other loop in the core follows.
 */
static int await_connect(int sock, unsigned timeout_ms)
{
    uint64_t start_ms = rc_time_ms();

    for (;;) {
        uint64_t elapsed = rc_time_ms() - start_ms;
        unsigned remaining;
        struct timeval tv;
        fd_set wr;
        int ready;

        if (elapsed >= (uint64_t)timeout_ms)
            return 0;
        remaining = timeout_ms - (unsigned)elapsed;

        FD_ZERO(&wr);
        /* FD_SET converts the descriptor through the fd_set mask type, which -Wconversion objects to
         * inside the SDK's own header on more than one platform. The conversion is theirs. */
#pragma GCC diagnostic push
#pragma GCC diagnostic ignored "-Wsign-conversion"
#pragma GCC diagnostic ignored "-Wconversion"
        FD_SET(sock, &wr);
#pragma GCC diagnostic pop
        tv.tv_sec = (time_t)(remaining / 1000u);
        tv.tv_usec = (suseconds_t)((remaining % 1000u) * 1000u);

        ready = select(sock + 1, NULL, &wr, NULL, &tv);
        if (ready > 0) {
            int err = 0;
            socklen_t len = (socklen_t)sizeof(err);

            return getsockopt(sock, SOL_SOCKET, SO_ERROR, &err, &len) == 0 && err == 0;
        }
        if (ready == 0)
            return 0;
        if (errno != EINTR)
            return 0;
    }
}

int rc_tcp_connect_timeout(const char *host, unsigned short port, unsigned timeout_ms)
{
    struct sockaddr_in addr;
    int sock;
    int connected;

    if (host == NULL)
        return -1;
    if (timeout_ms == 0u)
        timeout_ms = RC_TCP_CONNECT_TIMEOUT_MS;

    memset(&addr, 0, sizeof(addr));
    addr.sin_family = AF_INET;
    addr.sin_port = htons(port);
    if (inet_aton(host, &addr.sin_addr) == 0)
        return -1;

    sock = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (sock < 0)
        return -1;

    /*
     * A socket that cannot be made non-blocking cannot be given a deadline, and falling back to the
     * blocking connect would quietly reintroduce the hang this exists to remove. Refuse instead.
     */
    if (!rc_socket_set_nonblocking(sock)) {
        close(sock);
        return -1;
    }

    /*
     * errno is NOT consulted when connect() does not complete at once, and that is the PS3's lesson rather
     * than an oversight: lv2 sockets report through net_errno, so "EINPROGRESS" cannot be relied on to
     * appear. The pre-flight this replaces went straight to select() on any non-zero return, and SO_ERROR
     * afterwards separates a refusal from a success on every platform. An immediate hard failure at worst
     * costs the deadline.
     */
    if (connect(sock, (struct sockaddr *)&addr, sizeof(addr)) == 0)
        connected = 1;
    else
        connected = await_connect(sock, timeout_ms);

    if (!connected || !set_blocking(sock)) {
        close(sock);
        return -1;
    }
    return sock;
}

int rc_tcp_connect(const char *host, unsigned short port)
{
    return rc_tcp_connect_timeout(host, port, RC_TCP_CONNECT_TIMEOUT_MS);
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

int rc_socket_set_nonblocking(int sock)
{
    /*
     * SO_NBIO first, because where it exists it is the mechanism that actually works. See rc_tcp.h: on
     * the PS3, fcntl reports success and leaves the socket blocking, which turns every bounded poll loop
     * into an unbounded one.
     */
#ifdef SO_NBIO
    {
        int on = 1;
        if (setsockopt(sock, SOL_SOCKET, SO_NBIO, &on, (socklen_t)sizeof(on)) == 0)
            return 1;
    }
#endif

#if defined(O_NONBLOCK) && defined(F_SETFL)
    return fcntl(sock, F_SETFL, O_NONBLOCK) != -1;
#else
    (void)sock;
    return 0;
#endif
}
