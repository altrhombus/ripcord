/*
 * A UDP socket opened the way every port needs it opened.
 *
 * This exists because the 3DS port grew its own `open_udp` and the PS3 port was about to grow a second
 * one, and the two would have differed in a way that matters: the 3DS version sets non-blocking mode with
 * fcntl(F_SETFL, O_NONBLOCK), which is correct there and returns -1 on the PS3, whose sockets are lv2
 * descriptors rather than newlib file descriptors. A port that copied it would have got a BLOCKING socket
 * and no error, and a blocking socket inside Takion's poll loops is not a failure - it is a hang.
 *
 * So the knowledge lives once, in rc_socket_set_nonblocking (rc_tcp.h), and every port gets it.
 */
#ifndef RC_UDP_H
#define RC_UDP_H

#include <netinet/in.h>

/*
 * Opens a non-blocking UDP socket and fills `out_peer` with the address of `host`:`port`. Returns the
 * socket, or -1 - and -1 covers "could not create it" and "could not make it non-blocking" alike, because
 * a blocking socket is not a usable result here and returning one would be worse than failing.
 *
 * `rcvbuf_bytes` asks for a receive buffer; 0 leaves the platform default. A keyframe arrives as a burst
 * of back-to-back datagrams - ~24 KB across roughly twenty of them at 960x540 - and if the socket's
 * buffer is smaller than the burst, the stack drops the tail before any amount of draining can reach it.
 * That loss does not respond to CPU, which is how the 3DS port eventually identified it.
 *
 * The host must be a dotted quad; no name resolution happens here.
 */
int rc_udp_open(const char *host, unsigned port, struct sockaddr_in *out_peer, int rcvbuf_bytes);

#endif /* RC_UDP_H */
