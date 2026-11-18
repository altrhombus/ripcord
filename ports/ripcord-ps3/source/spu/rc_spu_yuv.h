/*
 * Colour conversion on the SPEs.
 *
 * The picture is split into horizontal strips, one per SPE, and nothing is shared between them: no locks,
 * no communication, and the only coordination is the PPE waiting for every done flag. That is what makes
 * this the right first job to move off the PPE - the arithmetic is per-pixel with no dependency between
 * pixels, which is the shape the SPEs exist for.
 *
 * The threads are created ONCE and told to work by a mailbox write. A thread group create/start/join per
 * frame at 30 fps would spend more time in lv2 than on pixels.
 *
 * FALLING BACK IS PART OF THE CONTRACT. If the SPEs cannot be brought up, or a frame does not complete
 * inside its deadline, the caller converts on the PPE instead - which is known to work at 12 ms and is a
 * far better outcome than a frozen picture. rc_spu_yuv_convert says which happened.
 */
#ifndef RC_SPU_YUV_H
#define RC_SPU_YUV_H

#include <stddef.h>
#include <stdint.h>

/*
 * Eight SPEs are physically present on a PS3; one is disabled and one is reserved by the hypervisor,
 * leaving six usable, of which lv2 may give a normal process fewer. Asking for this many and accepting
 * what arrives is more robust than asserting a number the firmware has opinions about.
 *
 * NOT EVERY SPE IS CLAIMED. cellVdec runs its decoder on the SPEs - vdecConfig has a num_spus field -
 * so a build that means to use the console's own H.264 decoder cannot also hold every SPE for this.
 * This group is created once and never released: the teardown at the foot of rc_spu_yuv.c writes a quit
 * sentinel and deliberately does NOT destroy the group, because terminating the group (b105) and
 * joining it (b107) each locked this console. So the SPEs that vdec needs have to be left free from
 * the start; there is no giving one back later. (b146 hung somewhere inside the vdec probe with five
 * held here, which does not prove this was the cause.)
 *
 * THREE, which leaves the decoder three. The decoder produces RGB itself and this group only scales -
 * 10,876 us a frame across the group against 16,444 with the colour pass - so it can spare the SPE:
 * with two SPEs decoding, b190 refused twelve submissions, all of them on the large frames of a
 * transition, and refusing one breaks the reference chain.
 *
 * Three SPEs scaling costs about 3,600 us each against four's 2,700, which the frame budget has room
 * for. Two decoding against three does not, which is where the blockiness comes from.
 */
#define RC_SPU_YUV_MAX_SPES 3

typedef struct {
    int  spes;              /* how many actually came up; 0 means the PPE path is the only path */
    int  init_failed_at;    /* which step refused, for the report */
    int  last_error;
    unsigned frames;        /* frames converted on the SPEs */
    unsigned fallbacks;     /* frames that timed out and went to the PPE instead */
    unsigned avg_us;
    unsigned worst_us;

    /*
     * WHERE AN SPE'S TIME GOES, summed across the SPEs of the last frame and converted from the SPU
     * decrementer, which runs at the same timebase rc_tick_hz reports.
     *
     * The kernel waits on every get and, before reusing an output buffer, on that buffer's put, and it
     * is idle while it waits. `last_dma_us` is that waiting and `last_work_us` is the arithmetic.
     */
    unsigned last_dma_us;
    unsigned last_work_us;
    int      disabled;      /* the SPE path gave up and the PPE carried the rest of the run */
} rc_spu_yuv_stats;

/* Brings up the SPE threads. Returns the number running - zero is a valid answer and not an error. */
int rc_spu_yuv_init(void);

/*
 * Converts one picture into `dst`. Returns microseconds on success, 0 if the SPEs are not available or
 * did not finish - in which case the caller must convert on the PPE.
 */
unsigned rc_spu_yuv_convert(const uint8_t *y, const uint8_t *u, const uint8_t *v,
                            int y_stride, int uv_stride, int width, int height,
                            uint32_t *dst, int dst_pitch, int dst_width, int dst_height);

/* The same, for a picture the decoder has already converted to packed 32-bit RGB. Scales only. */
unsigned rc_spu_yuv_convert_argb(const uint8_t *argb, int src_stride, int width, int height,
                                 uint32_t *dst, int dst_pitch, int dst_width, int dst_height);

/*
 * 0 nearest, 1 interpolate along the row, 2 interpolate in both directions. Honoured by the packed-RGB
 * path only. Mode 2 does not fit a 60 fps budget - see the note on `bilinear` in rc_spu_yuv_job.h.
 */
void rc_spu_yuv_set_bilinear(int mode);

void rc_spu_yuv_stats_get(rc_spu_yuv_stats *out);
void rc_spu_yuv_exit(void);

#endif /* RC_SPU_YUV_H */
