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
static unsigned char g_y[RC_SPU_YUV_MAX_WIDTH] __attribute__((aligned(128)));
static unsigned char g_u[RC_SPU_YUV_MAX_WIDTH / 2] __attribute__((aligned(128)));
static unsigned char g_v[RC_SPU_YUV_MAX_WIDTH / 2] __attribute__((aligned(128)));

/* One converted SOURCE line, then one scaled OUTPUT line. Two buffers rather than one because the
 * conversion is vectorised over contiguous source pixels and the scale is a gather - trying to do both
 * in one pass would make the expensive half scalar to suit the cheap half. */
static unsigned int g_line[RC_SPU_YUV_MAX_WIDTH] __attribute__((aligned(128)));
static unsigned int g_out[RC_SPU_YUV_MAX_DST_WIDTH] __attribute__((aligned(128)));

/* Sequence first, so the PPE's existing poll on the first word is unchanged; the two tick counts ride
 * along in the same 16-byte transfer. The MFC accepts 1, 2, 4, 8 or a multiple of 16 - see the note in
 * rc_spu_yuv_job.h - and 16 is the smallest that carries three words. */
static volatile unsigned int g_report[4] __attribute__((aligned(16)));
static unsigned int dma_ticks;
static unsigned int work_ticks;

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

/* One source row into g_line, vectorised. BT.709 limited range in 10-bit fixed point - the same
 * constants the PPE uses, so a picture converted here and one converted there are the same picture. */
static void convert_line(unsigned int width)
{
    unsigned int col = 0u;

    for (; col + 3u < width; col += 4u) {
        vec_int4 c, d, e, yy, r, g, b;
        int d0 = (int)g_u[(col >> 1)] - 128;
        int d1 = (int)g_u[(col >> 1) + 1u] - 128;
        int e0 = (int)g_v[(col >> 1)] - 128;
        int e1 = (int)g_v[(col >> 1) + 1u] - 128;

        c = (vec_int4){ (int)g_y[col] - 16, (int)g_y[col + 1u] - 16,
                        (int)g_y[col + 2u] - 16, (int)g_y[col + 3u] - 16 };
        d = (vec_int4){ d0, d0, d1, d1 };
        e = (vec_int4){ e0, e0, e1, e1 };

        yy = mul_coef(c, 1192);
        r = clamp_vec(spu_rlmaska(spu_add(yy, mul_coef(e, 1836)), -10));
        g = clamp_vec(spu_rlmaska(spu_add(yy, spu_add(mul_coef(d, -218), mul_coef(e, -546))), -10));
        b = clamp_vec(spu_rlmaska(spu_add(yy, mul_coef(d, 2163)), -10));

        *(vec_uint4 *)&g_line[col] = spu_or(spu_or(spu_sl((vec_uint4)r, 16u),
                                                   spu_sl((vec_uint4)g, 8u)),
                                            (vec_uint4)b);
    }

    /* Whatever a width not divisible by four leaves. Scalar, because it is at most three pixels. */
    for (; col < width; col++) {
        int d = (int)g_u[col >> 1] - 128;
        int e = (int)g_v[col >> 1] - 128;
        int y0 = 1192 * ((int)g_y[col] - 16);

        g_line[col] = (clamp255((y0 + 1836 * e) >> 10) << 16)
                    | (clamp255((y0 - 218 * d - 546 * e) >> 10) << 8)
                    |  clamp255((y0 + 2163 * d) >> 10);
    }
}

/*
 * The horizontal scale: g_line (src_width pixels) into g_out (dst_width pixels), nearest neighbour.
 *
 * FIXED-POINT STEPPING, NOT A DIVISION PER PIXEL. The source column for output column x is
 * x * src_width / dst_width, and computing that directly would be a divide for every pixel on the
 * screen. Accumulating a 16.16 step is an add and a shift instead.
 *
 * Nearest neighbour rather than bilinear, for now and on purpose: it is exactly correct for the integer
 * factors that matter most here (960x540 doubles to 1080p) and the measurement should say what the cheap
 * version costs before a better one is chosen. Bilinear is roughly three times the work and is the
 * obvious next step if the budget allows it.  [X] - not yet compared side by side on hardware.
 */
static void scale_line(unsigned int src_width, unsigned int dst_width)
{
    unsigned int step;
    unsigned int acc = 0u;
    unsigned int x;

    if (src_width == dst_width) {
        /* The common case once a source is chosen to match the display: no scaling at all. */
        for (x = 0u; x < dst_width; x++)
            g_out[x] = g_line[x];
        return;
    }

    step = (src_width << 16) / dst_width;
    for (x = 0u; x < dst_width; x++) {
        g_out[x] = g_line[acc >> 16];
        acc += step;
    }
}

int main(uint64_t job_ea, uint64_t unused1, uint64_t unused2, uint64_t unused3)
{
    (void)unused1;
    (void)unused2;
    (void)unused3;

    /*
     * START THE DECREMENTER. It does not run until it is written, so reading it without this returns the
     * same value every time and every elapsed figure comes out zero - which is exactly what b182 and
     * b183 reported, and why the DMA-versus-compute split never printed. It counts DOWN from whatever is
     * written, at the same timebase rc_tick_hz reports, so a large start value gives a long run before
     * it wraps.
     */
    spu_write_decrementer(0xffffffffu);

    mfc_write_tag_mask(1u << TAG);

    for (;;) {
        unsigned int phase;

        /*
         * Blocks until the PPE has a frame. The value is ignored - the job block's address was fixed at
         * thread creation and the mailbox is only a doorbell. Reading the EA from the mailbox instead
         * would need two writes for a 64-bit address and a protocol to match them up.
         */
        /*
         * A doorbell, and now also a way out. Zero means "stop": the thread returns from main and lv2
         * can join it normally.
         *
         * b105 completed its entire run, printed its summary, and then locked the console hard enough to
         * take the FTP server with it - because rc_spu_yuv_exit terminated a thread group whose five
         * SPEs were all blocked in this read with no way to be told to leave. Terminating is not the
         * same as asking, and the difference only shows at shutdown, which is exactly where nobody looks.
         */
        if (spu_read_in_mbox() == 0u)
            return 0;

        mfc_get(&g_job, job_ea, (uint32_t)sizeof(g_job), TAG, 0, 0);
        mfc_write_tag_mask(1u << TAG);
        (void)mfc_read_tag_status_all();

        if (g_job.src_width > RC_SPU_YUV_MAX_WIDTH || g_job.src_width == 0u
            || g_job.dst_width > RC_SPU_YUV_MAX_DST_WIDTH || g_job.dst_width == 0u
            || g_job.dst_height == 0u) {
            /* Wider than the line buffers, or a degenerate rectangle. Refusing is right: converting part
             * of a line would put a torn picture on the screen and look like a decode fault. */
            phase = RC_SPU_PHASE_ENTERED;
            mfc_put(&phase, g_job.done_ea, 4u, TAG, 0, 0);
            (void)mfc_read_tag_status_all();
            continue;
        }

        /*
         * DRIVEN BY OUTPUT ROWS, with the source row computed per row and the fetch cached.
         *
         * Vertical scaling falls out of which source row is fetched, so it costs nothing beyond the
         * mapping itself. Consecutive output rows often map to the SAME source row - at 2x, every one
         * does - and re-fetching it would double the DMA for no benefit, so the last row fetched is
         * remembered. That is the whole of the vertical scaler.
         */
        dma_ticks = 0u;
        work_ticks = 0u;
        {
            /*
             * WHERE THE TIME GOES, split between waiting for the MFC and doing the arithmetic.
             *
             * This loop blocks on every transfer - one tag, mfc_read_tag_status_all after each get and
             * each put - so the SPE is idle for the whole of every round trip. At 1280x720 into
             * 1920x1080 that is roughly 270 blocking puts and 180 blocking gets per SPE per frame, and
             * the conversion measures 4,945 us a frame without anyone knowing which half that is.
             *
             * It decides the next change. If the arithmetic dominates, dropping the YUV-to-RGB pass (the
             * decoder can output ARGB32 - b179 confirmed it) is worth doing. If the waiting dominates,
             * that same change makes things WORSE, because ARGB source is 4 bytes a pixel against
             * YUV420's 1.5 - and double buffering is the answer instead, which is what IBM's Cell
             * programming guide spends a chapter on.
             */
            unsigned int cached_y_row = 0xffffffffu;
            unsigned int cached_uv_row = 0xffffffffu;
            unsigned int out_row;

            for (out_row = 0u; out_row < g_job.dst_rows; out_row++) {
                unsigned int abs_row = g_job.first_dst_row + out_row;
                unsigned int src_row = (unsigned int)
                    (((unsigned long long)abs_row * g_job.src_height) / g_job.dst_height);
                unsigned int uv_row = src_row >> 1;

                if (src_row >= g_job.src_height)
                    src_row = g_job.src_height - 1u;

                if (src_row != cached_y_row) {
                    unsigned int t0;

                    mfc_get(g_y, g_job.y_ea + (unsigned long long)src_row * g_job.y_stride,
                            g_job.src_width, TAG, 0, 0);
                    cached_y_row = src_row;

                    if (uv_row != cached_uv_row) {
                        mfc_get(g_u, g_job.u_ea + (unsigned long long)uv_row * g_job.uv_stride,
                                g_job.src_width / 2u, TAG, 0, 0);
                        mfc_get(g_v, g_job.v_ea + (unsigned long long)uv_row * g_job.uv_stride,
                                g_job.src_width / 2u, TAG, 0, 0);
                        cached_uv_row = uv_row;
                    }
                    /* The decrementer counts DOWN, so elapsed is before-minus-after. */
                    t0 = spu_read_decrementer();
                    (void)mfc_read_tag_status_all();
                    dma_ticks += t0 - spu_read_decrementer();

                    t0 = spu_read_decrementer();
                    convert_line(g_job.src_width);
                    scale_line(g_job.src_width, g_job.dst_width);
                    work_ticks += t0 - spu_read_decrementer();
                }

                {
                    unsigned int t0;

                    mfc_put(g_out, g_job.dst_ea + (unsigned long long)out_row * g_job.dst_stride,
                            g_job.dst_width * 4u, TAG, 0, 0);
                    t0 = spu_read_decrementer();
                    (void)mfc_read_tag_status_all();
                    dma_ticks += t0 - spu_read_decrementer();
                }
            }
        }

        /*
         * The sequence number goes back rather than a constant, so the PPE can tell this frame's
         * completion from the previous frame's flag left in the same word.
         */
        g_report[0] = g_job.sequence;
        g_report[1] = dma_ticks;
        g_report[2] = work_ticks;
        g_report[3] = 0u;
        phase = g_job.sequence;
        mfc_put((void *)g_report, g_job.done_ea, 16u, TAG, 0, 0);
        (void)mfc_read_tag_status_all();
    }
    /* not reached */
}
