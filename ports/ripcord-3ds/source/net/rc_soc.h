/*
 * ripcord-3ds - libctru SOC service lifecycle.
 *
 * The BSD socket calls devkitARM's newlib exposes (socket, bind, recvfrom, ...) only work once libctru's
 * SOC service is up, and bringing it up needs a buffer the sockets stack keeps for its own bookkeeping -
 * separate from any buffer a caller passes to recvfrom() itself. SETUP.md's own gotcha list names the
 * mistake this file exists to make impossible: an undersized or misaligned buffer here produces packet
 * drops that show up first during the busiest bursts, which makes them look like a Wi-Fi ceiling instead
 * of the bug they are.
 *
 * This has no PlayStation knowledge and never will - it is the 3DS-side counterpart of the transport
 * primitives in Ripcord.Core.Net, not of anything Halyard-specific.
 */
#ifndef RC_SOC_H
#define RC_SOC_H

#include <3ds.h>
#include <netinet/in.h>

/* 1 MiB, 0x1000-aligned: the devkitPro-documented minimum for a socket workload beyond a single trivial
 * connection. rc_soc_init() owns the allocation; callers never see the pointer. */
#define RC_SOC_BUFFER_SIZE 0x100000

/* Bring up the SOC service. Returns 0 on success, or a negative libctru result code. Call after
 * gfxInitDefault() succeeds and before any BSD socket call. Idempotent - a second call while already
 * initialised is a no-op that returns 0. */
int rc_soc_init(void);

/* Tear down the SOC service and free its buffer. Safe to call even if rc_soc_init() failed partway, or
 * was never called at all. */
void rc_soc_exit(void);

/* The console's own IPv4 address, suitable for inet_ntoa(). Only meaningful after rc_soc_init() has
 * succeeded; resolved via gethostid(), the standard idiom here since libctru does not expose a
 * socket-level "what am I" call of its own. */
struct in_addr rc_soc_local_address(void);

#endif /* RC_SOC_H */
