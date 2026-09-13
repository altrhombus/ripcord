/*
 * ripcord-ps3 - find a console on the LAN, from the console.
 *
 * The first thing in this port that talks to a PS5 rather than to a file or to itself. Everything up to
 * now has been verified against vectors, captures and reference decodes - all of which are recordings.
 * A recording cannot refuse a datagram, answer from an unexpected address, or take longer than expected
 * to reply, and those are the things that break a client on a real network.
 *
 * IT ALSO SETTLES AN OPEN [X]. ports/common/platform/rc_platform.h argues that the core needs no socket
 * seam because both consoles expose the BSD names, and singles out one idiom it could not check by
 * compiling: "Whether bind() to port 0 is accepted. The 3DS SOC service rejects it outright, which is
 * correct on .NET and on Unix and cost a hardware run to find. No documentation was found either way."
 * Discovery needs a bound socket to receive unicast replies, so this is where that question gets asked.
 * It is asked deliberately and reported separately rather than being folded into whether discovery
 * worked, because the two failures look identical from the outside and are not the same problem.
 *
 * The protocol knowledge is entirely ports/common's: halyard_discovery_build_probe writes the SRCH
 * datagram and halyard_discovery_parse_response reads the reply. This file contributes sockets, a
 * broadcast address, and a deadline - which is precisely the division rc_platform.h describes, and the
 * first time it has been exercised end to end on this platform.
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
     * PSL1GHT's networktest sample does, and that was copied rather than tested. The distinction matters
     * to more than this port: ports/common builds sockaddr_in in three places -
     * session/halyard_control_session.c and net/rc_tcp.c - and sets sin_family without sin_len, so if
     * the field is genuinely required then the shared core cannot open a control session on this
     * platform, and if it is not then nothing needs changing anywhere.
     *
     * Answered by broadcasting the same SRCH probe twice, once with the field zeroed and once with it
     * set. Two sends, one extra datagram on the wire, and it settles whether a change to code every port
     * shares is warranted.
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

#ifdef __cplusplus
}
#endif

#endif /* RC_DISCOVER_H */
