/* See rc_profile.h for why these two live together and what they are meant to settle. */

#include "rc_profile.h"

#include "util/rc_log.h"

#include <3ds.h>

#include <string.h>

/*
 * MEASURED, NOT ASSUMED. svcGetSystemTick does not count at SYSCLOCK_ARM11 regardless of clock:
 * osSetSpeedupEnable(true) puts the ARM11 on SYSCLOCK_ARM11_LGR2, which os.h defines as exactly
 * SYSCLOCK_ARM11 * 3, and the tick counter goes with it - while libctru's CPU_TICKS_PER_USEC is fixed to
 * the base clock. Uncalibrated, every reported figure was 3x too large.
 *
 * Timing a known sleep settles it on whatever hardware is actually running, at any clock, with no model
 * detection. The sleep is long enough that scheduler jitter is noise and short enough to be invisible at
 * startup. If it returns something implausible the base clock is kept, because a wrong calibration is
 * worse than a wrong constant.
 */
static double s_ticks_per_usec;
static int s_scale_threaded;

void rc_profile_set_scale_threaded(int threaded)
{
    s_scale_threaded = threaded;
}

void rc_profile_calibrate(void)
{
    const s64 sleep_ns = 100000000ll; /* 100 ms */
    u64 before, after;
    double measured;

    before = svcGetSystemTick();
    svcSleepThread(sleep_ns);
    after = svcGetSystemTick();

    measured = (double)(after - before) / ((double)sleep_ns / 1000.0);
    if (measured > CPU_TICKS_PER_USEC * 0.5 && measured < CPU_TICKS_PER_USEC * 6.0)
        s_ticks_per_usec = measured;
    else
        s_ticks_per_usec = CPU_TICKS_PER_USEC;

    rc_log("timer: %.1f ticks/us measured (libctru constant says %.1f)\n",
        s_ticks_per_usec, (double)CPU_TICKS_PER_USEC);
}

void rc_profile_reset(rc_profile *p)
{
    if (p != NULL)
        memset(p, 0, sizeof(*p));
}

uint64_t rc_profile_start(void)
{
    return svcGetSystemTick();
}

void rc_profile_stop(rc_profile *p, rc_stage stage, uint64_t started)
{
    uint64_t elapsed;

    if (p == NULL || stage >= RC_STAGE_COUNT)
        return;
    elapsed = svcGetSystemTick() - started;
    p->ticks[stage] += elapsed;
    p->calls[stage]++;

    /*
     * These run inside the demux callback; record them so demux can subtract its own children. SCALE is
     * in the list because the blit runs inside rc_mvd_decode_frame: without it the scaling was counted
     * twice - demux reported 2684 us/call against ~750, and the receive core read 64% busy instead of
     * ~27%.
     */
    if (stage == RC_STAGE_MVD_FEED || stage == RC_STAGE_MVD_RENDER || stage == RC_STAGE_SCALE)
        p->inner += elapsed;
}

void rc_profile_stop_nested(rc_profile *p, rc_stage stage, uint64_t started, uint64_t inner_at_start)
{
    uint64_t elapsed, nested;

    if (p == NULL || stage >= RC_STAGE_COUNT)
        return;
    elapsed = svcGetSystemTick() - started;
    nested = p->inner - inner_at_start;
    p->ticks[stage] += (elapsed > nested) ? (elapsed - nested) : 0u;
    p->calls[stage]++;
}

static const char *stage_name(rc_stage stage)
{
    switch (stage) {
    case RC_STAGE_GMAC_VERIFY: return "GMAC verify   (per packet)";
    case RC_STAGE_DEMUX:       return "demux+decrypt (per packet)";
    case RC_STAGE_MVD_FEED:    return "MVD feed      (per NAL)   ";
    case RC_STAGE_MVD_RENDER:  return "MVD render    (per frame) ";
    case RC_STAGE_SCALE:       return "ARM11 scale   (per frame) ";
    default:                   return "?";
    }
}

void rc_profile_report(const rc_profile *p, unsigned window_ms)
{
    unsigned long total_us = 0;
    int i;

    if (p == NULL)
        return;

    rc_log("\nCPU profile over %u ms:\n", window_ms);
    rc_log("  stage                        calls    total ms    us/call   (exclusive)\n");

    for (i = 0; i < RC_STAGE_COUNT; i++) {
        double us = (double)p->ticks[i]
            / (s_ticks_per_usec > 0.0 ? s_ticks_per_usec : (double)CPU_TICKS_PER_USEC);
        unsigned long us_total = (unsigned long)us;
        unsigned long per_call = p->calls[i] ? (unsigned long)(us / (double)p->calls[i]) : 0ul;

        total_us += us_total;
        rc_log("  %s %7u   %7lu.%01lu   %7lu\n", stage_name((rc_stage)i), (unsigned)p->calls[i],
            us_total / 1000ul, (us_total % 1000ul) / 100ul, per_call);
    }

    /*
     * THE HEADLINE IS PER-CORE, because the stages no longer share one. Adding every stage up as "% of
     * one core" reported 74% once the scale moved to core 2, when core 0 - the core that actually drains
     * the socket - was at 47%. The receive loop's headroom is the number worth knowing, so it gets its
     * own line.
     */
    if (window_ms > 0) {
        unsigned long recv_us = 0;
        int i2;

        for (i2 = 0; i2 < RC_STAGE_COUNT; i2++) {
            if (i2 != RC_STAGE_SCALE || !s_scale_threaded)
                recv_us += (unsigned long)((double)p->ticks[i2]
                    / (s_ticks_per_usec > 0.0 ? s_ticks_per_usec : (double)CPU_TICKS_PER_USEC));
        }
        rc_log("  ---- receive core: %lu.%03lu s = %lu%% busy   (all stages: %lu.%03lu s)\n",
            recv_us / 1000000ul, (recv_us / 1000ul) % 1000ul,
            (unsigned long)((recv_us / 10ul) / (unsigned long)window_ms),
            total_us / 1000000ul, (total_us / 1000ul) % 1000ul);
        rc_log("  ---- scale:        %lu.%03lu s = %lu%% of a core   (%s)\n",
            (total_us - recv_us) / 1000000ul, ((total_us - recv_us) / 1000ul) % 1000ul,
            (unsigned long)(((total_us - recv_us) / 10ul) / (unsigned long)window_ms),
            s_scale_threaded ? "on a spare core" : "\x1b[33mon the receive core, included above\x1b[0m");
    }
}

/* A thread that does nothing: we only care whether it could be created on the requested core. */
static void probe_thread(void *arg)
{
    (void)arg;
}

unsigned rc_profile_probe_cores(void)
{
    unsigned mask = 0;
    int core;

    rc_log("CPU cores available to this process:\n");

    for (core = 0; core < 4; core++) {
        Thread t = threadCreate(probe_thread, NULL, 4 * 1024, 0x30, core, true);

        if (t != NULL) {
            mask |= (1u << core);
            rc_log("  core %d: \x1b[32mavailable\x1b[0m\n", core);
            /* Detached: it exits immediately and frees itself. */
        } else {
            rc_log("  core %d: no\n", core);
        }
    }

    /*
     * Core 1 is the system core and normally needs APT_SetAppCpuTimeLimit before a thread will take. It
     * is worth reporting the retry separately from the first attempt, because "available only after
     * asking" is a different design input from "available".
     */
    if ((mask & 0x2u) == 0u) {
        if (R_SUCCEEDED(APT_SetAppCpuTimeLimit(30))) {
            Thread t = threadCreate(probe_thread, NULL, 4 * 1024, 0x30, 1, true);
            if (t != NULL) {
                mask |= 0x2u;
                rc_log("  core 1: available after APT_SetAppCpuTimeLimit(30%%)\n");
            }
        }
    }

    if ((mask & ~1u) == 0u) {
        rc_log("  -> single core only. Decode, packet crypto and the frame scale all share core 0,\n");
        rc_log("     which is also the network receive loop.\n");
    }
    return mask;
}
