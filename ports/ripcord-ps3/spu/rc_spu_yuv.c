/*
 * ripcord-ps3 - YUV 4:2:0 to XRGB8888 on one SPE.
 *
 * The PPE measured this conversion at 12,037 us per 960x540 picture while also spending 11,731 us
 * decoding, which is 72% of a 30 fps budget for one 960x540 window - and filling a 1080p screen needs
 * four times the pixels. The arithmetic is per-pixel with no dependency between pixels, which is the
 * shape the SPEs exist for.
 *
 * ONE SPE CONVERTS ONE HORIZONTAL STRIP. The picture is divided by row, each SPE told where its strip
 * starts, and nothing is shared between them - no locks, no communication, and the only coordination is
 * the PPE waiting for every done flag.
 *
 * It waits on its inbound mailbox rather than being created per frame. A thread group create/start/join
 * per frame at 30 fps would spend more time on lv2 than on pixels; the thread is made once and told to
 * work by a mailbox write, which is one syscall on the PPE and a blocking read here.
 */
#include "rc_spu_phase.h"
#include "rc_spu_yuv_job.h"

#include <spu_intrinsics.h>
#include <spu_mfcio.h>

#define TAG 0u

/*
 * Local store is 256 KB for this program, its stack and every buffer below. At the maximum width this
 * is sized for, one pass costs: 2 luma rows (2 x 1280), one chroma row pair (2 x 640), and 2 output rows
 * (2 x 1280 x 4) = 13,824 bytes. Two of everything would allow overlapping DMA with compute, which is
 * the guide's central technique and is NOT done here - correctness first, and the measurement will say
 * whether it is worth it.
 */
static unsigned char g_y[RC_SPU_YUV_ROWS_PER_PASS][RC_SPU_YUV_MAX_WIDTH] __attribute__((aligned(128)));
static unsigned char g_u[RC_SPU_YUV_MAX_WIDTH / 2] __attribute__((aligned(128)));
static unsigned char g_v[RC_SPU_YUV_MAX_WIDTH / 2] __attribute__((aligned(128)));
static unsigned int  g_out[RC_SPU_YUV_ROWS_PER_PASS][RC_SPU_YUV_MAX_WIDTH] __attribute__((aligned(128)));

static rc_spu_yuv_job g_job __attribute__((aligned(128)));

/*
 * FOUR PIXELS AT A TIME, IN VECTORS.
 *
 * b100 ran this scalar and measured it: five SPEs converted a picture in 5884 us where the PPE alone
 * took 11869. Two times, for five processors - and per pixel each SPE was 2.5x SLOWER than the PPE.
 * That is what scalar code on an SPE costs. There is no scalar unit: every scalar operation is a vector
 * operation with the value extracted and reinserted around it, so the parallelism was buying back what
 * the instruction mix was throwing away.
 *
 * The multiply is the part worth explaining. The SPE has no 32x32 integer multiply, but spu_mulo takes
 * the ODD halfwords of two short vectors and produces 32-bit products - which, on this big-endian
 * machine, is the LOW 16 bits of each 32-bit lane. Every value here fits in 16 bits (y-16 is -16..239,
 * the largest coefficient is 2163), so keeping them as 32-bit lanes and multiplying with spu_mulo is a
 * 16x16 -> 32 multiply per lane with no packing at all.
 *
 * Clamping is spu_cmpgt and spu_sel rather than branches. A branch per channel per pixel on a processor
 * with no branch predictor is the other half of what made the scalar version slow.
 */
/* vec_int4, vec_short8 and vec_uint4 come from spu_intrinsics.h - this file does not define them. */
static inline vec_int4 mul_coef(vec_int4 v, short coef)
{
    return spu_mulo((vec_short8)v, spu_splats(coef));
}

static inline vec_int4 clamp_vec(vec_int4 v)
{
    const vec_int4 zero = spu_splats((signed int)0);
    const vec_int4 max = spu_splats((signed int)255);

    v = spu_sel(zero, v, spu_cmpgt(v, zero));
    return spu_sel(v, max, spu_cmpgt(v, max));
}

static inline unsigned int clamp255(int v)
{
    if (v < 0)
        return 0u;
    if (v > 255)
        return 255u;
    return (unsigned int)v;
}

/* One row pair, sharing one chroma row. BT.709 limited range in 10-bit fixed point - the same constants
 * the PPE uses, so a picture converted here and one converted there are the same picture. */
static void convert_pair(unsigned int width, unsigned int rows_this_pass)
{
    unsigned int row;

    for (row = 0u; row < rows_this_pass; row++) {
        const unsigned char *yp = g_y[row];
        unsigned int *outp = g_out[row];
        unsigned int col = 0u;

        /* Four luma pixels need two chroma samples, each used twice. */
        for (; col + 3u < width; col += 4u) {
            vec_int4 c, d, e, yy, r, g, b;
            int d0 = (int)g_u[(col >> 1)] - 128;
            int d1 = (int)g_u[(col >> 1) + 1u] - 128;
            int e0 = (int)g_v[(col >> 1)] - 128;
            int e1 = (int)g_v[(col >> 1) + 1u] - 128;

            c = (vec_int4){ (int)yp[col] - 16, (int)yp[col + 1u] - 16,
                            (int)yp[col + 2u] - 16, (int)yp[col + 3u] - 16 };
            d = (vec_int4){ d0, d0, d1, d1 };
            e = (vec_int4){ e0, e0, e1, e1 };

            yy = mul_coef(c, 1192);
            r = clamp_vec(spu_rlmaska(spu_add(yy, mul_coef(e, 1836)), -10));
            g = clamp_vec(spu_rlmaska(spu_add(yy, spu_add(mul_coef(d, -218), mul_coef(e, -546))), -10));
            b = clamp_vec(spu_rlmaska(spu_add(yy, mul_coef(d, 2163)), -10));

            *(vec_uint4 *)&outp[col] = spu_or(spu_or(spu_sl((vec_uint4)r, 16u),
                                                     spu_sl((vec_uint4)g, 8u)),
                                              (vec_uint4)b);
        }

        /* Whatever a width not divisible by four leaves. Scalar, because it is at most three pixels. */
        for (; col < width; col++) {
            int d = (int)g_u[col >> 1] - 128;
            int e = (int)g_v[col >> 1] - 128;
            int y0 = 1192 * ((int)yp[col] - 16);

            outp[col] = (clamp255((y0 + 1836 * e) >> 10) << 16)
                      | (clamp255((y0 - 218 * d - 546 * e) >> 10) << 8)
                      |  clamp255((y0 + 2163 * d) >> 10);
        }
    }
}

int main(uint64_t job_ea, uint64_t unused1, uint64_t unused2, uint64_t unused3)
{
    (void)unused1;
    (void)unused2;
    (void)unused3;

    mfc_write_tag_mask(1u << TAG);

    for (;;) {
        unsigned int row;
        unsigned int phase;

        /*
         * Blocks until the PPE has a frame. The value is ignored - the job block's address was fixed at
         * thread creation and the mailbox is only a doorbell. Reading the EA from the mailbox instead
         * would need two writes for a 64-bit address and a protocol to match them up.
         */
        (void)spu_read_in_mbox();

        mfc_get(&g_job, job_ea, (uint32_t)sizeof(g_job), TAG, 0, 0);
        mfc_write_tag_mask(1u << TAG);
        (void)mfc_read_tag_status_all();

        if (g_job.width > RC_SPU_YUV_MAX_WIDTH || g_job.width == 0u) {
            /* Wider than the strip buffers. Refusing is right: converting part of a line would put a
             * torn picture on the screen and look like a decode fault. */
            phase = RC_SPU_PHASE_ENTERED;
            mfc_put(&phase, g_job.done_ea, 4u, TAG, 0, 0);
            (void)mfc_read_tag_status_all();
            continue;
        }

        for (row = 0u; row < g_job.rows; row += RC_SPU_YUV_ROWS_PER_PASS) {
            unsigned int rows_this_pass = g_job.rows - row;
            unsigned int luma_bytes = g_job.width;
            unsigned int chroma_bytes = g_job.width / 2u;
            unsigned int out_bytes = g_job.width * 4u;
            unsigned int i;

            if (rows_this_pass > RC_SPU_YUV_ROWS_PER_PASS)
                rows_this_pass = RC_SPU_YUV_ROWS_PER_PASS;

            /* In: the luma rows of this pass, and the single chroma row they share. */
            for (i = 0u; i < rows_this_pass; i++) {
                mfc_get(g_y[i], g_job.y_ea + (uint64_t)(row + i) * g_job.y_stride,
                        luma_bytes, TAG, 0, 0);
            }
            mfc_get(g_u, g_job.u_ea + (uint64_t)(row >> 1) * g_job.uv_stride, chroma_bytes, TAG, 0, 0);
            mfc_get(g_v, g_job.v_ea + (uint64_t)(row >> 1) * g_job.uv_stride, chroma_bytes, TAG, 0, 0);
            (void)mfc_read_tag_status_all();

            convert_pair(g_job.width, rows_this_pass);

            /* Out: straight into the display buffer, at the display's pitch. */
            for (i = 0u; i < rows_this_pass; i++) {
                mfc_put(g_out[i], g_job.dst_ea + (uint64_t)(row + i) * g_job.dst_stride,
                        out_bytes, TAG, 0, 0);
            }
            (void)mfc_read_tag_status_all();
        }

        /*
         * The sequence number goes back rather than a constant, so the PPE can tell this frame's
         * completion from the previous frame's flag left in the same word.
         */
        phase = g_job.sequence;
        mfc_put(&phase, g_job.done_ea, 4u, TAG, 0, 0);
        (void)mfc_read_tag_status_all();
    }
    /* not reached */
}
