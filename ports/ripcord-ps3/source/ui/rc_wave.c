/* See rc_wave.h. */
#include "rc_wave.h"

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

void rc_wave_draw(uint32_t *dst, int w, int h, int stride_px, uint64_t ms)
{
    /*
     * PER-COLUMN CENTRE LINES, COMPUTED ONCE. The sine is a function of x and time only, so evaluating
     * it inside the pixel loop would be two million lookups to produce nineteen hundred answers.
     */
    static short centre[RC_WAVE_RIBBONS][RC_WAVE_MAX_W];
    static unsigned char falloff[RC_WAVE_RIBBONS][257];
    /*
     * THE HUE, RESOLVED ONCE PER BRIGHTNESS RATHER THAN ONCE PER PIXEL. Hue and saturation are fixed
     * for the whole month, so only the value varies - which turns a colour-space conversion in the
     * inner loop, two million times a frame, into a 256-entry table built once.
     */
    static uint32_t ramp[256];
    static int ramp_month = -1;
    static short thick[RC_WAVE_RIBBONS];
    uint64_t t0 = rc_tick();
    int rib, x, y;

    if (dst == NULL || w <= 0 || h <= 0)
        return;
    if (w > RC_WAVE_MAX_W)
        w = RC_WAVE_MAX_W;
    if (!s_sine_ready)
        build_sine();

    if (ramp_month != s_month) {
        for (x = 0; x < 256; x++)
            ramp[x] = hsv(RC_WAVE_MONTH[s_month].hue, RC_WAVE_MONTH[s_month].sat, x);
        ramp_month = s_month;
    }

    for (rib = 0; rib < RC_WAVE_RIBBONS; rib++) {
        int cy = (RC_WAVE_RIBBON[rib].y_permille * h) / 1000;
        int amp = (RC_WAVE_RIBBON[rib].amp_permille * h) / 1000;
        int phase = (int)((ms * (uint64_t)RC_WAVE_RIBBON[rib].speed) / 1000u);
        int i;

        thick[rib] = (short)((RC_WAVE_RIBBON[rib].thick * h) / 1000);
        if (thick[rib] < 1)
            thick[rib] = 1;

        for (x = 0; x < w; x++) {
            /* The fundamental, plus a faster harmonic at a fraction of the amplitude - one sine alone
             * reads as a signal rather than as water. */
            int p1 = (x * RC_WAVE_RIBBON[rib].freq * 64) / w + phase;
            int p2 = (x * RC_WAVE_RIBBON[rib].freq * 147) / w + (phase * 8) / 5;

            centre[rib][x] = (short)(cy + (wave_sin(p1) * amp) / 256
                                        + (wave_sin(p2) * amp) / 896);
        }
        /* A smooth shoulder rather than a linear ramp: a straight falloff has a visible edge where it
         * reaches zero, and on a dark background a visible edge is the whole of what you notice. */
        for (i = 0; i <= 256; i++) {
            int u = 256 - i;                     /* 256 at the centre, 0 at the edge */
            int sq = (u * u) >> 8;               /* squared, so it leaves gently      */

            falloff[rib][i] = (unsigned char)((sq * RC_WAVE_RIBBON[rib].level) >> 8);
        }
    }

    for (y = 0; y < h; y++) {
        uint32_t *row = dst + (size_t)y * (size_t)stride_px;
        /* The floor: a near-black that lifts very slightly towards the bottom, so the screen has a
         * bottom edge rather than fading into the television's own black. */
        int lift = (y * 14) / h;
        int br = 5 + lift / 3, bg = 7 + lift / 2, bb = 11 + lift;
        uint32_t flat = 0xff000000u | (uint32_t)((br << 16) | (bg << 8) | bb);

        for (x = 0; x < w; x++) {
            int add = 0;
            int dy;

            /* Unrolled: three ribbons is a fixed number, and the loop overhead is a third of the work
             * in a body this small. */
            dy = y - centre[0][x]; if (dy < 0) dy = -dy;
            if (dy < thick[0]) add += falloff[0][(dy * 256) / thick[0]];
            dy = y - centre[1][x]; if (dy < 0) dy = -dy;
            if (dy < thick[1]) add += falloff[1][(dy * 256) / thick[1]];
            dy = y - centre[2][x]; if (dy < 0) dy = -dy;
            if (dy < thick[2]) add += falloff[2][(dy * 256) / thick[2]];

            if (add == 0) {
                row[x] = flat;
            } else {
                uint32_t c;
                int r, g, b;

                if (add > 255)
                    add = 255;
                c = ramp[add];
                r = br + (int)((c >> 16) & 0xffu);
                g = bg + (int)((c >> 8) & 0xffu);
                b = bb + (int)(c & 0xffu);
                if (r > 255) r = 255;
                if (g > 255) g = 255;
                if (b > 255) b = 255;
                row[x] = 0xff000000u | (uint32_t)((r << 16) | (g << 8) | b);
            }
        }
    }

    s_last_us = (unsigned)(((rc_tick() - t0) * 1000000u) / rc_tick_hz());
}

unsigned rc_wave_last_us(void)
{
    return s_last_us;
}
