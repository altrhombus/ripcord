/*
 * ripcord-ps3 - the log channel that does not need a filesystem.
 *
 * WHY A THIRD CHANNEL. The bring-up program already writes through newlib stdio and through raw lv2
 * syscalls, and both write FILES. That was enough while the program was launched from the XMB as an
 * installed title, and it stopped being enough the moment it was launched by ps3load instead: the
 * transfer succeeds, the listener spawns the program, the screen goes black, and not one byte appears
 * anywhere on disk. A process spawned from /dev_hdd0/tmp/ with no content id plausibly has a tighter
 * sandbox than an installed game, in which case the program is running perfectly and has no voice.
 *
 * A UDP datagram per line needs no write permission anywhere. If the program runs at all and the network
 * is up, the output arrives - and it arrives LIVE, on the development machine, which is what was wanted
 * from ps3load in the first place and is worth more than the file channels during decoder work.
 *
 * IT ALSO CLOSES AN OPEN [X]. ports/common/platform/rc_platform.h argues at length that the core needs no
 * socket seam because both consoles expose the BSD names, and records that this was checked by compiling
 * and linking rather than by reading documentation - with the standing caveat that "it compiles" is not
 * "it works" and that socket idioms are exactly where a second platform bites. This is the first PS3 code
 * to open a socket. Whatever it proves, it proves on hardware.
 *
 * FIRE AND FORGET, DELIBERATELY. No retry, no sequence numbers, no acknowledgement. A dropped line costs
 * a line of a log; anything more elaborate would be a protocol to debug while debugging something else.
 * Every call ignores its result for the same reason: a logger that can fail a program is worse than a
 * logger that misses a line.
 */
#ifndef RC_NETLOG_H
#define RC_NETLOG_H

#include <stdint.h>

/*
 * Brings up networking and opens the datagram socket. Returns 1 on success, 0 on failure - the seam's
 * convention, not lv2's. `ip` is dotted-quad; there is no name resolution and none is wanted.
 *
 * Safe to call when nothing is listening: UDP does not care, and a run nobody is watching costs the
 * price of a few datagrams into an empty network.
 */
int rc_netlog_open(const char *ip, uint16_t port);

/* Sends one line. Never fails, never blocks on anything that matters, does nothing if not open. */
void rc_netlog_write(const char *text);

/* Closes the socket and tears down networking. Safe whether or not open succeeded. */
void rc_netlog_close(void);

/* Whether the channel is carrying this run, for the report the program prints about its own channels. */
int rc_netlog_is_open(void);

/* Not yet attempted. Distinct from any value sendto can return. */
#define RC_NETLOG_SEND_UNTRIED (-999L)

/*
 * What the FIRST sendto returned: the byte count on success, negative on failure, UNTRIED if no line has
 * been written yet. The channel reporting itself open says only that netInitialize, socket and inet_pton
 * all succeeded - on hardware that was true while nothing arrived at the far end, and a byte count here
 * separates "the console refused to send" from "the datagram did not survive the network".
 */
long rc_netlog_first_send_result(void);

#endif /* RC_NETLOG_H */
