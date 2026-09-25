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
#include <stdint.h>

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

/*
 * WHAT THE RECEIVE BUFFER ACTUALLY IS, which is not necessarily what was asked for.
 *
 * SO_RCVBUF is a hint. A platform may round it, cap it, double it for bookkeeping, or ignore it, and the
 * setsockopt return says only that the request was accepted - not that the size was. This port has
 * already been caught once believing an SDK call did what it said: fcntl(F_SETFL) returns -1 on these
 * sockets with nothing useful behind it.
 *
 * Returns the size the platform reports, or 0 if it will not say. The answer matters because a buffer
 * smaller than a burst loses the tail of that burst before any amount of draining can reach it, and the
 * symptom is loss that looks exactly like the network.
 */
int rc_udp_rcvbuf_actual(int sock);

/*
 * A non-blocking UDP socket BOUND to a local port, for a socket whose port must be known before anything
 * is sent on it: the rendezvous route advertises that port in a signaling OFFER and asks STUN what the NAT
 * maps it to (rc_stun_gather needs a bound socket), and only then talks to the console from it.
 *
 * `bind_address` is 4 bytes in network order, or NULL for INADDR_ANY. `port` 0 lets the platform choose.
 * The port actually bound is read back into *out_port (host order; may be NULL). `rcvbuf_bytes` as for
 * rc_udp_open. Returns the socket or -1. The socket is not connect()ed: every reader in the core uses
 * sendto/recvfrom, and on BSD sockets a sendto with an address on a connected UDP socket fails.
 *
 * .NET chooses the port with a throwaway bind (HalyardAccountConsoleSession.FreeUdpPort) and binds the real
 * socket later, leaving a window where something else can take the port; this binds once and keeps it.
 */
int rc_udp_open_bound(const uint8_t bind_address[4], unsigned short port, int rcvbuf_bytes,
                      unsigned short *out_port);

#endif /* RC_UDP_H */
