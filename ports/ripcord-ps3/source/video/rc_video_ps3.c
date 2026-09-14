#include "rc_video_ps3.h"

#include <malloc.h>
#include <string.h>
#include <unistd.h>

#include <rsx/rsx.h>
#include <sysutil/video.h>

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
#define RC_VIDEO_MB       (1024u * 1024u)
#define RC_VIDEO_CB_SIZE  (1u * RC_VIDEO_MB)
#define RC_VIDEO_IO_SIZE  (1u * RC_VIDEO_MB)

static gcmContextData *s_context;
static void *s_host_addr;
static uint32_t *s_buffer[RC_VIDEO_BUFFERS];
static u32 s_offset[RC_VIDEO_BUFFERS];
static int s_current;
static rc_video_info s_info;
static int s_open;

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

    /* 1 MB alignment AND a whole number of megabytes - see RC_VIDEO_IO_SIZE. */
    s_host_addr = memalign(RC_VIDEO_MB, RC_VIDEO_IO_SIZE);
    if (s_host_addr == NULL)
        return fail(out, "memalign for the RSX IO region", 0);

    rc = rsxInit(&s_context, RC_VIDEO_CB_SIZE, RC_VIDEO_IO_SIZE, s_host_addr);
    if (rc != 0 || s_context == NULL)
        return fail(out, "rsxInit", (int)rc);

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
    s_info.ok = 1;
    s_info.failed_at = NULL;
    s_open = 1;
    *out = s_info;
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

    /* Wait for the flip to land before anything writes the other buffer. rsx.h suggests a short sleep
     * between polls rather than a tight spin, and this program has other work to do. */
    while (gcmGetFlipStatus() != 0)
        usleep(200);
    gcmResetFlipStatus();

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
