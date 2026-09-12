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
 * Each gives its code a newlib, and the overlap between the two is real but not total: sockets turned
 * out to be shared (see the socket section below), clocks and sleep did not. Coding the core to
 * whichever subset happens to be common is how you get a core that silently depends on the platform you
 * developed against. Naming the seam makes the dependency checkable.
 *
 * IMPLEMENTATIONS
 *   ports/ripcord-3ds/source/platform/rc_platform_3ds.c    libctru
 *   ports/common/tests/rc_platform_host.c                  host C, for the known-answer tests
 *
 * A third is written and sits on the Vita branch rather than in this tree. That matters to read this
 * header correctly: the socket section below says what vitasdk does because a real build was made
 * against it, and only the file is elsewhere. Two implementations are visible here; three exist, and
 * the seam was derived from all three.
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
 * 3DS: osGetTime(). Vita: sceKernelGetProcessTimeWide() / 1000 - microseconds since process start,
 * which is process-relative rather than wall-clock and so cannot step backwards at all.
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
 * contract is unchanged: RETURNS 1 ON SUCCESS, 0 ON FAILURE, and a failure must never be papered over
 * with a fallback PRNG. Key material depends on this.
 *
 * This comment said the opposite - 0 on success - until 2026-09-11, and it is worth saying why the
 * wrong version is not a harmless typo. util/rc_random.h states the real contract and warns in its own
 * words that an inverted check here "would make every failure look like a success"; the 3DS
 * implementation returns 1 on success and its caller tests `if (!rc_random_bytes(...))`. But THIS is
 * the header a new port is written from - it is the whole list of what the core asks of an OS - so the
 * inversion was pointed exactly at the person with no other context, writing the first draft of a
 * platform's entropy source. It was found while writing the PS3 one, before that draft existed.
 *
 * Note that rc_random_rng_callback() in util/rc_random.h really is inverted relative to this, on
 * purpose, because mbedtls's f_rng convention is 0-on-success. Two conventions in one seam is the
 * hazard; the adaptor is where they are allowed to meet.
 */
int rc_random_bytes(uint8_t *out, size_t length);

/*
 * SOCKETS - RESOLVED 2026-08-17, and the answer is that there is nothing to do.
 *
 * The core makes plain BSD calls (socket/bind/connect/send/recv/sendto/recvfrom/setsockopt/close/poll)
 * and includes the BSD headers directly. That works on the 3DS because libctru's SOC service exposes
 * exactly those names.
 *
 * It works on the Vita too. This was the largest open question in this header and was expected to cost
 * a socket seam of ~12 mapped call names, because vitasdk's DOCUMENTED surface is `sceNetSocket`,
 * `sceNetBind`, `sceNetRecvfrom`, ... - the same shapes under a prefix. But vitasdk also ships real
 * POSIX headers (sys/socket.h, netinet/in.h, arpa/inet.h, fcntl.h, poll.h), and a program using the
 * POSIX names both COMPILES AND LINKS against -lSceNet_stub. Checked with a compile+link, not by
 * reading documentation, because header declarations are not link-time symbols.
 *
 * inet_aton() in particular exists on both, which was the specific landmine flagged when the core was
 * first compiled on a Linux host - there it needs _DEFAULT_SOURCE, being a BSD extension rather than
 * C99, and glibc hides it by default. Neither console libc does.
 *
 * So there is no socket seam and this header stays four functions. What has NOT been established:
 *
 *   - Whether sceNetInit()'s memory pool must be brought up before the POSIX names work. Almost
 *     certainly yes (the 3DS's socInit() is the same shape), and it is each port's own bring-up file's
 *     job - rc_soc.c on 3DS - not this seam's.
 *   - Whether bind() to port 0 is accepted. The 3DS SOC service rejects it outright, which is correct
 *     on .NET and on Unix and cost a hardware run to find. No documentation was found either way for
 *     the Vita. Assume nothing until a device answers.
 *
 * The lesson the 3DS taught still stands: socket idioms are exactly where a second platform bites, and
 * "it compiles" is not "it works".
 */

#endif /* RC_PLATFORM_H */
