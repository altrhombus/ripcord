/*
 * ripcord ports - the platform seam.
 *
 * This header is the ENTIRE list of things the portable protocol core asks of an operating system.
 * It is deliberately tiny, and it is tiny because it was measured rather than designed: the 3DS port
 * was written as a single-platform tree, and when it was audited for a second target, 71 of its 88
 * source files turned out to reference no OS at all. The whole coupling was three libctru calls
 * (osGetTime, svcSleepThread, svcGetSystemTick) in three files, plus sockets.
 *
 * So the rule for this file is: NOTHING GOES IN HERE THAT ONLY ONE PLATFORM NEEDS. A seam earns its
 * place by having at least two real implementations behind it. Anything a single port wants belongs in
 * that port's own tree, reached through a callback the port installs - which is how the media path
 * (decode, audio out, present) works, and why it is absent from this header despite being the largest
 * platform surface either port has.
 *
 * WHY NOT JUST USE POSIX. Both targets are ARM cross-compiles against a vendor SDK, not hosted Unix.
 * libctru gives 3DS code a newlib with BSD sockets and its own clock/sleep primitives; vitasdk gives
 * Vita code a newlib whose socket layer is `sceNet*`-prefixed rather than POSIX-named [X - see the
 * socket section below, this is not yet confirmed against a real vitasdk install]. Coding the core to
 * whichever subset happens to be common to both is how you get a core that silently depends on the one
 * you developed against. Naming the seam makes the dependency checkable.
 *
 * IMPLEMENTATIONS
 *   ports/ripcord-3ds/source/platform/rc_platform_3ds.c    libctru
 *   ports/ripcord-vita/source/platform/rc_platform_vita.c  vitasdk        [not written yet]
 *   ports/common/tests/rc_platform_host.c                  host C, for the known-answer tests
 */

#ifndef RC_PLATFORM_H
#define RC_PLATFORM_H

#include <stddef.h>
#include <stdint.h>

/*
 * MONOTONIC MILLISECONDS.
 *
 * Used for every timeout and retransmit deadline in the core. The epoch is unspecified and MUST NOT be
 * relied on - only differences are meaningful. It must not go backwards, which is the one property the
 * callers actually depend on: `while (rc_time_ms() - start <= timeout)` is the shape used throughout,
 * and on unsigned arithmetic a backwards clock turns that into a near-infinite loop rather than an
 * early exit.
 *
 * 3DS: osGetTime(). Vita: sceKernelGetProcessTimeWide() / 1000 [X - not yet verified].
 */
uint64_t rc_time_ms(void);

/*
 * SLEEP. Milliseconds, not nanoseconds - every call site in the core passes a whole number of
 * milliseconds (5, 20), and libctru's nanosecond argument was a units trap sitting in three files
 * waiting for someone to drop three zeroes.
 */
void rc_sleep_ms(uint32_t ms);

/*
 * HIGH-RESOLUTION TICK, for profiling and for seeding values that need to differ between runs.
 *
 * rc_tick_hz() reports the tick rate so a caller can convert to real time; it is a function rather than
 * a constant because the 3DS's SYSCLOCK_ARM11 and the Vita's timer base are different numbers and
 * neither is worth #ifdef-ing at every call site.
 *
 * NOT A RANDOMNESS SOURCE. takion_reliable_channel.c seeds its association tag from this, which is
 * correct only because the transport spec asks for a value that distinguishes a new association from a
 * stale one - not for anything unpredictable. Cryptographic material comes from rc_random_bytes().
 */
uint64_t rc_tick(void);
uint64_t rc_tick_hz(void);

/*
 * CSPRNG. Declared here rather than in util/rc_random.h so that the seam is one header, but the
 * contract is unchanged: returns 0 on success, non-zero on failure, and a failure must never be
 * papered over with a fallback PRNG. Key material depends on this.
 */
int rc_random_bytes(uint8_t *out, size_t length);

/*
 * SOCKETS.
 *
 * The core makes plain BSD calls (socket/bind/connect/send/recv/sendto/recvfrom/setsockopt/close/poll)
 * and includes the BSD headers directly. That works on the 3DS because libctru's SOC service exposes
 * exactly those names.
 *
 * [X] IT IS NOT YET CONFIRMED THAT IT WORKS ON VITA. vitasdk's documented surface is `sceNetSocket`,
 * `sceNetBind`, `sceNetRecvfrom`, ... - the same shapes under a prefix, plus sceNetEpoll* in place of
 * poll(). Whether vitasdk also ships POSIX-named wrappers is an open question, and it is the single
 * largest unknown in this seam: if it does not, ~12 call names need mapping and this header grows a
 * socket section. Resolve it against a real vitasdk install before writing the Vita transport, not
 * after. Two known-adjacent facts, both worth checking at the same time:
 *
 *   - sceNetInit() takes an explicit memory pool (~1 MB in vitasdk's own sample), exactly as the 3DS's
 *     socInit() takes its 0x100000 aligned buffer. That part is a direct parallel and is already
 *     modelled by each port's own bring-up file (rc_soc.c on 3DS), not by this seam.
 *   - non-blocking mode is fcntl(O_NONBLOCK) in the core today (10 call sites). The Vita equivalent may
 *     be sceNetSetsockopt(SCE_NET_SO_NBIO). If so, that is a seam function, and the natural first one
 *     to add here.
 *
 * The 3DS taught this port that socket idioms are exactly where a second platform bites: binding port 0
 * is correct on .NET and on Unix, and is rejected outright by the 3DS SOC service. Assume nothing here
 * transfers until a real device says otherwise.
 */

#endif /* RC_PLATFORM_H */
