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

#include "platform/rc_platform.h"

/*
 * [X] The PPE time base runs at 79,800,000 Hz - the documented figure for every retail PS3, derived
 * from the 3.2 GHz core clock divided by 40. It is the one number in this file that a wrong value
 * would corrupt silently rather than loudly: every timeout in the core would scale by the ratio, so a
 * 5-second handshake deadline could become 4 or 6 and look like a flaky console rather than a bug here.
 *
 * VERIFY FIRST, AND IT IS CHEAP: sleep a known wall-clock interval, read rc_tick() either side, and
 * divide. source/app/main.c does exactly that and prints the result, which is most of the reason that
 * program exists. If the printed figure is not within a percent or two of the constant below, fix the
 * constant before trusting a single timing-dependent line anywhere else in this port.
 */
#define RC_PS3_TIMEBASE_HZ 79800000ULL

/*
 * The time base, read with one instruction.
 *
 * On 64-bit PowerPC `mftb` yields the whole 64-bit counter in a single move, so none of the
 * read-high/read-low/re-read-high dance that 32-bit PowerPC needs applies - and the PPE runs this port
 * as 64-bit code. Writing that loop anyway would not be harmless caution: it would suggest to the next
 * reader that the hazard exists here, which is a worse defect than the two instructions it saves.
 *
 * At 79.8 MHz a 64-bit counter wraps after roughly seven thousand years, so no wrap handling is needed
 * and none is written.
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
    return rc_ps3_timebase() / (RC_PS3_TIMEBASE_HZ / 1000ULL);
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
    return RC_PS3_TIMEBASE_HZ;
}

/*
 * rc_random_bytes() IS NOT IMPLEMENTED HERE, AND MUST NOT BE STUBBED TO GET A BUILD.
 *
 * This port does not yet know, with confidence worth acting on, which call the PS3 exposes for
 * cryptographically secure bytes. The candidate recorded in README.md is an lv2 random-number syscall,
 * marked [X] there for the same reason it is unimplemented here. The earlier note in that file also
 * offered /dev/urandom as a fallback; that is withdrawn rather than carried forward, because whether
 * this platform's libc exposes such a device was never checked and a fallback nobody has confirmed is
 * exactly the kind of comfort this seam must not offer.
 *
 * So the port does not link once anything asks for key material, and that is the correct failure. Two
 * callers need real entropy and neither shows a symptom when it does not get it: the handshake key that
 * authenticates the ECDH exchange, and the ephemeral ECDH private scalar. A session keyed from a
 * counter connects, plays, and looks exactly like a working one. ports/common/util/rc_random.h makes
 * the argument in full; ports/ripcord-vita took precisely this position for precisely this reason, and
 * it is why the smoke test in source/app/main.c exercises the clock and nothing cryptographic.
 *
 * When the call is confirmed against real headers it belongs in its own source/platform/rc_random_ps3.c
 * - not appended here - so that the argument above stays in a file about entropy rather than being
 * buried in one about clocks.
 */
