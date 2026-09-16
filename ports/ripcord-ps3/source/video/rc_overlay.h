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
 * THE PALETTE. The top byte is carried but the panel is OPAQUE - see rc_video_overlay_blit for why
 * translucency is closed off, and what it cost to find out.
 *
 * Blues and greys rather than the pure black and white the first version used: a black panel over a
 * dark game disappears and a white-on-black one glares over a bright one. These sit above both, and
 * the body is a touch lighter than it would be if it were see-through, so it reads as a deliberate
 * panel rather than as a hole in the picture.
 */
#define RC_OV_PANEL    0xFF12161Cu   /* the body                                            */
#define RC_OV_HEADER   0xFF1B2129u   /* the title bar, lifted so the name separates from it  */
#define RC_OV_EDGE     0xFF39434Fu
#define RC_OV_ACCENT   0xFF4A9EFFu
#define RC_OV_TEXT     0xFFE6EAEFu
#define RC_OV_LABEL    0xFF8A94A0u   /* field names - present but not competing with the values */
#define RC_OV_GOOD     0xFF5FD08Au
#define RC_OV_WARN     0xFFF0C04Au
#define RC_OV_BAD      0xFFF06060u
#define RC_OV_TRACK    0xFF232B34u   /* the empty part of a sparkline                       */

void rc_overlay_set(int on);
int  rc_overlay_on(void);

/* 1 when the console's own face opened, 0 when the drawn fallback is in use. Worth reporting: the two
 * look different enough that "which font is this" is otherwise guessed from the screen. */
int  rc_overlay_using_system_font(void);

/* The bitmap's size, so the caller can lay out against it without duplicating the constants. */
int rc_overlay_width(void);
int rc_overlay_height(void);

/*
 * Starts a rebuild. 0 means "not yet" - the bitmap still holds the last text and the caller should go
 * straight to rc_overlay_end() to queue the copy.
 */
int  rc_overlay_begin(void);
void rc_overlay_rect(int x, int y, int w, int h, uint32_t argb);

/*
 * TWO FONTS, AND WHICH ONE TO USE IS A QUESTION ABOUT THE TEXT, NOT ABOUT TASTE.
 *
 * _text is proportional, with real lower case and descenders, and is for WORDS - labels, headings,
 * anything whose job is to be read.
 *
 * _num is the monospaced 5x7, and is for anything that CHANGES. A column of figures has to hold still
 * while the figures change: a digit one pixel narrower than its neighbour shuffles the whole row every
 * time it ticks, and a number that moves while you read it is a number you read twice. Prefer
 * _num_right for those, so the digits grow leftwards from a fixed edge.
 */
/*
 * BOTH RETURN THE ADVANCE, so a caller chains `x += rc_overlay_text(x, ...)` rather than writing down
 * where the next piece goes. The first version of the panel used hand-measured pixel offsets for every
 * run that mixed words and figures; they were measured against one font at one size, and the first
 * time either changed the labels started cutting into the numbers beside them. An offset that has to
 * be recomputed by hand whenever anything moves is a bug with a delay on it.
 */
int rc_overlay_text(int x, int y, int scale, uint32_t argb, const char *fmt, ...);
int rc_overlay_num(int x, int y, int scale, uint32_t argb, const char *fmt, ...);

/* Right-aligned: the text ENDS at x. For a figure that changes, so it grows leftwards from a fixed edge. */
void rc_overlay_text_right(int x, int y, int scale, uint32_t argb, const char *fmt, ...);
void rc_overlay_num_right(int x, int y, int scale, uint32_t argb, const char *fmt, ...);

/* What rc_overlay_num WOULD occupy, without drawing it - for reserving room to its left. */
int rc_overlay_num_width(int scale, const char *fmt, ...);

/*
 * A sparkline: `n` values drawn as columns left to right, scaled against `max`. Zero-height bars still
 * get a single pixel so a run of dead seconds reads as a flat line rather than as missing data.
 */
void rc_overlay_bars(int x, int y, int w, int h, const unsigned *v, unsigned n, unsigned max,
                     uint32_t argb);

void rc_overlay_end(void);

#endif /* RC_OVERLAY_H */
