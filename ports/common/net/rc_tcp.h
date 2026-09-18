/*
 * ripcord-3ds - a minimal TCP client, built on rc_soc's SOC bring-up.
 *
 * Plain BSD sockets, nothing PlayStation-specific - the control channel needs a plain-TCP connection to
 * a console's port 9295 (docs/protocol/ps5-local-discovery.md); this is the seam between that and
 * libctru's socket support, same role rc_soc.c plays for UDP. Unlike rc_soc.c/discovery/linktest,
 * rc_tcp_send_all()'s retry-with-timeout does depend on libctru (osGetTime/svcSleepThread) - the price of
 * tolerating a non-blocking socket without a caller-supplied event loop.
 */
#ifndef RC_TCP_H
#define RC_TCP_H

#include <stddef.h>
#include <sys/types.h>

/* Opens a TCP connection to host:port (the connect itself blocks until it succeeds or fails - callers
 * that want a non-blocking socket for what follows set O_NONBLOCK themselves afterward, same as
 * source/linktest and source/discovery already do for UDP). Returns the socket descriptor, or -1 on
 * failure (including resolution failure - `host` must be a dotted-quad IPv4 address; this port never
 * needs DNS, since consoles are only ever addressed by an IP a discovery/pairing step already resolved). */
int rc_tcp_connect(const char *host, unsigned short port);

/* Sends the entire buffer, looping over short writes and retrying on EAGAIN/EWOULDBLOCK (a non-blocking
 * socket's send buffer can be momentarily full even for a small request) up to a 5-second bound. Returns
 * 0 on success, -1 on error, a closed connection, or exceeding that bound. */
int rc_tcp_send_all(int sock, const void *data, size_t length);

/* Reads up to buf_size bytes. Returns the number of bytes read (0 means the peer closed the
 * connection), or -1 on error. Blocks according to the socket's own receive-timeout setting, if any -
 * this module does not impose one itself. */
ssize_t rc_tcp_recv(int sock, void *buf, size_t buf_size);

/*
 * PUT A SOCKET INTO NON-BLOCKING MODE, PORTABLY - and this exists because fcntl() does not do it
 * everywhere.
 *
 * On Linux and the 3DS, fcntl(fd, F_SETFL, O_NONBLOCK) is the idiom and works. On the PS3 it does not:
 * lv2 sockets are not newlib file descriptors, so fcntl operates on a different table entirely, returns
 * without complaint, and leaves the socket BLOCKING. PSL1GHT exposes SO_NBIO (0x1100) for the job
 * instead.
 *
 * The consequence is not a failed call, which is why it took hardware to find. A socket that is still
 * blocking makes every poll loop in halyard_control_session.c unreachable: recv() never returns, so the
 * deadline guarding it is never evaluated, and a function whose every loop is bounded hangs anyway. On
 * a PS3 that is a locked console needing a power cycle.
 *
 * SO_NBIO is only defined by PSL1GHT's headers, so this compiles to exactly the previous behaviour
 * everywhere else and changes nothing for the 3DS, the Vita or the host suite.
 *
 * Returns 1 on success, 0 if neither mechanism was available or accepted.
 */
int rc_socket_set_nonblocking(int sock);

#endif /* RC_TCP_H */
