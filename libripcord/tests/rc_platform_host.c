/*
 * libripcord - the platform seam, host side.
 *
 * Plain C on whatever machine runs the tests: the clock, sleep and tick of rc_platform.h over
 * clock_gettime and nanosleep. It is what lets the host build notice if a "portable" file grows a
 * dependency on a console.
 *
 * tests/Makefile links it into every runner that reaches a clock or a socket on loopback
 * (regist_flow_test, stun_test, dgram_test, wan_test, client_test, control_pipe_test,
 * rendezvous_test) and into the fuzz archive.
 *
 * rc_random_bytes() is deliberately ABSENT. There is no host CSPRNG here on purpose: the ports refuse
 * to fall back to a non-cryptographic PRNG (util/rc_random.h says why), and offering a host one would
 * make "built for the host" quietly mean "built with fake key material". A host test that needs
 * randomness defines its own rc_random_bytes rather than sourcing one.
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
