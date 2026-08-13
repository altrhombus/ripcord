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
 * THE WAY OUT IS mvdstdSetupOutputBuffers(), WHICH IS WHAT THIS FILE NOW USES.
 *
 * The obvious alternative is closed: making the stream screen-sized would let MVD render straight to the
 * framebuffer, but the console refuses 400x240 outright - two DISCONNECT messages within a second of
 * sealing, twice, reproducibly. A non-standard resolution is not negotiable.
 *
 * So a 640x360 frame has to reach a 400x240 screen, which needs an intermediate buffer, which is exactly
 * what finding (3) says does not work via the config. mvdstdSetupOutputBuffers is the documented
 * mechanism for that case: "rendered frames will be written to the output buffers specified by the
 * entrylist INSTEAD OF the output specified by configuration". Registering the buffer through the
 * entrylist rather than assigning it to config.physaddr_outdata0 is the difference between the two, and
 * it is the one thing never tried. The ARM11 then scales the frame into the framebuffer - a pass that
 * was already written and proven to reach the screen, by a CPU-drawn test square that appeared while the
 * decoded frame did not.
 *
 * If this does not work either, the remaining options are MVD's own input cropping (enable_cropping +
 * input_crop_*, showing a 400x240 window of the frame rather than the whole of it) or a PICA200 pass -
 * and the latter still needs the frame to land somewhere first, so it depends on this working.
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

/*
 * The candidate output geometries, walked with Y on the device.
 *
 * `transposed` expresses the config in the framebuffer's memory orientation (240x400 for a 400x240
 * screen) rather than the picture's - the devkitPro example does this and it is unclear whether that is
 * because MVD wants it or because its source happened to be portrait. `crop` uses MVD's own
 * enable_cropping fields to take a screen-sized window out of the middle of the frame, which is the only
 * way a 640x360 source can produce a 400x240 output if the block genuinely will not scale. `to_screen`
 * renders straight into the framebuffer - the one target MVD has ever been observed to write - and is
 * only legal when the output fits in 400x240.
 *
 * Candidate 0 is the current behaviour, kept first so a run starts from the known state.
 */
typedef struct {
    const char *name;
    int transposed;
    int crop;
    int to_screen;
} mvd_candidate;

static const mvd_candidate kCandidates[] = {
    { "source-size -> our buffer (known: renders, writes nothing)", 0, 0, 0 },
    { "transposed -> our buffer",                                   1, 0, 0 },
    { "cropped 400x240 -> framebuffer",                             0, 1, 1 },
    { "cropped, transposed 240x400 -> framebuffer",                 1, 1, 1 },
    { "source-size -> framebuffer (expect overrun guard)",          0, 0, 1 },
};
#define CANDIDATE_COUNT ((int)(sizeof(kCandidates) / sizeof(kCandidates[0])))

static int s_candidate;
/* Whether the CURRENT candidate has actually decoded a frame. Without this a candidate that never got a
 * keyframe is indistinguishable in the log from one that got frames and produced nothing. */
static int s_candidate_decoded;
static void apply_candidate(const rc_mvd *mvd);

static u32 s_workbuf_size;
static u8 *s_staging;
static u8 *s_output;
static size_t s_output_size;
static MVDSTD_Config s_config;
/* Set on loss, cleared by the next keyframe. See rc_mvd_signal_loss. */
static int s_awaiting_keyframe;
static int s_reported_output;
/* Owned by the caller; NULL until rc_mvd_set_profile is called. */
static rc_profile *s_profile;

int rc_mvd_init(rc_mvd *out, int input_width, int input_height)
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
    s_output = (u8 *)linearMemAlign(s_output_size, MVD_OUTPUT_ALIGN);
    if (s_staging == NULL || s_output == NULL) {
        rc_log("\x1b[31mFAIL\x1b[0m could not allocate aligned MVD buffers\n");
        rc_mvd_exit(out);
        return 0;
    }
    memset(s_output, 0, s_output_size);

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
    rc_log("MVD buffers: staging vaddr %p, output vaddr %p%s\n",
        (void *)s_staging, (void *)s_output,
        (((uintptr_t)s_output & 0xFF000000u) == 0x30000000u) ? " (0x30* - supported)"
            : (((uintptr_t)s_output & 0xFF000000u) == 0x14000000u) ? " \x1b[31m(0x14* - MVD REFUSES THIS)\x1b[0m"
            : " \x1b[33m(unexpected region)\x1b[0m");

    /*
     * SIZE THE WORK BUFFER FOR THIS STREAM RATHER THAN TAKING THE BROWSER'S DEFAULT.
     *
     * MVD_DEFAULT_WORKBUF_SIZE is 9.0 MB - the value the New3DS Internet Browser uses, which has to
     * cope with whatever a web page throws at it. This port decodes one known stream at one known
     * resolution, and mvdstdCalculateBufferSize will compute what that actually needs from the level
     * and reference-frame count. On a handheld where the whole port's fixed allocations came to ~10.3 MB,
     * nearly all of it this one buffer, that is worth asking for.
     *
     * Falls back to the default if the calculation fails or returns something implausible - a decoder
     * that will not start is a worse outcome than one that is generous with memory.
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

        if (R_SUCCEEDED(mvdstdCalculateBufferSize(&calc, &needed))
            && needed > 0 && needed <= MVD_DEFAULT_WORKBUF_SIZE) {
            s_workbuf_size = needed;
        } else {
            s_workbuf_size = MVD_DEFAULT_WORKBUF_SIZE;
        }
        rc_log("MVD work buffer: %u KB (browser default is %u KB)\n",
            (unsigned)(s_workbuf_size / 1024u), (unsigned)(MVD_DEFAULT_WORKBUF_SIZE / 1024u));
    }

    res = mvdstdInit(MVDMODE_VIDEOPROCESSING, MVD_INPUT_H264, MVD_OUTPUT_BGR565,
                     s_workbuf_size, NULL);
    if (!mvd_ok(res)) {
        rc_log("\x1b[31mFAIL\x1b[0m mvdstdInit: 0x%08x\n", (unsigned)res);
        linearFree(s_staging);
        s_staging = NULL;
        return 0;
    }

    apply_candidate(out);

    /*
     * Register the output buffer through the entrylist. This is the whole point of this revision: the
     * config's outdata fields were set correctly (verified on hardware, physical addresses matched) and
     * MVD still wrote nothing, and this is the documented API for directing frames at a buffer of ours
     * "instead of the output specified by configuration".
     */
    {
        MVDSTD_OutputBuffersEntryList list;

        memset(&list, 0, sizeof(list));
        list.total_entries = 1;
        list.entries[0].outdata0 = s_output;
        list.entries[0].outdata1 = s_output;

        res = mvdstdSetupOutputBuffers(&list, (u32)s_output_size);
        if (!mvd_ok(res)) {
            rc_log("\x1b[31mFAIL\x1b[0m mvdstdSetupOutputBuffers: 0x%08x\n", (unsigned)res);
            mvdstdExit();
            rc_mvd_exit(out);
            return 0;
        }
    }

    /* Start awaiting a keyframe: the first frame offered is otherwise whatever happens to arrive, which
     * mid-stream is an inter-frame referencing pictures MVD has never seen. */
    s_awaiting_keyframe = 1;
    s_reported_output = 0;
    s_candidate_decoded = 0;

    out->ready = 1;
    rc_log("MVD ready: %dx%d H.264 -> BGR565 via entrylist buffer (%u bytes), scaled to %dx%d\n",
        input_width, input_height, (unsigned)s_output_size, SCREEN_WIDTH, SCREEN_HEIGHT);
    rc_log("memory: MVD work %u KB + output %u KB + staging %u KB = %u KB in this module\n",
        (unsigned)(s_workbuf_size / 1024u), (unsigned)(s_output_size / 1024u),
        (unsigned)(MVD_INPUT_STAGING / 1024u),
        (unsigned)((s_workbuf_size + s_output_size + MVD_INPUT_STAGING) / 1024u));
    return 1;
}

/*
 * Builds the MVD config for the current candidate. Called at init and whenever the candidate changes.
 */
static void apply_candidate(const rc_mvd *mvd)
{
    const mvd_candidate *c = &kCandidates[s_candidate];
    u32 in_w = (u32)mvd->input_width;
    u32 in_h = (u32)mvd->input_height;
    u32 out_w, out_h;

    if (c->transposed) {
        u32 t = in_w;
        in_w = in_h;
        in_h = t;
    }

    if (c->crop) {
        out_w = c->transposed ? (u32)SCREEN_HEIGHT : (u32)SCREEN_WIDTH;
        out_h = c->transposed ? (u32)SCREEN_WIDTH : (u32)SCREEN_HEIGHT;
    } else {
        out_w = in_w;
        out_h = in_h;
    }

    mvdstdGenerateDefaultConfig(&s_config, in_w, in_h, out_w, out_h, NULL,
                               (u32 *)s_output, (u32 *)s_output);

    if (c->crop) {
        /* A screen-sized window from the middle of the frame. Field order in MVDSTD_Config is
         * x, y, HEIGHT, WIDTH - not the x, y, w, h the name ordering suggests at a glance. */
        s_config.enable_cropping = 1;
        s_config.input_crop_x_pos = (in_w > out_w) ? ((in_w - out_w) / 2u) : 0u;
        s_config.input_crop_y_pos = (in_h > out_h) ? ((in_h - out_h) / 2u) : 0u;
        s_config.input_crop_height = out_h;
        s_config.input_crop_width = out_w;
    }

    rc_log("MVD candidate %d/%d: %s\n", s_candidate, CANDIDATE_COUNT - 1, c->name);
    rc_log("   in %ux%u out %ux%u crop=%d target=%s\n",
        (unsigned)in_w, (unsigned)in_h, (unsigned)out_w, (unsigned)out_h, c->crop,
        c->to_screen ? "framebuffer" : "buffer");
}

int rc_mvd_renders_to_screen(const rc_mvd *mvd)
{
    if (mvd == NULL || !mvd->ready)
        return 0;
    return kCandidates[s_candidate].to_screen;
}

int rc_mvd_next_candidate(rc_mvd *mvd)
{
    if (mvd == NULL || !mvd->ready)
        return 0;
    if (!s_candidate_decoded) {
        rc_log("  (candidate %d never decoded a frame - untested, not failed)\n", s_candidate);
    }
    s_candidate = (s_candidate + 1) % CANDIDATE_COUNT;
    s_candidate_decoded = 0;
    s_reported_output = 0;
    s_awaiting_keyframe = 1; /* the new config takes effect at the next keyframe */
    mvd->frames_rendered = 0;
    mvd->render_errors = 0;
    mvd->first_render_error = 0;
    apply_candidate(mvd);
    return s_candidate;
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
static uint16_t s_map_x[SCREEN_WIDTH];
static uint32_t s_map_y[SCREEN_HEIGHT];
static int s_map_src_w, s_map_src_h, s_map_draw_h, s_map_y_offset;

static void build_scale_maps(int src_w, int src_h, int draw_h, int y_offset)
{
    int i;

    if (src_w == s_map_src_w && src_h == s_map_src_h
        && draw_h == s_map_draw_h && y_offset == s_map_y_offset) {
        return;
    }

    for (i = 0; i < SCREEN_WIDTH; i++)
        s_map_x[i] = (uint16_t)((i * src_w) / SCREEN_WIDTH);
    for (i = 0; i < SCREEN_HEIGHT; i++) {
        int sy = 0;
        if (i >= y_offset && i < y_offset + draw_h && draw_h > 0)
            sy = ((i - y_offset) * src_h) / draw_h;
        s_map_y[i] = (uint32_t)sy * (uint32_t)src_w;
    }

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
static void blit_to_screen(const rc_mvd *mvd)
{
    u8 *framebuffer = gfxGetFramebuffer(GFX_TOP, GFX_LEFT, NULL, NULL);
    const uint16_t *src = (const uint16_t *)(const void *)s_output;
    uint16_t *dst = (uint16_t *)(void *)framebuffer;
    int src_w = mvd->input_width;
    int src_h = mvd->input_height;
    int draw_h, y_offset, x, y;

    if (framebuffer == NULL || src_w <= 0 || src_h <= 0)
        return;

    /* MVD wrote this through hardware, which does not go through the ARM11 data cache - without the
     * invalidate the CPU can read stale lines and blit whatever was there before. */
    GSPGPU_InvalidateDataCache(s_output, (u32)s_output_size);

    if (!s_reported_output) {
        size_t total = s_output_size / 2u;
        size_t nonzero = 0;
        size_t i;

        for (i = 0; i < total; i += 16) {
            if (src[i] != 0)
                nonzero++;
        }
        if (nonzero > 0) {
            rc_log("  \x1b[32mcandidate %d PRODUCED PIXELS\x1b[0m: %u/%u sampled non-zero "
                   "(first %04x %04x)\n", s_candidate,
                (unsigned)nonzero, (unsigned)(total / 16u), src[0], src[1]);
        } else {
            rc_log("  candidate %d: buffer still empty (0/%u) - press Y for the next one\n",
                s_candidate, (unsigned)(total / 16u));
        }
        s_reported_output = 1;
    }

    draw_h = (SCREEN_WIDTH * src_h) / src_w;
    if (draw_h > SCREEN_HEIGHT)
        draw_h = SCREEN_HEIGHT;
    y_offset = (SCREEN_HEIGHT - draw_h) / 2;

    build_scale_maps(src_w, src_h, draw_h, y_offset);

    for (x = 0; x < SCREEN_WIDTH; x++) {
        uint16_t *col = &dst[x * SCREEN_HEIGHT];
        unsigned sx = s_map_x[x];

        /* Bottom letterbox band (highest y, so lowest indices in this column). */
        for (y = y_offset + draw_h; y < SCREEN_HEIGHT; y++)
            col[SCREEN_HEIGHT - 1 - y] = 0;

        /* The picture. No division, no multiply, no branch. */
        for (y = y_offset; y < y_offset + draw_h; y++)
            col[SCREEN_HEIGHT - 1 - y] = src[s_map_y[y] + sx];

        /* Top letterbox band. */
        for (y = 0; y < y_offset; y++)
            col[SCREEN_HEIGHT - 1 - y] = 0;
    }
}

/* Feeds one NAL unit (start code already stripped). Returns 1 if MVD says a picture is ready. */
static int feed_nal_unit(rc_mvd *mvd, const uint8_t *unit, size_t length)
{
    MVDSTD_ProcessNALUnitOut info;
    Result res;

    if (length == 0 || length > MVD_INPUT_STAGING) {
        /* Counted apart from MVD's own rejections: this one never reached the block. */
        mvd->oversized_units++;
        return 0;
    }

    memcpy(s_staging, unit, length);
    /* MVD reads through physical addresses and does not see the ARM11 data cache. */
    GSPGPU_FlushDataCache(s_staging, length);

    memset(&info, 0, sizeof(info));
    {
        uint64_t t = rc_profile_start();
        res = mvdstdProcessVideoFrame(s_staging, length, 0, &info);
        rc_profile_stop(s_profile, RC_STAGE_MVD_FEED, t);
    }
    mvd->nal_units_fed++;

    if (!MVD_CHECKNALUPROC_SUCCESS(res)) {
        mvd->process_errors++;
        if (mvd->first_process_error == 0)
            mvd->first_process_error = (unsigned)res;
        /*
         * The NAL type is the fastest way to tell a bitstream problem from a feeding problem. For H.264
         * it is the low 5 bits of the first byte: 1 = non-IDR slice, 5 = IDR, 6 = SEI, 7 = SPS, 8 = PPS.
         * Errors on type 1 mean inter-frames whose references we never had; errors on 7/8 would mean the
         * parameter sets themselves are wrong, which is a different bug entirely.
         */
        if (mvd->process_errors <= 4) {
            rc_log("  MVD reject #%ld: NAL type %u, %u bytes, 0x%08x\n",
                mvd->process_errors, (unsigned)(unit[0] & 0x1Fu), (unsigned)length, (unsigned)res);
        }
        return 0;
    }
    if (res == MVD_STATUS_PARAMSET) {
        /* SPS/PPS accepted. Not a picture, and emphatically not a failure - the first IDR is always
         * preceded by these, so treating it as an error would fail every keyframe. */
        mvd->param_sets++;
        return 0;
    }
    if (res == MVD_STATUS_INCOMPLETEPROCESSING) {
        /* Part of the unit was consumed; the rest follows in a later call. Nothing to render yet. */
        return 0;
    }

    /* Count what we actually got, so "render produced nothing" can be separated from "there was never a
     * frame to render". */
    if (res == MVD_STATUS_OK)
        mvd->status_ok++;
    else if (res == (Result)MVD_STATUS_FRAMEREADY)
        mvd->status_frameready++;
    else if (res == (Result)MVD_STATUS_NALUPROCFLAG)
        mvd->status_nalucproc++;
    else
        mvd->status_other++;

    return 1;
}

void rc_mvd_set_profile(rc_profile *profile)
{
    s_profile = profile;
}

void rc_mvd_signal_loss(rc_mvd *mvd)
{
    (void)mvd;
    s_awaiting_keyframe = 1;
}

int rc_mvd_decode_frame(rc_mvd *mvd, const uint8_t *annexb, size_t length, int is_keyframe)
{
    size_t offset;
    size_t prefix = 0;
    int rendered = 0;

    if (mvd == NULL || !mvd->ready || annexb == NULL || length == 0)
        return 0;

    /* After a loss, only a keyframe can resynchronise the decoder. Anything else is a difference against
     * a frame we do not have. */
    if (s_awaiting_keyframe) {
        if (!is_keyframe) {
            mvd->frames_skipped++;
            return 0;
        }
        s_awaiting_keyframe = 0;
    }

    offset = next_start_code(annexb, length, 0, &prefix);
    if (offset == length)
        return 0; /* no start code at all - not Annex-B, nothing to do */

    while (offset < length) {
        size_t unit_start = offset + prefix;
        size_t next_prefix = 0;
        size_t next = next_start_code(annexb, length, unit_start, &next_prefix);
        size_t unit_length = next - unit_start;

        if (feed_nal_unit(mvd, annexb + unit_start, unit_length)) {
            Result res;

            /* Point the render at whichever target this candidate uses. The framebuffer is the only one
             * MVD has ever been seen to write; our own buffer is the one that would let us scale. */
            if (kCandidates[s_candidate].to_screen) {
                u8 *framebuffer = gfxGetFramebuffer(GFX_TOP, GFX_LEFT, NULL, NULL);
                if (framebuffer == NULL)
                    break;
                s_config.physaddr_outdata0 = osConvertVirtToPhys(framebuffer);
            } else {
                s_config.physaddr_outdata0 = osConvertVirtToPhys(s_output);
            }

            {
                uint64_t t = rc_profile_start();
                res = mvdstdRenderVideoFrame(&s_config, true);
                rc_profile_stop(s_profile, RC_STAGE_MVD_RENDER, t);
            }

            if (!mvd_ok(res)) {
                mvd->render_errors++;
                if (mvd->first_render_error == 0)
                    mvd->first_render_error = (unsigned)res;
            } else {
                mvd->frames_rendered++;
                /* Set here rather than in the blit: the blit only runs for buffer candidates, so marking
                 * it there made every framebuffer candidate report "never decoded a frame" even while it
                 * was rendering successfully - which is exactly what three hardware runs showed. */
                s_candidate_decoded = 1;
                /* Rendering straight to the framebuffer needs no blit - and must not have one, since the
                 * blit would overwrite MVD's own output with a scale of our (empty) buffer. */
                if (!kCandidates[s_candidate].to_screen) {
                    uint64_t t = rc_profile_start();
                    blit_to_screen(mvd);
                    rc_profile_stop(s_profile, RC_STAGE_SCALE, t);
                }
                rendered = 1;
            }
        }

        offset = next;
        prefix = next_prefix;
    }

    return rendered;
}

void rc_mvd_exit(rc_mvd *mvd)
{
    if (mvd != NULL && mvd->ready) {
        mvdstdExit();
        mvd->ready = 0;
    }
    if (s_staging != NULL) {
        linearFree(s_staging);
        s_staging = NULL;
    }
    if (s_output != NULL) {
        linearFree(s_output);
        s_output = NULL;
    }
}
