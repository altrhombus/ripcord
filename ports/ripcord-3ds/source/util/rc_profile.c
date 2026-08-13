/* See rc_profile.h for why these two live together and what they are meant to settle. */

#include "rc_profile.h"

#include "rc_log.h"

#include <3ds.h>

#include <string.h>

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
    if (p == NULL || stage >= RC_STAGE_COUNT)
        return;
    p->ticks[stage] += svcGetSystemTick() - started;
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
    rc_log("  stage                        calls    total ms    us/call\n");

    for (i = 0; i < RC_STAGE_COUNT; i++) {
        double us = (double)p->ticks[i] / CPU_TICKS_PER_USEC;
        unsigned long us_total = (unsigned long)us;
        unsigned long per_call = p->calls[i] ? (unsigned long)(us / (double)p->calls[i]) : 0ul;

        total_us += us_total;
        rc_log("  %s %7u   %7lu.%01lu   %7lu\n", stage_name((rc_stage)i), (unsigned)p->calls[i],
            us_total / 1000ul, (us_total % 1000ul) / 100ul, per_call);
    }

    /*
     * The headline number. Anything approaching 100% means the frame pipeline is the constraint and no
     * amount of network tuning will help; well under it means the remaining loss is the link, not us.
     * Phase 2 already showed CPU contention on this core costs real UDP throughput, so this figure and
     * the packet-loss figure have to be read together.
     */
    if (window_ms > 0) {
        unsigned long pct = (unsigned long)((total_us / 10ul) / (unsigned long)window_ms);
        rc_log("  ---- measured work: %lu.%03lu s of %u.%03u s wall = %lu%% of one core\n",
            total_us / 1000000ul, (total_us / 1000ul) % 1000ul,
            window_ms / 1000u, window_ms % 1000u, pct);
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
