/*
 * Getting a picture onto the television.
 *
 * Deliberately the smallest display path that can work: the CPU writes straight into a display buffer
 * and the RSX is asked to flip it. No shaders, no textures, no render target. A scaler or a YUV->RGB
 * shader would be additions on top of this, and the reason for doing it this way first is that when
 * nothing appears there is almost nothing between the pixels and the screen to be wrong.
 *
 * The sequence is rsx.h's own, followed rather than improvised: create a context, configure video from
 * the state the console reports, set the flip mode to vsync, allocate 64-byte-aligned buffers in RSX
 * memory, register each with gcmSetDisplayBuffer, reset the flip status. Then per frame: write the
 * buffer that is NOT on screen, flip to it, flush, and wait for the flip to land.
 *
 * The resolution is the CONSOLE'S, read from videoGetState rather than chosen here. A program that
 * configures a mode the display does not have gets a blank screen, which is indistinguishable from
 * every other way of getting a blank screen - and this port has had enough of those.
 */
#ifndef RC_VIDEO_PS3_H
#define RC_VIDEO_PS3_H

#include <stdint.h>

typedef struct {
    int width;
    int height;
    int pitch;          /* bytes per line. NOT width * 4 necessarily - it is configured, so it is read */
    int buffers;
    int ok;
    int video_state;    /* whatever videoGetState reported, recorded rather than judged - see the .c */

    /* sysModuleLoad's return for each PRX the display needs. Recorded rather than judged: "already
     * loaded" is an error return and a perfectly good outcome. */
    int gcm_module;
    int sysutil_module;

    /* Which rsxInit size pair worked, and how many were tried - see the sweep in the .c. */
    int rsx_attempts;
    int cmd_size;
    int io_size;
    int last_error;
    const char *failed_at;  /* the step that refused; NULL on success */
} rc_video_info;

/*
 * Brings the display up. Returns 1 on success and fills `out`. On failure `out->failed_at` names the
 * step that refused, because "no picture" says nothing on its own.
 */
int rc_video_open(rc_video_info *out);

/* The back buffer: `pitch` bytes per line, `height` lines, XRGB8888. NULL if the display is not open. */
uint32_t *rc_video_back_buffer(void);

/* Presents the back buffer and waits for the flip to land. */
void rc_video_flip(void);

/*
 * Converts one YUV 4:2:0 picture into the back buffer, centred, one pixel per pixel.
 *
 * NO SCALING, deliberately, for the first light-up. The stream is 960x540 and the display is 1920x1080,
 * which is an exact doubling and therefore tempting - but doubling is four times the pixels, and the
 * open question here is what this conversion COSTS on the PPE while it is also decoding. Answer that at
 * the cheap size first; a 2x blit that drops frames would say nothing about whether the colour is right.
 *
 * The colour matrix is BT.709 limited range. **[X]** - this is what HD content normally uses and the
 * console has not been asked. A wrong matrix gives a picture that is visibly present and visibly
 * off-colour, which is a good failure: it cannot be confused with no picture at all.
 *
 * Returns the microseconds it took, so the cost is measured rather than assumed.
 */
unsigned rc_video_blit_yuv420(const uint8_t *y, const uint8_t *u, const uint8_t *v,
                              int y_stride, int uv_stride, int width, int height);

void rc_video_close(void);

#endif /* RC_VIDEO_PS3_H */
