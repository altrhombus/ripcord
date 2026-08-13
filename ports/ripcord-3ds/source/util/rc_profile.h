/*
 * ripcord-3ds - a stage timer, and a probe for which CPU cores this port may use.
 *
 * WHY BOTH LIVE IN ONE FILE. They answer the same question from two sides: what does a frame cost, and
 * how many cores are there to spend it on. Phase 1 measured the control plane at ~25 us per field and
 * said in as many words that a real per-packet A/V benchmark "is still owed before treating software AES
 * on the A/V path as settled". It stayed owed through every phase since, and the Phase 6d resolution
 * probe made it load-bearing: the console will only ever send 640x360, MVD will not scale, so a
 * per-frame resample is now a permanent fixed cost sitting on the same core as the packet crypto and the
 * decoder feed. Nobody has measured any of those three.
 *
 * ON MEASURING WITH svcGetSystemTick. It counts at SYSCLOCK_ARM11 regardless of whether the New 3DS is
 * running at its higher clock, so CPU_TICKS_PER_USEC converts honestly either way. Reading it is cheap
 * (a coprocessor read, no syscall), which matters when the thing being timed is a few microseconds -
 * a timer that costs as much as the work would report nonsense.
 *
 * ON THE CORE PROBE. libctru documents the rules but not what a .3dsx actually gets: core 0 is always
 * available, core 1 needs APT_SetAppCpuTimeLimit, core 2 is New3DS-only and requires exheader kernel
 * flag 0x2000 - which for homebrew launched through the Homebrew Launcher means whatever the HOST
 * application carries, not anything this code can declare. So it has to be tried rather than reasoned
 * about, and the answer decides whether decode can ever move off the network thread.
 *
 * That decision has a known cost already recorded in the tree: fec_reed_solomon_decode keeps 12.6 KB of
 * scratch static specifically because this port has no threads. Adding one means that function needs
 * caller-supplied scratch. Better to know the constraint before the media pipeline grows around it.
 */
#ifndef RC_PROFILE_H
#define RC_PROFILE_H

#include <stddef.h>
#include <stdint.h>

/* The stages worth separating. Each is a distinct suspect in a "why is this too slow" conversation. */
typedef enum {
    RC_STAGE_GMAC_VERIFY = 0, /* per A/V packet: the 4-byte tag over the whole packet */
    RC_STAGE_DEMUX,           /* per A/V packet: decrypt + framing + reassembly + FEC */
    RC_STAGE_MVD_FEED,        /* per NAL unit: mvdstdProcessVideoFrame */
    RC_STAGE_MVD_RENDER,      /* per frame: mvdstdRenderVideoFrame */
    RC_STAGE_SCALE,           /* per frame: the ARM11 resample into the framebuffer */
    RC_STAGE_COUNT
} rc_stage;

typedef struct {
    uint64_t ticks[RC_STAGE_COUNT];
    uint32_t calls[RC_STAGE_COUNT];
} rc_profile;

void rc_profile_reset(rc_profile *p);

/* Returns the current tick, to be passed to rc_profile_stop. Cheap enough to call per packet. */
uint64_t rc_profile_start(void);
void rc_profile_stop(rc_profile *p, rc_stage stage, uint64_t started);

/* Logs a table: total ms, call count, and microseconds per call for each stage. */
void rc_profile_report(const rc_profile *p, unsigned window_ms);

/*
 * Reports which cores threadCreate accepts, having actually tried each one. Logs the result; returns a
 * bitmask of usable cores (bit N = core N).
 */
unsigned rc_profile_probe_cores(void);

#endif /* RC_PROFILE_H */
