/*
 * ripcord-ps3 - running a job on an SPE, and measuring what it costs to ask.
 *
 * Step 6 of README.md's order of work. This is the PPU half; spu/rc_spu_probe.c is the program that
 * actually runs on the SPE, and it explains why the job is a memory copy rather than anything useful.
 *
 * WHAT THIS IS FOR. The PS3's decoder has to live on the SPEs - the PPE alone will not decode 720p
 * H.264 and there is no fixed-function block to fall back on. Before any of that is designed, one
 * number is needed: the cost of handing an SPE a unit of work. A large per-job overhead forces the
 * decoder to batch coarsely, whole slices at a time; a small one allows a pipeline at macroblock-row
 * granularity, which is what makes the entropy and reconstruction stages overlap. DECODE.md's staging
 * cannot be settled without it, and it cannot be reasoned out - it has to be measured on a console.
 *
 * NOT A JOB MODEL, AND NOT TRYING TO BE. One thread group, one SPE, one job, synchronous. The real
 * decoder will want several SPEs and a queue; this exists so that the first version of that has a
 * baseline to be compared against rather than being the first thing ever run.
 */
#ifndef RC_SPU_H
#define RC_SPU_H

#include <stddef.h>
#include <stdint.h>

/*
 * Brings up the SPU subsystem and imports the embedded probe image. Returns 1 on success, 0 on failure -
 * the same convention as rc_random_init, and deliberately NOT the 0-on-success lv2 convention, because
 * ports/common/platform/rc_platform.h having documented that backwards once is enough.
 *
 * Call once. Safe to call again; the second call is a no-op that reports the first call's result.
 */
int rc_spu_init(void);

/*
 * Runs one job on one SPE: copy `size` bytes from `src` to `dst`, then signal completion.
 *
 * `size` must be a multiple of 16 and `src`/`dst` must be 128-byte aligned - the MFC requires it and
 * this function does not check, because every caller here is in the same file that allocates them.
 * A size of 0 is legitimate and measures the overhead alone, which is the whole point of the first run.
 *
 * On success writes the elapsed time in rc_tick() units to *ticks: measured from immediately before the
 * thread group is started to the moment the SPE's completion flag is observed. That interval is what a
 * caller handing out work would actually pay, so it includes thread start and teardown rather than
 * pretending the DMA happens in isolation.
 *
 * Returns 1 on success, 0 on failure - and on failure the three out-parameters are what the caller
 * reports, because a bring-up program's job is to turn a non-result into a finding:
 *
 *   *phase       the last progress value the SPE managed to write. spu/rc_spu_phase.h says what each
 *                one rules in and out; the short version is that NOTHING and ENTERED point at
 *                completely different bugs.
 *   *join_cause  lv2's account of how the thread group ended.
 *   *join_status the thread's own exit status.
 *
 * All three are also filled in on success, where they are less interesting but cost nothing.
 *
 * THIS CALL CANNOT HANG. An earlier version waited on the SPE indefinitely, on the argument that a hang
 * pinpoints the line. It does not: what it produces is a blank screen and a power cycle, and it destroys
 * the log that would have said something. It now gives up after a fixed deadline and reports.
 */
int rc_spu_run(const void *src, void *dst, size_t size, uint64_t *ticks,
               uint32_t *phase, uint32_t *heartbeat, uint64_t *spu_args,
               uint32_t *join_cause, uint32_t *join_status);

/*
 * What lv2 made of the embedded image, and what the linker actually built. Valid after rc_spu_init.
 * REPORTING ONLY - see the implementation. PSL1GHT's sysSpuImage declaration does not match what lv2
 * writes on a 64-bit process, so these values are fragments of the real structure and must not be used
 * to conclude anything. They are logged because raw numbers are worth keeping.
 */
/* The effective address the SPE is told to signal completion at. For the caller's own report. */
uint64_t rc_spu_done_ea(void);

/* The address of the job block handed to the SPE as its single argument. Also for the report. */
uint64_t rc_spu_job_ea(void);

void rc_spu_image_info(uint32_t *entry, uint32_t *segments, uint32_t *expected_entry);

/* Releases the imported image. Safe whether or not init succeeded. */
void rc_spu_exit(void);

#endif /* RC_SPU_H */
