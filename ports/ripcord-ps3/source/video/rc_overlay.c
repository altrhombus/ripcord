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
 * solid pixels, and the glyphs are written over it. Twenty lines of 5x7 text at 2x is about 250 KB of
 * stores a frame, which is why it is drawn only when it is being shown.
 */
#include "rc_overlay.h"

#include <stdarg.h>
#include <stdio.h>
#include <string.h>

#include "rc_font5x7.h"
#include "rc_video_ps3.h"

#define RC_OVERLAY_SCALE   2
#define RC_OVERLAY_PAD     8
#define RC_OVERLAY_LINE    (RC_FONT_H * RC_OVERLAY_SCALE + 2)
#define RC_OVERLAY_MAX_COLS 60

static int s_on;
static int s_x = 24;
static int s_y = 24;
static int s_cursor;
static int s_widest;
static uint32_t *s_target;
static int s_stride_px;

void rc_overlay_set(int on)
{
    s_on = on;
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
    rc_video_info info;
    int box_w, box_h;

    if (!s_on)
        return 0;
    s_target = rc_video_back_buffer();
    if (s_target == NULL)
        return 0;
    if (!rc_video_info_get(&info) || info.pitch <= 0)
        return 0;

    s_stride_px = info.pitch / 4;
    box_w = RC_OVERLAY_MAX_COLS * (RC_FONT_W + 1) * RC_OVERLAY_SCALE + RC_OVERLAY_PAD * 2;
    box_h = lines * RC_OVERLAY_LINE + RC_OVERLAY_PAD * 2;

    if (s_x + box_w > info.width)
        box_w = info.width - s_x;
    if (s_y + box_h > info.height)
        box_h = info.height - s_y;
    if (box_w <= 0 || box_h <= 0)
        return 0;

    /* Dark grey rather than black: over a dark game a black box is invisible and the text appears to
     * float, which makes it harder to read, not easier. */
    fill(s_target, s_stride_px, s_x, s_y, box_w, box_h, 0x00141414u);
    s_cursor = 0;
    s_widest = box_w;
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

    x = s_x + RC_OVERLAY_PAD;
    for (i = 0; text[i] != '\0' && i < RC_OVERLAY_MAX_COLS; i++) {
        draw_char(x, s_y + RC_OVERLAY_PAD + s_cursor * RC_OVERLAY_LINE, text[i], colour);
        x += (RC_FONT_W + 1) * RC_OVERLAY_SCALE;
    }
    s_cursor++;
}

void rc_overlay_end(void)
{
    s_target = NULL;
}
