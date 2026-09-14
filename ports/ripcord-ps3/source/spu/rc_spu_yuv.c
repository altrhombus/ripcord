#include "rc_spu_yuv.h"
#include "rc_spu_phase.h"
#include "rc_spu_yuv_job.h"
#include "rc_platform.h"

#include <malloc.h>
#include <string.h>

#include <ppu-lv2.h>
#include <sys/spu.h>
#include <sys/thread.h>

/* bin2s names its symbols from the file, so the SPE image for this job is rc_spu_yuv_bin. */
extern const unsigned char rc_spu_yuv_bin[];
extern const unsigned int rc_spu_yuv_bin_size;

/* Effective address of a PPE pointer, as the SPE's MFC needs it. The same macro the probe uses. */
#define EA(p) ((u64)(u32)(u64)(p))

static sysSpuImage s_image;
static sys_spu_group_t s_group;
static sys_spu_thread_t s_thread[RC_SPU_YUV_MAX_SPES];
static int s_spes;
static int s_ready;
static uint32_t s_sequence;
static unsigned s_consecutive_failures;

/*
 * One frame time, not three. A conversion that has not finished in 25 ms has already missed its frame,
 * and waiting longer only delays the fallback that will produce the picture.
 */
#define RC_SPU_YUV_DEADLINE_MS 25u

/* Five consecutive misses is not a hiccup. After this the SPE path is abandoned for the rest of the
 * run and the PPE - which is known to work - carries the stream on its own. */
#define RC_SPU_YUV_GIVE_UP_AFTER 5u
static rc_spu_yuv_stats s_stats;

/*
 * The job blocks and the done words, one per SPE.
 *
 * 128-byte aligned and 128 bytes apart. Alignment because that is where DMA reaches peak rather than
 * merely working; SPACING because two done words in the same cache line would have two SPEs writing the
 * same line from different cores, and false sharing on a 128-byte line is a real cost on this hardware
 * even when the values never collide.
 */
static rc_spu_yuv_job *s_job;
static volatile uint32_t *s_done;

#define DONE_STRIDE_WORDS 32   /* 128 bytes */

static int fail_init(int step, int rc)
{
    s_stats.init_failed_at = step;
    s_stats.last_error = rc;
    return 0;
}

int rc_spu_yuv_init(void)
{
    sysSpuThreadGroupAttribute grpattr;
    sysSpuThreadAttribute attr;
    sysSpuThreadArgument arg;
    int i;

    if (s_ready)
        return s_spes;

    memset(&s_stats, 0, sizeof(s_stats));

    s_job = (rc_spu_yuv_job *)memalign(128, sizeof(rc_spu_yuv_job) * RC_SPU_YUV_MAX_SPES);
    s_done = (volatile uint32_t *)memalign(128, 128u * RC_SPU_YUV_MAX_SPES);
    if (s_job == NULL || s_done == NULL)
        return fail_init(1, 0);
    memset(s_job, 0, sizeof(rc_spu_yuv_job) * RC_SPU_YUV_MAX_SPES);
    memset((void *)s_done, 0, 128u * RC_SPU_YUV_MAX_SPES);

    /*
     * sysSpuInitialize is NOT called here. rc_spu.c's probe already initialises the SPU subsystem for
     * this process, and calling it twice returns an error that means "already done" - which would have
     * to be special-cased by value. Depending on the probe having run is the smaller assumption, and
     * rc_spu_yuv_init is only reached after it.
     */
    if (sysSpuImageImport(&s_image, rc_spu_yuv_bin, 0) != 0)
        return fail_init(3, 0);

    memset(&grpattr, 0, sizeof(grpattr));
    grpattr.nsize = (u32)sizeof("ripcord-yuv");
    grpattr.name = "ripcord-yuv";

    /*
     * Ask for the maximum and accept fewer. lv2 hands a normal process a number of SPEs it decides,
     * and asserting one would turn a working console with a different firmware into a broken port.
     */
    for (s_spes = RC_SPU_YUV_MAX_SPES; s_spes > 0; s_spes--) {
        if (sysSpuThreadGroupCreate(&s_group, (u32)s_spes, 100, &grpattr) == 0)
            break;
    }
    if (s_spes <= 0) {
        (void)sysSpuImageClose(&s_image);
        return fail_init(4, 0);
    }

    for (i = 0; i < s_spes; i++) {
        memset(&attr, 0, sizeof(attr));
        attr.nsize = (u32)sizeof("rc_spu_yuv");
        attr.name = "rc_spu_yuv";
        attr.option = SPU_THREAD_ATTR_NONE;

        memset(&arg, 0, sizeof(arg));
        /* The job block's address, fixed for the life of the thread. The mailbox is only a doorbell -
         * see the SPE side on why the address does not travel through it. */
        arg.arg0 = EA(&s_job[i]);
        arg.arg1 = 0u;
        arg.arg2 = 0u;
        arg.arg3 = 0u;

        if (sysSpuThreadInitialize(&s_thread[i], s_group, (u32)i, &s_image, &attr, &arg) != 0) {
            (void)sysSpuThreadGroupDestroy(s_group);
            (void)sysSpuImageClose(&s_image);
            s_spes = 0;
            return fail_init(5, i);
        }
    }

    if (sysSpuThreadGroupStart(s_group) != 0) {
        (void)sysSpuThreadGroupDestroy(s_group);
        (void)sysSpuImageClose(&s_image);
        s_spes = 0;
        return fail_init(6, 0);
    }

    s_ready = 1;
    s_stats.spes = s_spes;
    return s_spes;
}

unsigned rc_spu_yuv_convert(const uint8_t *y, const uint8_t *u, const uint8_t *v,
                            int y_stride, int uv_stride, int width, int height,
                            uint32_t *dst, int dst_pitch, int dst_width, int dst_height)
{
    uint64_t t0, deadline;
    int rows_each;
    int i;
    int outstanding;

    if (!s_ready || s_spes <= 0 || width <= 0 || height <= 0)
        return 0u;
    if ((unsigned)width > RC_SPU_YUV_MAX_WIDTH || (unsigned)dst_width > RC_SPU_YUV_MAX_DST_WIDTH)
        return 0u;
    if (dst_width <= 0 || dst_height <= 0)
        return 0u;

    t0 = rc_tick();
    s_sequence++;
    if (s_sequence == RC_SPU_PHASE_NOTHING || s_sequence == RC_SPU_PHASE_ENTERED)
        s_sequence += 2u;   /* never collide with the phase values the SPE also writes */

    /*
     * Strips are divided by OUTPUT row now, and the even-row rule no longer applies to them.
     *
     * It used to: a strip starting on an odd SOURCE row takes the wrong chroma line. With scaling the
     * SPE computes its own source row from the output row, so every row - first in a strip or not -
     * derives its chroma the same way. The constraint moved into the kernel where the mapping lives,
     * which is the right place for it, and keeping a stale evenness rule here would silently misalign
     * strips whenever the scale factor is not an integer.
     */
    rows_each = (dst_height + s_spes - 1) / s_spes;
    if (rows_each < 1)
        rows_each = 1;

    outstanding = 0;
    for (i = 0; i < s_spes; i++) {
        int first = i * rows_each;
        int rows = rows_each;

        if (first >= dst_height)
            break;
        if (first + rows > dst_height)
            rows = dst_height - first;
        if (rows <= 0)
            break;

        /* The planes are handed over at row ZERO. The SPE maps output rows to source rows itself, so
         * pre-offsetting here would be the same arithmetic in two places with a chance to disagree. */
        s_job[i].y_ea = EA(y);
        s_job[i].u_ea = EA(u);
        s_job[i].v_ea = EA(v);
        s_job[i].dst_ea = EA((uint8_t *)dst + (size_t)first * (size_t)dst_pitch);
        s_job[i].done_ea = EA(&s_done[(size_t)i * DONE_STRIDE_WORDS]);
        s_job[i].y_stride = (uint32_t)y_stride;
        s_job[i].uv_stride = (uint32_t)uv_stride;
        s_job[i].dst_stride = (uint32_t)dst_pitch;
        s_job[i].src_width = (uint32_t)width;
        s_job[i].src_height = (uint32_t)height;
        s_job[i].dst_width = (uint32_t)dst_width;
        s_job[i].dst_height = (uint32_t)dst_height;
        s_job[i].first_dst_row = (uint32_t)first;
        s_job[i].dst_rows = (uint32_t)rows;
        s_job[i].sequence = s_sequence;

        s_done[(size_t)i * DONE_STRIDE_WORDS] = 0u;
        outstanding++;
    }

    /* Ring every doorbell before waiting on any of them, so the SPEs run concurrently rather than in
     * turn - which is the entire point. */
    for (i = 0; i < outstanding; i++)
        (void)sysSpuThreadWriteMb(s_thread[i], 1u);

    /*
     * A DEADLINE, A YIELD, AND A GIVING-UP POINT. The first version had only the first, and that was the
     * difference between degrading and locking the console.
     *
     * It bounded ONE FRAME at 100 ms and spun with no sleep to do it - a hard poll of main memory from
     * the PPE. When the SPEs stopped answering, every frame then paid 100 ms of bus-saturating spin plus
     * a full PPE conversion, for as many frames as the session lasted. Ninety seconds of that starves
     * the machine, which is what an unresponsive console with no video looks like. The comment above it
     * said "a deadline, not an indefinite wait", and it was true of a frame and false of the run.
     *
     * So: sleep rather than spin, because the SPEs need the bus more than this loop does. A deadline of
     * one frame time rather than three, because a conversion that has not finished in 25 ms has already
     * lost its frame. And a consecutive-failure count, because one late frame is a hiccup while five in
     * a row is a broken subsystem - and paying for a broken subsystem on every frame forever is the
     * behaviour that turned a degraded picture into a dead console.
     */
    deadline = rc_time_ms() + RC_SPU_YUV_DEADLINE_MS;
    for (;;) {
        int complete = 0;

        for (i = 0; i < outstanding; i++) {
            if (s_done[(size_t)i * DONE_STRIDE_WORDS] == s_sequence)
                complete++;
        }
        if (complete == outstanding)
            break;
        if (rc_time_ms() > deadline) {
            s_stats.fallbacks++;
            s_consecutive_failures++;
            if (s_consecutive_failures >= RC_SPU_YUV_GIVE_UP_AFTER) {
                s_ready = 0;
                s_stats.disabled = 1;
            }
            return 0u;
        }
        rc_sleep_ms(1u);
    }
    s_consecutive_failures = 0;

    {
        uint64_t hz = rc_tick_hz();
        uint64_t ticks = rc_tick() - t0;
        unsigned us = (hz > 0u) ? (unsigned)((ticks * 1000000u) / hz) : 0u;

        s_stats.frames++;
        s_stats.avg_us = (s_stats.frames > 1u)
            ? (unsigned)(((uint64_t)s_stats.avg_us * (s_stats.frames - 1u) + us) / s_stats.frames)
            : us;
        if (us > s_stats.worst_us)
            s_stats.worst_us = us;
        return (us > 0u) ? us : 1u;   /* 0 is reserved for "did not convert" */
    }
}

void rc_spu_yuv_stats_get(rc_spu_yuv_stats *out)
{
    if (out != NULL)
        *out = s_stats;
}

void rc_spu_yuv_exit(void)
{
    int i;

    if (s_spes <= 0)
        return;

    /*
     * ASK THEM TO LEAVE, AND THEN STOP ASKING FOR ANYTHING.
     *
     * Two attempts at a tidy shutdown have now locked this console. b105 called
     * sysSpuThreadGroupTerminate on five threads blocked in a mailbox read - a blocked thread cannot
     * notice it is being terminated. b107 asked them to leave first and then joined the group, which
     * froze too: the stream had run to completion and the freeze was entirely in the teardown.
     *
     * So the teardown does the one thing that helps and nothing that can block. The sentinel is written
     * because an SPE waiting on its doorbell will wake, see zero and return from main - that is cheap and
     * it is the right thing when it works. Nothing then WAITS for that to have happened.
     *
     * No join, no terminate, no destroy, and that is not a leak being excused. This runs as the process
     * exits, and lv2 reclaims a process's SPE thread groups when it does. The question is not whether
     * cleanup is tidy but whether it can hang, because a shutdown that hangs costs a reboot and - until
     * the log order was fixed alongside this - the last lines of the log as well. Cleanup that cannot
     * fail is worth more here than cleanup that is complete.
     *
     * Deliberately NOT marked [X]: this is not an unverified guess about the protocol, it is a decision
     * about what to do at exit, and the reasoning is the whole justification.
     */
    for (i = 0; i < s_spes; i++)
        (void)sysSpuThreadWriteMb(s_thread[i], 0u);

    s_ready = 0;
    s_spes = 0;
}
