/*
 * ripcord-ps3 - platform bring-up: does the toolchain work, and is the clock what we think it is.
 *
 * This is step 4 of the port's order of work, and it is deliberately the cheapest program that can fail
 * informatively. It links the portable core's platform seam against PSL1GHT, prints a timestamp, and
 * then does the one measurement that everything timing-dependent in this port rests on.
 *
 * *** NEVER CROSS-COMPILED, NEVER RUN ON A CONSOLE. *** No PSL1GHT here and no PS3, so everything
 * SDK-facing is [X]. It has been built and run on a host, which is a narrower claim - see the end of
 * this comment for exactly how much narrower.
 *
 * WHY IT MEASURES THE CLOCK RATHER THAN JUST PRINTING ONE.
 *
 * rc_platform_ps3.c carries a single magic number - the PPE time base at 79.8 MHz - and a wrong value
 * there does not crash anything. It scales every timeout in the core by the ratio, so a handshake
 * deadline quietly becomes 20% short and the console looks flaky. That class of fault is expensive to
 * find later and trivial to find now: sleep a known interval, count ticks, divide.
 *
 * WHAT THE MEASUREMENT CAN AND CANNOT TELL YOU, which matters because it is easy to over-read.
 *
 * The two things under test are independent of each other - sysUsleep's units come from the SDK, the
 * tick comes from the CPU - so timing one with the other genuinely cross-checks both. But it checks
 * their CONJUNCTION: if the measured frequency comes out a thousand times wrong, that is either a
 * microsecond/millisecond mix-up in the sleep or a wrong constant, and this program cannot say which.
 *
 * Disambiguating needs no equipment beyond a wall clock. The run below sleeps for a total of about five
 * seconds. If it finishes in roughly five seconds, sysUsleep is right and any discrepancy is the
 * constant; if it finishes instantly or takes an hour and a half, the sleep is what is wrong. Look at a
 * clock while it runs - that is the entire protocol, and it settles the question in one boot.
 *
 * OUTPUT GOES TO A FILE, following both other ports. Results you can copy off the drive beat a
 * photograph of a television that you then retype.
 *
 * WHAT HAS ACTUALLY BEEN RUN, since "never built" above is about the PS3 and this file is not only a
 * PS3 file. Everything below touches the platform seam and the logger and nothing else, so it compiles
 * and runs on a host against a stand-in seam - which was done: clean at /W4, five seconds wall time
 * against the five this comment claims, and the calibration arithmetic and every format string
 * exercised. The FAIL branch was confirmed by feeding it a frequency wrong by a factor of a thousand,
 * because a check that has never failed is not yet known to be a check.
 *
 * That says nothing about rc_platform_ps3.c, whose mftb, sysUsleep and 79.8 MHz remain [X] - the host
 * run swapped all three out. It says the program around them is sound, which is the half that can be
 * settled without a console.
 */

#include "platform/rc_platform.h"
#include "util/rc_log.h"

#include <stdint.h>

/* The sleep used for calibration. Long enough that the sleep's own wake-up jitter - a scheduler
 * granularity of a millisecond or two - is a rounding error rather than the measurement, and short
 * enough that nobody minds running it. */
#define CALIBRATE_MS 1000u

static int measure_timebase(void)
{
    uint64_t t0, t1, ticks, measured_hz, claimed_hz;
    int failures = 0;

    claimed_hz = rc_tick_hz();

    t0 = rc_tick();
    rc_sleep_ms(CALIBRATE_MS);
    t1 = rc_tick();

    if (t1 <= t0) {
        /* Either the tick does not advance at all, or it went backwards - and the second would be
         * worse, because rc_time_ms() is derived from this same counter and the core's timeout loops
         * are written assuming it cannot. */
        rc_log("FAIL  tick did not advance across a %u ms sleep (%llu -> %llu)\n",
               CALIBRATE_MS, (unsigned long long)t0, (unsigned long long)t1);
        return 1;
    }

    ticks = t1 - t0;
    measured_hz = ticks * 1000ULL / CALIBRATE_MS;

    rc_log("tick:  %llu ticks across a %u ms sleep\n", (unsigned long long)ticks, CALIBRATE_MS);
    rc_log("       measured ~%llu Hz, rc_tick_hz() claims %llu Hz\n",
           (unsigned long long)measured_hz, (unsigned long long)claimed_hz);

    /* A tenth is a deliberately loose bound. It is not trying to certify the constant to the last
     * digit - it is trying to catch the failures that actually happen here, which are a wrong unit or
     * a wrong divisor, and those are out by factors of a thousand or of forty, not by percent. */
    if (measured_hz > claimed_hz + claimed_hz / 10ULL
        || measured_hz < claimed_hz - claimed_hz / 10ULL) {
        rc_log("FAIL  that is not within 10%% - fix RC_PS3_TIMEBASE_HZ, or the sleep's units\n");
        rc_log("      (check a wall clock: this program sleeps about five seconds in total)\n");
        failures++;
    } else {
        rc_log("ok    time base agrees with rc_platform_ps3.c\n");
    }

    return failures;
}

static int check_monotonic(void)
{
    uint64_t previous;
    unsigned i;
    int failures = 0;

    /* rc_time_ms() must never step backwards - see rc_platform.h. On this port it is derived from the
     * time base rather than from a wall clock precisely so that it cannot, which makes this check a
     * test of that reasoning rather than of the system clock. It is cheap, so it runs anyway: the
     * reasoning is only worth what the implementation actually does. */
    previous = rc_time_ms();
    for (i = 0u; i < 4u; i++) {
        uint64_t now;
        rc_sleep_ms(1000u);
        now = rc_time_ms();
        if (now < previous) {
            rc_log("FAIL  rc_time_ms went backwards: %llu then %llu\n",
                   (unsigned long long)previous, (unsigned long long)now);
            failures++;
        }
        previous = now;
    }

    if (failures == 0) {
        rc_log("ok    rc_time_ms advanced monotonically across 4 seconds, now %llu ms\n",
               (unsigned long long)previous);
    }
    return failures;
}

int main(void)
{
    int failures = 0;

    /* No argv[0] worth trusting here, so rc_program_dir falls back to RC_PROGRAM_DIR_FALLBACK, which
     * the Makefile defines. [X] That directory must already exist - nothing below creates it, and the
     * only symptom of getting the path wrong is a log file that silently never appears. If this program
     * seems to do nothing at all, suspect the path before suspecting the code. */
    rc_log_open(NULL, "ps3-bringup.log");

    rc_log("ripcord-ps3 platform bring-up\n");
    rc_log("=============================\n\n");

    /* The timestamp step 4 promised. It is also the first thing that proves the seam linked at all. */
    rc_log("boot:  rc_time_ms() = %llu ms since power-on\n", (unsigned long long)rc_time_ms());
    rc_log("       rc_tick()    = %llu\n\n", (unsigned long long)rc_tick());

    failures += measure_timebase();
    failures += check_monotonic();

    /* Nothing cryptographic runs here, and its absence is deliberate rather than an omission:
     * rc_random_bytes() is not implemented on this port yet, and rc_platform_ps3.c says why refusing to
     * stub it is the right call. A bring-up program that fabricated entropy to look complete would be
     * testing the fabrication. */
    rc_log("\nnot covered here: CSPRNG, sockets, threads, decode. See README.md.\n");
    rc_log("%s\n", failures == 0 ? "all checks passed" : "CHECKS FAILED");
    rc_log_close();

    return failures == 0 ? 0 : 1;
}
