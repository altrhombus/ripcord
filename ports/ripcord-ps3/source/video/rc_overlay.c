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
#include "rc_log.h"
#include "rc_sysfont.h"
#include "rc_video_ps3.h"
/* For HALYARD_PAD_*: rc_overlay_glyph takes the same button bits a caller tests input against. */
#include "halyard_input.h"
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
/*
 * THE SURFACE IS NOW THE WHOLE SCREEN, and that is what lets the shell put a moving background behind
 * antialiased text at all. Blending means reading what is underneath; reads from RSX memory are about a
 * hundred times slower than writes, and the one time a blend was handed to the RSX instead its command
 * processor stopped and every flip after the first stayed pending forever (b232 - "no video just
 * audio"). So everything composites here, in main memory, and exactly one opaque copy reaches the
 * screen.
 *
 * It costs 8 MB of main memory and 8 MB of video memory at 1080p, held for the run. The alternative was
 * a second surface with a second font atlas and a second copy of the blending, which costs the same
 * memory and an extra copy of every bug.
 */
#define RC_OV_MAX_W        1920
#define RC_OV_MAX_H        1088

static int s_scr_h = RC_OV_DESIGN_SCR_H;
static int s_w = RC_OV_MAX_W;   /* the bitmap - the whole screen */
static int s_h = RC_OV_MAX_H;
static int s_panel_w = RC_OV_DESIGN_W;
static int s_panel_h = RC_OV_DESIGN_H;

int rc_overlay_px(int design)
{
    return (design * s_scr_h) / RC_OV_DESIGN_SCR_H;
}

static int s_on;
static int s_x = 48;
static int s_y = 40;

static uint32_t *s_bitmap;      /* main memory - drawn into, read from, blended in          */

/*
 * WHERE THE TEXT ACTUALLY LANDS, which is no longer always the surface.
 *
 * The shell draws its whole interface once into a cached LAYER and composites that over the moving
 * background every frame, rather than re-drawing it onto the background sixty times a second - see
 * rc_shell.c. Text is most of what a layer contains, so this file has to be able to aim somewhere else.
 *
 * It is a destination and a pitch, not a mode: the compositing arithmetic below is the same either way.
 * The only thing that differs is that a layer starts transparent and accumulates an alpha, where the
 * surface starts opaque and stays opaque - and writing the alpha out properly covers both, because a
 * blend onto an opaque destination arrives at 255 on its own.
 */
/* An IO window onto s_bitmap, when the RSX would accept one - see rc_video_map_main. */
static uint32_t s_main_offset;
static int s_from_main;

#define RC_OV_MB (1024u * 1024u)

static uint32_t *s_dst;
static int s_dst_pitch;

/*
 * WHERE THE INK LANDED, told rather than looked for.
 *
 * A caller keeping a cached layer has to know which part of each row has anything on it, or every frame
 * composites the whole screen. Finding out afterwards means reading the layer back - eight megabytes,
 * on the one path that is supposed to be rare and quick. This file already knows the answer as it
 * draws, so it says so.
 */
static void (*s_ink)(int y, int x0, int x1);

void rc_overlay_set_ink_hook(void (*hook)(int y, int x0, int x1))
{
    s_ink = hook;
}
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
static void blend_px(uint32_t *dst, uint32_t argb, unsigned cov);

/* The PANEL - what the diagnostics overlay and the status card lay out against. */
int rc_overlay_width(void)  { return s_panel_w; }
int rc_overlay_height(void) { return s_panel_h; }

/* The SURFACE - the whole bitmap, which is larger. A menu uses this. */
int rc_overlay_surface_width(void)  { return s_w; }
int rc_overlay_surface_height(void) { return s_h; }

void rc_overlay_target(uint32_t *px, int pitch)
{
    s_dst = (px != NULL) ? px : s_bitmap;
    s_dst_pitch = (px != NULL && pitch > 0) ? pitch : s_w;
}

uint32_t *rc_overlay_pixels(int *pitch_px)
{
    if (!s_ready)
        return NULL;
    if (pitch_px != NULL)
        *pitch_px = s_w;
    return s_bitmap;
}

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

/* Whether the bitmap and atlas exist at all. Distinct from `shown`: something can be prepared and
 * hidden, and only one of those two can be fixed by asking for it. */
int rc_overlay_prepared(void)
{
    return s_ready;
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
        uint32_t *p = s_dst + (size_t)(y + row) * (size_t)s_dst_pitch + (size_t)x;

        if (s_ink != NULL)
            s_ink(y + row, x, x + w);
        for (col = 0; col < w; col++)
            p[col] = c;
    }
}

/*
 * A FILL THAT LETS WHAT IS UNDERNEATH THROUGH, which rc_overlay_rect deliberately does not.
 *
 * rc_overlay_rect writes its colour and is right to: the diagnostics panel is opaque by decision, and a
 * blended fill that reads every pixel it touches is real work. A card floating over a moving background
 * is the case that needs the other behaviour, and it is affordable for exactly the reason the panel's
 * was not - this runs at the menu, where nothing else is competing for the machine at all.
 */
void rc_overlay_blend_rect(int x, int y, int w, int h, uint32_t argb)
{
    unsigned alpha = (argb >> 24) & 0xffu;
    int row, col;

    if (!s_ready || w <= 0 || h <= 0 || alpha == 0u)
        return;
    if (alpha == 255u) {
        rc_overlay_rect(x, y, w, h, argb);
        return;
    }
    if (x < 0) { w += x; x = 0; }
    if (y < 0) { h += y; y = 0; }
    if (x + w > s_w) w = s_w - x;
    if (y + h > s_h) h = s_h - y;
    if (w <= 0 || h <= 0)
        return;

    for (row = 0; row < h; row++) {
        uint32_t *p = s_dst + (size_t)(y + row) * (size_t)s_dst_pitch + (size_t)x;

        if (s_ink != NULL)
            s_ink(y + row, x, x + w);
        for (col = 0; col < w; col++)
            blend_px(&p[col], argb, alpha);
    }
}

/* ------------------------------------------------------------------------------------------------
 * THE BUTTON GLYPHS.
 *
 * Same principle rc_shell.c draws its footer on: an exact distance to the shape's outline, so the
 * diagonals of a cross come out as clean lines rather than as stairs. It is a second implementation
 * rather than a shared one because the shell's rasteriser is built around its own cached layer - its
 * sub-pixel grid, its ink-bounds recording, its own pixel writer - and none of that exists here. What
 * is shared is the arithmetic, which is short, and the two are independently checkable by looking at
 * them. If a third caller ever wants these, that is the moment to move the shell's version down here.
 * ------------------------------------------------------------------------------------------------ */

#define OV_SUB      64      /* sub-pixel steps per pixel, for the distance arithmetic */
#define OV_SUB_HALF 32

/*
 * Integer square root, Newton from a power-of-four seed. The seed MUST be a power of four: seeded at
 * 1<<15 this answers sqrt(2v) and saturates, which in the shell drew corners at the wrong radius for
 * three builds before anyone could name what was wrong with them.
 */
static int ov_isqrt(int v)
{
    int g = 1 << 30;
    int r = 0;

    if (v <= 0)
        return 0;
    while (g > v)
        g >>= 2;
    while (g != 0) {
        if (v >= r + g) {
            v -= r + g;
            r = (r >> 1) + g;
        } else {
            r >>= 1;
        }
        g >>= 2;
    }
    return r;
}

/* Coverage from a signed distance in OV_SUB-ths of a pixel: negative is inside the stroke. */
static unsigned ov_cov(int d)
{
    if (d <= -OV_SUB_HALF)
        return 255u;
    if (d >= OV_SUB_HALF)
        return 0u;
    return (unsigned)((OV_SUB_HALF - d) * 255 / OV_SUB);
}

int rc_overlay_glyph(uint32_t button, int x, int y, int size, uint32_t argb)
{
    unsigned alpha = (argb >> 24) & 0xffu;
    uint32_t rgb = argb & 0x00FFFFFFu;
    int half = (size * OV_SUB) / 2;
    int stroke = (size * OV_SUB * 11) / 100;   /* 11% of the box, which reads at ten feet */
    int radius = (size * OV_SUB * 36) / 100;
    int row, col;

    if (!s_ready || size <= 0 || alpha == 0u)
        return size > 0 ? size : 0;
    if (button != HALYARD_PAD_CIRCLE && button != HALYARD_PAD_CROSS)
        return size;
    if (stroke < OV_SUB)
        stroke = OV_SUB;

    for (row = 0; row < size; row++) {
        int vy = row * OV_SUB + OV_SUB_HALF - half;
        int py = y + row;
        uint32_t *p;

        if (py < 0 || py >= s_h)
            continue;
        p = s_dst + (size_t)py * (size_t)s_dst_pitch;
        if (s_ink != NULL)
            s_ink(py, x, x + size);

        for (col = 0; col < size; col++) {
            int vx = col * OV_SUB + OV_SUB_HALF - half;
            int px = x + col;
            int d;
            unsigned c;

            if (px < 0 || px >= s_w)
                continue;

            if (button == HALYARD_PAD_CIRCLE) {
                int dist = ov_isqrt(vx * vx + vy * vy) - radius;

                if (dist < 0)
                    dist = -dist;
                d = dist - stroke / 2;
            } else {
                /*
                 * Two bars through the centre at forty-five degrees. The perpendicular distance to
                 * such a line is |vx -+ vy| / sqrt(2), and 181/256 is that divisor. The bars are then
                 * cut to length, so the cross is a cross and not two full-width diagonals.
                 */
                int a = vx - vy, b = vx + vy;
                int da, db, ext;

                if (a < 0) a = -a;
                if (b < 0) b = -b;
                da = (a * 181) / 256;
                db = (b * 181) / 256;
                d = (da < db) ? da : db;
                ext = (vx < 0 ? -vx : vx);
                if ((vy < 0 ? -vy : vy) > ext)
                    ext = (vy < 0 ? -vy : vy);
                if (ext - radius > d - stroke / 2)
                    d = ext - radius + stroke / 2;
                d -= stroke / 2;
            }

            c = ov_cov(d);
            if (c != 0u)
                blend_px(&p[px], rgb, (alpha * c) / 255u);
        }
    }
    return size;
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
            s_w = info.width;
            s_h = info.height;
            if (s_w > RC_OV_MAX_W)
                s_w = RC_OV_MAX_W;
            if (s_h > RC_OV_MAX_H)
                s_h = RC_OV_MAX_H;
            s_panel_w = rc_overlay_px(RC_OV_DESIGN_W);
            s_panel_h = rc_overlay_px(RC_OV_DESIGN_H);
            if (s_panel_w > s_w - s_x * 2)
                s_panel_w = s_w - s_x * 2;
            if (s_panel_h > s_h - s_y * 2)
                s_panel_h = s_h - s_y * 2;
        }
        if (s_w <= 0 || s_h <= 0)
            return;
        /*
         * MEGABYTE-ALIGNED AND A WHOLE NUMBER OF MEGABYTES, so the RSX can be given a window onto it.
         * See rc_video_map_main: if the mapping is accepted, the 2D engine reads this buffer where it
         * stands and the eight-megabyte copy into video memory every frame stops happening. It cost
         * 10,884 us a frame, which was 29 percent of the budget for a copy that exists only because
         * nothing had asked the RSX to look at main memory.
         *
         * The alignment costs a little padding and nothing else, so it is done unconditionally: a
         * refusal then falls back to the copy without needing a second allocation.
         */
        {
            size_t want = (size_t)s_w * (size_t)s_h * 4u;
            size_t mapped = (want + (RC_OV_MB - 1u)) & ~(size_t)(RC_OV_MB - 1u);

            s_bitmap = (uint32_t *)memalign(RC_OV_MB, mapped);
            s_vram = (uint32_t *)rc_video_alloc_rsx(want, &s_offset);
            s_ready = (s_bitmap != NULL && s_vram != NULL);
            if (s_ready && rc_video_map_main(s_bitmap, mapped, &s_main_offset))
                s_from_main = 1;
            rc_log("overlay: the RSX %s read the bitmap where it is - the per-frame copy is %s\n",
                   s_from_main ? "will" : "will NOT",
                   s_from_main ? "gone" : "still needed");
        }
        s_dst = s_bitmap;
        s_dst_pitch = s_w;

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
            s_sysfont = rc_sysfont_open(size_for(1), size_for(2), size_for(3));
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
                p = s_dst + (size_t)py * (size_t)s_dst_pitch;
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
/*
 * THE ALPHA IS WRITTEN RATHER THAN ASSUMED, and that one change is what lets the same arithmetic serve
 * the opaque surface and a transparent layer.
 *
 * The colour maths is unchanged: a layer holds PREMULTIPLIED colour, so a source's contribution is
 * src * cov and what is already there contributes dst * (255 - cov), which is exactly the blend that
 * was here. Only the top byte differs - and computing it costs nothing on the surface, because a
 * destination that is already opaque arrives back at 255 by itself.
 */
static void blend_px(uint32_t *dst, uint32_t argb, unsigned cov)
{
    unsigned inv = 255u - cov;
    uint32_t d = *dst;
    unsigned a = cov + (((d >> 24) & 0xffu) * inv) / 255u;
    unsigned r = ((((argb >> 16) & 0xffu) * cov) + (((d >> 16) & 0xffu) * inv)) / 255u;
    unsigned g = ((((argb >> 8) & 0xffu) * cov) + (((d >> 8) & 0xffu) * inv)) / 255u;
    unsigned b = (((argb & 0xffu) * cov) + ((d & 0xffu) * inv)) / 255u;

    if (a > 255u)
        a = 255u;
    *dst = (a << 24) | (r << 16) | (g << 8) | b;
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
    if (scale >= 3)
        return (float)rc_overlay_px(56);   /* the shell's wordmark and console names */
    return (float)rc_overlay_px((scale >= 2) ? 34 : 24);
}

static void draw_sys(int x, int y, int scale, uint32_t argb, const char *text)
{
    int base, w, x0, x1;
    int row, col, h;

    rc_sysfont_set_size(size_for(scale));
    base = rc_sysfont_ascent();

    /*
     * A RUN THAT CANNOT BE SEEN COSTS NOTHING, and until b363 it cost almost everything.
     *
     * Callers measure a string by drawing it at y = -10000 and taking the returned advance - the width
     * comes from text_width and is free. This function, though, ran the whole way for such a call: it
     * cleared its band of the coverage buffer, rasterised every glyph into it, and only then skipped
     * the composite row by row because each row was above the surface. So every measured run was
     * rasterised twice and shown once, and the shell measures nearly everything it draws - card names,
     * pill labels, the whole hint row.
     *
     * The guard is two comparisons and it halves the text cost of a frame.
     */
    if (y >= s_h || y + RC_OV_COV_H <= 0)
        return;

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
        uint32_t *dst = s_dst + (size_t)(y + row) * (size_t)s_dst_pitch;

        if (y + row < 0)
            continue;
        if (s_ink != NULL)
            s_ink(y + row, x0, x1);
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

/*
 * WHAT THE TEXT COSTS, kept here rather than at the call sites, because the shell measures its drawing
 * as one number and a caller cannot tell a slow glyph from a slow rectangle. b367 put the shell's
 * drawing at 67 ms a frame against an estimate of ten, and an estimate wrong by six times is not
 * something to reason further from - it means something is being paid for that nobody has named.
 */
static uint64_t s_text_us;
static unsigned s_text_runs;

void rc_overlay_text_cost(unsigned *us, unsigned *runs, int reset)
{
    if (us != NULL)
        *us = (unsigned)s_text_us;
    if (runs != NULL)
        *runs = s_text_runs;
    if (reset) {
        s_text_us = 0u;
        s_text_runs = 0u;
    }
}

static void draw_prop_timed(int x, int y, int scale, uint32_t argb, const char *text)
{
    uint64_t at = rc_tick();

    draw_prop(x, y, scale, argb, text);
    s_text_us += ((rc_tick() - at) * 1000000u) / rc_tick_hz();
    s_text_runs++;
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
    draw_prop_timed(x, y, scale, argb, text);
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
    draw_prop_timed(x - text_width(text, scale), y, scale, argb, text);
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

int rc_overlay_cap_height(int scale)
{
    if (s_sysfont) {
        rc_sysfont_set_size(size_for(scale));
        return rc_sysfont_cap_height();
    }
    /* The drawn font has no descenders and no accents: its cell IS its capital. */
    return RC_FONT_H * scale * 2;
}

int rc_overlay_text_y(int box_y, int box_h, int scale)
{
    /* rc_overlay_text takes the top of the RUN; the baseline is that plus the ascent. Put the baseline
     * where it has to be for the capitals to sit centred, then work back to the top. */
    return box_y + (box_h + rc_overlay_cap_height(scale)) / 2 - rc_overlay_ascent(scale);
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
    /*
     * ONLY THE PANEL, not the whole surface. The surface is the screen now; clearing all of it for a
     * 760-pixel panel would be eight megabytes of memset four times a second on the thread that is also
     * draining a socket. The shell, which does want all of it, asks for it by name.
     */
    rc_overlay_rect(0, 0, s_panel_w, s_panel_h, 0x00000000u);
    s_rebuilt = 1;
    return 1;
}

int rc_overlay_begin_surface(int clear)
{
    if (!s_ready)
        return 0;
    if (clear)
        rc_overlay_rect(0, 0, s_w, s_h, 0xFF000000u);
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
    rc_overlay_rect(0, 0, s_panel_w, s_panel_h, 0x00000000u);
    return 1;
}

/*
 * The copy, without the `shown` gate. rc_overlay_end is for the diagnostics panel and correctly does
 * nothing when it is hidden; a status card is drawn because something asked for it, not because a
 * toggle is on.
 */
/*
 * PUSH THE BITMAP OUT OF THE CACHE BEFORE THE RSX READS IT - AND READ THIS BEFORE DELETING IT.
 *
 * HONESTY FIRST: this was added to fix visible smearing, and the smearing turned out to be something
 * else entirely - rc_shell.c's glow was drawing outside the bounds it reported, so the composite was
 * clipping it. Nothing here was ever shown to fix anything. Two builds were spent inferring a cache
 * fault from a symptom that, being PERSISTENT AND REPEATABLE, should have ruled one out immediately.
 *
 * It is kept anyway, and the reason is not sentiment. The RSX now reads a buffer the PPE writes through
 * its cache, and whether that read path snoops the PPE's L2 is a question this project has not answered
 * either way. Absence of a symptom is not absence of a race - a rare one would be miserable to find
 * later, from a report of one bad frame an hour. `sync` orders the stores and costs nothing; dcbf
 * pushes the lines to memory, and the lines being pushed have to reach memory anyway, so it brings a
 * writeback forward rather than adding one.
 *
 * WHAT IT COSTS, measured: 482 us a frame of a 34,232 us frame, in place of the 10,884 us copy it
 * replaced. If somebody needs that 482 us back, this is the first thing to try removing - and the
 * honest way to test it is a long run watching for one bad frame, not a clean minute.
 */
static void flush_for_rsx(int h)
{
#if defined(__powerpc__) || defined(__PPC__) || defined(__powerpc64__)
    const char *p = (const char *)s_bitmap;
    size_t bytes = (size_t)s_w * (size_t)h * 4u;
    size_t i;

    for (i = 0; i < bytes; i += 128u)
        __asm__ __volatile__("dcbf 0,%0" : : "r"(p + i) : "memory");
    __asm__ __volatile__("sync" : : : "memory");
#else
    (void)h;
#endif
}

int rc_overlay_reads_main(void)
{
    return s_from_main;
}

void rc_overlay_end_now(int x, int y, int w, int h)
{
    if (!s_ready)
        return;
    if (w <= 0 || w > s_w)
        w = s_w;
    if (h <= 0 || h > s_h)
        h = s_h;
    /*
     * Copied only when something was actually drawn. A caller that redraws because a frame went past
     * rather than because anything moved - a menu nobody is touching - reaches this with the bitmap
     * unchanged, and the surface is 2.3 MB. rc_overlay_end has always been gated this way; this one
     * was not, and the shell is the first caller that runs at the flip rate.
     */
    if (s_rebuilt) {
        if (!s_from_main)
            memcpy(s_vram, s_bitmap, (size_t)s_w * (size_t)s_h * 4u);
        s_rebuilt = 0;
    }
    if (s_from_main)
        flush_for_rsx(h);
    /*
     * THE POSITION IS AN ARGUMENT, NOT STATE, and it is that way because making it state was a bug.
     * The status card set a shared origin to centre itself and never put it back, so the diagnostics
     * overlay - which wants the top left - moved to the middle for the rest of the session. Two callers
     * wanting different positions is not a reason for either to mutate the other's.
     */
    /* Only the region the caller drew. The surface is bigger than most of its users. */
    rc_video_overlay_blit(s_from_main ? s_main_offset : s_offset, s_w * 4, w, h, x, y,
                          s_from_main);
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
        if (!s_from_main)
            memcpy(s_vram, s_bitmap, (size_t)s_w * (size_t)s_h * 4u);
        s_rebuilt = 0;
    }
    rc_video_overlay_blit(s_from_main ? s_main_offset : s_offset, s_w * 4,
                          s_panel_w, s_panel_h, s_x, s_y, s_from_main);
}
