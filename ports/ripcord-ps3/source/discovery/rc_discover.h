/*
 * ripcord-ps3 - find a console on the LAN, from the console.
 *
 * IT ALSO ANSWERS A PER-PLATFORM QUESTION. libripcord/platform/rc_platform.h argues that the core needs
 * no socket seam because both consoles expose the BSD names, and singles out one idiom that compiling
 * cannot check: whether bind() to port 0 is accepted. The 3DS SOC service rejects it outright, though
 * it is correct on .NET and on Unix.
 * Discovery needs a bound socket to receive unicast replies, so the question is asked here, and
 * reported separately from whether discovery worked because the two failures look identical from the
 * outside.
 *
 * The protocol knowledge is entirely libripcord's: halyard_discovery_build_probe writes the SRCH
 * datagram and halyard_discovery_parse_response reads the reply. This file contributes sockets, a
 * broadcast address, and a deadline.
 */
#ifndef RC_DISCOVER_H
#define RC_DISCOVER_H

#include <stddef.h>

#include "halyard_discovery.h"

#ifdef __cplusplus
extern "C" {
#endif

#define RC_DISCOVER_MAX 4

typedef struct {
    /*
     * DOES sin_len ACTUALLY HAVE TO BE SET? source/net/rc_netlog.c sets it on every sockaddr because
     * PSL1GHT's networktest sample does, and that was copied rather than tested. libripcord builds
     * sockaddr_in in net/rc_tcp.c and net/rc_udp.c and sets sin_family without sin_len, so if the
     * field is genuinely required then the shared core cannot open a control session on this platform.
     *
     * Answered by broadcasting the same SRCH probe twice, once with the field zeroed and once with it
     * set.
     */
    int  sinlen_zero_send_ok;   /* sendto() succeeded with sin_len left at 0 */
    int  sinlen_set_send_ok;    /* sendto() succeeded with sin_len = sizeof  */

    int  bind_port0_ok;      /* 1 if bind() to port 0 was accepted - the [X] above          */
    int  bind_errno;         /* what it said if it was not                                  */
    int  socket_ok;          /* the socket was created and broadcast was enabled            */
    int  probes_sent;        /* SRCH datagrams put on the wire                              */
    int  datagrams_rx;       /* anything at all came back                                   */
    int  parsed;             /* how many were recognisable SRCH responses                   */
    halyard_discovered_console console[RC_DISCOVER_MAX];
} rc_discover_result;

/*
 * Broadcasts SRCH for `timeout_ms` and collects what answers. Returns the number of consoles found.
 * `out` is filled either way - a run that finds nothing still has to say how far it got, because
 * "no console on this network" and "this port cannot broadcast" are different findings.
 */
int rc_discover(unsigned timeout_ms, rc_discover_result *out);

/*
 * THE SAME, FOR A CALLER THAT CANNOT STOP - a menu with a screen to keep drawing.
 *
 * rc_discover_open sends the probes and returns a socket, or -1. rc_discover_pump takes whatever has
 * arrived since the last call, without waiting, and returns the running count; call it as often as you
 * like and stop when you have waited long enough. rc_discover_close ends it.
 *
 * `out` is filled progressively and belongs to the caller for the whole exchange. rc_discover is these
 * three in a loop, which is what keeps them exercised: every bring-up run drives that path.
 */
int  rc_discover_open(rc_discover_result *out);
int  rc_discover_pump(int sock, rc_discover_result *out, int found);
void rc_discover_close(int sock);

#ifdef __cplusplus
}
#endif

#endif /* RC_DISCOVER_H */
