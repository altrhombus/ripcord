/*
 * ripcord-ps3 - the console's own typeface, for the overlay.
 *
 * WHAT THIS IS. cellFont can open a fontset the FIRMWARE supplies rather than a file the application
 * ships, and FONT_TYPE_NEWRODIN_GOTHIC_LATIN_SET is New Rodin - the face the XMB is set in. Nothing is
 * redistributed and nothing is read off the flash by hand: the system font library is asked for a
 * glyph and hands back its coverage.
 *
 * WHY IT DID NOT COME FIRST. The hand-drawn 5x7 exists because libfont and libfontFT looked like a
 * dependency on firmware contents that could fail to load, and that judgement still holds - which is
 * why this reports whether it opened and the drawn font stays as the fallback. What was wrong was the
 * conclusion that the dependency was not worth it: the system face is antialiased, scales to any size,
 * has a full character set, and is the one the console itself uses, which no hand-drawn bitmap at 9
 * rows is going to match.
 *
 * COVERAGE, NOT PIXELS. A glyph comes back as 8-bit coverage, which the caller composites in whatever
 * colour it wants. That is what makes the antialiasing real, and it is also why the panel bitmap had to
 * move out of RSX memory: blending needs the destination read, and a Cell read from there is roughly
 * two orders of magnitude slower than a write.
 */
#ifndef RC_SYSFONT_H
#define RC_SYSFONT_H

#include <stdint.h>

/*
 * Brings up the system font and renders its atlas at the TWO sizes the caller will use. Both are
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
