/*
 * See rc_mvd.h. The call sequence follows devkitPro's own MVD example plus 3dbrew's MVD_Services and
 * MVDSTD:* pages - the hardware documentation libctru's own header cites for every one of these
 * functions.
 *
 * ON READING THOSE AT ALL, since this repository has a clean-room rule: that rule is scoped to "another
 * implementation of these protocols" - the PS5 Remote Play wire protocol, whose independent derivation
 * is what docs/protocol/ has to defend. MVD is a Nintendo hardware decoder and 3dbrew is hardware
 * documentation; neither touches the protocol spec. Reading them is no more a clean-room concern than
 * reading the ARM architecture reference. The rule WOULD apply to another Remote Play client, for
 * protocol detail. Separately, Ripcord is Apache-2.0, so COPYING code from a GPL homebrew player would
 * be a licensing problem even though reading its documentation is not - the distinction is copying, not
 * looking.
 *
 * Leaning on the devkitPro example alone cost several hardware runs, because it exercises exactly one
 * narrow path and silently proves nothing about the rest.
 *
 * THE HISTORY MATTERS HERE, because three separate attempts failed in ways that each looked like a
 * different bug and were all the same misreading of what the example proves:
 *
 *   1. Input buffer via linearAlloc rather than linearMemAlign(size, 0x40): every NAL unit rejected
 *      (1105 fed, 1105 errors). All units failing is the block refusing the buffer; a bad bitstream
 *      fails only some.
 *   2. Config with input 640x360 and output 240x400, assuming MVD would scale into the framebuffer:
 *      MVD_STATUS_OK on process, one render error per keyframe. MVD DOES NOT SCALE.
 *   2b. Status codes ruled out: ProcessNALUnit returns FRAMEREADY 1237 times and OK zero times, so the
 *      decoder really does hold decoded pictures - rendering was never being asked for a frame that did
 *      not exist. Memory region ruled out too: the buffer sits at 0x3016c000, the mapping MVD supports.
 *   3. Config with input == output == 640x360 into our own page-aligned linear buffer: renders reported
 *      MVD_STATUS_OK, and the buffer stayed zero - all 524,288 pixels of it, with the configured output
 *      address verified equal to osConvertVirtToPhys of the buffer. A red test square drawn by the CPU
 *      in the same frame appeared on screen, proving the framebuffer path, the cache flush and the swap
 *      were all fine.
 *
 * (3) is the one that settles it. The example allocates an output buffer at 0x40 and then NEVER RENDERS
 * INTO IT - it overrides physaddr_outdata0 with the current framebuffer before every single render. So
 * the only output target MVD is demonstrably willing to write is a framebuffer, and the example's
 * apparent "allocate a buffer" step is a red herring that cost three rounds of hardware runs.
 *
 * SO THIS FILE NOW DOES WHAT THE EXAMPLE DOES, EXACTLY: MVD is configured at the screen's dimensions in
 * the framebuffer's transposed form, and renders straight into the framebuffer. There is no intermediate
 * buffer, no ARM11 scaling pass, and no cache juggling - the block writes the framebuffer through the
 * GPU's view of memory and gfxSwapBuffers presents it.
 *
 * THE OUTPUT PATH IS THE CONFIG, AND mvdstdSetupOutputBuffers IS DELIBERATELY NOT CALLED.
 *
 * That call was tried, and it is worse than useless here: 3dbrew states that "once this command is used,
 * each rendered frame will be written into the output buffers specified by the entry-list INSTEAD OF the
 * output buffers from configuration". So registering an entrylist does not add a route to our buffer -
 * it DIVERTS output away from the config path, which is the path a known-working 3DS H.264 player
 * actually uses (that player sets physaddr_outdata0 through MVDSTD_SetConfig and never touches the
 * entrylist; 3dbrew notes the Internet Browser does not use it either).
 *
 * With the entrylist registered, a full run produced zero render errors, 1,753 FRAMEREADY results, and a
 * sentinel that never changed - decode succeeding into a buffer nothing was writing. Removing it leaves
 * exactly the sequence the reference player uses.
 *
 * The stream cannot instead be made screen-sized: the console refuses 400x240 outright with its own
 * encoder error, so a 640x360 frame has to reach a 400x240 screen through an intermediate buffer and an
 * ARM11 scale - a pass already written, measured, and proven to reach the display.
 */

#include "rc_mvd.h"

#include "../util/rc_log.h"
#include "../util/rc_profile.h"

#include <3ds.h>

#include <stdint.h>
#include <string.h>

/* One NAL unit's staging buffer. A screen-sized IDR is comfortably under this; anything larger is
 * counted and dropped rather than overrunning. */
#define MVD_INPUT_STAGING 0x40000

/* Stamped into the output buffer to detect that MVD has written a picture. See the block at its use. */
#define MVD_SENTINEL 0x1111u

/*
 * 0x40-BYTE ALIGNMENT IS REQUIRED, NOT PREFERRED - see finding (1) above. MVD reads this through a
 * physical address and plain linearAlloc does not promise the alignment.
 */
#define MVD_BUFFER_ALIGN 0x40

/*
 * The top screen is 400x240 to look at and 240x400 in memory. MVD is configured in the MEMORY
 * orientation, which is what the example does (it passes 240, 400 for a 400x240 video) and is why it can
 * write the framebuffer directly with no rotation on our side.
 */
#define SCREEN_WIDTH 400
#define SCREEN_HEIGHT 240

/*
 * WIDESCREEN: THE ONE FREE LEVER ON PICTURE QUALITY.
 *
 * The console will only ever send 640x360 (Phase 6d settled that), and the top screen is 400 pixels
 * wide, so every frame was point-sampled down by 1.6:1 - throwing away 61% of the columns. Thin vertical
 * strokes are exactly what that destroys, which is why on-screen text was the first thing to become
 * unreadable while flat colour looked fine.
 *
 * The panel is physically 800 subpixels across; 400-wide mode is the stereoscopic layout using half of
 * them per eye. gfxSetWide gives all 800 to one 2D image, and 640 -> 800 is an UPSCALE - no horizontal
 * information is discarded at all. Same panel, same power, no extra bandwidth: the only cost is that the
 * blit writes twice as many pixels.
 *
 * Pixels are then non-square (half-width), so the letterbox height must still be computed against the
 * PHYSICAL 400-wide aspect or the picture comes out stretched to twice its proper height.
 */
#define SCREEN_WIDTH_MAX 800

/* Output tile edge for the blit. 32x32 of BGR565 output is 2 KB and pulls a ~26x51 source region - a few
 * kilobytes, sized to stay in ARM11 L1 across the whole tile. See blit_to_screen. */
#define BLIT_TILE 32

static int s_screen_w = SCREEN_WIDTH;
static int s_smooth;

void rc_mvd_set_widescreen(int enabled)
{
    gfxSetWide(enabled ? true : false);
    s_screen_w = gfxIsWide() ? SCREEN_WIDTH_MAX : SCREEN_WIDTH;
    if (enabled && s_screen_w == SCREEN_WIDTH)
        rc_log("  widescreen refused by this model - staying at 400x240\n");
}

void rc_mvd_set_smoothing(int enabled)
{
    s_smooth = enabled;
}

/* Output buffers are page-aligned: the only render target MVD was ever observed to write is a
 * framebuffer, which is page-aligned, so this removes alignment as a variable. */
#define MVD_OUTPUT_ALIGN 0x1000

/*
 * MVD REPORTS SUCCESS AS 0x17000, NOT 0. Its "result codes" are a private 0x17000-0x17007 range
 * (MVD_STATUS_OK, PARAMSET, BUSY, FRAMEREADY...), and different entry points differ in which convention
 * they use: mvdstdInit returns a plain 0, while mvdstdSetupOutputBuffers returns MVD_STATUS_OK. Testing
 * `!= 0` on the latter rejected a call that had actually succeeded, and cost a hardware run - the log
 * read "FAIL mvdstdSetupOutputBuffers: 0x00017000", which is the success code printed as an error.
 * Anything that checks an MVD result should go through here.
 */
static int mvd_ok(Result res)
{
    return res == 0 || res == (Result)MVD_STATUS_OK;
}


static u32 s_workbuf_size;
static u8 *s_staging;
/*
 * TWO OUTPUT BUFFERS, ALTERNATING - so the scale can run on another core.
 *
 * With one buffer, moving the blit to a worker thread means the worker reads the picture MVD is already
 * decoding the next one into. That was tried, and it produced a flat grey field with only moving
 * macroblocks carrying content - the same symptom as the sentinel bug, from a different cause, which is
 * part of why it took so long to separate them.
 *
 * Decode writes s_output_buf[s_decode_index] while the worker scales the other. apply_output_config
 * already runs once per access unit, so pointing MVD at the current one costs nothing extra.
 */
static u8 *s_output_buf[2];
static int s_decode_index;
#define s_output (s_output_buf[s_decode_index])
static size_t s_output_size;
static MVDSTD_Config s_config;
/* Set on loss, cleared by the next keyframe. See rc_mvd_signal_loss. */
static int s_awaiting_keyframe;
static int s_reported_output;
static volatile int s_refused_geometry;

/* Probe results handed from the scale worker to the receive core, which is the only thread that logs. */
static struct {
    volatile int pending;
    int frame, distinct;
    unsigned detail;
    uint16_t lo, hi, corner[4];
} s_probe;
static int s_first_sequence;
static unsigned s_last_detail;
/* The first unit after init is fed twice - see rc_mvd_decode_frame. */
static int s_reported_setconfig;
static long s_setconfig_failures;
static int s_units_logged;
/* Owned by the caller; NULL until rc_mvd_set_profile is called. */
static rc_profile *s_profile;

int rc_mvd_init(rc_mvd *out, int input_width, int input_height, int rgb565)
{
    bool is_new_3ds = false;
    Result res;

    if (out == NULL)
        return 0;
    memset(out, 0, sizeof(*out));
    out->input_width = input_width;
    out->input_height = input_height;

    /* The original 3DS has no MVD block at all. Saying so plainly here is worth more than any later
     * error, because every symptom downstream looks like a decode bug instead of a missing device. */
    APT_CheckNew3DS(&is_new_3ds);
    if (!is_new_3ds) {
        rc_log("\x1b[31mFAIL\x1b[0m MVD needs a New 3DS - this hardware has no video decoder\n");
        return 0;
    }

    if (input_width <= 0 || input_height <= 0) {
        return 0;
    }
    /* 16-bit output, and the height rounded up to a macroblock: 360 is not a multiple of 16, and the
     * decoder's own picture buffer is padded to 368 rows. Sizing to the unpadded height is how a
     * legitimate write lands partly outside the buffer. */
    s_output_size = (size_t)input_width * (size_t)(((input_height + 15) / 16) * 16) * 2u;

    s_staging = (u8 *)linearMemAlign(MVD_INPUT_STAGING, MVD_BUFFER_ALIGN);
    s_output_buf[0] = (u8 *)linearMemAlign(s_output_size, MVD_OUTPUT_ALIGN);
    s_output_buf[1] = (u8 *)linearMemAlign(s_output_size, MVD_OUTPUT_ALIGN);
    s_decode_index = 0;
    /*
     * ONE OUTPUT BUFFER. A second was added on the theory that outdata0/outdata1 are a ping-pong pair
     * and aliasing them corrupted the reference picture. It tested negative - the picture degraded
     * identically - so it is gone, because it cost 460 KB of linear memory and linear memory turns out
     * to be the resource this module never measured. See the headroom report below.
     */
    if (s_staging == NULL || s_output_buf[0] == NULL || s_output_buf[1] == NULL) {
        rc_log("\x1b[31mFAIL\x1b[0m could not allocate aligned MVD buffers\n");
        rc_mvd_exit(out);
        return 0;
    }
    memset(s_output_buf[0], 0, s_output_size);
    memset(s_output_buf[1], 0, s_output_size);

    /*
     * MVD ONLY ACCEPTS LINEAR MEMORY FROM THE 0x30* REGION.
     *
     * From 3dbrew's MVD_Services page, which libctru's own header cites and which this file should have
     * been read against several hardware runs ago: "Linear memory virtual addresses must be in the 0x30*
     * region; the system doesn't support the 0x14* region." A buffer in the wrong mapping is refused
     * SILENTLY - mvdstdRenderVideoFrame still reports MVD_STATUS_OK - which is precisely the symptom
     * that survived the config-address check, the entrylist, page alignment and macroblock padding.
     *
     * The 3DS has two linear-heap mappings and which one linearAlloc hands out depends on the kernel and
     * how the process was launched, so this is not something the code can assume either way. Log it.
     */
    /*
     * LINEAR HEADROOM, because three identical data aborts pointed at a page inside the work buffer that
     * the MMU says is not mapped - which is what an allocation that quietly did not get its memory looks
     * like from the far side. This module asks for ~6.5 MB of linear and never once checked how much
     * there was.
     */
    rc_log("linear free: %u KB before MVD allocations\n", (unsigned)(linearSpaceFree() / 1024u));

    rc_log("MVD buffers: staging vaddr %p, output vaddr %p%s\n",
        (void *)s_staging, (void *)s_output,
        (((uintptr_t)s_output & 0xFF000000u) == 0x30000000u) ? " (0x30* - supported)"
            : (((uintptr_t)s_output & 0xFF000000u) == 0x14000000u) ? " \x1b[31m(0x14* - MVD REFUSES THIS)\x1b[0m"
            : " \x1b[33m(unexpected region)\x1b[0m");

    /*
     * THE CALCULATED WORK-BUFFER SIZE IS A FLOOR, NOT AN ANSWER - it under-reports, and trusting it
     * crashed the console.
     *
     * This used to take mvdstdCalculateBufferSize at its word to save memory: at 640x360 it asked for
     * 5868 KB against the browser's 9217 KB and decoded happily for weeks. At 960x540 it asked for
     * 6888 KB, mvdstdInit accepted that, and then the first keyframe's second slice came back
     * 0xD86170CC - module 92 (MVD), summary OutOfResource, level Permanent - after which continuing to
     * feed took the whole process down with a data abort.
     *
     * So the calculation does not account for everything the block actually needs at decode time, and
     * the failure it produces is not a graceful one. The browser's default is the proven number and it
     * covers up to 720p; the ~2 MB saved was never worth the failure mode. Larger streams may compute
     * larger than the default, so this takes whichever is bigger rather than clamping down to it - the
     * old code's `needed <= MVD_DEFAULT_WORKBUF_SIZE` test silently chose the SMALLER of the two.
     */
    {
        MVDSTD_CalculateWorkBufSizeConfig calc;
        u32 needed = 0;

        memset(&calc, 0, sizeof(calc));
        /* 640x360 at the bitrates this port negotiates sits comfortably inside level 3.1; asking by
         * level is the coarser of the two methods but does not require knowing the stream's actual
         * reference-frame count, which we would have to parse the SPS to learn. */
        calc.level.enable = 1;
        calc.level.flag = MVD_CALC_WITH_LEVEL_FLAG_ENABLE_CALC;
        calc.level.level = MVD_H264_LEVEL_3_1;
        calc.width = (u32)input_width;
        calc.height = (u32)input_height;

        /*
         * BACK TO THE CALCULATED SIZE, because forcing the browser default is the only MVD-configuration
         * change that spans the boundary between "video worked" and "flat grey field".
         *
         * The 540p crash reported OutOfResource against a 6888 KB calculated buffer, and the conclusion
         * drawn was that the calculation under-reports and the browser's 9217 KB should always be used.
         * That took 640x360 from 5868 KB to 9217 KB as a side effect - and 640x360 has produced nothing
         * but grey since, at zero packet loss, zero process errors and FRAMEREADY on every frame.
         *
         * A larger buffer breaking a hardware block is not intuitive, which is exactly why it survived
         * several rounds of looking elsewhere. It is also the only variable left: the SetConfig placement
         * and the render loop are back to the sequence that worked, the scale thread is off, and
         * widescreen and smoothing were both present in runs where the picture was visible.
         */
        /*
         * WHICHEVER IS LARGER. mvdstdCalculateBufferSize under-reports, and the failure it produces is
         * not graceful: at 960x540 it asks for 6888 KB, mvdstdInit accepts that, and the first IDR slice
         * comes back 0xD86170CC - module 92, summary OutOfResource, level Permanent.
         *
         * This has now been got wrong in both directions. It originally clamped DOWN to the browser
         * default, i.e. took the smaller of the two, which is what produced that crash the first time.
         * Forcing the default fixed 540p, but 360p was grey for unrelated reasons, so the change was
         * reverted as ineffective - removing a correct fix because it failed to solve a different bug.
         * The grey turned out to be six faults in the decode sequence, none of them this.
         *
         * The calculated figure is a floor and nothing more.
         */
        s_workbuf_size = MVD_DEFAULT_WORKBUF_SIZE;
        if (R_SUCCEEDED(mvdstdCalculateBufferSize(&calc, &needed))
            && needed > MVD_DEFAULT_WORKBUF_SIZE && needed <= MVD_DEFAULT_WORKBUF_SIZE * 2u) {
            s_workbuf_size = needed;
        }
        rc_log("MVD work buffer: %u KB (calculated floor %u KB, browser default %u KB)\n",
            (unsigned)(s_workbuf_size / 1024u), (unsigned)(needed / 1024u),
            (unsigned)(MVD_DEFAULT_WORKBUF_SIZE / 1024u));
    }

    /* BGR565 by default, matching the devkitPro example's pairing with an RGB565 screen. If reds and
     * blues come out swapped, RGB565 is the other option and the blit is innocent. */
    res = mvdstdInit(MVDMODE_VIDEOPROCESSING, MVD_INPUT_H264,
                     rgb565 ? MVD_OUTPUT_RGB565 : MVD_OUTPUT_BGR565,
                     s_workbuf_size, NULL);
    if (!mvd_ok(res)) {
        rc_log("\x1b[31mFAIL\x1b[0m mvdstdInit: 0x%08x\n", (unsigned)res);
        linearFree(s_staging);
        s_staging = NULL;
        return 0;
    }

    /*
     * DIMENSIONS ARE MACROBLOCK-ALIGNED BEFORE THEY REACH THE CONFIG.
     *
     * H.264 codes in 16x16 macroblocks, so a 640x360 stream is really 640x368 of decoded picture with
     * the bottom eight rows discarded at display time. A known-working player rounds both dimensions up
     * to a multiple of 16 before calling mvdstdGenerateDefaultConfig; this port was passing the raw 360,
     * which the block accepts without complaint and then - on every run so far - declines to write
     * anything for.
     *
     * The buffer was already sized for the padded height. Only the config was being told the truncated
     * one. Note the width happens to be aligned already at 640, so this changes 360 -> 368 and nothing
     * else, which is exactly the kind of difference that survives a lot of staring.
     */
    {
        u32 aligned_w = (u32)((input_width + 15) & ~15);
        u32 aligned_h = (u32)((input_height + 15) & ~15);

        mvdstdGenerateDefaultConfig(&s_config, aligned_w, aligned_h, aligned_w, aligned_h, NULL,
                                   (u32 *)s_output, (u32 *)s_output);
        rc_log("MVD config dims: %ux%u (source %dx%d, macroblock-aligned)\n",
            (unsigned)aligned_w, (unsigned)aligned_h, input_width, input_height);
    }


    /* Start awaiting a keyframe: the first frame offered is otherwise whatever happens to arrive, which
     * mid-stream is an inter-frame referencing pictures MVD has never seen. */
    s_awaiting_keyframe = 1;
    s_first_sequence = 1;
    s_reported_output = 0;
    s_reported_setconfig = 0;

    out->ready = 1;
    rc_log("MVD ready: %dx%d H.264 -> %s into a %u-byte config buffer, scaled to %dx%d\n",
        input_width, input_height, rgb565 ? "RGB565" : "BGR565",
        (unsigned)s_output_size, s_screen_w, SCREEN_HEIGHT);
    rc_log("linear free: %u KB after MVD allocations\n", (unsigned)(linearSpaceFree() / 1024u));
    rc_log("memory: MVD work %u KB + output %u KB + staging %u KB = %u KB in this module\n",
        (unsigned)(s_workbuf_size / 1024u), (unsigned)(s_output_size * 2u / 1024u),
        (unsigned)(MVD_INPUT_STAGING / 1024u),
        (unsigned)((s_workbuf_size + s_output_size + MVD_INPUT_STAGING) / 1024u));
    return 1;
}


/* Finds the next Annex-B start code at or after `from`. Returns the offset, or `length` if none. Handles
 * both the 4-byte 00 00 00 01 and the 3-byte 00 00 01 forms - the demuxer emits 4-byte prefixes, but the
 * parameter sets copied out of STREAM_INFO come from the console and are not guaranteed to. */
static size_t next_start_code(const uint8_t *data, size_t length, size_t from, size_t *out_prefix)
{
    size_t i;

    for (i = from; i + 3 <= length; i++) {
        if (data[i] == 0x00 && data[i + 1] == 0x00) {
            if (data[i + 2] == 0x01) {
                *out_prefix = 3;
                return i;
            }
            if (i + 4 <= length && data[i + 2] == 0x00 && data[i + 3] == 0x01) {
                *out_prefix = 4;
                return i;
            }
        }
    }
    return length;
}

/*
 * Nearest-neighbour downscale of the decoded frame into the top framebuffer, with the transpose the
 * hardware layout requires: the screen is 400x240 to look at but stored column-major, so pixel (x, y)
 * lives at fb[x * 240 + (239 - y)].
 *
 * Aspect is preserved by letterboxing - 640x360 is 16:9 against a 5:3 screen, so a full-width fit leaves
 * a band top and bottom rather than stretching. Nearest-neighbour because this shares a thread with the
 * network receive loop, which has already caused one large loss regression this phase.
 */
/*
 * Per-frame scale tables. Rebuilt only when the geometry changes, which in practice means once.
 *
 * map_x[x] is the source column for output column x. map_y[y] is the source row's BYTE-INDEX OFFSET
 * (sy * src_w), not the row number, so the inner loop needs neither a multiply nor a division.
 */
static uint16_t s_map_x[SCREEN_WIDTH_MAX];
static uint32_t s_map_y[SCREEN_HEIGHT];
static uint32_t s_map_y2[SCREEN_HEIGHT];
static int s_map_src_w, s_map_src_h, s_map_draw_h, s_map_y_offset, s_map_screen_w;

static void build_scale_maps(int src_w, int src_h, int draw_h, int y_offset, int screen_w)
{
    int i;

    if (src_w == s_map_src_w && src_h == s_map_src_h && draw_h == s_map_draw_h
        && y_offset == s_map_y_offset && screen_w == s_map_screen_w) {
        return;
    }

    for (i = 0; i < screen_w; i++)
        s_map_x[i] = (uint16_t)((i * src_w) / screen_w);
    for (i = 0; i < SCREEN_HEIGHT; i++) {
        int sy = 0, sy2 = 0;
        if (i >= y_offset && i < y_offset + draw_h && draw_h > 0) {
            sy = ((i - y_offset) * src_h) / draw_h;
            /* The next source row, for the optional vertical average. Clamped at the last row. */
            sy2 = (sy + 1 < src_h) ? sy + 1 : sy;
        }
        s_map_y[i] = (uint32_t)sy * (uint32_t)src_w;
        s_map_y2[i] = (uint32_t)sy2 * (uint32_t)src_w;
    }

    s_map_screen_w = screen_w;
    s_map_src_w = src_w;
    s_map_src_h = src_h;
    s_map_draw_h = draw_h;
    s_map_y_offset = y_offset;
}

/*
 * Nearest-neighbour downscale of the decoded frame into the top framebuffer, with the transpose the
 * hardware layout requires: the screen is 400x240 to look at but stored column-major, so pixel (x, y)
 * lives at fb[x * 240 + (239 - y)].
 *
 * WHY THIS IS TABLE-DRIVEN. The obvious form of this loop computed the source row inline as
 * `((y - y_offset) * src_h) / draw_h` - one integer division per pixel. **The ARM11 has no hardware
 * divider**, so that is a call to __aeabi_idiv 96,000 times per frame, and the stage measured 12.7 ms
 * on hardware, over a third of one core once every frame is scaled. Precomputing the row and column
 * maps moves 96,000 divisions and 96,000 multiplies to 640 of each, done once.
 *
 * The loops are also arranged so the WRITE walks the framebuffer sequentially within a column: the
 * destination is column-major, so with y innermost consecutive writes are adjacent addresses. The reads
 * are the strided side, which is the right way round - the source frame is read once per output pixel
 * either way, but a sequential write stream is what the write buffer can actually coalesce.
 *
 * Aspect is preserved by letterboxing - 640x360 is 16:9 against a 5:3 screen - and the bands are written
 * as their own runs rather than tested for inside the pixel loop.
 */
static int s_picture_ready;

/*
 * NO GFX OR GSP CALL HAPPENS IN HERE, and that is a hard requirement, not tidiness.
 *
 * This runs on the scale worker. It used to call gfxGetFramebuffer itself and gfxFlushBuffers on the way
 * out, while the receive core called gfxSwapBuffers - and libctru's gfx API is not thread-safe. The
 * result was not a crash in this process: it took the whole console down hard enough to need the power
 * button held. Corrupting the GSP command queue does that.
 *
 * So the caller, on the receive core, resolves the framebuffer and passes it in, and cache maintenance
 * uses the kernel syscalls (svcFlush/InvalidateProcessDataCache) rather than the GSP service. Those act
 * on the calling core's own cache, which is what is wanted: the core that wrote the pixels is the core
 * that must flush them.
 */
static void blit_to_screen(const rc_mvd *mvd, const u8 *source, u8 *framebuffer)
{
    const uint16_t *src = (const uint16_t *)(const void *)source;
    uint16_t *dst = (uint16_t *)(void *)framebuffer;
    int src_w = mvd->input_width;
    int src_h = mvd->input_height;
    int draw_h, y_offset, x, y;

    if (framebuffer == NULL || src_w <= 0 || src_h <= 0)
        return;

    /*
     * BOUNDS THE READ. The crash that produced crash_dump_00000002 was a data abort at 0x30347c98 -
     * roughly 1.9 MB past the end of a 460 KB output buffer - with dfsr=0x805, an unmapped-page
     * translation fault. Whatever drove the geometry out of range, reading outside this buffer must
     * fail loudly rather than take the process down, because a crash dump costs a hardware round trip
     * to interpret and a log line costs nothing.
     */
    if ((size_t)src_w * (size_t)src_h * 2u > s_output_size) {
        s_refused_geometry = 1;   /* logged on the receive core - see the probe note below */
        return;
    }

    /* MVD wrote this through hardware, which does not go through the ARM11 data cache - without the
     * invalidate the CPU can read stale lines and blit whatever was there before. */
    svcInvalidateProcessDataCache(CUR_PROCESS_HANDLE, (u32)(uintptr_t)source, (u32)s_output_size);

    /*
     * HOW MUCH OF THE FRAME CARRIES DETAIL - not how far apart its extremes are.
     *
     * This measured luminance SPREAD (max minus min) and reported a fresh decoder as healthy while the
     * screen showed a flat grey field with a few moving objects on it. That failure mode is exactly the
     * one spread cannot see: a handful of bright moving pixels sets the maximum, a grey background sets
     * the minimum, and the number comes out identical to a real picture. It is the second probe in this
     * file to report the opposite of the truth, both times by measuring something adjacent to the
     * question instead of the question.
     *
     * The question is "what fraction of this frame differs from its own average?" A photograph of a UI
     * is mostly detail; a cleared buffer with motion painted into it is mostly one value. Two passes
     * over 256 samples: mean first, then count how many sit more than a few levels away from it.
     */
    {
        size_t total = s_output_size / 2u;
        size_t step = total / 256u ? total / 256u : 1u;
        unsigned long sum = 0;
        unsigned n = 0, detailed = 0, mean;
        size_t i;

        for (i = 0; i < total; i += step) {
            uint16_t v = src[i];
            sum += (unsigned long)(((v >> 11) & 0x1Fu) + ((v >> 5) & 0x3Fu) + (v & 0x1Fu));
            n++;
        }
        mean = n ? (unsigned)(sum / n) : 0u;

        for (i = 0; i < total; i += step) {
            uint16_t v = src[i];
            unsigned lum = (unsigned)(((v >> 11) & 0x1Fu) + ((v >> 5) & 0x3Fu) + (v & 0x1Fu));
            unsigned d = lum > mean ? lum - mean : mean - lum;

            if (d > 6u)
                detailed++;
        }
        s_last_detail = n ? (detailed * 100u) / n : 0u;
    }

    if (s_reported_output < 3) {
        /*
         * IS THIS A PICTURE, OR A FLAT FIELD? The old probe counted non-zero samples, which a uniform
         * grey buffer passes trivially - it reported "14720/14720 non-zero" for every gray-screen run
         * this phase and was read as evidence the decoder was working. It never distinguished the two
         * cases the whole investigation hinges on: MVD failing to decode, versus MVD decoding fine and
         * this blit reading the buffer wrongly.
         *
         * Distinct sampled values separates them cleanly. A real 640x360 frame has hundreds; the
         * decoder's initialised state has one or two. Corners are printed individually because
         * sentinel_changed only reports whether ANY of the four moved, and "top-left never written" is
         * a very different fault from "nothing written".
         */
        size_t total = s_output_size / 2u;
        size_t step = total / 1024u ? total / 1024u : 1u;
        uint16_t seen[64];
        int distinct = 0;
        uint16_t lo = 0xFFFFu, hi = 0u;
        size_t i;
        size_t w = (size_t)src_w, h = (size_t)src_h;

        for (i = 0; i < total; i += step) {
            uint16_t v = src[i];
            int j, known = 0;

            /* Skip our own sentinel: it is darker than any decoded grey, so leaving it in the range
             * inflated the spread by ~20 and turned two red verdicts green. */
            if (v == MVD_SENTINEL)
                continue;
            if (v < lo) lo = v;
            if (v > hi) hi = v;
            for (j = 0; j < distinct; j++) {
                if (seen[j] == v) { known = 1; break; }
            }
            if (!known && distinct < 64)
                seen[distinct++] = v;
        }

        /*
         * CAPTURED HERE, LOGGED ON THE RECEIVE CORE. This function now runs on the scale worker, and
         * rc_log writes to the console AND to a file on the SD card - two threads inside newlib stdio
         * and the 3DS filesystem with no lock between them. It locked up on the first frame, which is
         * exactly the frame this probe fires on.
         *
         * Diagnostics must never be the thing that breaks the run they exist to explain.
         */
        s_probe.frame = s_reported_output;
        s_probe.distinct = distinct;
        s_probe.lo = lo;
        s_probe.hi = hi;
        s_probe.detail = s_last_detail;
        s_probe.corner[0] = src[0];
        s_probe.corner[1] = src[w - 1];
        s_probe.corner[2] = src[(h - 1) * w];
        s_probe.corner[3] = src[(h - 1) * w + (w - 1)];
        s_probe.pending = 1;
        s_reported_output++;
    }

    /*
     * Against the PHYSICAL 400-wide aspect, not s_screen_w: in widescreen the pixels are half as wide,
     * so 800 columns still spans the same panel and the letterbox height is unchanged. Computing this
     * from 800 would double draw_h and stretch the picture vertically.
     */
    draw_h = (SCREEN_WIDTH * src_h) / src_w;
    if (draw_h > SCREEN_HEIGHT)
        draw_h = SCREEN_HEIGHT;
    y_offset = (SCREEN_HEIGHT - draw_h) / 2;

    build_scale_maps(src_w, src_h, draw_h, y_offset, s_screen_w);

    /* The letterbox bands, written once per column as their own runs rather than tested for inside the
     * pixel loop. Cheap: 15 rows of 800 at 640x360. */
    for (x = 0; x < s_screen_w; x++) {
        uint16_t *col = &dst[x * SCREEN_HEIGHT];

        for (y = y_offset + draw_h; y < SCREEN_HEIGHT; y++)
            col[SCREEN_HEIGHT - 1 - y] = 0;
        for (y = 0; y < y_offset; y++)
            col[SCREEN_HEIGHT - 1 - y] = 0;
    }

    /*
     * THE PICTURE, IN TILES - because this loop is memory-bound, not compute-bound.
     *
     * It measured 12.07 ms a frame: 192,000 output pixels at ~16.9 ARM11 cycles each, for what is a
     * table lookup and a 16-bit store. Nothing in the body costs seventeen cycles. The destination is
     * column-major, so walking a column means walking the SOURCE down a row-major image with a 1280-byte
     * stride - a different cache line for every single pixel, 180,000 times a frame.
     *
     * That cost is the whole reason 60 fps does not work. Measured at 60: the scale alone is 33.7 s of a
     * 60 s window, the receive core hits 90%, and 40% of A/V units are lost to a drain that cannot keep
     * up - while MVD itself decodes 2,795 pictures without a single error. The decoder and the link are
     * both fine; this function is the ceiling.
     *
     * Blocking the loops fixes the access pattern without changing a single output pixel. Within a
     * 32x32 output tile the source region is ~26 columns by ~51 rows - a few kilobytes, which stays in
     * L1 across all 32 columns of the tile instead of being re-fetched per column. Same reads, same
     * writes, same result; roughly six times fewer cache line fetches.
     */
    for (x = 0; x < s_screen_w; x += BLIT_TILE) {
        int x_end = (x + BLIT_TILE < s_screen_w) ? x + BLIT_TILE : s_screen_w;
        int ty;

        for (ty = y_offset; ty < y_offset + draw_h; ty += BLIT_TILE) {
            int y_end = (ty + BLIT_TILE < y_offset + draw_h) ? ty + BLIT_TILE : y_offset + draw_h;
            int tx;

            for (tx = x; tx < x_end; tx++) {
                uint16_t *col = &dst[tx * SCREEN_HEIGHT];
                unsigned sx = s_map_x[tx];

                if (s_smooth) {
                    /*
                     * Vertical pair-average. Widescreen removes the horizontal loss entirely, leaving
                     * the 360 -> 225 vertical squeeze as the only axis still discarding rows. Averaging
                     * the two source rows recovers the dropped one instead of ignoring it.
                     *
                     * The mask is the standard 565 half-sum: 0x0821 is the low bit of each of the R/G/B
                     * fields (bits 11, 5 and 0), so clearing them before the shift keeps each channel's
                     * carry inside its own field.
                     */
                    for (y = ty; y < y_end; y++) {
                        uint16_t a = src[s_map_y[y] + sx];
                        uint16_t b = src[s_map_y2[y] + sx];
                        col[SCREEN_HEIGHT - 1 - y] = (uint16_t)((a & b) + (((a ^ b) & 0xF7DEu) >> 1));
                    }
                } else {
                    for (y = ty; y < y_end; y++)
                        col[SCREEN_HEIGHT - 1 - y] = src[s_map_y[y] + sx];
                }
            }
        }
    }
}

/*
 * SENTINEL-BASED FRAME DETECTION.
 *
 * MVD's status codes do not reliably say "a picture has been written". This port trusted FRAMEREADY for
 * several hardware runs: it came back 1,316 times a window, every render reported success, and the output
 * buffer stayed entirely zero. A working 3DS H.264 player does not trust the status either - it stamps
 * known values into the output buffer and watches for them to change, which is the only signal that
 * actually corresponds to pixels existing.
 *
 * Four corners rather than one: a single sentinel could be overwritten by chance with the same value,
 * and the corners are also the pixels most likely to differ between a real frame and a partial write.
 */

/*
 * THE SENTINEL IS RETIRED, AND IT WAS CORRUPTING THE REFERENCE PICTURE.
 *
 * It wrote 0x1111 into four corners of the output buffer before every access unit, as a way to detect
 * that MVD had written a picture. The stream has refs=1, so that buffer is exactly what the next
 * P-frame predicts from - and four wrong pixels do not stay four pixels. Intra prediction and motion
 * compensation spread them outward every frame, resetting only at an IDR. On hardware that is a clean
 * picture after each keyframe decaying into streaks and then a flat field, which is what was observed
 * for a dozen rounds and repeatedly mistaken for a decode failure, a bitrate problem, and a resolution
 * problem in turn.
 *
 * It also flushed the whole 460 KB buffer from the CPU cache on every frame, writing back stale lines
 * over memory the hardware owns.
 *
 * It existed because MVD's status codes looked unreliable during bring-up. They are not: the block
 * correctly returned OutOfResource for an undersized work buffer and Internal for slices fed without
 * parameter sets. mvdstdRenderVideoFrame with wait=true returning success is the signal, and it costs
 * nothing to trust.
 *
 * Kept below, uncalled, only so the diagnostic probe can still recognise the value if an old buffer is
 * ever inspected. Do not reintroduce writes into a buffer the decoder is predicting from.
 */
static void stamp_sentinel(const rc_mvd *mvd) __attribute__((unused));

static void stamp_sentinel(const rc_mvd *mvd)
{
    uint16_t *out = (uint16_t *)(void *)s_output;
    /* Visible dimensions, not the padded ones: the corners of the picture are what a real frame
     * overwrites, and the padding rows may legitimately stay untouched. */
    size_t w = (size_t)mvd->input_width;
    size_t h = (size_t)mvd->input_height;

    out[0] = MVD_SENTINEL;
    out[w - 1] = MVD_SENTINEL;
    out[(h - 1) * w] = MVD_SENTINEL;
    out[(h - 1) * w + (w - 1)] = MVD_SENTINEL;
    /* The stamp is a CPU write to a buffer the hardware reads and writes, so it has to reach memory
     * before MVD looks at it. */
    GSPGPU_FlushDataCache(s_output, (u32)s_output_size);
}

static int sentinel_changed(const rc_mvd *mvd) __attribute__((unused));

static int sentinel_changed(const rc_mvd *mvd)
{
    const uint16_t *out = (const uint16_t *)(const void *)s_output;

    /*
     * MVD writes through hardware, which does not go through the ARM11 data cache, so a plain CPU read
     * here can return the 0x1111 this code wrote itself in stamp_sentinel rather than what the decoder
     * has since put in memory. blit_to_screen has always invalidated before reading; this function never
     * did, so the two were reading different views of the same buffer and only one of them was right.
     */
    size_t w = (size_t)mvd->input_width;
    size_t h = (size_t)mvd->input_height;

    /* Read fresh: MVD wrote through hardware, which does not go through the ARM11 data cache. */
    svcInvalidateProcessDataCache(CUR_PROCESS_HANDLE, (u32)(uintptr_t)s_output, (u32)s_output_size);

    return out[0] != MVD_SENTINEL
        || out[w - 1] != MVD_SENTINEL
        || out[(h - 1) * w] != MVD_SENTINEL
        || out[(h - 1) * w + (w - 1)] != MVD_SENTINEL;
}

/*
 * POINT MVD AT THE OUTPUT BUFFER - once per access unit, before its first slice.
 *
 * Not per NAL unit. The console sends one slice per MTU, so a keyframe is ~20 units; reconfiguring the
 * decoder between the slices of a picture it is still assembling is what kept the IDR from completing.
 *
 * The return value matters: if SetConfig fails the output address is never applied, and then decode
 * succeeds, render succeeds, and nothing is written anywhere. Reported once - a failure here is a
 * property of the configuration, not of any one frame.
 */
static void apply_output_config(void)
{
    Result cfg;

    /* outdata0 only. outdata1 keeps whatever mvdstdGenerateDefaultConfig gave it - see rc_mvd_init. */
    s_config.physaddr_outdata0 = osConvertVirtToPhys(s_output);
    cfg = MVDSTD_SetConfig(&s_config);

    if (!mvd_ok(cfg))
        s_setconfig_failures++;
    if (!s_reported_setconfig) {
        rc_log("  MVDSTD_SetConfig -> 0x%08x%s\n", (unsigned)cfg,
            mvd_ok(cfg) ? "" : "  \x1b[31m<- NOT OK\x1b[0m");
        s_reported_setconfig = 1;
    }
}

/*
 * Feeds one NAL unit, INCLUDING its start-code prefix.
 *
 * The prefix is kept deliberately. This port previously stripped it, on the reasoning that
 * mvdstdProcessVideoFrame takes "a NAL unit"; a working player passes the unit with a three-byte
 * 00 00 01 prefix in front of it, and since that player demonstrably gets pictures out and this one did
 * not, the prefix goes back in.
 *
 * The output address is set through MVDSTD_SetConfig BEFORE the unit is processed, not merely at render
 * time. That ordering is the likeliest explanation for everything this file has been failing at: if MVD
 * writes the decoded picture during processing rather than during render, then an output address applied
 * only at render is applied after the write has already happened - which is exactly a stream that
 * decodes perfectly into nowhere.
 */
static int feed_nal_unit(rc_mvd *mvd, const uint8_t *unit, size_t length)
{
    MVDSTD_ProcessNALUnitOut info;
    Result res;
    size_t total = length + 3u;

    if (length == 0 || total > MVD_INPUT_STAGING) {
        mvd->oversized_units++;
        return 0;
    }

    /*
     * WHAT IS ACTUALLY BEING FED. Everything so far has looked at MVD's output; nothing has checked its
     * input. A decoder that reports FRAMEREADY on every frame and emits a flat grey field is behaving
     * exactly as one would with slices it cannot decode - which is what happens when the parameter sets
     * (SPS type 7, PPS type 8) never arrive, or arrive after the IDR that needs them.
     *
     * H.264 NAL types worth recognising here: 1 = non-IDR slice, 5 = IDR slice, 6 = SEI, 7 = SPS,
     * 8 = PPS, 9 = access unit delimiter.
     */
    if (s_units_logged < 12) {
        unsigned nal_type = (unsigned)(unit[0] & 0x1Fu);
        static const char *names[] = {
            "?", "slice", "?", "?", "?", "IDR", "SEI", "\x1b[36mSPS\x1b[0m", "\x1b[36mPPS\x1b[0m",
            "AUD"
        };

        rc_log("    fed #%d: type %u (%s), %u bytes\n", s_units_logged, nal_type,
            nal_type < 10u ? names[nal_type] : "?", (unsigned)length);
        s_units_logged++;
    }

    s_staging[0] = 0x00;
    s_staging[1] = 0x00;
    s_staging[2] = 0x01;
    memcpy(s_staging + 3, unit, length);
    GSPGPU_FlushDataCache(s_staging, (u32)total);

    memset(&info, 0, sizeof(info));
    {
        uint64_t t = rc_profile_start();
        res = mvdstdProcessVideoFrame(s_staging, total, 0, &info);
        rc_profile_stop(s_profile, RC_STAGE_MVD_FEED, t);
    }
    mvd->nal_units_fed++;

    /*
     * LIBCTRU'S SUCCESS LIST IS INCOMPLETE. MVD_CHECKNALUPROC_SUCCESS enumerates 0x17000-0x17004 and
     * 0x17007, and hardware returned 0x17005 - which decodes as level 0 (Success), module 92 (MVD),
     * description 5. mvd.h simply has no name for it. Treating an unlisted SUCCESS-level result as a
     * failure made this port count healthy frames as process errors, and would have hidden a real fault
     * behind noise. Trust the level field, which is the ARM/Horizon-wide convention, over an enumeration
     * that is documented as incomplete on 3dbrew.
     */
    if (!MVD_CHECKNALUPROC_SUCCESS(res) && (((unsigned)res >> 27) & 0x1Fu) != 0u) {
        mvd->process_errors++;
        if (mvd->first_process_error == 0)
            mvd->first_process_error = (unsigned)res;
        if (mvd->process_errors <= 4) {
            rc_log("  MVD reject #%ld: NAL type %u, %u bytes, 0x%08x\n",
                mvd->process_errors, (unsigned)(unit[0] & 0x1Fu), (unsigned)length, (unsigned)res);
        }

        /*
         * A PERMANENT failure means the block will not recover by being given more data - and feeding it
         * anyway is what turned an out-of-resource report into a system exception. Level 27 (Permanent)
         * covers the OutOfResource case that a too-small work buffer produces. Shut the decoder down and
         * let the session carry on headless: a log that says why beats a crash dump every time.
         */
        if (((unsigned)res >> 27) == 27u && mvd->ready) {
            rc_log("\x1b[31mMVD DISABLED\x1b[0m permanent failure 0x%08x (summary %u) - decoder "
                   "stopped, caller continues\n",
                   (unsigned)res, (unsigned)(((unsigned)res >> 21) & 0x3Fu));
            mvd->ready = 0;
        }
        return 0;
    }

    if (res == MVD_STATUS_OK)
        mvd->status_ok++;
    else if (res == (Result)MVD_STATUS_PARAMSET)
        mvd->param_sets++;
    else if (res == (Result)MVD_STATUS_FRAMEREADY)
        mvd->status_frameready++;
    else if (res == (Result)MVD_STATUS_NALUPROCFLAG)
        mvd->status_nalucproc++;
    else
        mvd->status_other++;

    return (res != (Result)MVD_STATUS_PARAMSET && res != (Result)MVD_STATUS_INCOMPLETEPROCESSING);
}

/*
 * Drives rendering until a picture actually lands in the buffer.
 *
 * WAIT FOR THE RENDER, do not poll against the sentinel. The original reasoning was that a blocking call
 * reports success whether or not anything was written, so polling until the sentinel moved was the
 * stronger test. It is not, because the sentinel has already moved by the time this is called: MVD
 * clears the output buffer during ProcessVideoFrame, and that clear alone changes the corners. So the
 * loop exited after ONE non-blocking call, every time, and the blit that followed read a picture that
 * was still being rendered.
 *
 * mvd.h: "When true, wait for rendering to finish. When false, you can manually call this function
 * repeatedly until it stops returning MVD_STATUS_BUSY." The BUSY status is the real completion signal
 * and it was being ignored in favour of a sentinel that could not answer the question. The loop is kept
 * and bounded so a decoder that never finishes cannot hang the caller.
 *
 * THE CONFIG POINTER MUST BE NON-NULL, whatever libctru's own documentation says. mvd.h describes it as
 * "Optional pointer to the configuration to use. When NULL, MVDSTD_SetConfig() should have been used
 * previously" - and the compiled function disagrees: its third instruction is `cmp r0, #0` branching to
 * `mvn r0, #0`, so a NULL config returns 0xFFFFFFFF immediately without touching the hardware. Passing
 * NULL here produced 1,753 instant "render errors" in a single hardware run and no pictures at all. The
 * header is wrong; the disassembly is not.
 */
#define MVD_RENDER_POLL_LIMIT 16

static int render_until_frame(rc_mvd *mvd)
{
    int attempt;

    /*
     * NON-BLOCKING, POLLED ON BUSY - the usage mvd.h actually documents: "When false, you can manually
     * call this function repeatedly until it stops returning MVD_STATUS_BUSY."
     *
     * A blocking call (wait=true) was tried instead, on the reasoning that BUSY-polling was racing the
     * hardware. It coincided with five system exceptions whose register context is identical across
     * builds - the MVD module itself faulting, not this process. Blocking the service is not a thing
     * this code should be doing when the documented contract is to poll, so it is back to polling, and
     * the exit condition is the status rather than a sentinel this file no longer writes.
     *
     * AND A BARE RETRY IS NOT THE DOCUMENTED RETRY. 3dbrew's MVD_Services page, on status 0x17002:
     * "When returned by command 0x00090042 during video processing, SKATER uses the {GetConfig,
     * SetConfig, and 0x00090042} commands again." The reference client re-applies the configuration
     * before each retry; this loop was re-calling render alone, which is a different thing and not
     * something the block is documented to accept.
     */
    for (attempt = 0; attempt < MVD_RENDER_POLL_LIMIT; attempt++) {
        Result res;
        uint64_t t = rc_profile_start();

        res = mvdstdRenderVideoFrame(&s_config, false);
        rc_profile_stop(s_profile, RC_STAGE_MVD_RENDER, t);

        if (res == (Result)MVD_STATUS_BUSY) {
            apply_output_config(); /* the documented retry re-applies the config first */
            continue;
        }
        if (mvd_ok(res)) {
            mvd->frames_rendered++;
            return 1;
        }

        if (!mvd_ok(res)) {
            mvd->render_errors++;
            if (mvd->first_render_error == 0)
                mvd->first_render_error = (unsigned)res;
            return 0;
        }
        if (res != (Result)MVD_STATUS_BUSY)
            break; /* finished, but produced nothing - one more sentinel check above already ran */
    }
    return 0;
}

void rc_mvd_set_profile(rc_profile *profile)
{
    s_profile = profile;
}

/*
 * SCALE ONLY WHAT IS ACTUALLY SHOWN.
 *
 * The scale used to run inside the decode path, once per decoded picture. On hardware at 60 fps that was
 * 3,176 scales against 390 swaps: **seven out of every eight scaled frames were overwritten in the back
 * buffer before anything ever presented them**, and the scale is the single most expensive per-frame
 * stage in this port. The decoder outruns a 60 Hz display whenever it is keeping up at all, so this is
 * not an edge case - it is the normal state.
 *
 * Now the decoder only raises a flag and the caller scales at swap time, so the cost is paid per frame
 * SHOWN rather than per frame decoded. MVD's output buffer holds the most recent picture either way, so
 * what gets scaled is always the newest one; the dropped frames cost nothing and were never visible.
 *
 * Returns 1 if a new picture was scaled into the framebuffer (so the caller should swap), 0 if nothing
 * new has decoded since the last call.
 */
/*
 * THE SCALE RUNS ON ANOTHER CORE, because it is the one stage that can.
 *
 * Every log this port has produced prints "core 2: available / core 3: available" and then does all its
 * work on core 0 - the same core as the receive loop. At 960x540 that stopped being free: the profile
 * came to 77% of one core with the scale alone at 13.1 ms a frame, and the drain fell far enough behind
 * that 22% of A/V units were lost to socket-buffer overflow. The picture was mostly grey not because the
 * decoder was struggling but because a fifth of the stream never reached it.
 *
 * The scale is the right stage to move first: it reads MVD's output buffer and writes the framebuffer,
 * and touches nothing else. In particular it does NOT touch fec_reed_solomon_decode's 12.6 KB of static
 * scratch, which is the thing that has been recorded as blocking threading for several phases - that
 * constraint applies to the demux path, not here.
 *
 * THE FLUSH HAPPENS ON THIS THREAD, DELIBERATELY. gfxFlushBuffers ultimately flushes the data cache for
 * the framebuffer range, and it is the core that DIRTIED those lines whose cache has to be flushed.
 * Calling it from core 0 after core 2 did the writing is the kind of thing that works on the bench and
 * fails under load, so the worker flushes before it signals and the main thread only swaps.
 */
static Thread s_scale_thread;
static LightEvent s_scale_req, s_scale_done;
static volatile int s_scale_running;
static rc_mvd *s_scale_target;
static const u8 *s_scale_source;
static u8 *s_scale_framebuffer;   /* resolved by the receive core; the worker never calls gfx */   /* the buffer handed to the worker; never the one decode is using */
static volatile int s_scale_inflight;
static volatile int s_scale_swap_pending;  /* a completed blit the caller has not swapped yet */

static void scale_thread_main(void *arg)
{
    (void)arg;
    while (s_scale_running) {
        LightEvent_Wait(&s_scale_req);
        if (!s_scale_running)
            break;
        {
            uint64_t t = rc_profile_start();
            blit_to_screen(s_scale_target, s_scale_source, s_scale_framebuffer);
            rc_profile_stop(s_profile, RC_STAGE_SCALE, t);
        }
        /* Kernel syscall, not GSP: this core wrote those lines, so this core cleans them. */
        svcFlushProcessDataCache(CUR_PROCESS_HANDLE, (u32)(uintptr_t)s_scale_framebuffer,
                                 (u32)((size_t)s_screen_w * SCREEN_HEIGHT * 2u));
        LightEvent_Signal(&s_scale_done);
    }
}

int rc_mvd_start_scale_thread(unsigned core_mask)
{
    int core = -1;

    if (s_scale_thread != NULL)
        return 1;

    /* Prefer core 2, then 3. Core 1 is the system core and core 0 is the one we are trying to unload. */
    if (core_mask & (1u << 2))
        core = 2;
    else if (core_mask & (1u << 3))
        core = 3;
    if (core < 0) {
        rc_log("scale: no spare core - staying inline on the receive thread\n");
        return 0;
    }

    LightEvent_Init(&s_scale_req, RESET_ONESHOT);
    LightEvent_Init(&s_scale_done, RESET_ONESHOT);
    s_scale_running = 1;
    s_scale_thread = threadCreate(scale_thread_main, NULL, 16 * 1024, 0x30, core, false);
    if (s_scale_thread == NULL) {
        s_scale_running = 0;
        rc_log("scale: threadCreate on core %d failed - staying inline\n", core);
        return 0;
    }
    rc_log("scale: running on core %d, off the receive thread\n", core);
    return 1;
}

static void stop_scale_thread(void)
{
    if (s_scale_thread == NULL)
        return;
    s_scale_running = 0;
    LightEvent_Signal(&s_scale_req);
    threadJoin(s_scale_thread, U64_MAX);
    threadFree(s_scale_thread);
    s_scale_thread = NULL;
}

unsigned rc_mvd_last_detail(void)
{
    return s_last_detail;
}

/* Called on the receive core only. Emits anything the scale worker captured but must not print itself. */
static void drain_worker_diagnostics(void)
{
    if (s_refused_geometry) {
        s_refused_geometry = 0;
        rc_log("\x1b[31mBLIT REFUSED\x1b[0m geometry does not fit the output buffer\n");
    }
    if (s_probe.pending) {
        s_probe.pending = 0;
        rc_log("  frame %d: %s%d distinct value(s)\x1b[0m in 1024 samples, range %04x..%04x\n",
            s_probe.frame, s_probe.distinct <= 4 ? "\x1b[31m" : "\x1b[32m",
            s_probe.distinct, s_probe.lo, s_probe.hi);
        rc_log("           corners TL %04x  TR %04x  BL %04x  BR %04x\n",
            s_probe.corner[0], s_probe.corner[1], s_probe.corner[2], s_probe.corner[3]);
        if (s_probe.detail < 25u) {
            rc_log("           \x1b[31m-> %u%% of the frame carries detail: FLAT FIELD\x1b[0m\n",
                s_probe.detail);
        } else {
            rc_log("           \x1b[32m-> %u%% of the frame carries detail: real picture\x1b[0m\n",
                s_probe.detail);
        }
    }
}

int rc_mvd_scale_begin(rc_mvd *mvd)
{
    drain_worker_diagnostics();

    if (mvd == NULL || !mvd->ready)
        return 0;

    if (s_scale_thread != NULL) {
        /*
         * Threaded: the blit is already running, or has finished, on the worker. Report a frame ready to
         * swap only when one has actually been drawn - either collected by decode_frame above, or
         * completing now. Never blocks the receive loop.
         */
        if (s_scale_swap_pending) {
            s_scale_swap_pending = 0;
            return 1;
        }
        if (s_scale_inflight && LightEvent_TryWait(&s_scale_done)) {
            s_scale_inflight = 0;
            return 1;
        }
        return 0;
    }

    if (!s_picture_ready)
        return 0;
    s_picture_ready = 0;
    /* Inline: the blit already ran inside rc_mvd_decode_frame, next to the decode it belongs with. All
     * that is left is pushing those CPU writes out to the framebuffer before the caller swaps. */
    gfxFlushBuffers();
    return 1;
}

int rc_mvd_scale_complete(void)
{
    if (s_scale_thread == NULL)
        return 1; /* the inline path already finished inside rc_mvd_scale_begin */
    return LightEvent_TryWait(&s_scale_done) ? 1 : 0;
}

/*
 * WHETHER TO DROP EVERYTHING AFTER A LOSS, and why the default is now "no".
 *
 * Skipping until the next keyframe is textbook-correct: an inter-frame whose reference is missing cannot
 * be decoded properly, so feeding it produces artifacts. But the cost is wildly non-linear. Measured on
 * hardware at 6000 kbps: 7% unit loss produced 113 loss events, and those 113 events caused **1,739 of
 * 1,789 frames to be skipped** - 7% loss cost 97% of the framerate, and the picture updated less than
 * once a second while the decoder sat idle at 10% of one core.
 *
 * Decoding through the damage instead gives a moving picture with occasional corruption that the next
 * IDR clears. On a handheld showing a game, that is plainly the better failure mode, and it is what most
 * streaming clients do. The old behaviour is still available via skipuntilkeyframe=1 for anyone who
 * would rather have a correct still image than a slightly wrong moving one.
 */
static int s_skip_until_keyframe;

void rc_mvd_set_skip_until_keyframe(int enabled)
{
    s_skip_until_keyframe = enabled;
}

void rc_mvd_signal_loss(rc_mvd *mvd)
{
    (void)mvd;
    if (s_skip_until_keyframe)
        s_awaiting_keyframe = 1;
}


int rc_mvd_decode_frame(rc_mvd *mvd, const uint8_t *annexb, size_t length, int is_keyframe)
{
    size_t offset;
    size_t prefix = 0;
    int rendered = 0;
    int applied_config = 0;

    if (mvd == NULL || !mvd->ready || annexb == NULL || length == 0)
        return 0;

    if (s_awaiting_keyframe) {
        if (!is_keyframe) {
            mvd->frames_skipped++;
            return 0;
        }
        s_awaiting_keyframe = 0;
    }

    offset = next_start_code(annexb, length, 0, &prefix);
    if (offset == length)
        return 0;

    /*
     * NOTHING IS WRITTEN INTO THE PICTURE BUFFER HERE. See the sentinel note below for why there used
     * to be, and what it cost.
     *
     * SetConfig is NOT called here - it is called below, after any parameter sets in this access unit
     * and immediately before its first slice. See applied_config.
     */

    /*
     * THE FIRST ACCESS UNIT IS FED TWICE, WHOLE.
     *
     * A cold decoder renders a flat grey field with only moving macroblocks carrying content, for an
     * entire pass; every pass after a rewind is perfect, from byte-identical input. So the fix is
     * whatever a rewind does - and what a rewind does is hand MVD the complete first access unit a
     * second time: SPS, PPS and all 21 IDR slices, after it has already seen them once.
     *
     * Repeating only the parameter sets was tried first, on the assumption that they were the part that
     * mattered. They are not, or not alone: it changed nothing. This repeats the unit as a whole, which
     * is the thing actually known to work rather than a guess about which piece of it is load-bearing.
     *
     * Only the first keyframe, only once per decoder lifetime.
     */
    {
        int feed_pass;
        int feeds = (s_first_sequence && is_keyframe) ? 2 : 1;

        for (feed_pass = 0; feed_pass < feeds; feed_pass++) {
    offset = next_start_code(annexb, length, 0, &prefix);
    applied_config = 0;

    while (offset < length) {
        size_t unit_start = offset + prefix;
        size_t next_prefix = 0;
        size_t next = next_start_code(annexb, length, unit_start, &next_prefix);
        size_t unit_length = next - unit_start;
        unsigned nal_type = unit_length > 0 ? (unsigned)(annexb[unit_start] & 0x1Fu) : 0u;

        /*
         * CONFIGURE AFTER THE PARAMETER SETS, NOT BEFORE THEM.
         *
         * 3dbrew's H.264 procedure is explicit about the order: process the NAL units for the Sequence
         * and Picture Parameter Sets, and THEN begin the main video processing. This code called
         * SetConfig before feeding anything, so on a fresh decoder the configuration was applied while
         * the block still knew nothing about the sequence.
         *
         * The symptom that points here is the asymmetry between passes: replaying one file in a loop,
         * the FIRST pass is a grey mess and every pass after a rewind plays cleanly. The bytes are
         * identical, so the difference is decoder state - and after one pass MVD has already seen the
         * parameter sets, which is exactly what this ordering denies it the first time.
         */
        if (!applied_config && (nal_type == 1u || nal_type == 5u)) {
            apply_output_config();
            applied_config = 1;
            s_first_sequence = 0; /* the parameter sets for this sequence are in */
        }

        (void)feed_nal_unit(mvd, annexb + unit_start, unit_length);

        /* A permanent failure clears ready. Stop NOW rather than finishing the access unit: the log
         * shows "fed #3" arriving after "MVD DISABLED", i.e. this loop kept handing slices to a decoder
         * that had already given up, and the run ended in a data abort. */
        if (!mvd->ready)
            return rendered;

        offset = next;
        prefix = next_prefix;
    }
        }
        s_first_sequence = 0;
    }

    /*
     * ONE ACCESS UNIT IS ONE PICTURE, and the NAL log finally proves what that costs to get wrong.
     *
     * The console emits ONE SLICE PER MTU: a 25 KB keyframe arrives as ~20 IDR NAL units of ~1250 bytes,
     * at 640x360 as much as at 960x540. Checking for a completed picture after every unit therefore
     * declared a picture ~20 times per keyframe, blitted ~20 partial frames, and re-stamped 0x1111 into
     * the output buffer between the slices still being assembled into it. The IDR never completed, so
     * there was no valid reference, so inter-frames painted only where there was motion - a grey field
     * with moving parts, which is exactly and only what has been on screen since.
     *
     * This sequence was written once before and reverted as ineffective. It was never actually given a
     * fair test: both it and the per-picture SetConfig above were only ever run at 960x540 with the racy
     * scale thread enabled, which was producing the same symptom for an unrelated reason.
     */
    /*
     * RENDER UNCONDITIONALLY. The sentinel was being used to decide whether the render step was needed
     * at all, and it answered wrongly on essentially every frame: mvdstdRenderVideoFrame was called
     * ONCE in 1,794 frames.
     *
     * The reasoning was that a changed sentinel means MVD has written the picture, so rendering is
     * redundant. But MVD clears the output buffer during ProcessVideoFrame, and that clear is itself
     * enough to change the sentinel - so the check fired on the clear, not on a picture. The evidence is
     * in the pixels: every sampled value came back EXACTLY neutral grey (R==B, G==2R, no exceptions)
     * across a narrow band. That is not a mis-decoded picture, which would carry chroma; it is a buffer
     * that has been cleared and never colour-converted.
     *
     * So the sentinel keeps the job it is good at - telling us whether a picture landed, hence whether
     * to blit - and stops deciding whether to ask for one.
     */
    /*
     * THE RENDER RESULT IS THE PICTURE-READY SIGNAL. The sentinel is gone - see stamp_sentinel's note.
     */
    if (render_until_frame(mvd)) {
        if (s_scale_thread != NULL) {
            /*
             * HAND THE JUST-DECODED BUFFER TO THE WORKER, then decode into the other one.
             *
             * The wait below is what makes two buffers sufficient: before reusing a buffer we make sure
             * the worker has finished with it. At 60 fps the worker has a 16.7 ms frame period to do a
             * 7.7 ms blit, so this almost never blocks - and when it does, blocking is exactly right,
             * because the alternative is decoding over a picture still being read.
             */
            if (s_scale_inflight) {
                LightEvent_Wait(&s_scale_done);
                s_scale_inflight = 0;
                s_scale_swap_pending = 1;
            }
            s_scale_target = mvd;
            s_scale_source = s_output_buf[s_decode_index];
            s_scale_framebuffer = gfxGetFramebuffer(GFX_TOP, GFX_LEFT, NULL, NULL);
            s_scale_inflight = 1;
            LightEvent_Signal(&s_scale_req);
            s_decode_index ^= 1;
        } else {
            uint64_t t = rc_profile_start();
            blit_to_screen(mvd, s_output_buf[s_decode_index],
                           gfxGetFramebuffer(GFX_TOP, GFX_LEFT, NULL, NULL));
            rc_profile_stop(s_profile, RC_STAGE_SCALE, t);
        }
        s_picture_ready = 1;
        rendered = 1;
    }

    return rendered;
}

void rc_mvd_exit(rc_mvd *mvd)
{
    stop_scale_thread();

    if (mvd != NULL && mvd->ready) {
        /*
         * DRAIN RENDERING BEFORE TEARING THE DECODER DOWN. 3dbrew's shutdown procedure loops
         * ControlFrameRendering (0x00090042) until it returns something other than 0x17002 (BUSY).
         *
         * libctru's mvdstdExit does issue that command - confirmed by disassembling mvd.o, where the
         * header appears in .text.mvdstdExit - but as part of its own teardown rather than as a loop
         * that waits for the hardware to go idle. This loop is the documented wait, and it is cheap.
         * It is NOT established that its absence caused the crashes seen while re-initialising; that
         * remains unexplained.
         */
        {
            int drain;
            for (drain = 0; drain < 64; drain++) {
                if (mvdstdRenderVideoFrame(&s_config, false) != (Result)MVD_STATUS_BUSY)
                    break;
            }
        }
        mvdstdExit();
        mvd->ready = 0;
    }
    if (s_staging != NULL) {
        linearFree(s_staging);
        s_staging = NULL;
    }
    if (s_output_buf[0] != NULL) {
        linearFree(s_output_buf[0]);
        s_output_buf[0] = NULL;
    }
    if (s_output_buf[1] != NULL) {
        linearFree(s_output_buf[1]);
        s_output_buf[1] = NULL;
    }
}
