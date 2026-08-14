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
 * ON MEASURING WITH svcGetSystemTick. Reading it is cheap (a coprocessor read, no syscall), which
 * matters when the thing being timed is a few microseconds - a timer that costs as much as the work
 * would report nonsense. But the RATE IS NOT THE LIBCTRU CONSTANT: this header previously claimed the
 * tick counts at SYSCLOCK_ARM11 whether or not the New 3DS is at its higher clock, and that is false.
 * osSetSpeedupEnable(true) triples the ARM11 clock (os.h: SYSCLOCK_ARM11_LGR2 == SYSCLOCK_ARM11 * 3)
 * and the tick with it, so every reported figure was 3x too large until rc_profile_calibrate landed.
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
    uint64_t inner;  /* running total of stages that nest inside another - see rc_profile_stop_nested */
} rc_profile;

/* Measures the real tick rate against a known sleep. Call once, after osSetSpeedupEnable. */
void rc_profile_calibrate(void);

/* Whether the scale runs on a spare core. Decides if its cost belongs to the receive core's total. */
void rc_profile_set_scale_threaded(int threaded);

void rc_profile_reset(rc_profile *p);

/* Returns the current tick, to be passed to rc_profile_stop. Cheap enough to call per packet. */
uint64_t rc_profile_start(void);
void rc_profile_stop(rc_profile *p, rc_stage stage, uint64_t started);

/*
 * For a stage that CONTAINS other timed stages. stream_demux_ingest invokes the video-frame callback
 * synchronously, so MVD feed and render both run inside the demux timer - which made the report add up
 * to 153% of one core on hardware and still nobody disbelieved it. Capture p->inner before the call and
 * pass it here; the nested time is subtracted so every row is exclusive and the total is meaningful.
 */
void rc_profile_stop_nested(rc_profile *p, rc_stage stage, uint64_t started, uint64_t inner_at_start);

/* Logs a table: total ms, call count, and microseconds per call for each stage. */
void rc_profile_report(const rc_profile *p, unsigned window_ms);

/*
 * Reports which cores threadCreate accepts, having actually tried each one. Logs the result; returns a
 * bitmask of usable cores (bit N = core N).
 */
unsigned rc_profile_probe_cores(void);

#endif /* RC_PROFILE_H */
