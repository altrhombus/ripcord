/*
 * ripcord-vita - the platform seam, vitasdk side.
 *
 * Everything the portable core (ports/common) needs from an OS, for the PS Vita. See
 * ports/common/platform/rc_platform.h for what each function promises.
 *
 * *** NOTHING IN THIS FILE HAS EVER BEEN COMPILED. ***
 *
 * It was written against vitasdk's published headers and documentation, on a machine with no vitasdk
 * installed, and every function below is therefore [X] - assumed, never confirmed. That marking is not
 * decoration: the 3DS port's first socket bug was an idiom that is correct on .NET, correct on Unix,
 * and rejected outright by the 3DS SOC service, and it cost a hardware run to find. Assume this file
 * has at least one of those in it.
 *
 * The first job on a real vitasdk install is to compile this and check each function against the
 * headers, not to wire it into a session.
 */

#include <psp2/kernel/processmgr.h>
#include <psp2/kernel/threadmgr.h>

#include "rc_platform.h"

uint64_t rc_time_ms(void)
{
    /* [X] sceKernelGetProcessTimeWide() returns MICROseconds since process start as a uint64_t.
     * Process-relative is fine - the seam promises only that differences are meaningful - and it
     * cannot step backwards the way a wall clock can.
     *
     * VERIFY: the name and the unit. vitasdk also exposes sceKernelGetSystemTimeWide(); if the process
     * variant is missing or is not microseconds, this is silently wrong by a factor of 1000, and the
     * symptom is every timeout in the core firing either instantly or never. */
    return sceKernelGetProcessTimeWide() / 1000ULL;
}

void rc_sleep_ms(uint32_t ms)
{
    /* [X] sceKernelDelayThread takes MICROseconds (the 3DS's svcSleepThread took nanoseconds - the
     * exact units trap this seam exists to contain). */
    sceKernelDelayThread(ms * 1000u);
}

uint64_t rc_tick(void)
{
    /* [X] Microseconds, same source as rc_time_ms but undivided. The Vita has no direct equivalent of
     * the 3DS's svcGetSystemTick (a raw 268 MHz counter), and it does not need one: the two callers
     * are profiling, which is happy with microseconds, and the SCTP association tag, which needs
     * "probably different from last time" rather than resolution. */
    return sceKernelGetProcessTimeWide();
}

uint64_t rc_tick_hz(void)
{
    return 1000000ULL; /* rc_tick() is microseconds here. */
}

/*
 * rc_random_bytes() IS NOT IMPLEMENTED HERE YET, and must not be stubbed.
 *
 * The 3DS reaches libctru's PS_GenerateRandomBytes. The Vita equivalent is likely
 * sceKernelGetRandomNumber() (psp2/kernel/rng.h) [X], but "likely" is not good enough for the function
 * that produces the ECDH private key and the handshake nonce. Until it is confirmed against real
 * headers, this port does not link, which is the correct failure: a port that builds and quietly
 * generates predictable key material is far worse than one that does not build.
 *
 * See ports/ripcord-3ds/source/util/rc_random.c for the argument in full - it is the same argument.
 */
