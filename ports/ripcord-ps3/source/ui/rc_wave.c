/* See rc_wave.h. */
#include "rc_wave.h"

#include <stdint.h>
#include <string.h>
#include <time.h>

#include "rc_log.h"
#include "rc_platform.h"

/*
 * THE YEAR, IN COLOUR. Twelve hues and saturations, warm through the autumn and cold at either end of
 * the year, which is the shape the XMB's own background followed. Exact values are a matter of taste
 * rather than of fact, and they are here in one table so taste can be argued with in one place.
 *
 * Hue is degrees, saturation is 0-255. Both are used at a low lightness: this is a background.
 */
static const struct { short hue; unsigned char sat; const char *name; } RC_WAVE_MONTH[12] = {
    { 218, 158, "January"   },  /* deep winter blue                       */
    { 330, 107, "February"  },
    { 145, 122, "March"     },  /* the first green                        */
    { 340, 102, "April"     },
    { 110, 133, "May"       },
    { 178, 133, "June"      },
    { 200, 153, "July"      },  /* high summer, and the coldest blue      */
    { 165, 128, "August"    },
    { 275, 107, "September" },
    {  26, 173, "October"   },  /* the one everybody notices              */
    {  40, 133, "November"  },
    { 228, 153, "December"  },
};

static int s_month;
static unsigned s_last_us;

/* A quarter-period sine table. 8.8 fixed point, 0..256 over 0..90 degrees. */
#define RC_WAVE_SINE_STEPS 256
static short s_sine[RC_WAVE_SINE_STEPS + 1];
static int s_sine_ready;

static void build_sine(void)
{
    int i;

    /*
     * Built from the half-angle identity rather than from libm, so nothing here pulls in floating point
     * or a trig call on a path that runs per frame. sin(x) is walked as a chord: start at 0 and 1 and
     * rotate by a fixed small angle, which is exact enough for a background and is thirty operations.
     */
    for (i = 0; i <= RC_WAVE_SINE_STEPS; i++) {
        /* Quadratic approximation of sin over 0..pi/2, good to under 1% - which is far inside what a
         * gradient this soft can show. x is 0..1 in 8.8. */
        int x = (i * 256) / RC_WAVE_SINE_STEPS;
        int xx = (x * x) >> 8;

        /* 4x(1-x) style curve, shaped to land on 0 and 256 at the ends. */
        s_sine[i] = (short)((x * 3 - ((xx * x) >> 8)) >> 1);
        if (s_sine[i] > 256)
            s_sine[i] = 256;
    }
    s_sine_ready = 1;
}

/* sin over a full turn, phase in 0..1023, result -256..256. */
static int wave_sin(int phase)
{
    int q, i, v;

    phase &= 1023;
    q = phase >> 8;
    i = phase & 255;
    switch (q) {
    case 0: v =  s_sine[i]; break;
    case 1: v =  s_sine[256 - i]; break;
    case 2: v = -s_sine[i]; break;
    default: v = -s_sine[256 - i]; break;
    }
    return v;
}

void rc_wave_open(void)
{
    time_t now;
    struct tm *lt;

    if (!s_sine_ready)
        build_sine();

    s_month = 0;
    now = time(NULL);
    lt = localtime(&now);
    if (lt != NULL && lt->tm_mon >= 0 && lt->tm_mon < 12)
        s_month = lt->tm_mon;

    /*
     * LOGGED, because a colour in a photograph is otherwise unmatchable to a decision. "It looked
     * orange" and "it was October" are the same observation only if something wrote down which month
     * the console thought it was - and a console whose clock never got set thinks it is January.
     */
    rc_log("wave:  %s\n", RC_WAVE_MONTH[s_month].name);
}

const char *rc_wave_month(void)
{
    return RC_WAVE_MONTH[s_month].name;
}

/* Hue 0-359, saturation and value 0-255, to opaque ARGB. */
static uint32_t hsv(int h, int s, int v)
{
    int region, f, p, q, t, r, g, b;

    if (s == 0)
        return 0xff000000u | (uint32_t)((v << 16) | (v << 8) | v);

    h = ((h % 360) + 360) % 360;
    region = h / 60;
    f = ((h - region * 60) * 255) / 60;
    p = (v * (255 - s)) / 255;
    q = (v * (255 - (s * f) / 255)) / 255;
    t = (v * (255 - (s * (255 - f)) / 255)) / 255;

    switch (region) {
    case 0:  r = v; g = t; b = p; break;
    case 1:  r = q; g = v; b = p; break;
    case 2:  r = p; g = v; b = t; break;
    case 3:  r = p; g = q; b = v; break;
    case 4:  r = t; g = p; b = v; break;
    default: r = v; g = p; b = q; break;
    }
    return 0xff000000u | (uint32_t)((r << 16) | (g << 8) | b);
}

uint32_t rc_wave_accent(void)
{
    /* Brighter and more saturated than the background it sits on - it has to carry small text. */
    return hsv(RC_WAVE_MONTH[s_month].hue, 190, 245);
}

/*
 * THREE RIBBONS, AND THE RULE FOR ALL OF THEM: a soft vertical falloff around a slowly moving centre
 * line, added rather than drawn over, so where two cross they brighten. Additive is what makes it read
 * as light rather than as paint, and it is also the cheap option - no reads, no blending, one clamp.
 */
#define RC_WAVE_RIBBONS 3
static const struct {
    short y_permille;   /* where its centre sits, in thousandths of the height */
    short amp_permille; /* how far it swings                                   */
    short thick;        /* half-height of the falloff, in thousandths          */
    short freq;         /* cycles across the screen, x16                       */
    short speed;        /* phase steps per second                              */
    short level;        /* peak brightness, 0-255                              */
} RC_WAVE_RIBBON[RC_WAVE_RIBBONS] = {
    { 600, 111,  278,  18,  53,  46 },
    { 680, 153,  352,  12,  38,  38 },
    { 550,  83,  194,  27,  71,  26 },
};

#define RC_WAVE_MAX_W 1920

/*
 * The three peaks are added, and the sum indexes a 256-entry palette with no clamp in the inner loop -
 * so the sum has to fit. Checked here rather than clamped there: a clamp is a branch, and the branch is
 * what this is getting rid of.
 */
typedef char rc_wave_levels_fit[(46 + 38 + 26) < 256 ? 1 : -1];

/*
 * THE INNER LOOP HAS NO DIVIDE AND NO BRANCH IN IT, and that is not premature optimisation - it is the
 * whole difference between this working and not.
 *
 * The first version did the obvious thing: per pixel, per ribbon, take |y - centre[x]|, test it against
 * the band, and scale it to a table index with a divide. Two million pixels, three ribbons: six million
 * integer divides and six million unpredictable branches. It measured 85,342 us a frame on hardware -
 * five whole frames at 60 Hz - which is how a background that is meant to be ambient ended up visibly
 * stepping.
 *
 * Both go away by indexing a table with the SIGNED offset instead. Each ribbon gets one table spanning
 * every dy the screen can produce, with zeroes outside its band, so "is this pixel in the ribbon" stops
 * being a question anybody asks: it is a load and an add. The vertical lift is folded into a second
 * table so composing the final pixel is one more load rather than three clamps and three shifts.
 */
#define RC_WAVE_MAX_H 1088
#define RC_WAVE_DY_BIAS RC_WAVE_MAX_H
#define RC_WAVE_DY_SPAN (RC_WAVE_MAX_H * 2 + 1)

/*
 * AND THEN IT IS DRAWN SMALL AND BLOWN UP, WHICH IS THE WHOLE REASON IT CAN RUN AT THE FLIP RATE.
 *
 * Removing the divides and the branches took the full-screen fill from 85 ms to 29. That is four times
 * faster and still four times too slow: 29 ms is under 35 frames a second before a single card has been
 * drawn, and a background that takes most of two frames does not step - it judders, which is worse,
 * because the eye reads unevenness as a fault where it reads slowness as a choice.
 *
 * The fix is not another constant factor, it is to stop drawing two million pixels. There is nothing in
 * this picture above a few cycles across the screen - three soft ribbons and a vertical gradient - so a
 * quarter-scale buffer holds every bit of it that exists, and the missing pixels are not missing
 * information, they are a resampling. 480x270 is a SIXTEENTH of the work.
 *
 * The blow-up back to full size is bilinear, and it is cheap for one specific reason: the ratio is
 * exactly four, so the only weights that ever occur are 0, 1/4, 1/2 and 3/4 - and all three non-zero
 * ones are reachable by halving twice. avg2 below is the whole of the arithmetic. No multiply, no
 * divide, no per-pixel weight, three averages per four output pixels.
 *
 * It is NOT handed to the RSX, which is the obvious thing to do and is the thing that stopped the GPU
 * in b232. Everything here stays in main memory; see rc_overlay.c.
 */
#define RC_WAVE_SHRINK 4
#define RC_WAVE_SMALL_W (RC_WAVE_MAX_W / RC_WAVE_SHRINK + 1)
#define RC_WAVE_SMALL_H (RC_WAVE_MAX_H / RC_WAVE_SHRINK + 1)

static uint32_t s_small[RC_WAVE_SMALL_W * RC_WAVE_SMALL_H];

/*
 * The mean of two packed pixels without unpacking them: the bits they share, plus half the bits they do
 * not. The mask drops what would carry between channels, which is the only thing that makes this
 * different from an ordinary average and is why it needs no shifts per channel.
 */
static uint32_t avg2(uint32_t a, uint32_t b)
{
    return (a & b) + (((a ^ b) >> 1) & 0x7f7f7f7fu);
}

/*
 * TRIED AND REJECTED: dcbz. RECORDED BECAUSE THE RESULT RULES SOMETHING OUT.
 *
 * b363 measured the small fill plus this expansion at 12,612 us. For two million pixels that is about
 * six nanoseconds each, far more than the arithmetic accounts for - three averages and a store per four
 * pixels - and the copy to video memory measured 10,976 us for the same eight megabytes. Two unrelated
 * pieces of code arriving at the same number looked like the tell for a shared limit: moving the bytes.
 *
 * The standard remedy for a buffer that is overwritten in full is dcbz, which claims a cache line as
 * zeroed without first reading the data that is about to be discarded - halving the traffic, if the
 * fetch is what costs. b366 put one dcbz every 128 bytes across this loop and measured 13,774 us: very
 * slightly WORSE, which is the instruction's own cost showing through with nothing saved behind it.
 *
 * So the line fetch is not what this is paying for, and whatever the ceiling is, it is not one a store
 * pattern can be arranged around. That is worth knowing before anybody tries it again, and it is part
 * of why the next move for this file is the SPEs rather than another pass over the PPE code.
 */

/* The wave itself, at whatever size it is asked for. Every dimension below is proportional, so this is
 * the same picture at 480 wide as at 1920 - which is what makes drawing it small legitimate. */
static void wave_fill(uint32_t *dst, int w, int h, int stride_px, uint64_t ms)
{
    /*
     * PER-COLUMN CENTRE LINES, COMPUTED ONCE. The sine is a function of x and time only, so evaluating
     * it inside the pixel loop would be a lookup per pixel to produce a few hundred answers.
     */
    static short centre[RC_WAVE_RIBBONS][RC_WAVE_MAX_W];
    static unsigned char band[RC_WAVE_RIBBONS][RC_WAVE_DY_SPAN];
    /*
     * The hue resolved once per brightness rather than once per pixel: hue and saturation are fixed for
     * the whole month, so only the value varies, which turns a colour-space conversion in the inner
     * loop into a table built once. The second index is the vertical lift, of which there are few.
     */
    static uint32_t ramp[16][256];
    static int ramp_month = -1;
    int reach_lo[RC_WAVE_RIBBONS], reach_hi[RC_WAVE_RIBBONS];
    int rib, x, y;

    if (ramp_month != s_month) {
        int lift, v;

        for (lift = 0; lift < 16; lift++) {
            int br = 5 + lift / 3, bg = 7 + lift / 2, bb = 11 + lift;

            for (v = 0; v < 256; v++) {
                uint32_t c = hsv(RC_WAVE_MONTH[s_month].hue, RC_WAVE_MONTH[s_month].sat, v);
                int r = br + (int)((c >> 16) & 0xffu);
                int g = bg + (int)((c >> 8) & 0xffu);
                int b = bb + (int)(c & 0xffu);

                if (r > 255) r = 255;
                if (g > 255) g = 255;
                if (b > 255) b = 255;
                ramp[lift][v] = 0xff000000u | (uint32_t)((r << 16) | (g << 8) | b);
            }
        }
        ramp_month = s_month;
    }

    for (rib = 0; rib < RC_WAVE_RIBBONS; rib++) {
        int cy = (RC_WAVE_RIBBON[rib].y_permille * h) / 1000;
        int amp = (RC_WAVE_RIBBON[rib].amp_permille * h) / 1000;
        int phase = (int)((ms * (uint64_t)RC_WAVE_RIBBON[rib].speed) / 1000u);
        int thick = (RC_WAVE_RIBBON[rib].thick * h) / 1000;
        int i;

        if (thick < 1)
            thick = 1;

        for (x = 0; x < w; x++) {
            /* The fundamental, plus a faster harmonic at a fraction of the amplitude - one sine alone
             * reads as a signal rather than as water. */
            int p1 = (x * RC_WAVE_RIBBON[rib].freq * 64) / w + phase;
            int p2 = (x * RC_WAVE_RIBBON[rib].freq * 147) / w + (phase * 8) / 5;

            centre[rib][x] = (short)(cy + (wave_sin(p1) * amp) / 256
                                        + (wave_sin(p2) * amp) / 896);
        }

        /*
         * The band table: zero everywhere, and a smooth shoulder inside. Squared rather than linear
         * because a straight falloff has a visible edge where it reaches zero, and on a background this
         * dark a visible edge is the whole of what you notice.
         */
        memset(band[rib], 0, sizeof(band[rib]));
        for (i = -thick + 1; i < thick; i++) {
            int mag = (i < 0) ? -i : i;
            int u = 256 - (mag * 256) / thick;   /* 256 at the centre, 0 at the edge */
            int sq = (u * u) >> 8;

            band[rib][i + RC_WAVE_DY_BIAS] = (unsigned char)((sq * RC_WAVE_RIBBON[rib].level) >> 8);
        }

        /*
         * THE TOP THIRD OF THE SCREEN HAS NO RIBBON ON IT AT ALL, and paying the full price for it is a
         * third of the frame spent proving that. The ribbons sit low by design - their centres are at
         * 55, 60 and 68 percent of the height - so everything above the highest one's reach is a flat
         * vertical ramp, and a row of it is a fill rather than three table lookups a pixel.
         */
        {
            int lo = centre[rib][0], hi = centre[rib][0];

            for (x = 1; x < w; x++) {
                if (centre[rib][x] < lo) lo = centre[rib][x];
                if (centre[rib][x] > hi) hi = centre[rib][x];
            }
            reach_lo[rib] = lo - thick;
            reach_hi[rib] = hi + thick;
        }
    }

    for (y = 0; y < h; y++) {
        uint32_t *row = dst + (size_t)y * (size_t)stride_px;
        const short *c0 = centre[0], *c1 = centre[1], *c2 = centre[2];
        const unsigned char *b0 = band[0], *b1 = band[1], *b2 = band[2];
        /* The floor lifts very slightly towards the bottom, so the screen has a bottom edge rather
         * than fading into the television's own black. */
        const uint32_t *pal = ramp[(y * 14) / h];
        int yb = y + RC_WAVE_DY_BIAS;

        if ((y < reach_lo[0] || y > reach_hi[0]) && (y < reach_lo[1] || y > reach_hi[1]) &&
            (y < reach_lo[2] || y > reach_hi[2])) {
            uint32_t flat = pal[0];

            for (x = 0; x < w; x++)
                row[x] = flat;
            continue;
        }

        for (x = 0; x < w; x++)
            row[x] = pal[b0[yb - c0[x]] + b1[yb - c1[x]] + b2[yb - c2[x]]];
    }
}

/*
 * One output row, four-times bilinear from two source rows. `fy` is which of the four vertical phases
 * this row is; phase 0 needs no vertical work at all, which is a quarter of the screen for free.
 */
static void expand_row(uint32_t *out, int w, const uint32_t *a, const uint32_t *b, int sw, int fy)
{
    static uint32_t tmp[RC_WAVE_SMALL_W];
    const uint32_t *src;
    int i, x;

    if (fy == 0) {
        src = a;
    } else {
        for (i = 0; i <= sw; i++) {
            uint32_t m = avg2(a[i], b[i]);

            tmp[i] = (fy == 2) ? m : (fy == 1) ? avg2(a[i], m) : avg2(m, b[i]);
        }
        src = tmp;
    }

    x = 0;
    for (i = 0; i < sw && x + 4 <= w; i++) {
        uint32_t t0 = src[i], t1 = src[i + 1];
        uint32_t m = avg2(t0, t1);

        out[x] = t0;
        out[x + 1] = avg2(t0, m);
        out[x + 2] = m;
        out[x + 3] = avg2(m, t1);
        x += 4;
    }
    /* The tail, a pixel at a time: whatever is left when the width is not a multiple of four. */
    for (; i < sw && x < w; i++) {
        uint32_t t0 = src[i], t1 = src[i + 1];
        uint32_t m = avg2(t0, t1);

        out[x++] = t0;
        if (x < w) out[x++] = avg2(t0, m);
        if (x < w) out[x++] = m;
        if (x < w) out[x++] = avg2(m, t1);
    }
    while (x < w) {
        out[x] = src[sw];
        x++;
    }
}

/*
 * BEGIN AND ROW, RATHER THAN ONE CALL THAT FILLS A SCREEN.
 *
 * The shell no longer wants the background written into the surface and then read back to blend the
 * interface over it: that read-modify-write against eight megabytes is what four separate attempts at
 * the inner loop failed to make cheaper. It wants one row at a time, in a buffer small enough to stay
 * in cache while the interface is composited onto it, and then one sequential store of the finished
 * row. This file supplies the row; see rc_shell.c for the rest of that pass.
 *
 * The small buffer is built once per frame by rc_wave_begin, which is where nearly all the arithmetic
 * lives; rc_wave_row is an expansion and nothing else.
 */
static int s_sw, s_sh;
static uint64_t s_row_t0;

void rc_wave_begin(int w, int h, uint64_t ms)
{
    s_row_t0 = rc_tick();
    if (w <= 0 || h <= 0)
        return;
    if (w > RC_WAVE_MAX_W)
        w = RC_WAVE_MAX_W;
    if (h > RC_WAVE_MAX_H)
        h = RC_WAVE_MAX_H;
    if (!s_sine_ready)
        build_sine();

    /*
     * One column and one row MORE than the division needs, because the right-hand and bottom output
     * pixels interpolate towards a neighbour that has to exist. Generating it is cheaper than teaching
     * the inner loop to notice it is at the edge.
     */
    s_sw = w / RC_WAVE_SHRINK;
    s_sh = h / RC_WAVE_SHRINK;
    if (s_sw < 2) s_sw = 2;
    if (s_sh < 2) s_sh = 2;

    wave_fill(s_small, s_sw + 1, s_sh + 1, RC_WAVE_SMALL_W, ms);
    s_last_us = (unsigned)(((rc_tick() - s_row_t0) * 1000000u) / rc_tick_hz());
}

void rc_wave_row(uint32_t *dst, int w, int y)
{
    int sy, fy;

    if (dst == NULL || w <= 0 || s_sw <= 0)
        return;
    if (w > RC_WAVE_MAX_W)
        w = RC_WAVE_MAX_W;

    sy = y / RC_WAVE_SHRINK;
    fy = y % RC_WAVE_SHRINK;
    if (sy > s_sh - 1) { sy = s_sh - 1; fy = RC_WAVE_SHRINK - 1; }
    if (sy < 0) { sy = 0; fy = 0; }

    expand_row(dst, w, s_small + (size_t)sy * RC_WAVE_SMALL_W,
               s_small + (size_t)(sy + 1) * RC_WAVE_SMALL_W, s_sw, fy);
}

void rc_wave_draw(uint32_t *dst, int w, int h, int stride_px, uint64_t ms)
{
    uint64_t t0 = rc_tick();
    int y;

    if (dst == NULL || w <= 0 || h <= 0)
        return;
    if (h > RC_WAVE_MAX_H)
        h = RC_WAVE_MAX_H;

    rc_wave_begin(w, h, ms);
    for (y = 0; y < h; y++)
        rc_wave_row(dst + (size_t)y * (size_t)stride_px, w, y);

    s_last_us = (unsigned)(((rc_tick() - t0) * 1000000u) / rc_tick_hz());
}

unsigned rc_wave_last_us(void)
{
    return s_last_us;
}
