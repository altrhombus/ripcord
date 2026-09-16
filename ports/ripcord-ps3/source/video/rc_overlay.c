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
 * THE PANEL IS BUILT IN MAIN MEMORY AND COPIED TO RSX MEMORY WHEN IT CHANGES, which is a reversal.
 * It used to be built directly in RSX memory under a rule that it may only ever be written, because a
 * Cell read from there is roughly two orders of magnitude slower than a write. That rule made
 * antialiasing impossible - blending a glyph's coverage over a background IS a read - and antialiasing
 * is most of what separates the console's own typeface from a hand-drawn bitmap.
 *
 * So the drawing happens where reads are cheap and the result is copied across on rebuild: 555 KB,
 * four times a second, in the direction that is fast. The per-frame path is unchanged - the RSX copies
 * the same VRAM bitmap over the picture every frame either way.
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
#include <malloc.h>
#include <string.h>

#include "rc_font5x7.h"
#include "rc_font_prop.h"
#include "rc_sysfont.h"
#include "rc_video_ps3.h"
#include "platform/rc_platform.h"

/*
 * THE PANEL IS DESIGNED AT 1080p AND BUILT AT WHATEVER THE TELEVISION NEGOTIATED.
 *
 * Every measurement here and in the layout is a "design pixel" against a 1920x1080 screen, and
 * rc_overlay_px converts one to a real one. Without that the panel is a fixed 760 pixels wide: 40% of
 * a 1080p screen, 59% of a 720p one, and WIDER THAN a 720x480 one - at which point
 * rc_video_overlay_blit refuses the rectangle and the overlay silently is not there. A diagnostic that
 * disappears on the displays least able to spare the bandwidth is worse than no diagnostic.
 *
 * Scaling the bitmap on the way to the screen would have been less code and would have softened every
 * glyph. The atlas is built once at open, so building it at the right size instead costs nothing.
 */
#define RC_OV_DESIGN_W     760
#define RC_OV_DESIGN_H     352
#define RC_OV_DESIGN_SCR_H 1080
#define RC_OV_REBUILD_MS   250u

/* Big enough for the design size at 1080p; anything smaller uses less of it. */
#define RC_OV_MAX_W        RC_OV_DESIGN_W
#define RC_OV_MAX_H        RC_OV_DESIGN_H

static int s_scr_h = RC_OV_DESIGN_SCR_H;
static int s_w = RC_OV_DESIGN_W;
static int s_h = RC_OV_DESIGN_H;

int rc_overlay_px(int design)
{
    return (design * s_scr_h) / RC_OV_DESIGN_SCR_H;
}

static int s_on;
static int s_x = 48;
static int s_y = 40;

static uint32_t *s_bitmap;      /* main memory - drawn into, read from, blended in          */
static uint32_t *s_vram;        /* RSX memory  - what the per-frame copy actually reads     */
static uint32_t s_offset;
static int s_ready;
static int s_rebuilt;           /* a rebuild happened this frame and owes the RSX a copy    */
static uint64_t s_next_rebuild;

/*
 * Coverage from the system font, one line's worth. Cleared per run rather than per rebuild, because a
 * run has to be composited in its own colour and runs overlap on a line.
 */
#define RC_OV_COV_H 72
static unsigned char s_cov[RC_OV_MAX_W * RC_OV_COV_H];
static int s_sysfont;
static int s_want_sysfont;

/* Defined below; rc_overlay_set needs both of the panel's sizes to build the atlas with. */
static float size_for(int scale);

int rc_overlay_width(void)  { return s_w; }
int rc_overlay_height(void) { return s_h; }

/*
 * PREPARED AND SHOWN ARE DIFFERENT QUESTIONS, and separating them is what makes a runtime toggle safe.
 *
 * Preparing means allocating two megabytes of panel and rasterising an atlas of a couple of hundred
 * glyphs. Doing that when someone presses the toggle would do it ON THE DECODE THREAD, mid-stream,
 * which is precisely the b256 fault: 1,483 ms inside one rebuild and 2 fps. So preparation happens once
 * at session set-up whether or not the overlay is wanted, and the toggle only decides whether to draw.
 *
 * The cost of preparing something nobody asked for is memory that is there anyway and a few
 * milliseconds before the stream starts. The cost of the alternative is a visible stall every time
 * someone wants to see the numbers - which is, by definition, when something already looks wrong.
 */
static int s_shown;

void rc_overlay_show(int on)
{
    s_shown = on;
}

int rc_overlay_shown(void)
{
    return s_shown;
}

int rc_overlay_on(void)
{
    return s_on && s_ready && s_shown;
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
    if (x + w > s_w) w = s_w - x;
    if (y + h > s_h) h = s_h - y;
    if (w <= 0 || h <= 0)
        return;

    for (row = 0; row < h; row++) {
        uint32_t *p = s_bitmap + (size_t)(y + row) * (size_t)s_w + (size_t)x;

        for (col = 0; col < w; col++)
            p[col] = c;
    }
}

void rc_overlay_set_system_font(int on)
{
    s_want_sysfont = on;
}

void rc_overlay_set(int on)
{
    rc_video_info info;

    s_on = on;
    if (on && !s_ready) {
        /*
         * The display's height drives everything: the atlas sizes, the panel, and the layout the
         * caller computes through rc_overlay_px. Read once at open because it cannot change without
         * the display being reopened, and the atlas is built here.
         */
        if (rc_video_info_get(&info) && info.height > 0) {
            s_scr_h = info.height;
            s_x = rc_overlay_px(48);
            s_y = rc_overlay_px(32);
            s_w = rc_overlay_px(RC_OV_DESIGN_W);
            s_h = rc_overlay_px(RC_OV_DESIGN_H);
            if (s_w > info.width - s_x * 2)
                s_w = info.width - s_x * 2;
            if (s_h > info.height - s_y * 2)
                s_h = info.height - s_y * 2;
        }
        if (s_w <= 0 || s_h <= 0)
            return;
        s_bitmap = (uint32_t *)memalign(128, (size_t)s_w * (size_t)s_h * 4u);
        s_vram = (uint32_t *)rc_video_alloc_rsx((size_t)s_w * (size_t)s_h * 4u, &s_offset);
        s_ready = (s_bitmap != NULL && s_vram != NULL);

        /*
         * The console's own face if it will open, the drawn one if it will not. Asked for here rather
         * than at start-up so a font library that refuses costs an overlay nobody had yet rather than
         * a session: everything below falls back glyph for glyph.
         */
        /*
         * The console's own face, read out of /dev_flash with FreeType. Opt-in only because the
         * hand-drawn fallback is the thing that cannot fail to load; see rc_sysfont.c.
         */
        if (s_ready && s_want_sysfont)
            s_sysfont = rc_sysfont_open(size_for(1), size_for(2));
    }
}

int rc_overlay_using_system_font(void)
{
    return s_sysfont;
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

                if (py < 0 || py >= s_h)
                    continue;
                p = s_bitmap + (size_t)py * (size_t)s_w;
                for (sx = 0; sx < scale; sx++) {
                    if (px + sx >= 0 && px + sx < s_w)
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

/*
 * One pixel of antialiased text: the glyph's coverage says how much of the run's colour to put over
 * what is already there. This is the read the panel moved out of RSX memory for.
 */
static void blend_px(uint32_t *dst, uint32_t argb, unsigned cov)
{
    unsigned inv = 255u - cov;
    uint32_t d = *dst;
    unsigned r = ((((argb >> 16) & 0xffu) * cov) + (((d >> 16) & 0xffu) * inv)) / 255u;
    unsigned g = ((((argb >> 8) & 0xffu) * cov) + (((d >> 8) & 0xffu) * inv)) / 255u;
    unsigned b = (((argb & 0xffu) * cov) + ((d & 0xffu) * inv)) / 255u;

    *dst = 0xff000000u | (r << 16) | (g << 8) | b;
}

/*
 * A run in the console's own face. The coverage buffer is one line tall and the run is rendered at a
 * baseline inside it, then composited at `y`, which is the TOP of the line - callers lay out against
 * boxes, not against a baseline they cannot see.
 */
/*
 * `scale` carries over from the drawn font, where it meant "multiply every pixel". Here it selects a
 * size instead, and the two sizes are the two the panel uses - a heading and everything else. Keeping
 * the parameter rather than adding a second one means the layout code did not have to change when the
 * face did.
 */
/* Design pixels, converted like every other measurement here. */
static float size_for(int scale)
{
    return (float)rc_overlay_px((scale >= 2) ? 34 : 24);
}

static void draw_sys(int x, int y, int scale, uint32_t argb, const char *text)
{
    int base, w, x0, x1;
    int row, col, h;

    rc_sysfont_set_size(size_for(scale));
    base = rc_sysfont_ascent();

    /*
     * ONLY THE COLUMNS THIS RUN TOUCHES ARE CLEARED AND COMPOSITED.
     *
     * Measuring is free now that the glyphs are an atlas, so the run's width is known before it is
     * drawn - and clearing the whole 760-pixel band for a six-character label, twenty times a rebuild,
     * is most of a megabyte of memset and a million pixel tests to put a few thousand pixels down.
     * That was affordable before only because nothing else was competing; this runs on the thread that
     * drains the socket.
     */
    w = rc_sysfont_render(NULL, 0, 0, 0, 0, text);
    x0 = x - 2;
    x1 = x + w + 2;
    if (x0 < 0)
        x0 = 0;
    if (x1 > s_w)
        x1 = s_w;
    if (x1 <= x0)
        return;

    h = RC_OV_COV_H;
    if (y + h > s_h)
        h = s_h - y;
    if (h <= 0)
        return;

    for (row = 0; row < RC_OV_COV_H; row++)
        memset(s_cov + (size_t)row * (size_t)s_w + (size_t)x0, 0, (size_t)(x1 - x0));

    (void)rc_sysfont_render(s_cov, s_w, RC_OV_COV_H, x, base, text);

    for (row = 0; row < h; row++) {
        const unsigned char *src = s_cov + (size_t)row * (size_t)s_w;
        uint32_t *dst = s_bitmap + (size_t)(y + row) * (size_t)s_w;

        if (y + row < 0)
            continue;
        for (col = x0; col < x1; col++) {
            if (src[col] != 0u)
                blend_px(&dst[col], argb, src[col]);
        }
    }
}

static void draw_prop(int x, int y, int scale, uint32_t argb, const char *text)
{
    uint32_t c;
    int i;

    if (s_sysfont) {
        draw_sys(x, y, scale, argb, text);
        return;
    }
    /*
     * THE DRAWN FONT IS STEPPED UP TO MATCH. `scale` is written for the system face, where 1 means
     * body text at 26 px; the same 1 through the drawn 9-row face is 9 px, which is unreadable across
     * a room - and across a room is where this is read. Doubling keeps one set of layout constants
     * describing both faces instead of two sets that have to be kept in step.
     */
    c = premul(argb);
    scale *= 2;
    for (i = 0; text[i] != '\0'; i++)
        x += draw_prop_char(x, y, scale, text[i], c);
}

/* Measured through whichever face is in use, so right-alignment does not depend on which one opened. */
static int text_width(const char *text, int scale)
{
    if (s_sysfont) {
        rc_sysfont_set_size(size_for(scale));
        return rc_sysfont_render(NULL, 0, 0, 0, 0, text);
    }
    return prop_width(text, scale * 2);
}

/* The same, for a figure - which is a different width, because tabular cells are wider than the
 * digits in them. Measuring and drawing have to agree or right-aligned numbers drift. */
static int num_width(const char *text, int scale)
{
    int w;

    if (!s_sysfont)
        return (int)strlen(text) * (RC_FONT_W + 1) * scale * 2;
    rc_sysfont_set_size(size_for(scale));
    rc_sysfont_set_tabular(1);
    w = rc_sysfont_render(NULL, 0, 0, 0, 0, text);
    rc_sysfont_set_tabular(0);
    return w;
}

/*
 * Figures. In the system face they are drawn tabular - fixed cells - so the panel is set in ONE
 * typeface and the numbers still hold still. The drawn fallback has no proportional digits to make
 * tabular, so it keeps the monospaced 5x7, which is what that face is for.
 */
static void draw_mono(int x, int y, int scale, uint32_t argb, const char *text)
{
    uint32_t c;
    int i;

    if (s_sysfont) {
        rc_sysfont_set_tabular(1);
        draw_sys(x, y, scale, argb, text);
        rc_sysfont_set_tabular(0);
        return;
    }
    c = premul(argb);

    /* Stepped up for the same reason as the drawn proportional face - see draw_prop. */
    scale *= 2;
    for (i = 0; text[i] != '\0'; i++) {
        draw_mono_char(x, y, scale, text[i], c);
        x += (RC_FONT_W + 1) * scale;
    }
}

int rc_overlay_text(int x, int y, int scale, uint32_t argb, const char *fmt, ...)
{
    char text[96];
    va_list ap;

    if (!s_ready)
        return 0;
    va_start(ap, fmt);
    (void)vsnprintf(text, sizeof(text), fmt, ap);
    va_end(ap);
    draw_prop(x, y, scale, argb, text);
    return text_width(text, scale);
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
    draw_prop(x - text_width(text, scale), y, scale, argb, text);
}

int rc_overlay_num_width(int scale, const char *fmt, ...)
{
    char text[96];
    va_list ap;

    va_start(ap, fmt);
    (void)vsnprintf(text, sizeof(text), fmt, ap);
    va_end(ap);
    return num_width(text, scale);
}

int rc_overlay_num(int x, int y, int scale, uint32_t argb, const char *fmt, ...)
{
    char text[96];
    va_list ap;

    if (!s_ready)
        return 0;
    va_start(ap, fmt);
    (void)vsnprintf(text, sizeof(text), fmt, ap);
    va_end(ap);
    draw_mono(x, y, scale, argb, text);
    return num_width(text, scale);
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
    draw_mono(x - num_width(text, scale), y, scale, argb, text);
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

/*
 * The baseline offset for a size, so two sizes on one row can share a baseline instead of sharing a
 * top edge. Aligning tops is what made "Ripcord" and the build id sit at different heights in the
 * header: their boxes lined up and their letters did not.
 */
int rc_overlay_ascent(int scale)
{
    if (s_sysfont) {
        rc_sysfont_set_size(size_for(scale));
        return rc_sysfont_ascent();
    }
    return RC_FONT_H * scale * 2;
}

/*
 * A rebuild that ignores the throttle.
 *
 * The throttle exists because the diagnostics panel is redrawn from a 60 Hz present path and its
 * numbers are rates nobody reads at that speed. A status card is drawn when the state CHANGES, which is
 * rare and is exactly when waiting a quarter of a second would be wrong - the change is the thing the
 * viewer is waiting to see.
 */
int rc_overlay_begin_now(void)
{
    if (!s_ready)
        return 0;
    rc_overlay_rect(0, 0, s_w, s_h, 0x00000000u);
    s_rebuilt = 1;
    return 1;
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
    s_rebuilt = 1;

    /*
     * Cleared to fully transparent rather than to the panel colour. The caller paints the panel, and
     * the corners it leaves alone stay see-through - which is what makes a rounded corner possible at
     * all when nothing here can read what is underneath.
     */
    rc_overlay_rect(0, 0, s_w, s_h, 0x00000000u);
    return 1;
}

/*
 * The copy, without the `shown` gate. rc_overlay_end is for the diagnostics panel and correctly does
 * nothing when it is hidden; a status card is drawn because something asked for it, not because a
 * toggle is on.
 */
void rc_overlay_end_now(int x, int y)
{
    if (!s_ready)
        return;
    memcpy(s_vram, s_bitmap, (size_t)s_w * (size_t)s_h * 4u);
    s_rebuilt = 0;
    /*
     * THE POSITION IS AN ARGUMENT, NOT STATE, and it is that way because making it state was a bug.
     * The status card set a shared origin to centre itself and never put it back, so the diagnostics
     * overlay - which wants the top left - moved to the middle for the rest of the session. Two callers
     * wanting different positions is not a reason for either to mutate the other's.
     */
    rc_video_overlay_blit(s_offset, s_w * 4, s_w, s_h, x, y);
}

void rc_overlay_end(void)
{
    if (!s_on || !s_ready)
        return;

    /*
     * The copy across happens only when the panel actually changed. It is 555 KB in the direction the
     * Cell is fast at, four times a second - and doing it every frame instead would be 33 MB/s of
     * writes to VRAM for a picture that is identical fourteen times out of fifteen.
     */
    if (s_rebuilt) {
        memcpy(s_vram, s_bitmap, (size_t)s_w * (size_t)s_h * 4u);
        s_rebuilt = 0;
    }
    rc_video_overlay_blit(s_offset, s_w * 4, s_w, s_h, s_x, s_y);
}
