/*
 * ripcord-ps3 - the on-screen diagnostics overlay.
 *
 * The same answers the .NET client's diagnostics report gives, on a console with no second window to
 * put them in. Every number this port produced used to go to a log file readable only after the session
 * ended, over FTP, from another machine - fine for bring-up and useless for "why did it just go blocky".
 *
 * IT DRAWS INTO ITS OWN BITMAP, NOT INTO THE BACK BUFFER, and b225 is why. Drawing into the back buffer
 * put the text there with the PPE immediately after the picture blit had been QUEUED - so the RSX ran
 * the picture afterwards and painted over every pixel of it. Nothing appeared, on a path where every
 * counter said the frame had been drawn. The bitmap is copied over the picture by a command issued
 * after it, which is an ordering the hardware keeps instead of one this code has to win.
 *
 * THE BITMAP IS IN RSX MEMORY AND IS ONLY EVER WRITTEN. Writes there are fast and reads are roughly two
 * orders of magnitude slower, so there is no blending here, no read-modify-write and no clearing by
 * reading first. Translucency is not this code's job: the panel is written as premultiplied ARGB and
 * the RSX blends it against the picture when it copies, which is the one place the read is free.
 *
 * TWO FONTS, ON PURPOSE. Words are set in the proportional face and anything that MOVES is set in the
 * monospaced one and right-aligned. A column of figures has to hold still while the figures change: a
 * digit one pixel narrower than its neighbour makes the whole row shuffle every time it ticks, and a
 * number that moves while you read it is a number you read twice. Fixed pitch is wrong for the words
 * for the opposite reason - it reads like a teletype, and every label here is prose.
 *
 * THE TEXT IS REBUILT A FEW TIMES A SECOND, NOT EVERY FRAME. All of it is rates and totals nobody reads
 * at 60 Hz. The COPY is queued every frame, because the picture blit clears the back buffer each time.
 */
#include "rc_overlay.h"

#include <stdarg.h>
#include <stdio.h>
#include <string.h>

#include "rc_font5x7.h"
#include "rc_font_prop.h"
#include "rc_video_ps3.h"
#include "platform/rc_platform.h"

#define RC_OV_W            560
#define RC_OV_H            248
#define RC_OV_REBUILD_MS   250u

static int s_on;
static int s_x = 48;
static int s_y = 40;

static uint32_t *s_bitmap;
static uint32_t s_offset;
static int s_ready;
static uint64_t s_next_rebuild;

int rc_overlay_width(void)  { return RC_OV_W; }
int rc_overlay_height(void) { return RC_OV_H; }

int rc_overlay_on(void)
{
    return s_on && s_ready;
}

/*
 * Straight ARGB in, premultiplied ARGB out.
 *
 * The RSX's blend expects the colour already scaled by the alpha; writing the constants that way would
 * make every one of them unreadable and impossible to adjust. The division is by 255 rather than a
 * shift by 8 so that full opacity stays exactly the input colour - >>8 loses a level and turns white
 * into 254 grey, which is invisible alone and shows up as a seam where two "white" things meet.
 */
static uint32_t premul(uint32_t argb)
{
    unsigned a = (argb >> 24) & 0xffu;
    unsigned r = ((argb >> 16) & 0xffu) * a / 255u;
    unsigned g = ((argb >> 8) & 0xffu) * a / 255u;
    unsigned b = (argb & 0xffu) * a / 255u;

    return (a << 24) | (r << 16) | (g << 8) | b;
}

void rc_overlay_rect(int x, int y, int w, int h, uint32_t argb)
{
    uint32_t c = premul(argb);
    int row, col;

    if (!s_ready || w <= 0 || h <= 0)
        return;
    if (x < 0) { w += x; x = 0; }
    if (y < 0) { h += y; y = 0; }
    if (x + w > RC_OV_W) w = RC_OV_W - x;
    if (y + h > RC_OV_H) h = RC_OV_H - y;
    if (w <= 0 || h <= 0)
        return;

    for (row = 0; row < h; row++) {
        uint32_t *p = s_bitmap + (size_t)(y + row) * RC_OV_W + (size_t)x;

        for (col = 0; col < w; col++)
            p[col] = c;
    }
}

void rc_overlay_set(int on)
{
    s_on = on;
    if (on && !s_ready) {
        s_bitmap = (uint32_t *)rc_video_alloc_rsx((size_t)RC_OV_W * RC_OV_H * 4u, &s_offset);
        s_ready = (s_bitmap != NULL);
    }
}

/*
 * One glyph, from rows of bits. `msb` is the mask for the leftmost column - the two fonts pack from
 * opposite ends of the byte (5 wide in the low bits, up to 8 wide from the top) and passing the mask in
 * is cheaper than making them agree, which would mean rewriting one of the tables.
 */
static void draw_bits(int x, int y, int scale, const unsigned char *rows, int nrows, int width,
                      unsigned msb, uint32_t premul_colour)
{
    int gy, gx, sy, sx;

    for (gy = 0; gy < nrows; gy++) {
        unsigned char bits = rows[gy];

        for (gx = 0; gx < width; gx++) {
            if ((bits & (msb >> gx)) == 0u)
                continue;
            for (sy = 0; sy < scale; sy++) {
                int py = y + gy * scale + sy;
                int px = x + gx * scale;
                uint32_t *p;

                if (py < 0 || py >= RC_OV_H)
                    continue;
                p = s_bitmap + (size_t)py * RC_OV_W;
                for (sx = 0; sx < scale; sx++) {
                    if (px + sx >= 0 && px + sx < RC_OV_W)
                        p[px + sx] = premul_colour;
                }
            }
        }
    }
}

/* Monospaced 5x7, for numbers. See the note at the top of this file for why there are two. */
static void draw_mono_char(int x, int y, int scale, char c, uint32_t premul_colour)
{
    if (c >= 'a' && c <= 'z')
        c = (char)(c - 'a' + 'A');
    if (c < RC_FONT_FIRST || c > RC_FONT_LAST)
        return;
    draw_bits(x, y, scale, kFont5x7[c - RC_FONT_FIRST], RC_FONT_H, RC_FONT_W, 0x10u, premul_colour);
}

/* Proportional, for words. Returns the advance so a caller can lay out a run of them. */
static int draw_prop_char(int x, int y, int scale, char c, uint32_t premul_colour)
{
    const rc_prop_glyph *g;

    if (c < RC_PROP_FIRST || c > RC_PROP_LAST)
        c = '?';
    g = &kFontProp[c - RC_PROP_FIRST];
    draw_bits(x, y, scale, g->row, RC_PROP_ROWS, g->width, 0x80u, premul_colour);
    return ((int)g->width + RC_PROP_GAP) * scale;
}

static int prop_width(const char *text, int scale)
{
    int w = 0, i;

    for (i = 0; text[i] != '\0'; i++) {
        char c = text[i];

        if (c < RC_PROP_FIRST || c > RC_PROP_LAST)
            c = '?';
        w += ((int)kFontProp[c - RC_PROP_FIRST].width + RC_PROP_GAP) * scale;
    }
    return w;
}

static void draw_prop(int x, int y, int scale, uint32_t argb, const char *text)
{
    uint32_t c = premul(argb);
    int i;

    for (i = 0; text[i] != '\0'; i++)
        x += draw_prop_char(x, y, scale, text[i], c);
}

static void draw_mono(int x, int y, int scale, uint32_t argb, const char *text)
{
    uint32_t c = premul(argb);
    int i;

    for (i = 0; text[i] != '\0'; i++) {
        draw_mono_char(x, y, scale, text[i], c);
        x += (RC_FONT_W + 1) * scale;
    }
}

void rc_overlay_text(int x, int y, int scale, uint32_t argb, const char *fmt, ...)
{
    char text[96];
    va_list ap;

    if (!s_ready)
        return;
    va_start(ap, fmt);
    (void)vsnprintf(text, sizeof(text), fmt, ap);
    va_end(ap);
    draw_prop(x, y, scale, argb, text);
}

void rc_overlay_text_right(int x, int y, int scale, uint32_t argb, const char *fmt, ...)
{
    char text[96];
    va_list ap;

    if (!s_ready)
        return;
    va_start(ap, fmt);
    (void)vsnprintf(text, sizeof(text), fmt, ap);
    va_end(ap);
    draw_prop(x - prop_width(text, scale), y, scale, argb, text);
}

void rc_overlay_num(int x, int y, int scale, uint32_t argb, const char *fmt, ...)
{
    char text[96];
    va_list ap;

    if (!s_ready)
        return;
    va_start(ap, fmt);
    (void)vsnprintf(text, sizeof(text), fmt, ap);
    va_end(ap);
    draw_mono(x, y, scale, argb, text);
}

/*
 * The one that matters for a figure that changes: the RIGHT edge lands on x, so digits grow leftwards
 * and everything after the number stays where it was. See the two-font note at the top of the file.
 */
void rc_overlay_num_right(int x, int y, int scale, uint32_t argb, const char *fmt, ...)
{
    char text[96];
    va_list ap;

    if (!s_ready)
        return;
    va_start(ap, fmt);
    (void)vsnprintf(text, sizeof(text), fmt, ap);
    va_end(ap);
    draw_mono(x - (int)strlen(text) * (RC_FONT_W + 1) * scale, y, scale, argb, text);
}

void rc_overlay_bars(int x, int y, int w, int h, const unsigned *v, unsigned n, unsigned max,
                     uint32_t argb)
{
    unsigned i;
    int bar_w;

    if (!s_ready || n == 0u || h <= 0)
        return;
    bar_w = w / (int)n;
    if (bar_w < 1)
        bar_w = 1;
    if (max == 0u)
        max = 1u;

    for (i = 0u; i < n; i++) {
        unsigned value = (v[i] > max) ? max : v[i];
        int bh = (int)((unsigned long)value * (unsigned long)h / max);
        int bx = x + (int)i * bar_w;

        /*
         * The track is drawn first and the bar over it, so a short bar reads as "low" rather than as
         * "missing" - an empty column and a column that never got data look identical otherwise, and
         * one of those is a fault.
         */
        rc_overlay_rect(bx, y, bar_w - 1, h, RC_OV_TRACK);
        if (bh < 1)
            bh = 1;
        rc_overlay_rect(bx, y + h - bh, bar_w - 1, bh, argb);
    }
}

int rc_overlay_begin(void)
{
    uint64_t now;

    if (!s_on || !s_ready)
        return 0;

    now = rc_time_ms();
    if (now < s_next_rebuild)
        return 0;
    s_next_rebuild = now + RC_OV_REBUILD_MS;

    /*
     * Cleared to fully transparent rather than to the panel colour. The caller paints the panel, and
     * the corners it leaves alone stay see-through - which is what makes a rounded corner possible at
     * all when nothing here can read what is underneath.
     */
    rc_overlay_rect(0, 0, RC_OV_W, RC_OV_H, 0x00000000u);
    return 1;
}

void rc_overlay_end(void)
{
    if (!s_on || !s_ready)
        return;
    rc_video_overlay_blit(s_offset, RC_OV_W * 4, RC_OV_W, RC_OV_H, s_x, s_y);
}
