/*
 * ripcord-ps3 - the on-screen diagnostics overlay.
 *
 * The same answers the .NET client's diagnostics report gives, on a console that has no second window
 * to put them in. Until now every number this port produced went to a log file that can only be read
 * after the session ends, over FTP, on another machine - which is fine for bring-up and useless for
 * "why did it just go blocky".
 *
 * IT IS DRAWN BY THE PPE, INTO RSX MEMORY, AND THAT IS THE ONE THING TO BE CAREFUL ABOUT. Writes that
 * way are fast and reads are roughly two orders of magnitude slower, so this only ever writes: no
 * blending, no read-modify-write, no clearing by reading first. A solid background box is written as
 * solid pixels, and the glyphs are written over it.
 *
 * IT DRAWS INTO ITS OWN BITMAP, NOT INTO THE BACK BUFFER, and b225 is why. Drawing into the back buffer
 * put the text there with the PPE immediately after the picture blit had been QUEUED - so the RSX ran
 * the picture afterwards and painted over every pixel of it. Nothing appeared, on a path where every
 * counter said the frame had been drawn. The bitmap is copied over the picture by a command issued
 * after it, which is an ordering the hardware keeps instead of one this code has to win.
 *
 * THE TEXT IS REBUILT A FEW TIMES A SECOND, NOT EVERY FRAME. All of it is rates and totals that no one
 * can read at 60 Hz, and rebuilding costs a few hundred KB of stores. The COPY is queued every frame,
 * because the picture blit overwrites the back buffer each time.
 */
#include "rc_overlay.h"

#include <stdarg.h>
#include <stdio.h>
#include <string.h>

#include "rc_font5x7.h"
#include "rc_video_ps3.h"
#include "platform/rc_platform.h"

#define RC_OVERLAY_SCALE    2
#define RC_OVERLAY_PAD      8
#define RC_OVERLAY_LINE     (RC_FONT_H * RC_OVERLAY_SCALE + 2)
#define RC_OVERLAY_MAX_COLS 44
#define RC_OVERLAY_W        (RC_OVERLAY_MAX_COLS * (RC_FONT_W + 1) * RC_OVERLAY_SCALE \
                             + RC_OVERLAY_PAD * 2)
#define RC_OVERLAY_LINES    12
#define RC_OVERLAY_BOX_H    (RC_OVERLAY_LINES * RC_OVERLAY_LINE + RC_OVERLAY_PAD * 2)
#define RC_OVERLAY_REBUILD_MS 250u

static int s_on;
static int s_x = 32;
static int s_y = 32;
static int s_cursor;

/* The bitmap, in RSX memory so the copy can be a command rather than a store. */
static uint32_t *s_target;
static uint32_t s_offset;
static int s_stride_px = RC_OVERLAY_W;
static int s_ready;
static uint64_t s_next_rebuild;

/* Defined below; rc_overlay_set clears the bitmap the moment it gets one. */
static void fill(uint32_t *base, int stride_px, int x, int y, int w, int h, uint32_t colour);

void rc_overlay_set(int on)
{
    s_on = on;
    if (on && !s_ready) {
        s_target = (uint32_t *)rc_video_alloc_rsx((size_t)RC_OVERLAY_W * RC_OVERLAY_BOX_H * 4u,
                                                  &s_offset);
        s_ready = (s_target != NULL);
        if (s_ready)
            fill(s_target, s_stride_px, 0, 0, RC_OVERLAY_W, RC_OVERLAY_BOX_H, 0x00141414u);
    }
}

int rc_overlay_on(void)
{
    return s_on;
}

/*
 * The background is written as a block BEFORE any text, for the reason in the header: darkening what is
 * already there would mean reading it. A fixed box in a fixed place is also steadier to read over moving
 * video than per-glyph shadows.
 */
static void fill(uint32_t *base, int stride_px, int x, int y, int w, int h, uint32_t colour)
{
    int row, col;

    for (row = 0; row < h; row++) {
        uint32_t *p = base + (size_t)(y + row) * (size_t)stride_px + (size_t)x;

        for (col = 0; col < w; col++)
            p[col] = colour;
    }
}

int rc_overlay_begin(int lines)
{
    uint64_t now;

    (void)lines;
    if (!s_on || !s_ready)
        return 0;

    now = rc_time_ms();
    if (now < s_next_rebuild)
        return 0;   /* the bitmap still holds the last text; the caller queues the copy regardless */
    s_next_rebuild = now + RC_OVERLAY_REBUILD_MS;

    /* Dark grey rather than black: over a dark game a black box is invisible and the text appears to
     * float, which makes it harder to read, not easier. */
    fill(s_target, s_stride_px, 0, 0, RC_OVERLAY_W, RC_OVERLAY_BOX_H, 0x00141414u);
    s_cursor = 0;
    return 1;
}

static void draw_char(int x, int y, char c, uint32_t colour)
{
    int gy, gx, sy, sx;
    const unsigned char *g;

    if (c >= 'a' && c <= 'z')
        c = (char)(c - 'a' + 'A');
    if (c < RC_FONT_FIRST || c > RC_FONT_LAST)
        c = '?';
    g = kFont5x7[c - RC_FONT_FIRST];

    for (gy = 0; gy < RC_FONT_H; gy++) {
        unsigned char bits = g[gy];

        for (gx = 0; gx < RC_FONT_W; gx++) {
            if ((bits & (0x10u >> gx)) == 0u)
                continue;
            for (sy = 0; sy < RC_OVERLAY_SCALE; sy++) {
                uint32_t *p = s_target
                            + (size_t)(y + gy * RC_OVERLAY_SCALE + sy) * (size_t)s_stride_px
                            + (size_t)(x + gx * RC_OVERLAY_SCALE);

                for (sx = 0; sx < RC_OVERLAY_SCALE; sx++)
                    p[sx] = colour;
            }
        }
    }
}

void rc_overlay_line(uint32_t colour, const char *fmt, ...)
{
    char text[RC_OVERLAY_MAX_COLS + 1];
    va_list ap;
    int x, i;

    if (!s_on || s_target == NULL)
        return;

    va_start(ap, fmt);
    (void)vsnprintf(text, sizeof(text), fmt, ap);
    va_end(ap);

    if (s_cursor >= RC_OVERLAY_LINES)
        return;

    x = RC_OVERLAY_PAD;
    for (i = 0; text[i] != '\0' && i < RC_OVERLAY_MAX_COLS; i++) {
        draw_char(x, RC_OVERLAY_PAD + s_cursor * RC_OVERLAY_LINE, text[i], colour);
        x += (RC_FONT_W + 1) * RC_OVERLAY_SCALE;
    }
    s_cursor++;
}

/*
 * Queued every frame whether or not the text was rebuilt, because the picture blit overwrites the whole
 * back buffer each time. Must be called after the picture has been queued - see rc_video_overlay_blit.
 */
void rc_overlay_end(void)
{
    if (!s_on || !s_ready)
        return;
    rc_video_overlay_blit(s_offset, RC_OVERLAY_W * 4, RC_OVERLAY_W, RC_OVERLAY_BOX_H, s_x, s_y);
}
