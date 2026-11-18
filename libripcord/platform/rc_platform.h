/*
 * libripcord - the platform seam.
 *
 * This header is the ENTIRE list of things the portable protocol core asks of an operating system: a
 * monotonic clock, a sleep, a high-resolution tick with its rate, and a CSPRNG - five functions - plus
 * the BSD socket calls the core makes directly (see the socket section below).
 *
 * NOTHING GOES IN HERE THAT ONLY ONE PLATFORM NEEDS. A seam needs at least two real implementations
 * behind it. Anything a single port wants belongs in that port's own tree, reached through a callback
 * the port installs - which is how the media path (decode, audio out, present) works.
 *
 * WHY NOT JUST USE POSIX. The targets are cross-compiles against a vendor SDK, not hosted Unix. Each
 * gives its code a newlib, and the overlap is not total: sockets are shared (see the socket section
 * below), clocks and sleep are not. Naming the seam makes the dependency checkable.
 *
 * IMPLEMENTATIONS
 *   ports/ripcord-3ds/source/platform/rc_platform_3ds.c    libctru; rc_random_bytes is in
 *                                                          ports/ripcord-3ds/source/util/rc_random.c
 *   ports/ripcord-ps3/source/platform/rc_platform_ps3.c    PSL1GHT; rc_random_bytes is in
 *                                                          ports/ripcord-ps3/source/platform/rc_random_ps3.c
 *   libripcord/tests/rc_platform_host.c                    host C, for the tests; no rc_random_bytes
 *
 * No Vita implementation is in this tree. What the socket section below says about vitasdk comes from
 * a real build made against it.
 */

#ifndef RC_PLATFORM_H
#define RC_PLATFORM_H

#include <stddef.h>
#include <stdint.h>

/*
 * MONOTONIC MILLISECONDS.
 *
 * Used for every timeout and retransmit deadline in the core. The epoch is unspecified and MUST NOT be
 * relied on - only differences are meaningful. It must not go backwards: callers loop on
 * `while (rc_time_ms() - start <= timeout)`, and on unsigned arithmetic a backwards clock turns that
 * into a near-infinite loop.
 *
 * 3DS: osGetTime(). PS3: the PowerPC timebase register divided down by its reported frequency. Vita:
 * sceKernelGetProcessTimeWide() / 1000 - microseconds since process start, which is process-relative
 * rather than wall-clock and so cannot step backwards at all.
 */
uint64_t rc_time_ms(void);

/*
 * SLEEP. Milliseconds, not nanoseconds - every call site in the core passes a whole number of
 * milliseconds, and a platform's native unit (libctru's svcSleepThread takes nanoseconds) is a units
 * trap that belongs in one implementation rather than at every call site.
 */
void rc_sleep_ms(uint32_t ms);

/*
 * HIGH-RESOLUTION TICK, for profiling and for seeding values that need to differ between runs.
 *
 * rc_tick_hz() reports the tick rate so a caller can convert to real time; it is a function rather than
 * a constant because each platform's timer base is a different number (the 3DS's is SYSCLOCK_ARM11)
 * and none is worth #ifdef-ing at every call site.
 *
 * NOT A RANDOMNESS SOURCE. takion_reliable_channel.c seeds its association tag from this, which is
 * correct only because the transport spec asks for a value that distinguishes a new association from a
 * stale one - not for anything unpredictable. Cryptographic material comes from rc_random_bytes().
 */
uint64_t rc_tick(void);
uint64_t rc_tick_hz(void);

/*
 * CSPRNG. Declared here rather than in util/rc_random.h so that the seam is one header, but the
 * contract is unchanged: RETURNS 1 ON SUCCESS, 0 ON FAILURE, and a failure must never be papered over
 * with a fallback PRNG. Key material depends on this.
 *
 * util/rc_random.h states the same contract; callers test `if (!rc_random_bytes(...))`, so an inverted
 * implementation would make every failure look like a success.
 *
 * rc_random_rng_callback() in util/rc_random.h is inverted relative to this, on purpose, because
 * mbedtls's f_rng convention is 0-on-success. Only that adaptor may use the other convention.
 */
int rc_random_bytes(uint8_t *out, size_t length);

/*
 * SOCKETS: there is no socket seam.
 *
 * The core makes plain BSD calls (socket/bind/connect/send/recv/sendto/recvfrom/setsockopt/getsockopt/
 * getsockname/select/close) and includes the BSD headers directly. That works on the 3DS because libctru's SOC service exposes
 * exactly those names.
 *
 * It works on the Vita too. vitasdk's DOCUMENTED surface is `sceNetSocket`, `sceNetBind`,
 * `sceNetRecvfrom`, ..., but it also ships real POSIX headers (sys/socket.h, netinet/in.h, arpa/inet.h,
 * fcntl.h, poll.h), and a program using the POSIX names both COMPILES AND LINKS against -lSceNet_stub
 * (checked with a compile+link, not from documentation).
 *
 * inet_aton() exists on both of those. On a Linux host it needs _DEFAULT_SOURCE, being a BSD extension rather
 * than C99 that glibc hides by default; neither console libc hides it.
 *
 * So this header holds the five functions above and no socket calls. What is NOT established:
 *
 *   - [X] Whether sceNetInit()'s memory pool must be brought up before the POSIX names work on the
 *     Vita. Almost certainly yes (the 3DS's socInit() is the same shape), and it is each port's own
 *     bring-up file's job - rc_soc.c on 3DS - not this seam's.
 *   - Whether bind() to port 0 is accepted is per platform. The 3DS SOC service rejects it outright,
 *     though it is correct on .NET and on Unix.
 *
 *     The PS3 accepts it (2026-09-12): a UDP socket bound to port 0 and then received the unicast
 *     SRCH reply from a real console, so the 3DS's restriction is that platform's own. Established
 *     on hardware by ports/ripcord-ps3/source/discovery/rc_discover.c, which reports the bind answer
 *     whether or not discovery then succeeds.
 *
 *     [X] The Vita: no documentation was found either way, and no device has answered. Assume
 *     nothing.
 *
 * Socket idioms are where a second platform bites: "it compiles" is not "it works".
 */

#endif /* RC_PLATFORM_H */
