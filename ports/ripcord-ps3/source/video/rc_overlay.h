/*
 * ripcord-ps3 - the on-screen diagnostics overlay. See the .c for what it may and may not do to the
 * memory it draws into.
 *
 * ORDER MATTERS AND IS THE CALLER'S PROBLEM. rc_overlay_end() queues the copy over the picture, so it
 * has to run AFTER the picture has been queued and BEFORE the flip. It also has to run on every frame
 * even when rc_overlay_begin() declines to rebuild the text, because the picture blit clears the back
 * buffer each time - see b228 for what skipping it looks like.
 */
#ifndef RC_OVERLAY_H
#define RC_OVERLAY_H

#include <stdint.h>

/*
 * THE PALETTE, IN STRAIGHT (NOT PREMULTIPLIED) ARGB. The top byte is opacity and the drawing code
 * premultiplies on the way into the bitmap, because that is the form the RSX's blend wants and doing
 * it here would make every constant unreadable.
 *
 * Blues and greys rather than the pure black and white the first version used: a black panel over a
 * dark game disappears and a white-on-black one glares over a bright one. These sit above both.
 */
#define RC_OV_PANEL    0xC80E1014u   /* the body, deliberately see-through                */
#define RC_OV_HEADER   0xE81B2129u   /* the title bar, more solid so the name stays legible */
#define RC_OV_EDGE     0xE02A323Cu
#define RC_OV_ACCENT   0xFF4A9EFFu
#define RC_OV_TEXT     0xFFE6EAEFu
#define RC_OV_LABEL    0xFF8A94A0u   /* field names - present but not competing with the values */
#define RC_OV_GOOD     0xFF5FD08Au
#define RC_OV_WARN     0xFFF0C04Au
#define RC_OV_BAD      0xFFF06060u
#define RC_OV_TRACK    0x902A323Cu   /* the empty part of a sparkline                       */

void rc_overlay_set(int on);
int  rc_overlay_on(void);

/* The bitmap's size, so the caller can lay out against it without duplicating the constants. */
int rc_overlay_width(void);
int rc_overlay_height(void);

/*
 * Starts a rebuild. 0 means "not yet" - the bitmap still holds the last text and the caller should go
 * straight to rc_overlay_end() to queue the copy.
 */
int  rc_overlay_begin(void);
void rc_overlay_rect(int x, int y, int w, int h, uint32_t argb);
void rc_overlay_text(int x, int y, int scale, uint32_t argb, const char *fmt, ...);

/* Text whose RIGHT edge lands on x, for a column of numbers that should not jitter as digits change. */
void rc_overlay_text_right(int x, int y, int scale, uint32_t argb, const char *fmt, ...);

/*
 * A sparkline: `n` values drawn as columns left to right, scaled against `max`. Zero-height bars still
 * get a single pixel so a run of dead seconds reads as a flat line rather than as missing data.
 */
void rc_overlay_bars(int x, int y, int w, int h, const unsigned *v, unsigned n, unsigned max,
                     uint32_t argb);

void rc_overlay_end(void);

#endif /* RC_OVERLAY_H */
