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
 * Six SPEs are physically present on a PS3 and one is reserved by the hypervisor, leaving six usable of
 * which lv2 will give a normal process rather fewer. Asking for five and accepting what arrives is more
 * robust than asserting a number the firmware has opinions about.
 */
#define RC_SPU_YUV_MAX_SPES 5

typedef struct {
    int  spes;              /* how many actually came up; 0 means the PPE path is the only path */
    int  init_failed_at;    /* which step refused, for the report */
    int  last_error;
    unsigned frames;        /* frames converted on the SPEs */
    unsigned fallbacks;     /* frames that timed out and went to the PPE instead */
    unsigned avg_us;
    unsigned worst_us;
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
                            uint32_t *dst, int dst_pitch);

void rc_spu_yuv_stats_get(rc_spu_yuv_stats *out);
void rc_spu_yuv_exit(void);

#endif /* RC_SPU_YUV_H */
