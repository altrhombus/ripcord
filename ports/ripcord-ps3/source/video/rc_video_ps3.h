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

/*
 * Is the previous flip finished, so the back buffer is safe to write?
 *
 * The caller asks BEFORE converting a picture, not after. b87 blitted and flipped for every decoded
 * frame and waited for vsync each time, on the receive thread - so the display's pace became the
 * network's pace, the socket buffer overflowed, and 90 units were lost where the run before had lost
 * none. The loss looked like a network fault and was self-inflicted.
 *
 * Dropping a picture is the right answer when the display is behind: a frame not shown costs a frame,
 * while a frame waited on costs every packet that arrives during the wait.
 */
/*
 * Clears EVERY buffer, not just the back one, and presents.
 *
 * Both matter. The test pattern is drawn into both buffers alternately, so clearing one leaves the
 * other still holding it - and with the video window occupying only the centre, the result is a stream
 * framed by a test pattern that flickers between two stale frames. That is what b87 and b89 actually
 * looked like on a television, and it reads as "a glimpse in the middle" rather than as a stream.
 */
void rc_video_clear_all(uint32_t colour);

int rc_video_present_ready(void);

/* Presents the back buffer. Does NOT wait for the flip - see rc_video_present_ready. */
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

/*
 * The one-shot agreement check between the SPE and PPE conversions. `checked` is 0 until a frame has
 * been converted both ways. See rc_video_blit_yuv420 for why this exists at all.
 */
/*
 * Converts a synthetic picture both ways and compares. Returns 1 if the comparison was actually made,
 * 0 if the SPEs were unavailable - which is not a mismatch and the caller is told apart. Run this BEFORE
 * streaming: it used to run on the first live frame and cost the Takion channel at 1080p.
 */
/* A picture the decoder already packed as 32-bit RGB: scaled and placed, never converted. Returns
 * microseconds, or 0 if the SPEs could not do it - there is no PPE scaler to fall back to. */
unsigned rc_video_blit_argb32(const uint8_t *argb, int src_stride, int width, int height);

/*
 * Hand the scaling to the RSX's 2D engine instead of the SPEs. `linear` picks its interpolator -
 * bilinear where the SPE path charged 21,038 us a frame for the same thing, nearest otherwise.
 *
 * Off until called, and it falls back on its own if the staging buffer was refused at open. See
 * blit_argb32_on_rsx in the .c for what this does and does not buy.
 */
void rc_video_set_rsx_scale(int on, int linear);

/*
 * A block of RSX local memory, with the offset the RSX knows it by. For a caller that wants the RSX to
 * read what it writes, and ONLY for that: the Cell writes this memory quickly and reads it roughly two
 * orders of magnitude more slowly, so anything that samples the contents on the PPE does not belong in
 * here. NULL if the display is not open or the heap refused.
 */
void *rc_video_alloc_rsx(size_t bytes, uint32_t *offset);

/*
 * Scale straight out of memory the caller already holds an offset for, with no copy at all. Returns 0
 * if the RSX scaler is not selected, in which case the caller still owns getting the picture up.
 */
unsigned rc_video_blit_rsx_offset(uint32_t src_offset, int width, int height);

/* available is 0 when the staging buffer could not be allocated, in which case nothing else is useful. */
void rc_video_rsx_scale_stats(int *available, unsigned *blits, unsigned *refused);

int rc_video_self_test(void);

/* Which scale factor disagreed, or NULL if none did. */
const char *rc_video_self_test_failure(void);

void rc_video_verify_get(int *checked, int *match, uint64_t *spu_hash, uint64_t *ppe_hash);

/* How the flip-readiness check has been answering - see rc_video_present_ready. */
void rc_video_flip_stats(unsigned *calls, unsigned *busy, unsigned *last_status);

/* The rectangle the last picture was scaled into, and the display it sits on. */
void rc_video_scale_info(int *scaled_w, int *scaled_h, int *display_w, int *display_h);

void rc_video_close(void);

#endif /* RC_VIDEO_PS3_H */
