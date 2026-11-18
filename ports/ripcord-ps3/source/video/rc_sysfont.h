/*
 * ripcord-ps3 - the console's own typeface, for the overlay.
 *
 * WHAT THIS IS. The firmware's faces are ordinary TrueType files in /dev_flash/data/font. This opens
 * one at run time - Rodin Regular first, the face the menus are set in - and the FreeType portlib
 * parses it. Nothing is redistributed: the file stays on the console and is read at run time.
 * rc_sysfont.c says why this is the route rather than cellFont.
 *
 * WHY THE DRAWN FONT REMAINS. This depends on firmware contents that can fail to load, which is why it
 * reports whether it opened and the hand-drawn 5x7 stays as the fallback. The dependency is worth it:
 * the system face is antialiased, scales to any size, has a full character set, and is the one the
 * console itself uses, which no hand-drawn bitmap at 9 rows is going to match.
 *
 * COVERAGE, NOT PIXELS. A glyph comes back as 8-bit coverage, which the caller composites in whatever
 * colour it wants. That is what makes the antialiasing real, and it is also why the panel bitmap lives
 * in main memory rather than RSX memory: blending needs the destination read, and a Cell read from RSX
 * memory is roughly two orders of magnitude slower than a write.
 */
#ifndef RC_SYSFONT_H
#define RC_SYSFONT_H

#include <stdint.h>

/*
 * Brings up the system font and renders its atlas at the THREE sizes the caller will use. All are
 * given rather than one and a ratio, because the caller derives them from the display and a ratio here
 * would silently ignore that. 0 if anything refused, and the caller must then use
 * its own font - every step is reported through rc_sysfont_status() so a refusal names itself rather
 * than arriving as "the text looks wrong".
 */
int rc_sysfont_open(float body_px, float heading_px, float display_px);

/* One of: "not tried", "ready", or the step that failed with its error code. Never NULL. */
const char *rc_sysfont_status(void);

/* The distance from the top of a line to the baseline, in pixels - for laying a run out against a box
 * rather than against a baseline nobody can see. */
int rc_sysfont_ascent(void);

/* The height of a capital, in pixels. This, not the ascent, is what a label is centred on - see the
 * note in the .c for why centring on the line box hangs a word of capitals visibly high. */
int rc_sysfont_cap_height(void);

/* Changes the em size. The ascent moves with it, so re-read it after calling this. */
void rc_sysfont_set_size(float pixels);

/*
 * Draw digits in a fixed cell the width of the widest digit, centred - a tabular figure. Without it a
 * proportional face gives '1' a narrower advance than '8', so a number that ticks changes width and
 * drags everything after it. With it, one typeface can set both the words and the figures.
 */
void rc_sysfont_set_tabular(int on);

/*
 * Renders `text` into an 8-bit coverage buffer, left edge at x, baseline at `baseline`. Returns the
 * advance in pixels - the width the run occupied - so a caller can centre or right-align it by
 * measuring first with a NULL buffer.
 */
int rc_sysfont_render(unsigned char *cov, int cov_w, int cov_h, int x, int baseline, const char *text);

#endif /* RC_SYSFONT_H */
