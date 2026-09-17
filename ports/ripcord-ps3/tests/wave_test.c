/*
 * ripcord-ps3 - the background's twelve colours, checked without waiting a year.
 *
 * THE SEASONAL HUE IS THE ONE PIECE OF THIS SHELL NOBODY CAN REVIEW BY LOOKING AT IT. It takes its
 * colour from the month, so eleven twelfths of the design is invisible on any given day, and
 * SHELL-DESIGN.md's own plan for checking it was to move the console's clock twelve times. That is a
 * dozen reboots to answer a question that is arithmetic, and it is the sort of check nobody repeats
 * after the first time - which is exactly when a table of twelve hand-picked colours gets edited.
 *
 * So the properties the design actually depends on are asserted here instead:
 *
 *   IT IS A BACKGROUND. Every month's wave has to stay dark. What gets measured is the NINETY-NINTH
 *   PERCENTILE rather than the single brightest pixel, and that is not a softening of the test - the
 *   motes are points of light by design and are meant to be the brightest thing in the picture. A check
 *   on the maximum would be a check on the motes, which is a different question and is answered by
 *   looking at one. The percentile is the ribbons where all three cross, which is the ambient level the
 *   cards have to sit on top of.
 *
 *   THE ACCENT HAS TO CARRY SMALL TEXT. It sets the family tag on a card, the rule under the wordmark
 *   and the value in a settings row, all at the body size on the card's near-black fill. Contrast is
 *   measured the way WCAG defines it, against that fill rather than against the wave, because that is
 *   what the text actually sits on.
 *
 *   AND THE YEAR HAS TO GO SOMEWHERE. The conceit is that somebody launching in October and again in
 *   July sees something they cannot name - a claim about the year having variety, not about any
 *   particular pair. Two earlier versions of this check were wrong in the same way, by asserting
 *   something the table deliberately does not do: consecutive months differing (December and January are
 *   both blue, because winter is), and opposite halves differing (January and July are eighteen degrees
 *   apart because the table calls July "high summer, and the COLDEST blue", which is the joke).
 *
 *   What is actually claimed is that the twelve cover the wheel. So: no two months are the same colour,
 *   and the largest gap left in the ring of hues is not so wide that the year lives in one corner of it.
 *
 * WHAT THIS CANNOT DO is tell you the colours are NICE. Taste is not checkable and the table's own
 * comment says so. It can tell you they are dark, legible and distinct, which is every property the
 * design states in prose.
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <stdint.h>

#include "rc_wave.h"

/* The card's fill, from draw_card in rc_shell.c - what the accent's text is actually read against. */
#define CARD_FILL_R 0x0E
#define CARD_FILL_G 0x12
#define CARD_FILL_B 0x18

/*
 * rc_wave logs the month it chose, so a photograph of a colour can be matched to a decision. Nothing
 * here wants that on stdout among the table, so it is swallowed - which also keeps this test from
 * dragging in the real logger and the file handling under it.
 */
void rc_log(const char *fmt, ...);
void rc_log(const char *fmt, ...)
{
    (void)fmt;
}

static int g_failures;

static int by_luminance(const void *a, const void *b)
{
    double x = *(const double *)a, y = *(const double *)b;

    return (x < y) ? -1 : ((x > y) ? 1 : 0);
}

static void check(int ok, const char *what)
{
    if (!ok) {
        printf("FAIL  %s\n", what);
        g_failures++;
    }
}

/* WCAG relative luminance, 0..1. */
static double luminance(unsigned rgb)
{
    double c[3];
    int i;

    c[0] = (double)((rgb >> 16) & 0xffu) / 255.0;
    c[1] = (double)((rgb >> 8) & 0xffu) / 255.0;
    c[2] = (double)(rgb & 0xffu) / 255.0;
    for (i = 0; i < 3; i++)
        c[i] = (c[i] <= 0.03928) ? (c[i] / 12.92) : pow((c[i] + 0.055) / 1.055, 2.4);
    return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2];
}

static double contrast(unsigned a, unsigned b)
{
    double la = luminance(a), lb = luminance(b);
    double hi = (la > lb) ? la : lb, lo = (la > lb) ? lb : la;

    return (hi + 0.05) / (lo + 0.05);
}

/* The hue of a colour, in degrees - so two months can be compared as the table describes them. */
static double hue_of(unsigned rgb)
{
    double r = (double)((rgb >> 16) & 0xffu) / 255.0;
    double g = (double)((rgb >> 8) & 0xffu) / 255.0;
    double b = (double)(rgb & 0xffu) / 255.0;
    double mx = r > g ? (r > b ? r : b) : (g > b ? g : b);
    double mn = r < g ? (r < b ? r : b) : (g < b ? g : b);
    double d = mx - mn, h;

    if (d < 1e-9)
        return 0.0;
    if (mx == r)      h = 60.0 * fmod((g - b) / d, 6.0);
    else if (mx == g) h = 60.0 * (((b - r) / d) + 2.0);
    else              h = 60.0 * (((r - g) / d) + 4.0);
    return (h < 0.0) ? h + 360.0 : h;
}

static double hue_gap(double a, double b)
{
    double d = fabs(a - b);

    return (d > 180.0) ? 360.0 - d : d;
}

#define W 480
#define H 270

static uint32_t g_frame[W * H];

int main(void)
{
    double accent_hue[12];
    int month;

    printf("the background's twelve colours\n\n");
    printf("  month      ambient  brightest  accent     contrast on a card   hue\n");

    for (month = 0; month < 12; month++) {
        unsigned peak = 0u, accent;
        unsigned card = (CARD_FILL_R << 16) | (CARD_FILL_G << 8) | CARD_FILL_B;
        double ambient, ratio;
        static double lum[W * H];
        int i;

        /*
         * The month is chosen from the clock, so the test sets the clock - TZ-independent, because
         * rc_wave_open reads localtime and a machine running this in another zone must get the same
         * answer. Noon on the 15th is nowhere near a boundary in any zone on earth.
         */
        rc_wave_test_set_month(month);
        rc_wave_open();

        memset(g_frame, 0, sizeof(g_frame));
        rc_wave_draw(g_frame, W, H, W, 0u);

        for (i = 0; i < W * H; i++) {
            unsigned rgb = g_frame[i] & 0x00FFFFFFu;

            lum[i] = luminance(rgb);
            if (lum[i] > luminance(peak))
                peak = rgb;
        }
        /* Sorted so the percentile is exact; a frame is small and this runs twelve times. */
        qsort(lum, (size_t)(W * H), sizeof(lum[0]), by_luminance);
        ambient = lum[(W * H) * 99 / 100];
        accent = rc_wave_accent() & 0x00FFFFFFu;
        ratio = contrast(accent, card);
        accent_hue[month] = hue_of(accent);

        printf("  %-10s  %.3f    #%06X   #%06X   %5.2f:1              %3.0f\n",
               rc_wave_month(), ambient, peak, accent, ratio, accent_hue[month]);

        /*
         * 0.16 is generous for an ambient level: past it the background starts competing with the cards
         * for being the brightest thing on the screen, which is the one thing a background must not do.
         */
        check(ambient < 0.16, "the wave's ambient level stays a background");
        /*
         * 4.5:1 is WCAG AA for body text, which is exactly what the family tag on a card and the values
         * in a settings row are.
         */
        check(ratio >= 4.5, "the accent carries small text on a card");
    }

    {
        int a, b;
        double sorted[12], closest = 360.0, widest = 0.0;

        for (a = 0; a < 12; a++) {
            for (b = a + 1; b < 12; b++) {
                double gap = hue_gap(accent_hue[a], accent_hue[b]);

                if (gap < closest)
                    closest = gap;
            }
        }

        /* The widest stretch of the wheel with no month in it - the ring's largest gap. */
        memcpy(sorted, accent_hue, sizeof(sorted));
        qsort(sorted, 12, sizeof(sorted[0]), by_luminance);
        for (a = 0; a < 12; a++) {
            double gap = (a == 11) ? (360.0 - sorted[11] + sorted[0]) : (sorted[a + 1] - sorted[a]);

            if (gap > widest)
                widest = gap;
        }
        printf("\n  closest any two months come: %.0f degrees\n", closest);
        printf("  widest gap left in the ring:  %.0f degrees\n\n", widest);

        /* No two months may be the same colour - that would be a table entry buying nothing. */
        check(closest >= 8.0, "no two months are the same colour");
        /* And the twelve must not all live in one corner of the wheel. */
        check(widest <= 120.0, "the year covers the wheel rather than one corner of it");
    }

    if (g_failures == 0)
        printf("wave: twelve months, all dark, all legible, all distinct\n");
    else
        printf("wave: %d check(s) failed\n", g_failures);
    return g_failures == 0 ? 0 : 1;
}
