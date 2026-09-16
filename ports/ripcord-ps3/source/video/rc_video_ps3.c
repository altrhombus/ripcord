#include "rc_video_ps3.h"

#include <malloc.h>
#include <string.h>
#include <unistd.h>

#include <rsx/rsx.h>
#include <sysutil/video.h>
#include <sysmodule/sysmodule.h>
#include "rc_platform.h"
#include "rc_spu_yuv.h"

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
/*
 * b105 dropped 890 of 891 pictures on "the display was busy" after the first frame flipped. The raw
 * status the check is reading is recorded so the next run says whether the flip never completes or the
 * question is being asked wrongly - two very different faults behind one symptom.
 */
static unsigned s_ready_calls;
static unsigned s_busy_calls;
static unsigned s_last_flip_status;
static int s_scaled_w, s_scaled_h;
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

void rc_video_clear_all(uint32_t colour)
{
    int i;

    if (!s_open)
        return;
    for (i = 0; i < RC_VIDEO_BUFFERS; i++) {
        size_t pixels = (size_t)(s_info.pitch / 4) * (size_t)s_info.height;
        size_t n;

        /* memset only helps for a byte-uniform colour; black is, but say it generally. */
        for (n = 0; n < pixels; n++)
            s_buffer[i][n] = colour;
    }
    /* Present one of them so the screen is clear immediately rather than at the next picture. */
    rc_video_flip();
}

int rc_video_present_ready(void)
{
    u32 status;

    if (!s_open)
        return 0;

    status = gcmGetFlipStatus();
    s_last_flip_status = (unsigned)status;
    s_ready_calls++;

    /*
     * ASKING MUST NOT CONSUME THE ANSWER.
     *
     * This used to reset the status here, on the reasoning that the answer is consumed where it is read.
     * It is not: the caller can still decline to flip - if the conversion fails there is nothing to show
     * - and then no flip ever completes, the status never returns to zero, and EVERY later picture is
     * reported busy. b195 is that: one failed scale, and 1,058 decoded pictures found the display busy
     * for the rest of the run.
     *
     * The reset belongs with the flip, which is the act that makes a new answer possible, and is now
     * done there.
     */
    if (status != 0) {
        s_busy_calls++;
        return 0;
    }
    return 1;
}

void rc_video_flip_stats(unsigned *calls, unsigned *busy, unsigned *last_status)
{
    if (calls != NULL)
        *calls = s_ready_calls;
    if (busy != NULL)
        *busy = s_busy_calls;
    if (last_status != NULL)
        *last_status = s_last_flip_status;
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

    /*
     * Paired with rc_video_present_ready, which only asks. Resetting here - once a flip is actually
     * queued - means a caller that decides not to flip leaves the display exactly as it found it, and
     * the status returns to zero when this flip completes rather than never.
     */
    gcmResetFlipStatus();
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
static void convert_on_ppe(const uint8_t *y, const uint8_t *u, const uint8_t *v,
                           int y_stride, int uv_stride, int width, int height,
                           uint32_t *out_base, int stride_px, int dst_width, int dst_height);

/* One-shot agreement check between the two conversions - see rc_video_blit_yuv420. */
static int s_verified;
static int s_verify_match;
static uint64_t s_verify_hash_spu;
static uint64_t s_verify_hash_ppe;
static const char *s_verify_failed_case;

/*
 * FNV-1a over the converted pixels. An identity check between two implementations inside one program,
 * not a security property: it wants to be cheap and identical on both sides, nothing more.
 */
static uint64_t hash_region(const uint32_t *base, int stride_px, int width, int height)
{
    uint64_t h = 1469598103934665603ULL;
    int row, col;

    for (row = 0; row < height; row++) {
        const uint32_t *line = base + (size_t)row * (size_t)stride_px;

        for (col = 0; col < width; col++) {
            h ^= (uint64_t)line[col];
            h *= 1099511628211ULL;
        }
    }
    return h;
}

static inline uint32_t clamp255(int32_t v)
{
    if (v < 0)
        return 0u;
    if (v > 255)
        return 255u;
    return (uint32_t)v;
}

/*
 * The same placement and scaling as rc_video_blit_yuv420, for a picture the decoder has already turned
 * into packed 32-bit RGB. Only the conversion is absent, which is the whole point: b185 measured the
 * SPE spending 16,444 us a frame converting and scaling against 1,667 us waiting for the MFC.
 *
 * NO PPE FALLBACK HERE, and that is deliberate rather than an omission. The YUV path keeps one because
 * the PPE converter is the implementation that is known to work and a silent failure would be worse than
 * a slow frame. There is no PPE scaler to fall back TO - so if the SPEs cannot do it, the caller is told
 * (0) and the decoder seam can go back to asking for YUV, which is a decision for the caller and not
 * something to paper over here.
 */
unsigned rc_video_blit_argb32(const uint8_t *argb, int src_stride, int width, int height)
{
    uint32_t *back = rc_video_back_buffer();
    int stride_px;
    int ox, oy;
    int dst_w, dst_h;

    if (back == NULL || argb == NULL || width <= 0 || height <= 0)
        return 0u;

    stride_px = s_info.pitch / 4;
    {
        int by_w = (s_info.width * 1024) / width;
        int by_h = (s_info.height * 1024) / height;
        int scale = (by_w < by_h) ? by_w : by_h;

        dst_w = (width * scale) / 1024;
        dst_h = (height * scale) / 1024;
        dst_w &= ~1;
        dst_h &= ~1;
    }
    if (dst_w <= 0 || dst_h <= 0 || dst_w > s_info.width || dst_h > s_info.height)
        return 0u;

    s_scaled_w = dst_w;
    s_scaled_h = dst_h;
    ox = (s_info.width - dst_w) / 2;
    oy = (s_info.height - dst_h) / 2;

    {
        uint32_t *dst = back + (size_t)oy * (size_t)stride_px + (size_t)ox;

        return rc_spu_yuv_convert_argb(argb, src_stride, width, height,
                                       dst, s_info.pitch, dst_w, dst_h);
    }
}

unsigned rc_video_blit_yuv420(const uint8_t *y, const uint8_t *u, const uint8_t *v,
                              int y_stride, int uv_stride, int width, int height)
{
    uint32_t *back = rc_video_back_buffer();
    uint64_t t0;
    int stride_px;
    int ox, oy;
    int dst_w, dst_h;

    if (back == NULL || y == NULL || u == NULL || v == NULL)
        return 0u;
    if (width <= 0 || height <= 0)
        return 0u;

    stride_px = s_info.pitch / 4;

    /*
     * FILL THE SCREEN, PRESERVING THE ASPECT RATIO.
     *
     * The display is whatever mode the television negotiated and the source is whatever the console
     * agreed to send; those will rarely match, which is why this is a permanent part of the path rather
     * than an alternative to choosing a resolution well.
     *
     * The scale is the SMALLER of the two ratios, so the picture fits inside the screen in both
     * directions and is centred in whichever one has room left. Taking the larger would fill the screen
     * by cropping, and cropping a game someone is playing is worse than a border.
     */
    {
        int by_w = (s_info.width * 1024) / width;
        int by_h = (s_info.height * 1024) / height;
        int scale = (by_w < by_h) ? by_w : by_h;

        dst_w = (width * scale) / 1024;
        dst_h = (height * scale) / 1024;
        dst_w &= ~1;    /* even, so the chroma mapping lands the same way on both halves of a pair */
        dst_h &= ~1;
    }
    if (dst_w <= 0 || dst_h <= 0 || dst_w > s_info.width || dst_h > s_info.height)
        return 0u;

    s_scaled_w = dst_w;
    s_scaled_h = dst_h;
    ox = (s_info.width - dst_w) / 2;
    oy = (s_info.height - dst_h) / 2;

    /*
     * THE SPEs FIRST, THE PPE AS FALLBACK.
     *
     * Not a preference between two implementations - the PPE path is the one that is known to work, and
     * it stays because a conversion that silently produces nothing is far worse than a slow one. If the
     * SPEs are not up, or a frame does not finish inside its deadline, this falls through and converts
     * here exactly as before. The caller cannot tell, which is the point; the statistics can, which is
     * how the choice is measured rather than assumed.
     */
    {
        uint32_t *dst = back + (size_t)oy * (size_t)stride_px + (size_t)ox;
        unsigned spu_us = rc_spu_yuv_convert(y, u, v, y_stride, uv_stride, width, height,
                                             dst, s_info.pitch, dst_w, dst_h);
        if (spu_us > 0u)
            return spu_us;
    }

    t0 = rc_tick();
    convert_on_ppe(y, u, v, y_stride, uv_stride, width, height,
                   back + (size_t)oy * (size_t)stride_px + (size_t)ox, stride_px, dst_w, dst_h);

    {
        uint64_t hz = rc_tick_hz();
        uint64_t ticks = rc_tick() - t0;

        return (hz > 0u) ? (unsigned)((ticks * 1000000u) / hz) : 0u;
    }
}

/*
 * The reference implementation, and it MUST scale exactly as the SPEs do - the one-shot verification
 * compares their outputs, and a reference that framed the picture differently would report a mismatch on
 * every run while both halves were individually correct.
 *
 * Same nearest-neighbour mapping, same 16.16 stepping, same constants, same order of operations.
 */
static void convert_on_ppe(const uint8_t *y, const uint8_t *u, const uint8_t *v,
                           int y_stride, int uv_stride, int width, int height,
                           uint32_t *out_base, int stride_px, int dst_width, int dst_height)
{
    const unsigned step = ((unsigned)width << 16) / (unsigned)dst_width;
    int out_row;

    for (out_row = 0; out_row < dst_height; out_row++) {
        int row = (int)(((long long)out_row * height) / dst_height);
        const uint8_t *yr;
        const uint8_t *ur;
        const uint8_t *vr;
        uint32_t *out = out_base + (size_t)out_row * (size_t)stride_px;
        unsigned acc = 0u;
        int x;

        if (row >= height)
            row = height - 1;
        yr = y + (size_t)row * (size_t)y_stride;
        ur = u + (size_t)(row >> 1) * (size_t)uv_stride;
        vr = v + (size_t)(row >> 1) * (size_t)uv_stride;

        for (x = 0; x < dst_width; x++) {
            int col = (int)(acc >> 16);
            int32_t d = (int32_t)ur[col >> 1] - 128;
            int32_t e = (int32_t)vr[col >> 1] - 128;
            int32_t y0 = 1192 * ((int32_t)yr[col] - 16);

            out[x] = (clamp255((y0 + 1836 * e) >> 10) << 16)
                   | (clamp255((y0 - 218 * d - 546 * e) >> 10) << 8)
                   |  clamp255((y0 + 2163 * d) >> 10);
            acc += step;
        }
    }
}

void rc_video_verify_get(int *checked, int *match, uint64_t *spu_hash, uint64_t *ppe_hash)
{
    if (checked != NULL)
        *checked = s_verified;
    if (match != NULL)
        *match = s_verify_match;
    if (spu_hash != NULL)
        *spu_hash = s_verify_hash_spu;
    if (ppe_hash != NULL)
        *ppe_hash = s_verify_hash_ppe;
}

void rc_video_scale_info(int *scaled_w, int *scaled_h, int *display_w, int *display_h)
{
    if (scaled_w != NULL)
        *scaled_w = s_scaled_w;
    if (scaled_h != NULL)
        *scaled_h = s_scaled_h;
    if (display_w != NULL)
        *display_w = s_info.width;
    if (display_h != NULL)
        *display_h = s_info.height;
}

/*
 * THE AGREEMENT CHECK, MOVED OFF THE STREAMING PATH.
 *
 * It used to run on the first frame of the session, which was affordable at 960x540 and became a
 * disaster at 1920x1080: an SPE conversion, two hashes of two million pixels, and a full PPE conversion
 * of the same - well over a tenth of a second stalled inside the receive loop, at exactly the moment the
 * console is establishing its cadence. b112 lost the Takion channel to it and received 58 packets where
 * the run before received 6287.
 *
 * The check was never about a particular frame. It is about two implementations agreeing, and a
 * synthetic input tests that better: the gradients below sweep luma and both chroma channels across
 * their whole range, including the values that clamp, which a given frame of a game may never contain.
 *
 * It runs once, before streaming, on a small picture, and it SCALES - because the scaler is now part of
 * what has to agree, and a 1:1 check would pass while every scaled pixel was wrong.
 */
#define RC_VERIFY_SRC_W 64
#define RC_VERIFY_SRC_H 32
#define RC_VERIFY_DST_W 160    /* widest destination any case below uses */
#define RC_VERIFY_DST_H 80

/*
 * SEVERAL SCALE FACTORS, not one, and specifically not only the integer one.
 *
 * The first version checked 64x32 into 128x64 - exactly 2x, which is the forgiving case: source column
 * x maps to output columns 2x and 2x+1 and any reasonable mapping gets it right. A 1280x720 source on a
 * 1920x1080 display is 1.5x, where the 16.16 stepping and the PPE's reference have to agree about which
 * source pixel each output pixel takes, and an off-by-one in either would show only here.
 *
 * 1:1 is included because it takes a different branch in the kernel, and a shrink because nothing says
 * the display is always the larger of the two.
 */
static const struct { int dw, dh; const char *what; } kVerifyCases[] = {
    { 128, 64, "2x, the integer case" },
    {  96, 48, "1.5x, what 720p on a 1080p display actually needs" },
    {  64, 32, "1:1, which takes its own branch" },
    {  48, 24, "0.75x, a shrink" }
};

int rc_video_self_test(void)
{
    static uint8_t yp[RC_VERIFY_SRC_W * RC_VERIFY_SRC_H] __attribute__((aligned(128)));
    static uint8_t up[(RC_VERIFY_SRC_W / 2) * (RC_VERIFY_SRC_H / 2)] __attribute__((aligned(128)));
    static uint8_t vp[(RC_VERIFY_SRC_W / 2) * (RC_VERIFY_SRC_H / 2)] __attribute__((aligned(128)));
    static uint32_t spu_out[RC_VERIFY_DST_W * RC_VERIFY_DST_H] __attribute__((aligned(128)));
    static uint32_t ppe_out[RC_VERIFY_DST_W * RC_VERIFY_DST_H] __attribute__((aligned(128)));
    int x, ynd;

    s_verified = 0;
    s_verify_match = 0;

    /* Sweep the full range, including values that clamp - a real frame may never contain them. */
    for (ynd = 0; ynd < RC_VERIFY_SRC_H; ynd++) {
        for (x = 0; x < RC_VERIFY_SRC_W; x++)
            yp[ynd * RC_VERIFY_SRC_W + x] = (uint8_t)((x * 255) / (RC_VERIFY_SRC_W - 1));
    }
    for (ynd = 0; ynd < RC_VERIFY_SRC_H / 2; ynd++) {
        for (x = 0; x < RC_VERIFY_SRC_W / 2; x++) {
            up[ynd * (RC_VERIFY_SRC_W / 2) + x] = (uint8_t)((x * 255) / ((RC_VERIFY_SRC_W / 2) - 1));
            vp[ynd * (RC_VERIFY_SRC_W / 2) + x] =
                (uint8_t)((ynd * 255) / ((RC_VERIFY_SRC_H / 2) - 1));
        }
    }

    s_verify_match = 1;
    for (x = 0; x < (int)(sizeof(kVerifyCases) / sizeof(kVerifyCases[0])); x++) {
        int dw = kVerifyCases[x].dw;
        int dh = kVerifyCases[x].dh;
        uint64_t hs, hp;

        memset(spu_out, 0, sizeof(spu_out));
        memset(ppe_out, 0, sizeof(ppe_out));

        if (rc_spu_yuv_convert(yp, up, vp, RC_VERIFY_SRC_W, RC_VERIFY_SRC_W / 2,
                               RC_VERIFY_SRC_W, RC_VERIFY_SRC_H,
                               spu_out, RC_VERIFY_DST_W * 4, dw, dh) == 0u)
            return 0;   /* no SPEs, or they refused - not a mismatch, and the caller is told apart */

        convert_on_ppe(yp, up, vp, RC_VERIFY_SRC_W, RC_VERIFY_SRC_W / 2,
                       RC_VERIFY_SRC_W, RC_VERIFY_SRC_H,
                       ppe_out, RC_VERIFY_DST_W, dw, dh);

        hs = hash_region(spu_out, RC_VERIFY_DST_W, dw, dh);
        hp = hash_region(ppe_out, RC_VERIFY_DST_W, dw, dh);
        if (hs != hp) {
            s_verify_match = 0;
            s_verify_failed_case = kVerifyCases[x].what;
        }
        /* The last case's hashes are kept for the report; a mismatch keeps its own. */
        if (s_verify_match || s_verify_failed_case == kVerifyCases[x].what) {
            s_verify_hash_spu = hs;
            s_verify_hash_ppe = hp;
        }
    }
    s_verified = 1;
    return 1;
}

const char *rc_video_self_test_failure(void)
{
    return s_verify_failed_case;
}
