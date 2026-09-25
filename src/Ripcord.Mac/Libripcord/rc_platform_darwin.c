/*
 * ripcord for Mac - libripcord's platform seam (libripcord/platform/rc_platform.h) and its entropy
 * contract (libripcord/util/rc_random.h), on Darwin.
 *
 * This lives in the Mac tree rather than the core for the reason the seam header gives: nothing goes in
 * the core that only one platform needs, and every port supplies its own implementation of these.
 */
#include "rc_platform.h"
#include "../../../libripcord/util/rc_random.h"

#include <Security/SecRandom.h>
#include <string.h>
#include <time.h>

/*
 * CLOCK_MONOTONIC on Darwin keeps counting while the machine sleeps, where CLOCK_UPTIME_RAW stops. Every
 * caller measures a timeout as `rc_time_ms() - start`, and a connect attempt that spans a closed lid
 * should find its deadline passed when the lid opens, not resume a countdown that the console stopped
 * waiting for long ago.
 */
uint64_t rc_time_ms(void)
{
    return clock_gettime_nsec_np(CLOCK_MONOTONIC) / 1000000ULL;
}

void rc_sleep_ms(uint32_t ms)
{
    struct timespec ts;

    ts.tv_sec = (time_t)(ms / 1000u);
    ts.tv_nsec = (long)(ms % 1000u) * 1000000L;
    while (nanosleep(&ts, &ts) != 0) {
        /* Interrupted by a signal: sleep out the remainder, which nanosleep wrote back into ts. */
    }
}

/* Nanoseconds, from the clock that does not include sleep: this is for profiling a stretch of work,
 * and a sample that straddles a sleep is not a measurement of the work. */
uint64_t rc_tick(void)
{
    return clock_gettime_nsec_np(CLOCK_UPTIME_RAW);
}

uint64_t rc_tick_hz(void)
{
    return 1000000000ULL;
}

/* Nothing to bring up: SecRandomCopyBytes draws on the kernel's CSPRNG, which is always available. */
int rc_random_init(void)
{
    return 1;
}

void rc_random_exit(void)
{
}

int rc_random_bytes(uint8_t *out, size_t length)
{
    if (out == NULL)
        return 0;
    if (length == 0)
        return 1;
    if (SecRandomCopyBytes(kSecRandomDefault, length, out) != errSecSuccess) {
        /* Zeroed so a caller that ignores the result produces an obviously broken key; see rc_random.h. */
        memset(out, 0, length);
        return 0;
    }
    return 1;
}

/* The mbedtls-shaped adaptor: 0 on success, the inverse of rc_random_bytes. See rc_random.h. */
int rc_random_rng_callback(void *ctx, uint8_t *out, size_t length)
{
    (void)ctx;
    return rc_random_bytes(out, length) ? 0 : 1;
}
