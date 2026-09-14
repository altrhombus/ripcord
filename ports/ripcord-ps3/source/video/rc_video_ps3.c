#include "rc_video_ps3.h"

#include <malloc.h>
#include <string.h>
#include <unistd.h>

#include <rsx/rsx.h>
#include <sysutil/video.h>
#include <sysmodule/sysmodule.h>
#include "rc_platform.h"

#define RC_VIDEO_BUFFERS 2

/*
 * The RSX command buffer and the IO region it is mapped through.
 *
 * THE IO SIZE MUST BE A WHOLE NUMBER OF MEGABYTES, and the alignment must be 1 MB too. rsx.h says so in
 * as many words - "a 1 MB-aligned IO buffer allocated in main memory, which size is a multiple of one
 * megabyte" - and b80 passed 512 KB, which is neither. rsxInit answered 0x802100FF and the screen stayed
 * black. The requirement was in the documentation the rest of this file was written from; I read the
 * sequence and skipped the sentence above it.
 *
 * Kept as an explicit multiple rather than a bare hex constant so the constraint is visible at the place
 * someone would change it.
 */
#define RC_VIDEO_MB      (1024u * 1024u)

/* The host buffer is allocated once at the largest size any attempt below asks for, so the sweep does
 * not have to reallocate - and every attempt's IO size must fit inside it. */
#define RC_VIDEO_IO_MAX  (2u * RC_VIDEO_MB)

static gcmContextData *s_context;
static void *s_host_addr;
static uint32_t *s_buffer[RC_VIDEO_BUFFERS];
static u32 s_offset[RC_VIDEO_BUFFERS];
static int s_current;
static rc_video_info s_info;
static int s_open;
static int s_gcm_module;
static int s_sysutil_module;

static int fail(rc_video_info *out, const char *where, int code)
{
    out->ok = 0;
    out->failed_at = where;
    out->last_error = code;
    return 0;
}

int rc_video_open(rc_video_info *out)
{
    videoState state;
    videoConfiguration config;
    videoResolution resolution;
    s32 rc;
    int i;

    if (out == NULL)
        return 0;
    memset(out, 0, sizeof(*out));
    if (s_open) {
        *out = s_info;
        return 1;
    }

    /*
     * LOAD THE MODULES FIRST, because these libraries are PRXes and nothing in them exists until they
     * are loaded.
     *
     * b80 and b81 both got 0x802100FF out of rsxInit with nothing on screen, and the second run proved
     * the IO size was not the whole story. nm on the SDK's own libraries says why: rsxInit and
     * videoGetState are `D` symbols with matching _stub entries - PRX import pointers, not linked code.
     * sprxlinker patches the call sites at build time, which is why this links and runs, but the module
     * behind them still has to be resident at runtime, and this program called sysModuleLoad nowhere at
     * all.
     *
     * Networking did not need it because PSL1GHT's netInitialize loads its own module; the display has
     * no such courtesy.
     *
     * A module that is already loaded returns an error rather than success, so the return is recorded
     * and not treated as fatal - refusing to continue because something was already there would be a
     * new way to produce a black screen.
     */
    s_gcm_module = (int)sysModuleLoad(SYSMODULE_GCM_SYS);
    s_sysutil_module = (int)sysModuleLoad(SYSMODULE_SYSUTIL);
    out->gcm_module = s_gcm_module;
    out->sysutil_module = s_sysutil_module;

    /* 1 MB alignment AND a whole number of megabytes - see RC_VIDEO_IO_SIZE. */
    s_host_addr = memalign(RC_VIDEO_MB, RC_VIDEO_IO_MAX);
    if (s_host_addr == NULL)
        return fail(out, "memalign for the RSX IO region", 0);

    /*
     * TRY SEVERAL SIZES RATHER THAN BELIEVING ONE, which is the same discipline the log-directory probe
     * in main.c uses and for the same reason: four hypotheses about this call have now been wrong, and
     * a sweep costs one console round trip where a guess costs one each.
     *
     * The leading suspect is the relationship between the two sizes. The command buffer is carved OUT of
     * the IO region - rsxInit is documented to build a heap in what remains - and every attempt so far
     * asked for a 1 MB command buffer inside a 1 MB region, leaving nothing for that heap. PSL1GHT's own
     * convention is a small command buffer inside a much larger IO region.
     *
     * Ordered smallest-command-buffer first, so the most likely pair is also the first tried, and the
     * one that works is reported so the next build can stop sweeping.
     */
    {
        static const struct { u32 cmd; u32 io; } kAttempts[] = {
            { 0x10000u,  1u * RC_VIDEO_MB },   /* 64 KB command buffer in 1 MB - PSL1GHT's usual shape */
            { 0x20000u,  1u * RC_VIDEO_MB },
            { 0x10000u,  2u * RC_VIDEO_MB },
            { 0x80000u,  1u * RC_VIDEO_MB },
            { 1u * RC_VIDEO_MB, 2u * RC_VIDEO_MB }
        };
        const int attempts = (int)(sizeof(kAttempts) / sizeof(kAttempts[0]));
        int a;

        rc = -1;
        for (a = 0; a < attempts; a++) {
            if (kAttempts[a].io > RC_VIDEO_IO_MAX)
                continue;
            s_context = NULL;
            rc = rsxInit(&s_context, kAttempts[a].cmd, kAttempts[a].io, s_host_addr);
            out->rsx_attempts++;
            if (rc == 0 && s_context != NULL) {
                out->cmd_size = (int)kAttempts[a].cmd;
                out->io_size = (int)kAttempts[a].io;
                break;
            }
            out->last_error = (int)rc;
        }
        if (rc != 0 || s_context == NULL)
            return fail(out, "rsxInit (every size combination refused)", (int)rc);
    }

    rc = videoGetState(0, 0, &state);
    if (rc != 0)
        return fail(out, "videoGetState", (int)rc);

    /*
     * The state value is RECORDED AND NOT JUDGED.
     *
     * PSL1GHT defines VIDEO_STATE_DISABLED as 0 and ENABLED as 1, while samples in the wild treat 0 as
     * the good case - so the polarity is genuinely ambiguous from here, and refusing to open the display
     * on a guess would produce exactly the blank screen this is meant to explain. It goes in the report
     * instead, where one hardware run settles it.
     */
    out->video_state = (int)state.state;

    rc = videoGetResolution(state.displayMode.resolution, &resolution);
    if (rc != 0)
        return fail(out, "videoGetResolution", (int)rc);

    memset(&config, 0, sizeof(config));
    config.resolution = state.displayMode.resolution;
    config.format = VIDEO_BUFFER_FORMAT_XRGB;
    config.aspect = state.displayMode.aspect;
    config.pitch = (u32)resolution.width * 4u;

    rc = videoConfigure(0, &config, NULL, 0);
    if (rc != 0)
        return fail(out, "videoConfigure", (int)rc);

    /* On vsync rather than immediately - the alternative tears, visibly. */
    gcmSetFlipMode(GCM_FLIP_VSYNC);

    for (i = 0; i < RC_VIDEO_BUFFERS; i++) {
        u32 size = config.pitch * (u32)resolution.height;

        s_buffer[i] = (uint32_t *)rsxMemalign(64, size);
        if (s_buffer[i] == NULL)
            return fail(out, "rsxMemalign for a display buffer", i);
        if (rsxAddressToOffset(s_buffer[i], &s_offset[i]) != 0)
            return fail(out, "rsxAddressToOffset", i);
        if (gcmSetDisplayBuffer((u8)i, s_offset[i], config.pitch,
                                (u32)resolution.width, (u32)resolution.height) != 0)
            return fail(out, "gcmSetDisplayBuffer", i);
        memset(s_buffer[i], 0, size);
    }

    gcmResetFlipStatus();
    s_current = 0;

    s_info.width = (int)resolution.width;
    s_info.height = (int)resolution.height;
    s_info.pitch = (int)config.pitch;
    s_info.buffers = RC_VIDEO_BUFFERS;
    s_info.video_state = out->video_state;
    s_info.gcm_module = s_gcm_module;
    s_info.sysutil_module = s_sysutil_module;
    /* These are filled in on `out` during the sweep and would be lost by the copy below, which is
     * exactly what b85 reported: "0 attempts, 0 bytes" from a run that had plainly succeeded. */
    s_info.rsx_attempts = out->rsx_attempts;
    s_info.cmd_size = out->cmd_size;
    s_info.io_size = out->io_size;
    s_info.ok = 1;
    s_info.failed_at = NULL;
    s_open = 1;
    *out = s_info;
    return 1;
}

int rc_video_present_ready(void)
{
    if (!s_open)
        return 0;
    /* Zero means the flip has completed. Reset it here, where the answer is consumed. */
    if (gcmGetFlipStatus() != 0)
        return 0;
    gcmResetFlipStatus();
    return 1;
}

uint32_t *rc_video_back_buffer(void)
{
    if (!s_open)
        return NULL;
    /* The one NOT on screen. Writing the displayed buffer is how a program tears against itself. */
    return s_buffer[s_current ^ 1];
}

void rc_video_flip(void)
{
    int back;

    if (!s_open)
        return;
    back = s_current ^ 1;

    if (gcmSetFlip(s_context, (u8)back) != 0)
        return;
    rsxFlushBuffer(s_context);
    gcmSetWaitFlip(s_context);

    /*
     * NOT waited on here. The caller asks rc_video_present_ready before it converts the next picture,
     * so the wait becomes "skip a frame" instead of "block the thread that is also draining a socket".
     */
    s_current = back;
}

void rc_video_close(void)
{
    if (!s_open)
        return;
    /* The buffers belong to the RSX heap and the context owns the rest. PSL1GHT offers nothing that
     * hands the IO region back, so this only marks the display closed. */
    s_open = 0;
    s_info.ok = 0;
}

/* Clamp to a byte without a branch per channel in the common case. */
static inline uint32_t clamp255(int32_t v)
{
    if (v < 0)
        return 0u;
    if (v > 255)
        return 255u;
    return (uint32_t)v;
}

unsigned rc_video_blit_yuv420(const uint8_t *y, const uint8_t *u, const uint8_t *v,
                              int y_stride, int uv_stride, int width, int height)
{
    uint32_t *back = rc_video_back_buffer();
    uint64_t t0;
    int stride_px;
    int ox, oy;
    int row;

    if (back == NULL || y == NULL || u == NULL || v == NULL)
        return 0u;
    if (width <= 0 || height <= 0)
        return 0u;

    stride_px = s_info.pitch / 4;
    ox = (s_info.width - width) / 2;
    oy = (s_info.height - height) / 2;
    if (ox < 0 || oy < 0)
        return 0u;   /* a picture larger than the screen wants scaling, which this does not do */

    t0 = rc_tick();

    for (row = 0; row < height; row++) {
        const uint8_t *yr = y + (size_t)row * (size_t)y_stride;
        const uint8_t *ur = u + (size_t)(row >> 1) * (size_t)uv_stride;
        const uint8_t *vr = v + (size_t)(row >> 1) * (size_t)uv_stride;
        uint32_t *out = back + (size_t)(oy + row) * (size_t)stride_px + (size_t)ox;
        int col;

        /*
         * TWO LUMA PIXELS PER CHROMA SAMPLE, which is what 4:2:0 already means - the chroma planes are
         * half resolution in both directions, so a pair of columns shares one sample and so does a pair
         * of rows.
         *
         * The first version wrote `ur[col / 2]` inside the pixel loop: an integer divide and a fresh
         * chroma load for every luma pixel, doing twice the chroma work and the division for nothing.
         * Stepping the chroma pointer once per PAIR removes both, and the red/blue terms - which depend
         * only on chroma - are computed once for the pair instead of twice.
         */
        for (col = 0; col + 1 < width; col += 2) {
            int32_t d = (int32_t)*ur++ - 128;
            int32_t e = (int32_t)*vr++ - 128;
            int32_t r_term = 1836 * e;
            int32_t g_term = -218 * d - 546 * e;
            int32_t b_term = 2163 * d;
            int32_t y0 = 1192 * ((int32_t)yr[col] - 16);
            int32_t y1 = 1192 * ((int32_t)yr[col + 1] - 16);

            out[col] = (clamp255((y0 + r_term) >> 10) << 16)
                     | (clamp255((y0 + g_term) >> 10) << 8)
                     |  clamp255((y0 + b_term) >> 10);
            out[col + 1] = (clamp255((y1 + r_term) >> 10) << 16)
                         | (clamp255((y1 + g_term) >> 10) << 8)
                         |  clamp255((y1 + b_term) >> 10);
        }
        if (col < width) {
            /* An odd width leaves one pixel, which reuses the last chroma pair. */
            int32_t d = (int32_t)ur[-1] - 128;
            int32_t e = (int32_t)vr[-1] - 128;
            int32_t y0 = 1192 * ((int32_t)yr[col] - 16);

            out[col] = (clamp255((y0 + 1836 * e) >> 10) << 16)
                     | (clamp255((y0 - 218 * d - 546 * e) >> 10) << 8)
                     |  clamp255((y0 + 2163 * d) >> 10);
        }
    }

    {
        uint64_t hz = rc_tick_hz();
        uint64_t ticks = rc_tick() - t0;

        return (hz > 0u) ? (unsigned)((ticks * 1000000u) / hz) : 0u;
    }
}
