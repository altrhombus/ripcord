/*
 * ripcord ports - the platform seam, host side.
 *
 * Plain C on whatever machine runs the known-answer tests. This exists so the seam has three
 * implementations rather than two, which is the difference between a seam and a rename: a header with
 * one caller and one implementation is just indirection, and the host build is the one that will notice
 * if a "portable" file quietly grows a dependency on a console.
 *
 * Nothing in the test suite links this today - the three seamed files (net/rc_tcp.c,
 * session/halyard_control_session.c, takion/takion_reliable_channel.c) all own sockets, and the suite
 * deliberately stops at the socket boundary (see tests/Makefile). It is here so that when someone
 * writes a loopback test for the reliable channel - the one protocol file with retransmit/SACK timing
 * and no host test at all - the platform half is already waiting rather than being invented under
 * deadline.
 *
 * rc_random_bytes() is deliberately ABSENT. There is no host CSPRNG here on purpose: the 3DS port
 * refuses to fall back to a non-cryptographic PRNG (source/util/rc_random.c says why), and offering a
 * host one would make "built for the host" quietly mean "built with fake key material". A host test
 * that needs randomness should inject it, not source it.
 */

/* clock_gettime/CLOCK_MONOTONIC are POSIX.1b, not C99, and the suite compiles with -std=c99 (not gnu99)
 * deliberately. Without this the compiler silently takes `struct timespec` as an unknown type. */
#define _POSIX_C_SOURCE 199309L

#include <stddef.h>
#include <time.h>

#include "rc_platform.h"

uint64_t rc_time_ms(void)
{
    struct timespec ts;
    /* CLOCK_MONOTONIC, not CLOCK_REALTIME: the seam promises a clock that does not go backwards, and
     * every caller does unsigned `now - start` arithmetic that turns a backwards step into a very long
     * loop rather than an early timeout. An NTP correction would be enough to trigger it. */
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (uint64_t)ts.tv_sec * 1000ULL + (uint64_t)(ts.tv_nsec / 1000000L);
}

void rc_sleep_ms(uint32_t ms)
{
    struct timespec ts;
    ts.tv_sec = (time_t)(ms / 1000u);
    ts.tv_nsec = (long)(ms % 1000u) * 1000000L;
    nanosleep(&ts, NULL);
}

uint64_t rc_tick(void)
{
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (uint64_t)ts.tv_sec * 1000000000ULL + (uint64_t)ts.tv_nsec;
}

uint64_t rc_tick_hz(void)
{
    return 1000000000ULL; /* rc_tick() is nanoseconds here. */
}
