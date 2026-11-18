/*
 * ripcord-3ds - the platform seam, libctru side.
 *
 * Everything the portable core (libripcord) needs from an OS, for the 3DS. See
 * libripcord/platform/rc_platform.h for what each function promises; this file only says how libctru
 * delivers it.
 *
 * rc_random_bytes() is NOT here - it lives in source/util/rc_random.c, and libripcord/util/rc_random.h
 * carries the argument for why the port refuses to fall back to a non-cryptographic PRNG.
 */

#include <3ds.h>

#include "platform/rc_platform.h"

uint64_t rc_time_ms(void)
{
    /* osGetTime() is already milliseconds, and already monotonic for our purposes. */
    return osGetTime();
}

void rc_sleep_ms(uint32_t ms)
{
    /* svcSleepThread takes NANOseconds as s64; the conversion is done once, here. */
    svcSleepThread((s64)ms * 1000000LL);
}

uint64_t rc_tick(void)
{
    return svcGetSystemTick();
}

uint64_t rc_tick_hz(void)
{
    /* SYSCLOCK_ARM11 is 268111856 Hz - the ARM11 system tick, which is what svcGetSystemTick counts.
     * Note this is the *base* clock and does not change when osSetSpeedupEnable(true) raises the CPU to
     * 804 MHz on New 3DS hardware; the tick source is independent of the core clock. */
    return SYSCLOCK_ARM11;
}
