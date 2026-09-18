/*
 * ripcord-ps3 - the platform seam, PSL1GHT side.
 *
 * Everything the portable core (ports/common) needs from an OS, for the PlayStation 3. See
 * ports/common/platform/rc_platform.h for what each function promises; this file only says how the PPE
 * delivers it.
 *
 * *** NOTHING IN THIS FILE HAS EVER BEEN COMPILED. ***
 *
 * There is no PSL1GHT installation on the machine this was written on and no PS3 to run it against, so
 * every SDK-facing line below is [X] - assumed, never confirmed. That marking is not decoration. The
 * 3DS port's first socket bug was an idiom that is correct on .NET, correct on Unix, and rejected
 * outright by the 3DS SOC service, and it cost a hardware run to find. Assume this file has one of
 * those in it, and see the VERIFY notes for where to look first.
 *
 * THE SDK SURFACE IS DELIBERATELY ALMOST EMPTY, AND THAT IS THE DESIGN.
 *
 * Three of the four functions here read the PowerPC time base with one instruction. `mftb` is an
 * architectural fact about the CPU rather than a name in somebody's header, so it cannot be wrong in
 * the way an SDK call can: there is no wrapper to misremember and no units to invert. The whole file's
 * exposure to PSL1GHT is therefore one sleep call and one frequency constant, which is a much smaller
 * surface to be wrong about than four independent SDK calls would have been.
 *
 * ONE CLOCK, NOT TWO - which is what makes the seam's monotonicity promise structural here.
 *
 * rc_platform.h is explicit that rc_time_ms() must never go backwards, because the core is written as
 * `while (rc_time_ms() - start <= timeout)` and on unsigned arithmetic a backwards clock turns that
 * into a near-infinite loop rather than an early exit. A wall clock cannot promise that: the PS3 has a
 * user-settable date and an internet time sync, and both step it. So rc_time_ms() is derived from the
 * same time base rc_tick() reads, which counts up from power-on and has nothing that can move it. The
 * cost is one division; the benefit is that the property the core depends on is a property of the
 * hardware rather than a hope about the system clock.
 */

#include <stdint.h>

#include <sys/systime.h>
#include <sysutil/sysutil.h>

#include "platform/rc_platform.h"

#include "rc_log.h"
#include "rc_platform_ps3.h"

/*
 * THE TIME BASE FREQUENCY IS ASKED FOR, NOT ASSUMED - which is a change from how this file was first
 * written, and the reason is worth recording.
 *
 * The original version hard-coded 79,800,000 Hz, the documented figure for every retail PS3 (3.2 GHz
 * divided by 40), and carried a long warning that it was the one number here a wrong value would
 * corrupt silently: every timeout in the core scales by the ratio, so a 5-second handshake deadline
 * becomes 4 or 6 and the console reads as flaky rather than as a bug in this file. All of that is still
 * true. What changed is that the warning was unnecessary, because lv2 will simply tell us:
 * sysGetTimebaseFrequency() is syscall 147, declared in <sys/systime.h>, returning the rate as a u64.
 * It was found by reading the SDK's own header while confirming something else.
 *
 * So the constant below is no longer the source of truth. It is the EXPECTED value, kept for two jobs:
 * as the fallback if the syscall ever returns something unusable, and as the figure source/app/main.c
 * cross-checks the kernel's answer against.
 *
 * This also sharpens the bring-up program. Its own comment worried that measuring the tick with a sleep
 * tests the CONJUNCTION of two unknowns - sysUsleep's units and the frequency - and cannot say which is
 * wrong. With the frequency coming from the kernel, that stops being true: an authoritative frequency
 * plus a measured one isolates the sleep. See main.c, which now says so.
 *
 * [X] The syscall number and the unit are the SDK's, not ours, and neither has been run on a console.
 */
#define RC_PS3_TIMEBASE_HZ_EXPECTED 79800000ULL

/*
 * Cached because rc_time_ms() calls this on every timeout check in the core, and a syscall per check is
 * a poor trade for a value that cannot change while the machine is on. Not thread-safe by construction,
 * and it does not need to be: the worst a race can do is make two threads each perform the same syscall
 * and store the same answer.
 */
static uint64_t rc_ps3_timebase_hz(void)
{
    static uint64_t cached;

    if (cached == 0ULL) {
        uint64_t hz = sysGetTimebaseFrequency();

        /* A frequency under 1 kHz is not a slow clock, it is a failed call - and rc_time_ms() divides
         * by hz/1000, so taking it at face value would divide by zero. Fall back rather than trust it.
         * Anything else, including a figure that disagrees with the expected one, is believed: the
         * kernel knows this machine's clock and this file does not. main.c reports the disagreement. */
        cached = (hz >= 1000ULL) ? hz : RC_PS3_TIMEBASE_HZ_EXPECTED;
    }
    return cached;
}

/*
 * The time base, read with one instruction.
 *
 * On 64-bit PowerPC `mftb` yields the whole 64-bit counter in a single move, so none of the
 * read-high/read-low/re-read-high dance that 32-bit PowerPC needs applies - and the PPE runs this port
 * as 64-bit code. Writing that loop anyway would not be harmless caution: it would suggest to the next
 * reader that the hazard exists here, which is a worse defect than the two instructions it saves.
 *
 * At 79.8 MHz a 64-bit counter wraps after roughly seven thousand years, so no wrap handling is needed
 * and none is written. That conclusion survives any plausible correction to the frequency.
 */
static inline uint64_t rc_ps3_timebase(void)
{
    uint64_t tb;
    __asm__ __volatile__("mftb %0" : "=r"(tb));
    return tb;
}

uint64_t rc_time_ms(void)
{
    /* Milliseconds since power-on. The epoch is unspecified by the seam and only differences are
     * meaningful, so counting from an arbitrary point costs nothing and buys monotonicity outright. */
    return rc_ps3_timebase() / (rc_ps3_timebase_hz() / 1000ULL);
}

void rc_sleep_ms(uint32_t ms)
{
    /* [X] sysUsleep() takes MICROseconds. This is the units trap the seam exists to contain - libctru's
     * svcSleepThread wanted nanoseconds and the three original call sites each wrote the conversion out
     * by hand - so the multiply happens once, here, and nowhere else in the port.
     *
     * VERIFY: the name and the unit together. If sysUsleep is absent under this spelling, the lv2 call
     * beneath it is sys_timer_usleep, taking the same microseconds. A wrong unit here does not fail
     * loudly: a factor of a thousand shows up as a port that either spins or hangs, and both look like
     * a network fault from the outside. main.c times a one-second sleep for this reason. */
    sysUsleep(ms * 1000u);
}

uint64_t rc_tick(void)
{
    return rc_ps3_timebase();
}

uint64_t rc_tick_hz(void)
{
    return rc_ps3_timebase_hz();
}

uint64_t rc_ps3_timebase_hz_expected(void)
{
    /* Not the seam - see rc_platform_ps3.h. This is here so main.c can cross-check lv2's answer without
     * a second copy of the number existing anywhere. */
    return RC_PS3_TIMEBASE_HZ_EXPECTED;
}

/*
 * rc_random_bytes() IS NOT HERE, AND THAT IS STILL DELIBERATE - but the reason has changed.
 *
 * It used to be absent because this port did not know which call the PS3 exposes for cryptographically
 * secure bytes, and a seam whose failure is invisible must refuse to link rather than be stubbed. The
 * call was confirmed on 2026-09-11 - sysGetRandomNumber, from sysPrxForUser - so it is now implemented,
 * in source/platform/rc_random_ps3.c.
 *
 * It lives there rather than here for the reason this comment gave when it was still a refusal: the
 * argument about why entropy fails silently belongs in a file about entropy, not buried in one about
 * clocks. That file carries the argument, what was confirmed and how, and the two things about the call
 * that are still [X].
 */

/* See the header: an XMB quit that nothing answers is a force-termination, not a clean exit. */

static volatile int s_exit_requested;

static void ps3_sysutil_event(u64 status, u64 param, void *user)
{
    (void)param;
    (void)user;
    if (status == SYSUTIL_EXIT_GAME)
        s_exit_requested = 1;
}

void rc_ps3_exit_watch(void)
{
    s32 rc = sysUtilRegisterCallback(SYSUTIL_EVENT_SLOT1, ps3_sysutil_event, NULL);

    /*
     * Reported rather than checked: a console that will not let us hear about the quit still runs, it
     * just goes back to behaving the way every build before this one did. Saying so is what stops the
     * next person diagnosing the reboot from scratch.
     */
    if (rc != 0)
        rc_log("plat:  sysUtilRegisterCallback refused (0x%08X) - an XMB quit will not be clean\n",
               (unsigned)rc);
}

int rc_ps3_exit_requested(void)
{
    return s_exit_requested;
}
