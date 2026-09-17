/* See rc_shell.h. */
#include "rc_shell.h"

#include <malloc.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

#include <sysutil/sysutil.h>

#include "rc_wave.h"

#include "halyard_pairing_file.h"
#include "rc_build_id.h"
#include "rc_discover.h"
#include "rc_log.h"
#include "rc_menu.h"
#include "rc_overlay.h"
#include "rc_pad_ps3.h"
#include "rc_pair_ps3.h"
#include "rc_thermal.h"
#include "rc_platform.h"
#include "rc_platform_ps3.h"
#include "rc_video_ps3.h"

/* ------------------------------------------------------------------------------------------------
 * LAYOUT, in design pixels against a 1920x1080 screen, converted by sx()/sy() for whatever the
 * television actually is. Nothing here is a real pixel count: b265 shipped a panel measured in real
 * pixels and it was wider than a 480p screen.
 *
 * Both axes are scaled, which the diagnostics panel's rc_overlay_px does not do - it scales by height
 * alone, which is right for a panel that keeps its shape and wrong for a layout that fills the screen.
 * A 4:3 480p mode is 720x480, where scaling x by the height ratio would put the right-hand margin 133
 * pixels off the side of the picture.
 */
#define SH_MARGIN     120
#define SH_WORD_Y      74     /* the wordmark's top edge */
#define SH_RULE_Y     150
#define SH_CARD_W     360
#define SH_CARD_H     430
#define SH_CARD_GAP    54
#define SH_CARD_MID   560     /* the centre line the row sits on */
#define SH_DESC_Y     900
#define SH_HINT_Y     976

/* The card, lifted and brightened when it is the one you are on. */
#define SH_SELECT_LIFT 18


#define SH_BACKDROP   0xFF05070Bu

/*
 * HOW LONG A HELD DIRECTION WAITS, and then how fast it goes. 340 ms is long enough that a deliberate
 * single press never repeats and short enough that holding down feels like holding down.
 */
#define SH_REPEAT_DELAY_MS 340u
#define SH_REPEAT_RATE_MS   90u

/* How long a broadcast waits for consoles to answer. Short: this runs while somebody is looking at it. */
#define SH_DISCOVER_MS 1500u

/* ------------------------------------------------------------------------------------------------ */

/*
 * Row ids. Consoles get a block of their own, because how many there are is decided by the network
 * rather than by this file - see rc_menu.h on why rows are addressed by id and not by index.
 */
enum {
    SH_ID_PAIR_NEW = 1,
    SH_ID_SEARCH,
    SH_ID_SETTINGS,
    SH_ID_QUIT,
    SH_ID_BACK,
    SH_ID_FORGET_YES,
    SH_ID_FORGET_NO,

    SH_ID_RESOLUTION = 20,
    SH_ID_FPS,
    SH_ID_BITRATE,
    SH_ID_SCALER,
    SH_ID_SMOOTHING,
    SH_ID_DIAGNOSTICS,

    SH_ID_CONSOLE_BASE = 100,  /* + the index into s_found - a console that answered and is not paired */
    SH_ID_PAIRED_BASE  = 200   /* + the index into s_set    - one this PS3 already has keys for       */
};

static rc_menu s_menu;
static rc_discover_result s_found;
static int s_found_count;
static int s_searched;              /* a broadcast has been run at least once this session */

/*
 * THE WHOLE LIST, not one console. The file used to hold exactly one, so pairing a second destroyed the
 * first one's keys - see halyard_pairing_file.h. `s_settings` is a record of its own because a PS3 with
 * nothing paired still has settings to hold and no entry to hold them in.
 */
static halyard_pairing_set s_set;
static halyard_pairing_record s_settings;
static const char *s_record_dir;    /* where it was loaded from, so it is saved back to the same place */

/*
 * WHETHER ANYTHING ACTUALLY CHANGED, because the file being written holds the only copy of material
 * that cannot be regenerated without standing in front of a console reading a PIN off its screen. A
 * write truncates before it writes, so every unnecessary one is a window in which a power cut costs
 * somebody a trip to another room. Launching the shell and picking the console that was already
 * selected should not open that window at all.
 */
static int s_dirty;

static uint32_t s_prev_buttons;
static uint64_t s_repeat_at;

/* ------------------------------------------------------------------------------------------------
 * The record
 */

/*
 * LOADED THE WAY rc_connect LOADS IT, from the same ordered list, because a shell that edits one file
 * while the connect path reads another is worse than no shell: every setting would appear to be
 * ignored, and nothing on either side would look wrong.
 */
static void load_record(const char *const *dirs, int dir_count)
{
    int i;
    int loaded = 0;

    memset(&s_set, 0, sizeof(s_set));
    s_set.selected = -1;
    s_record_dir = NULL;

    for (i = 0; i < dir_count && !loaded; i++) {
        if (halyard_pairing_file_load_set(dirs[i], &s_set) > 0) {
            loaded = 1;
            s_record_dir = dirs[i];
        }
    }

    s_dirty = 0;
    if (loaded) {
        /* Identical in every entry by construction, so any of them carries the settings. */
        s_settings = s_set.console[0];
        rc_log("shell: %d console(s) paired\n", s_set.count);
    } else {
        /*
         * The loader defaults every optional field whether or not it found a file - but to ports/common's
         * defaults, which are a 3DS's. Overwritten here with this port's measured ones so the settings
         * screen opens showing what an unpaired PS3 would actually stream at, rather than 960x540 at 30.
         */
        memset(&s_set, 0, sizeof(s_set));
        s_set.selected = -1;
        memset(&s_settings, 0, sizeof(s_settings));
        rc_pair_apply_port_defaults(&s_settings);
        s_record_dir = rc_pair_record_dir();
        rc_log("shell: nothing paired yet\n");
    }
}

static void save_record(void)
{
    const char *dir = (s_record_dir != NULL) ? s_record_dir : rc_pair_record_dir();

    /*
     * SETTINGS LIVE WITH THE CONSOLES, so there is nowhere to put them until there is at least one. Said
     * rather than failing quietly: somebody who changed a setting before pairing anything would
     * otherwise watch it not take, with nothing anywhere explaining why. The port's own defaults are
     * applied at the first pairing regardless, and they are better than a half-explored settings screen.
     */
    if (s_set.count <= 0) {
        rc_log("shell: nothing is paired, so there is nowhere to save settings yet\n");
        return;
    }
    if (!s_dirty) {
        rc_log("shell: nothing changed - the pairing file is left alone\n");
        return;
    }
    halyard_pairing_set_apply_settings(&s_set, &s_settings);
    if (halyard_pairing_file_save_set(dir, &s_set)) {
        s_dirty = 0;
        rc_log("shell: saved (%d console(s))\n", s_set.count);
    }
    else
        rc_log("shell: settings could NOT be saved\n");
}

/* ------------------------------------------------------------------------------------------------
 * Drawing
 */

static int s_scr_w = 1920, s_scr_h = 1080;
static uint32_t s_accent = 0xFF4A9EFFu;

/* Design pixels to real ones, on each axis independently - see the note above SH_MARGIN. */
static int sx(int v) { return (v * s_scr_w) / 1920; }
static int sy(int v) { return (v * s_scr_h) / 1080; }

/*
 * WHICH BUTTON MEANS "ENTER" IS THE CONSOLE'S DECISION, NOT OURS.
 *
 * Japanese PlayStation hardware confirms with circle and cancels with cross; western hardware is the
 * other way round, and the PS3 says which one this machine uses in a documented system parameter. A
 * client that hardcodes the cross is choosing to be wrong for some of the people using it, and they
 * notice immediately and permanently. Read once, and every glyph, prompt and handler below follows it.
 */
static uint32_t s_enter = HALYARD_PAD_CROSS;
static uint32_t s_back  = HALYARD_PAD_CIRCLE;

static void read_enter_button(void)
{
    s32 value = 0;

    if (sysUtilGetSystemParamInt(SYSUTIL_SYSTEMPARAM_ID_ENTER_BUTTON_ASSIGN, &value) != 0) {
        rc_log("shell: the console did not say which button is enter - assuming cross\n");
        return;
    }
    /*
     * [X] 0 is circle-confirms and 1 is cross-confirms. The parameter is documented to exist and its
     * values are not, so this is an assumption - but it is a SAFE one to get wrong in one direction
     * only: the fallback is the cross, which is what this port did before it asked at all.
     */
    if (value == 0) {
        s_enter = HALYARD_PAD_CIRCLE;
        s_back = HALYARD_PAD_CROSS;
    }
    rc_log("shell: enter is %s on this console\n", (s_enter == HALYARD_PAD_CROSS) ? "cross" : "circle");
}

/* ------------------------------------------------------------------------------------------------ */

/*
 * SHAPES, BY DISTANCE RATHER THAN BY SAMPLING - and the difference is visible on a television.
 *
 * The previous version tested nine sub-positions per pixel against the shape and counted them. That is
 * the textbook answer and it was wrong here for three reasons. Nine samples give nine levels of
 * coverage, so a curve gets a stepped edge rather than a smooth one. The corner test was built from
 * distances to the four straight sides, which meets itself at forty-five degrees and leaves a wedge -
 * the "weird artifacting". And sampling is a THRESHOLD, so a corner pixel sitting near the boundary
 * flips between counts as the background moves under it, which is exactly the shimmer.
 *
 * A rounded rectangle has an exact distance function, so this asks for the distance instead:
 *
 *     dx = max(|px - cx| - (w/2 - r), 0)
 *     dy = max(|py - cy| - (h/2 - r), 0)
 *      d = sqrt(dx*dx + dy*dy) - r
 *
 * Negative inside, zero on the edge, and correct in the corners because the corner IS a circle by
 * construction. Coverage comes straight off it as a one-pixel ramp, which is smooth, continuous, and -
 * the part that kills the shimmer - depends only on the geometry. The same pixel gets the same coverage
 * on every frame no matter what is behind it.
 *
 * It is also far cheaper: one square root per corner pixel instead of nine inside-tests per pixel, and
 * the straight edges need no square root at all.
 *
 * AND THEN IT WAS STILL WRONG, because the square root underneath it was. b360 drew this and the
 * corners still read as "a line, then a corner, then another line" - which is the exact symptom of the
 * arc and the straight edges not meeting. They did not: isqrt_i seeded its search at 1<<15, which is
 * not a power of four, so the digit-by-digit algorithm underneath it was answering sqrt(2v) - and, for
 * anything above 65535, saturating at 510 regardless of the input. A corner 22 pixels across feeds it
 * values into six figures, so the arc it drew was a chamfer of the wrong radius joined to the straight
 * edges at a step.
 *
 * The seed is now 1<<30, which IS a power of four and is the largest that fits an int. The lesson is
 * the cheap one: the formula was right for two builds, and the four-line helper under it was never
 * checked against the thing it claims to compute.
 */

/*
 * SUB-PIXEL RESOLUTION, and it is the AA quality as well as the geometry's.
 *
 * Coverage is a one-pixel ramp read off the distance, so the number of distinct alphas an edge can take
 * is exactly the number of distance steps in a pixel. At sixteenths that is sixteen, which is visibly
 * stepped on a slow curve at television size. Sixty-fourths costs nothing - the distances are clamped
 * to the corner radius, so the squares stay small - and gives sixty-four.
 */
#define SH_SUB      64
#define SH_SUB_HALF (SH_SUB / 2)

/* Integer square root, exact. The seed must be a power of four; see above for what it costs when not. */
static int isqrt_i(int v)
{
    unsigned rem, root = 0u, b = 1u << 30;

    if (v <= 0)
        return 0;
    rem = (unsigned)v;
    while (b > rem)
        b >>= 2;
    while (b != 0u) {
        if (rem >= root + b) {
            rem -= root + b;
            root = (root >> 1) + b;
        } else {
            root >>= 1;
        }
        b >>= 2;
    }
    return (int)root;
}

/*
 * The surface, held while drawing so a pixel costs a store rather than a call.
 *
 * rc_overlay_blend_rect is the right shape for a panel and the wrong one for a shape being antialiased:
 * called once per pixel it re-does the clip, the bounds and the loop setup two hundred thousand times a
 * frame. Everything below writes through this instead, and the clip happens once per row.
 */
static uint32_t *s_px;
static int s_pitch;

/* Defined with the layer, below. Every shape reports the rows it may touch, so nothing has to read the
 * finished layer back to find out where the interface is. */
static void layer_note_ink(int y, int x0, int x1);

/* Defined with the other blends, below. */
static void blend_one(uint32_t *p, uint32_t rgb, unsigned a);

/*
 * INSIDE THE DRAWING, because "drawing is 67 ms" names a half and not a cause. The estimate for these
 * parts came to about ten milliseconds all told; being wrong by six times means something here is doing
 * work nobody has accounted for, and the only way to find out which is to time the pieces separately.
 */
static uint64_t s_sum_glow, s_sum_shape, s_sum_text, s_sum_cards, s_sum_hints, s_sum_header;
static uint64_t s_shape_at;

static unsigned us_since(uint64_t t)
{
    return (unsigned)(((rc_tick() - t) * 1000000u) / rc_tick_hz());
}

/*
 * A bounds-checked single pixel, for the glyphs - which are drawn by testing every pixel of a small box
 * rather than by walking spans, so there is nothing to hoist.
 *
 * IT FORCED THE ALPHA TO 255 UNTIL b387, and that was wrong the moment the shapes started drawing into
 * a transparent layer instead of onto an opaque screen: every partially-covered pixel of a cross, a
 * triangle or a pill's outline came out fully opaque, so each glyph carried a dark box the width of its
 * own antialiasing. Missed because the other two blends were converted together and this one is called
 * from one place.
 */
static void blend_at(int x, int y, uint32_t rgb, unsigned a)
{
    if (a == 0u || s_px == NULL || x < 0 || y < 0 || x >= s_scr_w || y >= s_scr_h)
        return;
    blend_one(&s_px[(size_t)y * (size_t)s_pitch + (size_t)x], rgb, a);
}

/* Coverage from a signed distance in SH_SUB-ths of a pixel: a one-pixel ramp centred on the edge. */
static unsigned cov_from(int d)
{
    if (d <= -SH_SUB_HALF)
        return 255u;
    if (d >= SH_SUB_HALF)
        return 0u;
    return (unsigned)((SH_SUB_HALF - d) * 255 / SH_SUB);
}

/*
 * The signed distance to a rounded rectangle, in SH_SUB-ths of a pixel, for the pixel centre at
 * (col,row). Split out because the fill, the outline and the glow are three readings of one shape and
 * having three copies of the formula is how the three stopped agreeing.
 */
typedef struct { int cx, cy, hw, hh, r; } sh_rrect;

static void rrect_set(sh_rrect *s, int x, int y, int w, int h, int r)
{
    if (r * 2 > w) r = w / 2;
    if (r * 2 > h) r = h / 2;
    if (r < 0) r = 0;
    s->cx = (x * 2 + w) * SH_SUB_HALF;
    s->cy = (y * 2 + h) * SH_SUB_HALF;
    s->hw = (w * SH_SUB) / 2 - r * SH_SUB;
    s->hh = (h * SH_SUB) / 2 - r * SH_SUB;
    s->r = r * SH_SUB;
}

/* The row's vertical term, computed once per row rather than once per pixel. */
static int rrect_dy(const sh_rrect *s, int row)
{
    int py = row * SH_SUB + SH_SUB_HALF;
    int dy = (py > s->cy ? py - s->cy : s->cy - py) - s->hh;

    return dy < 0 ? 0 : dy;
}

static int rrect_dist(const sh_rrect *s, int col, int dy)
{
    int px = col * SH_SUB + SH_SUB_HALF;
    int dx = (px > s->cx ? px - s->cx : s->cx - px) - s->hw;

    if (dx < 0)
        dx = 0;
    /* The square root is only needed in a corner, where both axes are outside the straight part. Along
     * an edge one of them is zero and the distance is the other one. */
    if (dx == 0)
        return dy - s->r;
    if (dy == 0)
        return dx - s->r;
    return isqrt_i(dx * dx + dy * dy) - s->r;
}

/*
 * One rounded rectangle, filled when `t` is 0 and outlined otherwise.
 */
/*
 * A SPAN AT A TIME, NOT A PIXEL AT A TIME - and the reason is a property of this shape rather than an
 * optimisation trick.
 *
 * b370 timed the parts of the drawing and settled where the frame goes: of 67,199 us, the text is 2,629
 * and the rounded rectangles are 63,695. Every guess before that had been aimed at the text, on the
 * strength of SHELL-DESIGN.md's warning about rasterising glyphs sixty times a second - which is sound
 * advice about a cost that turns out to be four percent of this screen.
 *
 * At 42,029 us for roughly 620,000 pixels the fill was taking about 217 cycles each, which is an order
 * of magnitude more than a blend costs. It was computing a distance function per pixel across the whole
 * card, and the distance function is the expensive part - but look at what it returns. Inside the
 * straight vertical band, which for a 360-wide card with a 22-pixel radius is 87 percent of every row,
 * the horizontal term is clamped to zero and the distance is dy - r: a function of the ROW alone. Every
 * pixel in that span gets the same coverage, and the whole per-pixel calculation was re-deriving one
 * answer three hundred times a row.
 *
 * So each row is now three pieces: a left edge, a flat middle, and a right edge. The middle is one
 * constant coverage and a tight blend with the multiply-by-alpha hoisted out; only the edges - about
 * twenty pixels a side, where the corner arc lives - pay for a distance and a square root.
 *
 * This holds for the outline as well as the fill: an outline's coverage in that band is min of two
 * functions of the same dy, which is still constant across the span.
 */

/* Exact division by 255 for a value already in 0..65535 - what a blend of two eight-bit terms produces.
 * The compiler turns / 255u into a multiply and a shift anyway; this says so once, where it is read. */
static unsigned div255(unsigned v)
{
    v += 128u;
    return (v + (v >> 8)) >> 8;
}

/*
 * TRIED AND REJECTED: dcbt. The second memory-level idea to change nothing, and together they point
 * somewhere else entirely.
 *
 * b371 took roughly ninety percent of the arithmetic out of this loop and the shapes fell by 2.5x, not
 * by ten. What was left came to about 170 cycles a blended pixel, which looked like a read-modify-write
 * to an eight-megabyte surface that is never in cache: an in-order core stopping dead on each dependent
 * load. So b373 prefetched four cache lines ahead. Glow 8,972 to 9,045 us, card shapes 16,582 to
 * 16,619 - nothing, within noise.
 *
 * With dcbz on the write path in rc_wave.c having done nothing either, that is two interventions aimed
 * at memory with no effect, and the conclusion is that memory is not what this is waiting for.
 *
 * WHICH LEAVES THE MULTIPLIES. The PPE's integer multiply is slow and poorly pipelined, and a blend
 * does three of them per pixel - one per channel - each feeding a dependent chain. Three multiplies at
 * the PPE's latency is most of the per-pixel budget on its own, and no amount of arranging the memory
 * was ever going to touch it. See blend_span: they are now a table.
 */

/*
 * THE BLEND, WITH THE MULTIPLIES REPLACED BY A TABLE.
 *
 * For one span the source colour and the alpha are constant, so a channel's result - (src*a + dst*inv)
 * / 255 - depends on nothing but that channel's DESTINATION byte, of which there are 256 possible
 * values. Three 256-entry tables, built once and shifted into position as they are built, turn the
 * inner loop into three loads and three ors.
 *
 * The tables are cached on the colour and alpha that built them, because a card's whole interior is one
 * span after another at the same two values: the fill is rebuilt once for the shape rather than once a
 * row. They are three kilobytes all told, which stays in L1 alongside everything else in this loop.
 */
static uint32_t s_lut_r[256], s_lut_g[256], s_lut_b[256], s_lut_a[256];
static uint32_t s_lut_key_rgb = 1u;  /* an impossible colour - the top byte is never set here */
static unsigned s_lut_key_a = 256u;  /* and an impossible alpha, so the first call always builds */

static void blend_lut_for(uint32_t rgb, unsigned a)
{
    unsigned inv = 255u - a;
    unsigned sr = ((rgb >> 16) & 0xffu) * a;
    unsigned sg = ((rgb >> 8) & 0xffu) * a;
    unsigned sb = (rgb & 0xffu) * a;
    int v;

    if (rgb == s_lut_key_rgb && a == s_lut_key_a)
        return;
    for (v = 0; v < 256; v++) {
        unsigned d = (unsigned)v * inv;
        unsigned ao = a + div255((unsigned)v * inv);

        if (ao > 255u)
            ao = 255u;
        s_lut_r[v] = div255(sr + d) << 16;
        s_lut_g[v] = div255(sg + d) << 8;
        s_lut_b[v] = div255(sb + d);
        s_lut_a[v] = ao << 24;     /* the layer accumulates coverage; the surface is already 255 */
    }
    s_lut_key_rgb = rgb;
    s_lut_key_a = a;
}

/*
 * One horizontal run at a single alpha. The globals are read ONCE here rather than per pixel: s_px and
 * s_pitch are file-scope, the loop stores through s_px, and the compiler has to assume a store might
 * reach them - so every iteration of the old per-pixel path reloaded all four before it could work out
 * where the next pixel was.
 */
/*
 * ONE PIXEL AT AN ALPHA OF ITS OWN, which is what the table cannot do.
 *
 * The table above is keyed on the colour AND the alpha, so it is only free where a run shares both. A
 * glow's alpha changes with every pixel by definition, and b374 shipped it through blend_span anyway:
 * the key missed on nearly every pixel, so a 768-entry table was rebuilt two hundred thousand times a
 * frame and the glow went from 9,045 us to 43,227. The table was a good idea applied to the one place
 * it cannot apply.
 *
 * So varying alpha keeps the multiplies. Three per pixel is what that costs, and the glow is now small
 * enough - see rounded_glow - that it no longer matters.
 */
static void blend_one(uint32_t *p, uint32_t rgb, unsigned a)
{
    uint32_t d;
    unsigned inv, ao;

    if (a == 0u)
        return;
    if (a >= 255u) {
        *p = 0xff000000u | rgb;
        return;
    }
    d = *p;
    inv = 255u - a;
    /*
     * The alpha is COMPUTED, not forced. These same functions draw the cached layer, which starts
     * transparent and has to accumulate coverage; on the opaque surface the arithmetic arrives back at
     * 255 by itself, so one path serves both. See the layer's note further down.
     */
    ao = a + div255(((d >> 24) & 0xffu) * inv);
    if (ao > 255u)
        ao = 255u;
    *p = (ao << 24)
       | (div255(((rgb >> 16) & 0xffu) * a + ((d >> 16) & 0xffu) * inv) << 16)
       | (div255(((rgb >> 8) & 0xffu) * a + ((d >> 8) & 0xffu) * inv) << 8)
       |  div255((rgb & 0xffu) * a + (d & 0xffu) * inv);
}

static void blend_span(uint32_t *row, int x0, int x1, uint32_t rgb, unsigned a)
{
    int x;

    if (a == 0u || x1 <= x0)
        return;
    if (a >= 255u) {
        uint32_t c = 0xff000000u | rgb;

        for (x = x0; x < x1; x++)
            row[x] = c;
        return;
    }
    blend_lut_for(rgb, a);

    for (x = x0; x < x1; x++) {
        uint32_t d = row[x];

        row[x] = s_lut_a[d >> 24]
               | s_lut_r[(d >> 16) & 0xffu]
               | s_lut_g[(d >> 8) & 0xffu]
               | s_lut_b[d & 0xffu];
    }
}

static void rounded_shape(int x, int y, int w, int h, int r, int t, uint32_t argb)
{
    unsigned alpha = (argb >> 24) & 0xffu;
    uint32_t rgb = argb & 0x00FFFFFFu;
    sh_rrect box;
    int row, t_sub = t * SH_SUB;
    int lo = x - 1, hi = x + w + 1;

    if (w <= 0 || h <= 0 || alpha == 0u || s_px == NULL)
        return;
    rrect_set(&box, x, y, w, h, r);
    if (lo < 0) lo = 0;
    if (hi > s_scr_w) hi = s_scr_w;
    if (hi <= lo)
        return;

    for (row = y - 1; row <= y + h; row++) {
        uint32_t *dst;
        int dy, col, flat_lo, flat_hi;
        unsigned flat;

        if (row < 0 || row >= s_scr_h)
            continue;
        dy = rrect_dy(&box, row);
        /* Nothing on this row can be inside if it is already further out than the radius allows. */
        if (dy - box.r >= SH_SUB_HALF && t == 0)
            continue;

        dst = s_px + (size_t)row * (size_t)s_pitch;
        layer_note_ink(row, lo, hi);

        /*
         * The straight band, where the horizontal term is zero by construction and the distance is the
         * row's alone. box.hw is the half-width with the corner radius already removed, so this is
         * exactly the range over which that is true.
         */
        flat_lo = (box.cx - box.hw) / SH_SUB;
        flat_hi = (box.cx + box.hw) / SH_SUB;
        if (flat_lo < lo) flat_lo = lo;
        if (flat_hi > hi) flat_hi = hi;

        if (t <= 0) {
            flat = cov_from(dy - box.r);
        } else {
            unsigned outer = cov_from(dy - box.r);
            unsigned inner = cov_from(-((dy - box.r) + t_sub));

            flat = (outer < inner) ? outer : inner;
        }
        if (flat_hi > flat_lo)
            blend_span(dst, flat_lo, flat_hi, rgb, div255(alpha * flat));

        /* The two ends, where the corner arc lives and a square root is genuinely needed. */
        for (col = lo; col < hi; col++) {
            int d;
            unsigned c;

            if (col >= flat_lo && col < flat_hi)
                continue;
            d = rrect_dist(&box, col, dy);

            if (t <= 0) {
                c = cov_from(d);
            } else {
                unsigned outer = cov_from(d);
                unsigned inner = cov_from(-(d + t_sub));

                c = (outer < inner) ? outer : inner;
            }
            if (c != 0u)
                blend_one(&dst[col], rgb, div255(alpha * c));
        }
    }
}

/*
 * THE GLOW, AS ONE FALLOFF RATHER THAN AS FOUR RINGS - AND CAPTURED, NOT DRAWN.
 *
 * The first version stacked four concentric rounded rectangles at a falling alpha. On a photograph that
 * is a halo; on a television it is four bands, because four steps is what four shapes give you. The
 * distance function answers it properly: alpha falls as the square of how far outside the card a pixel
 * is, which is continuous and is one pass instead of four.
 *
 * IT DOES NOT GO INTO THE CACHED LAYER, because it breathes. The layer is rebuilt when the interface
 * changes, which is a cursor move; anything that changes every frame cannot live in it. So the shape is
 * rasterised ONCE into a coverage mask - which is what it really is, one colour at a varying alpha - and
 * each frame multiplies that mask by a single scalar. Rasterising is the expensive half and it happens
 * on a rebuild; the per-frame half is a byte, a multiply and a blend.
 *
 * The mask is a box around the card rather than a screen, because that is all it can ever cover.
 *
 * ITS INSIDE IS STILL NOT PAINTED. The card covers exactly the region where the distance is negative,
 * and what showed through was the glow's own alpha - at most 64 of 255 - through the twelve percent the
 * card's fill lets past. One pixel of overlap is kept so the card's antialiased edge has something to
 * sit on.
 */
#define SH_GLOW_MAX      2048
#define SH_GLOW_MASK_W    540
#define SH_GLOW_MASK_H    640

static unsigned char s_glow_mask[SH_GLOW_MASK_W * SH_GLOW_MASK_H];
static short s_glow_lo[SH_GLOW_MASK_H], s_glow_hi[SH_GLOW_MASK_H];
static int s_glow_x, s_glow_y, s_glow_w, s_glow_h;
static uint32_t s_glow_rgb;
static int s_glow_live;

static void glow_capture(int x, int y, int w, int h, int r, int spread, uint32_t argb)
{
    static unsigned char fall[SH_GLOW_MAX];
    unsigned alpha = (argb >> 24) & 0xffu;
    sh_rrect box;
    int reach = spread * SH_SUB;
    int row, i, lo, hi;

    s_glow_live = 0;
    if (w <= 0 || h <= 0 || alpha == 0u || spread <= 0)
        return;
    if (reach > SH_GLOW_MAX)
        reach = SH_GLOW_MAX;

    s_glow_x = x - spread;
    s_glow_y = y - spread;
    s_glow_w = w + spread * 2;
    s_glow_h = h + spread * 2;
    /* Too large for the mask is not a failure worth handling twice: the card simply goes unlit. */
    if (s_glow_w > SH_GLOW_MASK_W || s_glow_h > SH_GLOW_MASK_H)
        return;
    s_glow_rgb = argb & 0x00FFFFFFu;
    rrect_set(&box, x, y, w, h, r);

    /*
     * The falloff, as a table. Written the obvious way it is (fade * fade) / reach / reach per pixel,
     * and `reach` is a RUN-TIME value - two integer divisions by a non-constant on every pixel, which
     * the compiler cannot strength-reduce the way it does a division by 255.
     */
    for (i = 0; i < reach; i++) {
        int fade = reach - i;

        fall[i] = (unsigned char)((alpha * (unsigned)((fade * fade) / reach)) / (unsigned)reach);
    }

    memset(s_glow_mask, 0, (size_t)s_glow_h * SH_GLOW_MASK_W);
    for (i = 0; i < s_glow_h; i++) {
        s_glow_lo[i] = 0;
        s_glow_hi[i] = 0;
    }

    lo = s_glow_x;
    hi = s_glow_x + s_glow_w;

    for (row = s_glow_y; row < s_glow_y + s_glow_h; row++) {
        unsigned char *m = s_glow_mask + (size_t)(row - s_glow_y) * SH_GLOW_MASK_W;
        int dy = rrect_dy(&box, row);
        int col, first = -1, last = -1;

        if (dy - box.r >= reach)
            continue;

        for (col = lo; col < hi; col++) {
            int d = rrect_dist(&box, col, dy);
            unsigned a;

            if (d >= reach || d < -SH_SUB)
                continue;
            a = fall[d < 0 ? 0 : d];
            if (a == 0u)
                continue;
            m[col - s_glow_x] = (unsigned char)a;
            if (first < 0)
                first = col - s_glow_x;
            last = col - s_glow_x;
        }
        if (first >= 0) {
            s_glow_lo[row - s_glow_y] = (short)first;
            s_glow_hi[row - s_glow_y] = (short)(last + 1);
        }
    }
    s_glow_live = 1;
}

/* One row of the captured glow over whatever is already there, at `scale` of its rasterised alpha. */
static void glow_row(uint32_t *dst, int y, unsigned scale)
{
    const unsigned char *m;
    int my = y - s_glow_y;
    int mx, from, to;

    if (!s_glow_live || my < 0 || my >= s_glow_h)
        return;
    from = s_glow_lo[my];
    to = s_glow_hi[my];
    if (to <= from)
        return;
    m = s_glow_mask + (size_t)my * SH_GLOW_MASK_W;

    for (mx = from; mx < to; mx++) {
        int px = s_glow_x + mx;
        unsigned a = m[mx];

        if (a == 0u || px < 0 || px >= s_scr_w)
            continue;
        blend_one(&dst[px], s_glow_rgb, div255(a * scale));
    }
}

static void rounded(int x, int y, int w, int h, int r, uint32_t argb, int blend)
{
    (void)blend;
    rounded_shape(x, y, w, h, r, 0, argb);
}

static void rounded_edge(int x, int y, int w, int h, int r, int t, uint32_t argb)
{
    rounded_shape(x, y, w, h, r, t < 1 ? 1 : t, argb);
}

/*
 * THE BUTTON GLYPHS, on the same principle: an exact distance to the shape's outline, so the diagonals
 * of a cross come out as clean lines and the triangle's edges stop where they meet instead of running
 * past each other. Returns the width consumed, so a row of hints is laid out by chaining rather than by
 * hand-measured offsets - which is how the last one ended up off-centre.
 */
static int glyph(uint32_t button, int x, int y, int size, uint32_t argb)
{
    unsigned alpha = (argb >> 24) & 0xffu;
    uint32_t rgb = argb & 0x00FFFFFFu;
    int half = (size * SH_SUB) / 2;
    int stroke = (size * SH_SUB * 11) / 100;   /* 11% of the box, which reads at ten feet */
    int radius = (size * SH_SUB * 36) / 100;
    int row, col;

    if (size <= 0 || alpha == 0u || s_px == NULL)
        return size;
    if (stroke < SH_SUB)
        stroke = SH_SUB;

    for (row = 0; row < size; row++) {
        int vy = row * SH_SUB + SH_SUB_HALF - half;

        layer_note_ink(y + row, x, x + size);

        for (col = 0; col < size; col++) {
            int vx = col * SH_SUB + SH_SUB_HALF - half;
            int d = 1 << 24;   /* distance to the stroke's centre line, positive outside */
            unsigned c;

            if (button == HALYARD_PAD_CROSS) {
                /*
                 * Two bars through the centre at forty-five degrees. The perpendicular distance to
                 * such a line is |vx -+ vy| / sqrt(2), and 181/256 is that divisor.
                 */
                int a = vx - vy, b = vx + vy;
                int da, db, ext;

                if (a < 0) a = -a;
                if (b < 0) b = -b;
                da = (a * 181) / 256;
                db = (b * 181) / 256;
                d = (da < db) ? da : db;
                /* Cut the bars to length, so the cross is a cross and not two full-width diagonals. */
                ext = (vx < 0 ? -vx : vx);
                if ((vy < 0 ? -vy : vy) > ext)
                    ext = (vy < 0 ? -vy : vy);
                if (ext - radius > d - stroke / 2)
                    d = ext - radius + stroke / 2;
                d -= stroke / 2;
            } else if (button == HALYARD_PAD_CIRCLE) {
                int dist = isqrt_i(vx * vx + vy * vy) - radius;

                if (dist < 0) dist = -dist;
                d = dist - stroke / 2;
            } else if (button == HALYARD_PAD_TRIANGLE) {
                /*
                 * Three half-planes, each giving the signed distance OUTSIDE one edge. The distance to
                 * the triangle is the largest of the three, so nothing can run past a corner - a corner
                 * is exactly where two of them agree. The stroke is the band just inside that boundary,
                 * the same two-sided form the rounded rectangle's outline uses.
                 */
                int apex = -radius;
                int base = (radius * 7) / 10;
                int out_left  = -((vy - apex) * 50 - vx * 87) / 100;
                int out_right = -((vy - apex) * 50 + vx * 87) / 100;
                int out_base  = vy - base;
                int outside = out_base;
                unsigned outer, inner;

                if (out_left > outside) outside = out_left;
                if (out_right > outside) outside = out_right;

                outer = cov_from(outside);
                inner = cov_from(-(outside + stroke));
                c = (outer < inner) ? outer : inner;
                if (c != 0u)
                    blend_at(x + col, y + row, rgb, (alpha * c) / 255u);
                continue;
            } else {
                int ax = (vx < 0 ? -vx : vx) - radius;
                int ay = (vy < 0 ? -vy : vy) - radius;
                int dist = (ax > ay) ? ax : ay;

                if (dist < 0) dist = -dist;
                d = dist - stroke / 2;
            }

            c = cov_from(d);
            if (c != 0u)
                blend_at(x + col, y + row, rgb, (alpha * c) / 255u);
        }
    }
    return size;
}

/*
 * SELECT and START, as words in a rounded pill - which is how PS3-era games drew them. There is no
 * hamburger on a DualShock 3; that is a phone idiom, and putting one on a console screen is the sort of
 * thing that marks software as not from here.
 */
static int pill(const char *label, int x, int y, int h, uint32_t argb)
{
    int pad = sx(18);
    int w = rc_overlay_text(0, -10000, 1, 0x00000000u, "%s", label) + pad * 2;

    rounded_shape(x, y, w, h, h / 2, sy(3), argb);
    (void)rc_overlay_text(x + pad, rc_overlay_text_y(y, h, 1), 1, argb, "%s", label);
    return w;
}

/* ------------------------------------------------------------------------------------------------ */

/*
 * THE STATUS PIPS, held aside for the same reason as the glow: they move.
 *
 * ONLY THE GREEN ONE BREATHES, which is not what SHELL-DESIGN.md asked for and is the better call.
 *
 * The plan was a slow breathe on a console in STANDBY - "a console you must wake looks asleep". On a
 * screen it turned out to be the wrong way round: amber already says "not ready" by being amber, and
 * moving it as well makes the thing you cannot use the most animated thing on the card. Green moving
 * gently says "this is awake and waiting for you", which is the one piece of information somebody is
 * actually looking for. So amber sits still and green breathes, slowly and shallowly - it is saying
 * "alive", not "look at me".
 */
#define SH_PIPS 4
static struct { int x, y, d, ready; } s_pip[SH_PIPS];
static int s_pip_count;

/*
 * A breath: `lo` to `hi` and back over `period`, eased at both ends so it turns rather than bounces.
 * Smoothstep on a triangle - and the easing is the whole difference between something breathing and
 * something blinking.
 */
static unsigned breathe(uint64_t ms, unsigned period, unsigned lo, unsigned hi)
{
    unsigned t = (unsigned)(ms % (uint64_t)period);
    unsigned v = (t * 512u) / period;
    unsigned e;

    if (v > 511u) v = 511u;
    v = (v < 256u) ? v : (511u - v);          /* a triangle, 0..255 */
    e = (v * v * (765u - 2u * v)) / 65025u;   /* 3v^2 - 2v^3, eased at both ends */
    if (e > 255u) e = 255u;
    return lo + ((hi - lo) * e) / 255u;
}

static void draw_card(const rc_menu_item *item, int x, int y, int w, int h, int selected)
{
    int r = sy(22);
    int pad = sy(34);
    uint32_t name_colour = item->enabled ? RC_OV_TEXT : RC_OV_LABEL;

    if (selected) {
        /*
         * THE GLOW IS WHAT MAKES IT "PICKED UP" RATHER THAN "HIGHLIGHTED" - one continuous falloff off
         * the card's own outline, captured here and breathed over it each frame. See glow_capture.
         */
        s_shape_at = rc_tick();
        glow_capture(x, y, w, h, r, sy(26), 0x40000000u | (s_accent & 0x00FFFFFFu));
        s_sum_glow += us_since(s_shape_at);
    }

    s_shape_at = rc_tick();
    rounded(x, y, w, h, r, selected ? 0xE00E1218u : 0xB00A0D12u, 1);
    rounded_edge(x, y, w, h, r, sy(2), selected ? s_accent : 0x30FFFFFFu);
    s_sum_shape += us_since(s_shape_at);

    if (item->id == SH_ID_PAIR_NEW || !item->enabled) {
        /* The "pair a console" card: a plus, and nothing else to read. */
        int cx = x + w / 2, cy = y + h / 2 - sy(30);
        int arm = sx(30), t = sy(5);

        rc_overlay_blend_rect(cx - arm, cy - t / 2, arm * 2, t, selected ? RC_OV_TEXT : 0x80E6EAEFu);
        rc_overlay_blend_rect(cx - t / 2, cy - arm, t, arm * 2, selected ? RC_OV_TEXT : 0x80E6EAEFu);
        {
            int tw = rc_overlay_text(0, -10000, 2, 0x00000000u, "%s", item->label);

            (void)rc_overlay_text(x + (w - tw) / 2, y + h / 2 + sy(36), 2, name_colour, "%s",
                                  item->label);
        }
        return;
    }

    /* The family tag, small and in this month's accent. */
    if (item->tag[0] != '\0')
        (void)rc_overlay_text(x + pad, y + pad, 1, s_accent, "%s", item->tag);

    /* The console's own name, large and centred - that is the word its owner thinks in. */
    {
        int tw = rc_overlay_text(0, -10000, 3, 0x00000000u, "%s", item->label);
        int tx = x + (w - tw) / 2;

        if (tw > w - sy(20)) {
            /* Too long to centre without touching the edges: set it smaller and left-aligned instead
             * of letting it run off the card. */
            tw = rc_overlay_text(0, -10000, 2, 0x00000000u, "%s", item->label);
            tx = x + (w - tw) / 2;
            if (tx < x + sy(12))
                tx = x + sy(12);
            (void)rc_overlay_text(tx, y + h / 2 - sy(18), 2, name_colour, "%s", item->label);
        } else {
            (void)rc_overlay_text(tx, y + h / 2 - sy(30), 3, name_colour, "%s", item->label);
        }
    }

    /* A pip and one word. Green is ready, amber is asleep, grey is a console that did not answer. */
    /*
     * A PIP ONLY WHEN IT MEANS SOMETHING. Green is ready and amber is asleep; a console that did not
     * answer gets no dot at all, because a grey circle beside the word "paired" is a thing somebody has
     * to work out rather than read, and there is nothing to work out - it just has not been seen.
     */
    if (item->value[0] != '\0') {
        int px = x + pad, py = y + h - pad - sy(18);
        int d = sy(16);
        int ready = (strcmp(item->value, "ready") == 0);
        int standby = (strcmp(item->value, "standby") == 0);

        if (ready || standby) {
            /*
             * RECORDED, NOT DRAWN - it breathes, and the layer is not rebuilt per frame. Two hundred
             * and fifty pixels put straight on the screen after everything else is cheaper than any
             * arrangement that keeps it in the cache, and it sits on top of the card, so last is also
             * the right order.
             */
            if (s_pip_count < SH_PIPS) {
                s_pip[s_pip_count].x = px;
                s_pip[s_pip_count].y = py;
                s_pip[s_pip_count].d = d;
                s_pip[s_pip_count].ready = ready;
                s_pip_count++;
            }
            px += d + sx(14);
        }
        (void)rc_overlay_text(px, rc_overlay_text_y(py, d, 1), 1, RC_OV_LABEL, "%s", item->value);
    }
}

/* The footer, as glyphs and pills rather than letters. Drawn by the caller through `draw`'s hint slot
 * would mean a string; this is a row of shapes, so it is its own function and `draw` calls it. */
static void draw_hints(int forget, int options)
{
    int x = sx(SH_MARGIN);
    int y = sy(SH_HINT_Y);
    int size = sy(28);

    /* The glyphs are square boxes `size` on a side, so the words beside them are centred against that
     * box rather than nudged down by a constant. */
    int ty = rc_overlay_text_y(y, size, 1);

    x += glyph(s_enter, x, y, size, RC_OV_TEXT) + sx(14);
    x += rc_overlay_text(x, ty, 1, RC_OV_LABEL, "%s",
                         (s_menu.selected >= 0 && s_menu.item[s_menu.selected].id == SH_ID_PAIR_NEW)
                             ? "pair" : "stream") + sx(46);
    if (forget) {
        x += glyph(HALYARD_PAD_TRIANGLE, x, y, size, RC_OV_TEXT) + sx(14);
        x += rc_overlay_text(x, ty, 1, RC_OV_LABEL, "%s", "forget") + sx(46);
    }
    if (options) {
        x += pill("START", x, y, size, RC_OV_TEXT) + sx(14);
        (void)rc_overlay_text(x, ty, 1, RC_OV_LABEL, "%s", "options");
    }
}

/*
 * A vertical list, for everything that is not the home screen: options, settings, a confirmation. Over
 * the same wave, with the selected row on a panel rather than a highlight bar - the card row's idea,
 * flattened.
 */
#define SH_LIST_TOP  300
#define SH_LIST_H     82
#define SH_LIST_MAX    6

static int s_first_row;

static void draw_list(void)
{
    int w = s_scr_w - sx(SH_MARGIN) * 2;
    int shown, i;

    if (s_menu.selected >= 0) {
        if (s_menu.selected < s_first_row)
            s_first_row = s_menu.selected;
        else if (s_menu.selected >= s_first_row + SH_LIST_MAX)
            s_first_row = s_menu.selected - SH_LIST_MAX + 1;
    }
    if (s_first_row > s_menu.count - SH_LIST_MAX)
        s_first_row = s_menu.count - SH_LIST_MAX;
    if (s_first_row < 0)
        s_first_row = 0;

    shown = s_menu.count - s_first_row;
    if (shown > SH_LIST_MAX)
        shown = SH_LIST_MAX;

    for (i = 0; i < shown; i++) {
        int index = s_first_row + i;
        const rc_menu_item *item = &s_menu.item[index];
        int selected = (index == s_menu.selected);
        int y = sy(SH_LIST_TOP + i * SH_LIST_H);
        int h = sy(SH_LIST_H - 12);
        int x = sx(SH_MARGIN);
        uint32_t colour = item->enabled ? RC_OV_TEXT : RC_OV_LABEL;

        if (selected) {
            rounded(x, y, w, h, sy(14), 0xC00E1218u, 1);
            rounded_edge(x, y, w, h, sy(14), sy(2), s_accent);
        }
        (void)rc_overlay_text(x + sx(28), rc_overlay_text_y(y, h, 2), 2, colour, "%s", item->label);
        if (item->value[0] != '\0') {
            /* Centred in the SAME box as the label, at its own size - which is what puts two sizes on
             * one row on a shared optical centre line instead of on two guessed offsets. */
            int vy = rc_overlay_text_y(y, h, 1);

            if (item->adjustable && selected)
                rc_overlay_text_right(x + w - sx(28), vy, 1, s_accent, "< %s >", item->value);
            else
                rc_overlay_text_right(x + w - sx(28), vy, 1,
                                      item->enabled ? RC_OV_LABEL : RC_OV_TRACK, "%s", item->value);
        }
    }

    if (s_menu.count > SH_LIST_MAX) {
        int tx = s_scr_w - sx(SH_MARGIN) + sx(20);
        int ty = sy(SH_LIST_TOP);
        int th = sy(SH_LIST_MAX * SH_LIST_H - 12);

        rc_overlay_blend_rect(tx, ty, sx(4), th, RC_OV_TRACK);
        rc_overlay_blend_rect(tx, ty + th * s_first_row / s_menu.count, sx(4),
                              th * SH_LIST_MAX / s_menu.count, s_accent);
    }
}

/*
 * WHAT WAS ON THE SCREEN LAST TIME, so a menu nobody is touching costs only its background.
 */
/*
 * TWO WAYS TO SHOW ONE MODEL. The home screen is a row of cards because choosing a destination is a
 * different act from choosing a setting - and a settings screen laid out as cards would be four
 * enormous tiles saying "30 fps". rc_menu does not know or care which of these is drawing it.
 */
static int s_cards = 1;

static void pump_input(void);
static uint64_t s_ui_at;
static unsigned s_ui_us, s_ui_worst_us, s_frames;
/*
 * WHAT A FRAME ACTUALLY COSTS, AND WHY THE FIRST VERSION OF THIS LIED.
 *
 * b363 and b366 reported "15 fps" and "10 fps" from frames divided by the time the shell was open. That
 * is not a frame rate. The shell blocks for a second and a half inside a discovery broadcast, and for
 * as long as somebody takes inside the pairing prompts and the on-screen keyboard - none of which draws
 * a frame, all of which is on that clock. The two numbers differed by a third because the person
 * holding the controller spent longer in a sub-screen, and nothing about the drawing had changed at all.
 * It also sent me looking for fifty milliseconds a frame that were never there.
 *
 * So the interval between successive draws is measured directly, and an interval longer than a fifth of
 * a second is discarded as "the shell was doing something else" rather than averaged in. The components
 * are summed rather than sampled, because the last frame's cost is not the typical one and the spread
 * here is wide.
 *
 * And the whole of draw() is timed as well as its parts, so the parts can be checked against the whole.
 * The gap between them is the flip wait, which is the one cost that SHOULD be there - it is the frame
 * rate being held to the refresh - and telling it apart from an unmeasured cost needs both numbers.
 */
static uint64_t s_draw_at;           /* rc_tick() at the top of this draw */
static uint64_t s_frame_at;          /* rc_tick() at the start of the previous draw */
static uint64_t s_sum_frame, s_sum_draw, s_sum_wave, s_sum_ui, s_sum_vram, s_sum_wait;
static unsigned s_intervals;         /* frames whose interval counted towards s_sum_frame */
static unsigned s_worst_frame_us;
static unsigned s_vram_us, s_vram_worst_us;
static rc_thermal_record s_thermal;


/* ------------------------------------------------------------------------------------------------
 * THE INTERFACE IS DRAWN ONCE AND COMPOSITED, NOT REDRAWN OVER A MOVING BACKGROUND
 *
 * Four attempts to make the blending cheaper - dcbz, dcbt, removing the arithmetic, removing the
 * multiplies - left it at about 140 cycles a blended pixel, having started at 217. The arithmetic was
 * never what it was waiting for. What it was waiting for is the shape of the access: every blended
 * pixel READ the surface before writing it, the surface is eight megabytes, nothing of it is ever in
 * cache, and an in-order core stops dead on each dependent load.
 *
 * The background, by contrast, only ever WRITES its surface, and costs about 21 cycles a pixel doing
 * it. The difference between those two numbers is the whole of this design.
 *
 * So the surface stops being read. The interface is drawn once into a layer of its own and kept until
 * something about it changes - which is on a cursor move, not on a frame - and each frame is then one
 * pass down the screen:
 *
 *     the background's row is expanded into a single-row buffer, which stays in cache;
 *     the layer's ink for that row is composited onto it, also in cache;
 *     the finished row is stored to the surface, once, sequentially.
 *
 * The surface therefore receives exactly one streaming store per frame and no reads at all, which is
 * the access pattern the background has already demonstrated this core is good at.
 *
 * THE LAYER HOLDS PREMULTIPLIED COLOUR. Compositing it is then out = layer.rgb + under * (255 - a),
 * with no division by the alpha and no special case for a pixel that is fully covered. rc_overlay
 * draws into it with the same arithmetic it uses on the surface - see the note above its blend_px.
 *
 * AND MOST ROWS HAVE ALMOST NOTHING ON THEM. A row's leftmost and rightmost ink is recorded when the
 * layer is built, so compositing walks that span rather than the screen; rows with no ink at all are
 * skipped entirely, and on this screen that is most of them.
 */

/* The largest surface rc_overlay will ever hand out; see RC_OV_MAX_W/H there. The layer and the row
 * buffer are sized against it rather than against the current display, so a mode change reallocates
 * nothing and cannot overrun either. */
#define SH_MAX_W 1920
#define SH_MAX_H 1088

static uint32_t *s_layer;              /* premultiplied ARGB - the interface, with no background */
/*
 * ONE SPAN A ROW. b382 split each row into two runs so a header row would not composite the empty
 * middle of the screen between the wordmark and the build id; it measured no difference at all, so the
 * second run and its gap search are gone again rather than kept for the story they tell.
 */
static short s_lrow_lo[SH_MAX_H], s_lrow_hi[SH_MAX_H];
static int s_layer_fresh;
static int s_layer_checks;
static int s_layer_w, s_layer_h;

/* What the layer was built for. Any of these changing is what makes it stale. */
static unsigned s_layer_revision;
static int s_layer_hint = -1;
static int s_layer_cards = -1;
static int s_layer_valid;

static uint64_t s_sum_layer;
static unsigned s_layer_builds;
/*
 * The two halves of the frame's pass, kept apart. b380 folded the background's expansion, a line copy
 * and the composite into one number and it went the wrong way; having to guess which third was
 * responsible is what that cost.
 */
static uint64_t s_ticks_rows, s_ticks_comp;

static int layer_ensure(void)
{
    if (s_layer != NULL && s_layer_w == s_scr_w && s_layer_h == s_scr_h)
        return 1;
    if (s_scr_w <= 0 || s_scr_h <= 0 || s_scr_w > SH_MAX_W || s_scr_h > SH_MAX_H)
        return 0;
    free(s_layer);
    s_layer = (uint32_t *)memalign(128, (size_t)s_scr_w * (size_t)s_scr_h * 4u);
    if (s_layer == NULL) {
        /*
         * SAID, AND THEN LIVED WITH. Without the layer the shell still draws - build_layer simply
         * aims at the surface as every build before this one did - so a console short of memory gets
         * the old frame rate rather than no menu.
         */
        rc_log("shell: no memory for the interface layer - drawing straight to the screen\n");
        s_layer_w = s_layer_h = 0;
        return 0;
    }
    s_layer_w = s_scr_w;
    s_layer_h = s_scr_h;
    s_layer_valid = 0;
    s_layer_fresh = 1;
    return 1;
}

/*
 * WHERE THE INK IS, RECORDED AS IT IS LAID DOWN.
 *
 * b380 found this out afterwards by reading the finished layer back - eight megabytes, twice, on the
 * one path that has to be quick. A rebuild measured 36,023 us, which is a stall of nearly two frames
 * every time the cursor moves, and it is a thing somebody feels rather than measures.
 *
 * Both halves of the drawing already know: rc_overlay through its ink hook, and the shapes here at the
 * top of each row's loop. The bounds are deliberately conservative - the full extent a row COULD touch
 * rather than what it did - because one comparison per row is free and being wrong the other way would
 * lose part of the picture.
 */
static int s_layer_building;

static void layer_note_ink(int y, int x0, int x1)
{
    if (!s_layer_building || y < 0 || y >= s_layer_h)
        return;
    if (x0 < 0) x0 = 0;
    if (x1 > s_layer_w) x1 = s_layer_w;
    if (x1 <= x0)
        return;
    if (s_lrow_hi[y] <= s_lrow_lo[y]) {
        s_lrow_lo[y] = (short)x0;
        s_lrow_hi[y] = (short)x1;
        return;
    }
    if (x0 < s_lrow_lo[y]) s_lrow_lo[y] = (short)x0;
    if (x1 > s_lrow_hi[y]) s_lrow_hi[y] = (short)x1;
}

/*
 * Everything the interface is made of, painted into whatever s_px and rc_overlay's target currently
 * point at. Split out so the layer and the no-layer fallback below are the same drawing rather than two
 * that have to be kept in step.
 */
static void paint_ui(int can_forget)
{
    int i;

    s_pip_count = 0;
    s_glow_live = 0;

    (void)rc_overlay_text_cost(NULL, NULL, 1);
    s_shape_at = rc_tick();

    /* The wordmark, and a short rule under it in this month's colour. */
    (void)rc_overlay_text(sx(SH_MARGIN), sy(SH_WORD_Y), 3, RC_OV_TEXT, "%s", "RIPCORD");
    rc_overlay_blend_rect(sx(SH_MARGIN), sy(SH_RULE_Y), sx(112), sy(3), s_accent);
    rc_overlay_text_right(s_scr_w - sx(SH_MARGIN), sy(SH_WORD_Y + 22), 1, RC_OV_LABEL, "%s",
                          RC_PS3_BUILD_ID);

    if (s_menu.subtitle[0] != '\0')
        (void)rc_overlay_text(sx(SH_MARGIN), sy(SH_RULE_Y + 22), 1, RC_OV_LABEL, "%s",
                              s_menu.subtitle);

    s_sum_header += us_since(s_shape_at);
    s_shape_at = rc_tick();

    if (!s_cards)
        draw_list();
    else
    /*
     * THE CARDS, CENTRED AS A ROW. More than four and they would not fit at this width, so the row
     * narrows rather than running off the screen - a console nobody can see is worse than a small one.
     */
    {
        int n = s_menu.count;
        int cw = sx(SH_CARD_W), ch = sy(SH_CARD_H), gap = sx(SH_CARD_GAP);
        int total;
        int x0;

        if (n > 0) {
            while (n * cw + (n - 1) * gap > s_scr_w - sx(SH_MARGIN)) {
                cw = cw * 9 / 10;
                gap = gap * 9 / 10;
                ch = ch * 9 / 10;
                if (cw < sx(120))
                    break;
            }
            total = n * cw + (n - 1) * gap;
            x0 = (s_scr_w - total) / 2;

            for (i = 0; i < n; i++) {
                int selected = (i == s_menu.selected);
                int lift = selected ? sy(SH_SELECT_LIFT) : 0;
                int x = x0 + i * (cw + gap);
                int y = sy(SH_CARD_MID) - ch / 2 - lift;

                draw_card(&s_menu.item[i], x, y, cw, ch + lift, selected);
            }
        }
    }

    s_sum_cards += us_since(s_shape_at);
    s_shape_at = rc_tick();

    /*
     * ONE DESCRIPTION LINE, IN A FIXED PLACE, changing with the focus. The XMB does this and it is
     * right: the explanation lives somewhere the eye learns once instead of on every row.
     */
    {
        const rc_menu_item *item = rc_menu_selected(&s_menu);

        if (item != NULL && item->note[0] != '\0')
            (void)rc_overlay_text(sx(SH_MARGIN), sy(SH_DESC_Y), 2, RC_OV_TEXT, "%s", item->note);
    }

    draw_hints(can_forget, 1);
    s_sum_hints += us_since(s_shape_at);
    {
        unsigned tus = 0u;

        (void)rc_overlay_text_cost(&tus, NULL, 0);
        s_sum_text += tus;
    }
    pump_input();

    /*
     * WHAT EACH HALF COSTS, kept apart. "The menu is slow" is not a finding; "the background is 7 ms and
     * the cards are 19" names which one to fix, and the first version of this measured only the
     * background and drew the conclusion about the wrong half.
     */

}

/*
 * EVERY PIXEL DRAWN MUST BE INSIDE THE BOUNDS ITS DRAWER REPORTED, and until b391 nothing checked it.
 *
 * The composite walks the recorded bounds and nothing else, so a primitive that draws without saying
 * where does not lose its pixels - it loses the composite's permission to read them. rounded_glow did
 * exactly that, and what reached the television was a glow with square corners, present on the side
 * facing the other card and absent on the outside, visible along the bottom only where the word
 * "standby" happened to widen the row. Five readings of the drawing code did not find it; a description
 * of the shape on screen did, which is not a method that scales.
 *
 * Nothing in this project's host tests could have caught it either: they compare one implementation
 * against another and both would have drawn the glow correctly. The fault was in the BOOKKEEPING beside
 * the drawing, and the only thing that knows about it is this invariant.
 *
 * Checked on the first couple of rebuilds and then never again: a layout fault is there from the first
 * frame or not at all, and the scan is the cost the ink hook exists to avoid paying per frame.
 */
static void layer_verify(void)
{
    int y, x, bad = 0;

    for (y = 0; y < s_layer_h && bad < 4; y++) {
        const uint32_t *row = s_layer + (size_t)y * (size_t)s_layer_w;

        for (x = 0; x < s_layer_w; x++) {
            if ((row[x] >> 24) == 0u)
                continue;
            if (x >= s_lrow_lo[y] && x < s_lrow_hi[y])
                continue;
            rc_log("shell: INK OUTSIDE ITS BOUNDS at row %d column %d, recorded %d..%d - something\n"
                   "       drew without saying where, and the composite will clip it off\n",
                   y, x, (int)s_lrow_lo[y], (int)s_lrow_hi[y]);
            bad++;
            break;
        }
    }
    if (bad == 0)
        rc_log("shell: the layer's ink is inside the bounds it reported\n");
}

static void build_layer(int can_forget)
{
    uint64_t at = rc_tick();
    int y;

    if (!layer_ensure())
        return;

    /*
     * CLEARED IN FULL, AND THIS IS A RETREAT FROM SOMETHING CLEVERER.
     *
     * b386 cleared only the rows and columns the previous build had recorded ink on, which is sound by
     * induction - every build erases the last one's full extent, so the only ink present is the last
     * one's - and it was eight megabytes of memset saved on a path that runs a few times a second. What
     * was reported from hardware was the previous screen's glow showing faintly through the next one,
     * and the argument above has exactly one weak point: it holds only while every write really is
     * inside its recorded bounds, across two files and seven drawing primitives.
     *
     * A full clear removes that entire class of question for about seven milliseconds on a rebuild, and
     * a rebuild is not in the frame budget. The ink bounds are still recorded and still used - they are
     * what stops the COMPOSITE walking the whole screen sixty times a second, which is where the time
     * actually was. This gives up the small half of that saving and keeps the large one.
     *
     * If the smearing survives this, it is not the bookkeeping: it is the RSX reading main memory that
     * the PPE has written, and that is a different fix in a different file.
     */
    memset(s_layer, 0, (size_t)s_layer_w * (size_t)s_layer_h * 4u);
    s_layer_fresh = 0;
    for (y = 0; y < s_layer_h; y++) {
        s_lrow_lo[y] = 0;
        s_lrow_hi[y] = 0;
    }

    s_px = s_layer;
    s_pitch = s_layer_w;
    s_layer_building = 1;
    rc_overlay_target(s_layer, s_layer_w);
    rc_overlay_set_ink_hook(layer_note_ink);

    paint_ui(can_forget);

    rc_overlay_set_ink_hook(NULL);
    rc_overlay_target(NULL, 0);
    s_layer_building = 0;

    if (s_layer_checks < 2) {
        s_layer_checks++;
        layer_verify();
    }

    s_layer_revision = s_menu.revision;
    s_layer_hint = can_forget;
    s_layer_cards = s_cards;
    s_layer_valid = 1;
    s_sum_layer += us_since(at);
    s_layer_builds++;
}

/* out = layer + under * (1 - layer alpha), the layer being premultiplied. */
static void composite_span(uint32_t *line, const uint32_t *src, int x, int end)
{
    for (; x < end; x++) {
        uint32_t L = src[x];
        unsigned a = L >> 24;
        uint32_t u;
        unsigned inv;

        if (a == 0u)
            continue;
        if (a >= 255u) {
            line[x] = 0xff000000u | (L & 0x00FFFFFFu);
            continue;
        }
        u = line[x];
        inv = 255u - a;
        line[x] = 0xff000000u
                | ((((L >> 16) & 0xffu) + div255(((u >> 16) & 0xffu) * inv)) << 16)
                | ((((L >> 8) & 0xffu) + div255(((u >> 8) & 0xffu) * inv)) << 8)
                |  (((L & 0xffu) + div255((u & 0xffu) * inv)));
    }
}

static void composite_row(uint32_t *line, int y)
{
    composite_span(line, s_layer + (size_t)y * (size_t)s_layer_w, s_lrow_lo[y], s_lrow_hi[y]);
}

static void draw(int can_forget)
{
    rc_video_info info;
    uint32_t *pixels;
    int pitch = 0;
    uint64_t now = rc_time_ms();
    unsigned glow_scale;
    int y;

    if (!rc_video_info_get(&info))
        return;
    s_scr_w = rc_overlay_surface_width();
    s_scr_h = rc_overlay_surface_height();

    /*
     * WAIT FOR THE LAST FLIP BEFORE QUEUING ANOTHER, which the streaming path does not do and must not:
     * there, a flip that has not landed means skip a frame, because the thread is also draining a
     * socket. Here there is nothing else to do. Bounded, because b232 left the RSX stopped with every
     * flip pending forever - a menu that waits for a flip that will never complete is a hang.
     */
    s_draw_at = rc_tick();
    {
        uint64_t give_up = now + 100u;
        uint64_t wait_at = rc_tick();

        /*
         * PUMPED BEFORE THE TEST, NOT ONLY INSIDE IT. While the frame cost 40 ms the flip was never
         * ready and this loop always ran, so the pad got looked at; the moment the frame fits in a
         * refresh the loop stops running and the ONLY poll left in the pass is the one at the top of
         * rc_shell_run. Making the menu fast would have made it less responsive, which is not a
         * trade-off anybody would choose on purpose.
         */
        pump_input();
        while (!rc_video_present_ready() && rc_time_ms() < give_up) {
            /* The wait is the best place in the loop to be watching the pad: it is time this thread
             * has nothing else to do with, and it is most of the gap a press used to fall into. */
            pump_input();
            usleep(1000);
        }
        s_sum_wait += us_since(wait_at);
    }

    /* No clear: the pass below writes every pixel of the surface, and clearing it to black first is
     * two million stores spent on a colour that is never seen. */
    if (!rc_overlay_begin_surface(0))
        return;
    pixels = rc_overlay_pixels(&pitch);
    if (pixels == NULL)
        return;

    /*
     * REBUILT ONLY WHEN THE INTERFACE ITSELF CHANGED, which is a cursor move rather than a frame. The
     * menu's revision counts every mutation INCLUDING the selection moving, so it answers this on its
     * own; the other two are state the menu does not know about.
     */
    if (!s_layer_valid || s_menu.revision != s_layer_revision || can_forget != s_layer_hint ||
        s_cards != s_layer_cards || s_layer_w != s_scr_w || s_layer_h != s_scr_h)
        build_layer(can_forget);

    /*
     * WHAT MOVES THIS FRAME. Three numbers, read once here so every part of the frame agrees about
     * where in its breath it is - and so the periods are visible together rather than scattered.
     */
    glow_scale = breathe(now, 4200u, 172u, 255u);

    s_ui_at = rc_tick();
    rc_wave_begin(s_scr_w, s_scr_h, now);

    /*
     * AND AGAIN HERE, because a button pressed and released while this thread is busy would otherwise
     * leave no trace - rc_pad_read reports the pad's state now, not what it did in between. See the
     * note above poll_edges.
     */
    pump_input();

    if (s_layer != NULL && s_layer_valid) {
        /*
         * STRAIGHT INTO THE SURFACE, NOT THROUGH A LINE BUFFER.
         *
         * b380 expanded each row into a single-row buffer, composited there, and then copied the
         * finished row out - on the reasoning that the row would stay in cache and the surface would
         * receive nothing but a streaming store. The copy is what that costs: eight megabytes read back
         * out of the line buffer and eight megabytes stored, on top of the eight already written INTO
         * it. It measured 46,559 us against 19,091 for simply drawing the interface every frame.
         *
         * The row is in cache either way. A row is 7,680 bytes and the L2 is half a megabyte, so the
         * row rc_wave_row has just written is still there when the composite reads it back - which is
         * the whole benefit the line buffer was supposed to provide, already paid for and free.
         */
        /*
         * TICKS ACCUMULATED, CONVERTED ONCE. us_since divides by rc_tick_hz(), and two of those per row
         * for two timers is four thousand 64-bit divisions a frame - which is instrumentation costing
         * about three milliseconds of the thing it is measuring. The raw counter is added up here and
         * turned into microseconds when the shell closes.
         */
        for (y = 0; y < s_scr_h; y++) {
            uint32_t *row = pixels + (size_t)y * (size_t)pitch;
            uint64_t at = rc_tick(), mid;

            rc_wave_row(row, s_scr_w, y);
            mid = rc_tick();
            s_ticks_rows += mid - at;
            /*
             * UNDER THE CARD AND OVER THE BACKGROUND, which is the order it is lit in - so it goes
             * between the two rather than into either.
             */
            glow_row(row, y, glow_scale);
            if (s_lrow_hi[y] > s_lrow_lo[y]) {
                composite_row(row, y);
                s_ticks_comp += rc_tick() - mid;
            }
        }
    } else {
        /*
         * NO LAYER - the console refused the memory for it. This is the way every build before b376
         * worked: the background straight into the surface, then the interface blended over it. Slower,
         * correct, and the reason layer_ensure reports rather than fails.
         */
        s_px = pixels;
        s_pitch = pitch;
        for (y = 0; y < s_scr_h; y++)
            rc_wave_row(pixels + (size_t)y * (size_t)pitch, s_scr_w, y);
        paint_ui(can_forget);
        for (y = 0; y < s_scr_h; y++)
            glow_row(pixels + (size_t)y * (size_t)pitch, y, glow_scale);
    }
    /*
     * THE PIPS, LAST, because they sit on top of the card and because they are the one thing here small
     * enough that drawing it per frame never needed a cache in the first place.
     */
    if (s_pip_count > 0) {
        s_px = pixels;
        s_pitch = pitch;
        for (y = 0; y < s_pip_count; y++) {
            const int d = s_pip[y].d;
            unsigned a = s_pip[y].ready ? breathe(now, 4600u, 188u, 255u) : 255u;
            uint32_t c = (s_pip[y].ready ? RC_OV_GOOD : RC_OV_WARN) & 0x00FFFFFFu;

            rounded_shape(s_pip[y].x, s_pip[y].y, d, d, d / 2, 0, (a << 24) | c);
        }
    }
    pump_input();

    s_ui_us = (unsigned)(((rc_tick() - s_ui_at) * 1000000u) / rc_tick_hz());
    if (s_ui_us > s_ui_worst_us)
        s_ui_worst_us = s_ui_us;
    s_frames++;

    {
        uint64_t at = rc_tick();

        rc_overlay_end_now(0, 0, s_scr_w, s_scr_h);
        /* The copy to video memory, measured apart from the drawing. It is 8 MB across a bus the PPE
         * is not fast at, it is not optional, and it is the half that no amount of work on the shapes
         * or the background would ever move. */
        s_vram_us = us_since(at);
        if (s_vram_us > s_vram_worst_us)
            s_vram_worst_us = s_vram_us;
        s_sum_vram += s_vram_us;
    }
    rc_video_flip();

    s_sum_wave += rc_wave_last_us();
    s_sum_ui += s_ui_us;
    s_sum_draw += us_since(s_draw_at);

    /*
     * THE INTERVAL, WHICH IS THE ONLY NUMBER THAT IS A FRAME RATE. Measured from the top of one draw to
     * the top of the next, and dropped when the shell went off to do something that does not draw.
     */
    if (s_frame_at != 0u) {
        unsigned gap = (unsigned)(((s_draw_at - s_frame_at) * 1000000u) / rc_tick_hz());

        if (gap < 200000u) {
            s_sum_frame += gap;
            s_intervals++;
            if (gap > s_worst_frame_us)
                s_worst_frame_us = gap;
        }
    }
    s_frame_at = s_draw_at;
}

/* ------------------------------------------------------------------------------------------------
 * Input
 */

/*
 * Buttons that went down since the last poll, with auto-repeat on the directions.
 *
 * EDGES RATHER THAN STATE because the loop runs at whatever rate it runs at: a menu driven by "is Cross
 * held" opens six things per press. Repeat is on the d-pad only - a held Cross must never fire twice,
 * which is how a confirmation gets past somebody.
 */
/*
 * EDGES ARRIVING BETWEEN DRAWS ARE KEPT, and this is why a press used to need repeating.
 *
 * The loop was: poll the pad once, act, draw. Drawing waits for the flip and then fills the screen, so
 * the pad was being READ about ten times a second - and rc_pad_read reports the pad's state right now,
 * not what it did in between. A press and release inside one of those gaps left no trace at all. It is
 * not a slow menu, it is a menu that never saw the button.
 *
 * So polling is separated from drawing. pump_input runs while waiting for the flip, at whatever rate
 * the loop can manage, and ORs every edge it sees into a pending mask that the next pass consumes. A
 * press cannot now fall between two looks no matter how long the drawing takes.
 */
static uint32_t s_pending;

static uint32_t poll_edges(void);

static void pump_input(void)
{
    s_pending |= poll_edges();
}

static uint32_t take_edges(void)
{
    uint32_t edges = s_pending | poll_edges();

    s_pending = 0u;
    return edges;
}

static uint32_t poll_edges(void)
{
    const uint32_t directions = HALYARD_PAD_DPAD_UP | HALYARD_PAD_DPAD_DOWN |
                                HALYARD_PAD_DPAD_LEFT | HALYARD_PAD_DPAD_RIGHT;
    halyard_input_state pad;
    uint32_t now, edges, held;
    uint64_t t = rc_time_ms();

    /*
     * A READ THAT FAILED IS NOT A PAD WITH NOTHING HELD. rc_pad_read returns 0 for a pad that has not
     * yet reported as well as for one that is gone, and treating that as all-buttons-up releases
     * whatever is actually held - so the next successful read looks like a fresh press of a button
     * nobody touched, and a held direction restarts its repeat delay on every hiccup. The last known
     * state is the honest answer: a button nobody has told us about is where it was.
     */
    if (!rc_pad_read(&pad))
        return 0u;
    now = pad.buttons;

    edges = now & ~s_prev_buttons;
    held = now & directions;

    if ((edges & directions) != 0u) {
        s_repeat_at = t + SH_REPEAT_DELAY_MS;
    } else if (held == 0u) {
        s_repeat_at = 0u;
    } else if (s_repeat_at != 0u && t >= s_repeat_at) {
        edges |= held;
        s_repeat_at = t + SH_REPEAT_RATE_MS;
    }

    s_prev_buttons = now;
    return edges;
}

/* Drops whatever is currently held, so returning from a sub-screen does not act on the press that left
 * it. Without this, Circle out of the settings screen arrives at the home screen as a fresh Circle. */
static void forget_held(void)
{
    halyard_input_state pad;

    s_prev_buttons = rc_pad_read(&pad) ? pad.buttons : 0u;
    s_repeat_at = 0u;
    s_pending = 0u;
}

/* ------------------------------------------------------------------------------------------------
 * The home screen
 */

/*
 * A CONSOLE'S NAME OUTLIVES ITS ADDRESS, so take the name whenever the network offers one.
 *
 * A record written before this port stored names has only an address in it, and an address is not
 * something anybody recognises on a menu - worse, it is the part most likely to be wrong later, because
 * a DHCP lease is not a promise. Discovery knows what each console calls itself; when one answers at an
 * address we already have keys for, that name is adopted and written down, and the card stops saying
 * 192.168 anything.
 *
 * AND ITS ID, WHICH IS THE PART THAT SURVIVES THE ADDRESS CHANGING. A record written before this port
 * stored one has a host and nothing else that identifies the console, so the first time it moves the
 * only way back is to pair it again. The id is learned here, quietly, on any broadcast where a paired
 * address answers - which means the repair happens on an ordinary visit to the menu, before it is
 * needed, rather than during the failure it prevents.
 *
 * A CONSOLE ALREADY MATCHED BY ID IS RE-ADDRESSED TOO. Once an id is on file, a console answering from
 * somewhere new is recognisable, and the record follows it. That is the other half of the problem the
 * previous version of this comment said it did not solve.
 */
static void adopt_names(void)
{
    int changed = 0;
    int i;

    for (i = 0; i < s_found_count && i < RC_DISCOVER_MAX; i++) {
        const char *found = s_found.console[i].host_name;
        const char *id = s_found.console[i].host_id;
        const char *addr = s_found.console[i].address;
        int at = halyard_pairing_set_find(&s_set, addr);

        /*
         * Matched by id FIRST, because that is the match that is still right when the address is not.
         * Falling back to the address covers the record that has no id yet - which is the record this
         * loop is about to give one to.
         */
        if (at < 0)
            at = halyard_pairing_set_find_id(&s_set, id);
        if (at < 0)
            continue;

        if (id[0] != '\0' && strcmp(s_set.console[at].console_id, id) != 0) {
            snprintf(s_set.console[at].console_id, sizeof(s_set.console[at].console_id), "%s", id);
            rc_log("shell: learned a paired console's own id from the network\n");
            changed = 1;
        }
        if (addr[0] != '\0' && strcmp(s_set.console[at].host, addr) != 0) {
            snprintf(s_set.console[at].host, sizeof(s_set.console[at].host), "%s", addr);
            rc_log("shell: a paired console moved - its record now points at where it answered\n");
            changed = 1;
        }
        if (found[0] != '\0' && strcmp(s_set.console[at].name, found) != 0) {
            snprintf(s_set.console[at].name, sizeof(s_set.console[at].name), "%s", found);
            rc_log("shell: learned a paired console's name from the network\n");
            changed = 1;
        }
    }
    if (changed) {
        s_dirty = 1;
        save_record();
    }
}

static void search(void)
{
    int i;

    rc_menu_reset(&s_menu, "Ripcord", "Searching for consoles...");
    draw(0);

    s_found_count = rc_discover(SH_DISCOVER_MS, &s_found);
    s_searched = 1;
    adopt_names();
    rc_log("shell: %d console(s) answered\n", s_found_count);
    for (i = 0; i < s_found_count; i++) {
        /* The name and the state, never the address - a log leaves this console and that line is the
         * one that would carry somebody's network into it. */
        rc_log("shell:   \"%s\" %s\n", s_found.console[i].host_name,
               s_found.console[i].is_awake ? "ready" : "in standby");
    }
}

/* Where a discovered console sits in the paired list, or -1 when this PS3 has no keys for it. */
static int paired_index_of(int found_index)
{
    return halyard_pairing_set_find(&s_set, s_found.console[found_index].address);
}

/* What a paired console answered with, or -1 when nothing did. */
static int awake_state_of(const char *host)
{
    int i;

    for (i = 0; i < s_found_count && i < RC_DISCOVER_MAX; i++) {
        if (strcmp(s_found.console[i].address, host) == 0)
            return s_found.console[i].is_awake;
    }
    return -1;
}

static void build_home(void)
{
    char subtitle[RC_MENU_NOTE_MAX];
    char value[RC_MENU_VALUE_MAX];
    int unpaired = 0;
    int row, i;

    for (i = 0; i < s_found_count && i < RC_DISCOVER_MAX; i++) {
        if (paired_index_of(i) < 0)
            unpaired++;
    }

    if (s_set.count == 0)
        snprintf(subtitle, sizeof(subtitle), "%s",
                 s_searched && unpaired > 0
                     ? "Nothing paired yet - pick a console to link it"
                     : "No console is paired with this PS3 yet");
    else if (s_set.count == 1)
        snprintf(subtitle, sizeof(subtitle), "One console paired%s",
                 unpaired > 0 ? ", and another answered on this network" : "");
    else
        snprintf(subtitle, sizeof(subtitle), "%d consoles paired%s", s_set.count,
                 unpaired > 0 ? ", and more answered" : "");

    rc_menu_reset(&s_menu, "Ripcord", subtitle);
    s_cards = 1;

    /*
     * THE PAIRED CONSOLES FIRST, AND ALL OF THEM, whether or not they answered. One in standby in
     * another room is still a console this PS3 can wake and stream from, and a home screen that hides
     * what did not reply is a home screen that looks empty most of the time.
     */
    for (i = 0; i < s_set.count; i++) {
        const halyard_pairing_record *rec = &s_set.console[i];
        int awake = awake_state_of(rec->host);

        snprintf(value, sizeof(value), "%s",
                 (awake < 0) ? (s_searched ? "not seen" : "paired") : (awake ? "ready" : "standby"));
        row = rc_menu_add(&s_menu, SH_ID_PAIRED_BASE + i,
                          rec->name[0] != '\0' ? rec->name : rec->host, value,
                          (awake == 0) ? "In standby - Ripcord will wake it first"
                                       : "Stream from this console");
        rc_menu_set_tag(&s_menu, row, rec->is_ps5 ? "PS5" : "PS4");
    }

    /* Anything on the network this PS3 has no keys for. */
    for (i = 0; i < s_found_count && i < RC_DISCOVER_MAX; i++) {
        if (paired_index_of(i) >= 0)
            continue;
        snprintf(value, sizeof(value), "%s", s_found.console[i].is_awake ? "ready" : "standby");
        row = rc_menu_add(&s_menu, SH_ID_CONSOLE_BASE + i,
                          s_found.console[i].host_name[0] != '\0' ? s_found.console[i].host_name
                                                                  : "A PlayStation",
                          value, "Not paired yet - link this PS3 to it");
        rc_menu_set_tag(&s_menu, row, "NEW");
    }

    /*
     * ONE CARD FOR THE THING THERE IS NO CONSOLE FOR YET, and everything else behind START. The front
     * screen is for the one decision somebody came here to make; searching, settings and quitting are
     * not that decision and do not belong beside it.
     */
    (void)rc_menu_add(&s_menu, SH_ID_PAIR_NEW, "Pair a console", NULL,
                      "Link this PS3 to a console on your network");
}

/* Everything the home screen does not show, behind START. */
static void build_options(void)
{
    rc_menu_reset(&s_menu, "Options", NULL);
    s_cards = 0;
    (void)rc_menu_add(&s_menu, SH_ID_SEARCH, "Search the network", NULL,
                      "Ask every console on this network to answer");
    (void)rc_menu_add(&s_menu, SH_ID_PAIR_NEW, "Pair by address", NULL,
                      "Type the console's address yourself, if it did not answer");
    (void)rc_menu_add(&s_menu, SH_ID_SETTINGS, "Settings", NULL,
                      "Picture size, frame rate and how much bandwidth to ask for");
    (void)rc_menu_add(&s_menu, SH_ID_QUIT, "Quit to the XMB", NULL,
                      "Close Ripcord and go back to the menu");
    (void)rc_menu_add(&s_menu, SH_ID_BACK, "Back", NULL, "Return to your consoles");
}

/*
 * FORGETTING A CONSOLE IS CONFIRMED, because it cannot be undone from here: the keys it throws away are
 * the whole product of standing in front of that console reading a PIN off its screen, and getting them
 * back means doing it again. Triangle is one button, and one button is not enough between somebody and
 * a trip to another room.
 */
static int confirm_forget(int index)
{
    char question[RC_MENU_NOTE_MAX];
    const halyard_pairing_record *rec;
    int answered = 0;
    int forget = 0;

    if (index < 0 || index >= s_set.count)
        return 0;
    rec = &s_set.console[index];
    snprintf(question, sizeof(question), "Forget %s?",
             rec->name[0] != '\0' ? rec->name : rec->host);

    rc_menu_reset(&s_menu, "Forget this console", question);
    s_cards = 0;
    (void)rc_menu_add(&s_menu, SH_ID_FORGET_NO, "Keep it", NULL, "Go back and change nothing");
    (void)rc_menu_add(&s_menu, SH_ID_FORGET_YES, "Forget it", NULL,
                      "This PS3 will need its PIN again to stream from it");
    s_first_row = 0;
    forget_held();

    while (!answered) {
        uint32_t edges;

        sysUtilCheckCallback();
        /* The XMB asked us to go. Leave by the ordinary route so main()'s teardown runs - see
         * rc_platform_ps3.h on what happens to a program that does not. */
        if (rc_ps3_exit_requested())
            break;
        edges = take_edges();

        if (edges & HALYARD_PAD_DPAD_UP)
            (void)rc_menu_move(&s_menu, -1);
        if (edges & HALYARD_PAD_DPAD_DOWN)
            (void)rc_menu_move(&s_menu, 1);
        if (edges & s_enter) {
            forget = (rc_menu_selected_id(&s_menu) == SH_ID_FORGET_YES);
            answered = 1;
        }
        /* Circle is the safe answer, which is why "Keep it" is also the row the cursor starts on. */
        if (edges & s_back)
            answered = 1;

        draw(0);
    }

    if (forget) {
        rc_log("shell: forgetting a paired console (%d of %d)\n", index + 1, s_set.count);
        (void)halyard_pairing_set_remove(&s_set, index);
        s_dirty = 1;
        if (s_set.count > 0)
            save_record();
        else
            rc_log("shell: nothing is paired now - the record file is left as it is\n");
    }
    forget_held();
    return forget;
}

/* ------------------------------------------------------------------------------------------------
 * Settings
 *
 * EVERY CHOICE HERE IS ONE THIS CONSOLE WAS MEASURED AT. The lists are short on purpose: a setting
 * whose values are a free-form number is a setting somebody can put the console into a state nothing
 * was ever tested in, and the interesting range on this machine turned out to be narrow. DECODE.md
 * carries the measurements behind each one.
 */

typedef struct { int w, h; } sh_resolution;

/*
 * 1920x1080 IS DELIBERATELY ABSENT. cellVdec opens at level 4.2, whose decoded-picture buffer is not
 * large enough for 1080p at the reference counts the console encodes with - so asking for it produces
 * a decoder that opens and then fails on the first picture. An option that cannot work is worse than
 * no option, because the person who picks it concludes the client is broken.
 */
static const sh_resolution SH_RESOLUTIONS[] = { { 640, 360 }, { 960, 540 }, { 1280, 720 } };
static const int SH_BITRATES[] = { 2000, 4000, 6000, 8000, 10000, 15000, 20000, 25000, 30000 };
static const int SH_RATES[] = { 30, 60 };

#define SH_COUNT(a) ((int)(sizeof(a) / sizeof((a)[0])))

/* Steps through a list of ints and returns the new value. Wraps, like the cursor does. */
static int step_list(const int *values, int count, int current, int delta)
{
    int at = 0, i;

    for (i = 0; i < count; i++) {
        if (values[i] == current)
            at = i;
    }
    at += delta;
    if (at >= count)
        at = 0;
    else if (at < 0)
        at = count - 1;
    return values[at];
}

static void step_resolution(int delta)
{
    int at = SH_COUNT(SH_RESOLUTIONS) - 1, i;

    for (i = 0; i < SH_COUNT(SH_RESOLUTIONS); i++) {
        if (SH_RESOLUTIONS[i].w == s_settings.stream_width &&
            SH_RESOLUTIONS[i].h == s_settings.stream_height)
            at = i;
    }
    at += delta;
    if (at >= SH_COUNT(SH_RESOLUTIONS))
        at = 0;
    else if (at < 0)
        at = SH_COUNT(SH_RESOLUTIONS) - 1;
    s_settings.stream_width = SH_RESOLUTIONS[at].w;
    s_settings.stream_height = SH_RESOLUTIONS[at].h;
}

/* kbps as somebody would say it out loud. 20000 is "20 Mbps"; 2500 would be "2.5 Mbps". */
static void bitrate_text(int kbps, char *out, size_t size)
{
    if (kbps % 1000 == 0)
        snprintf(out, size, "%d Mbps", kbps / 1000);
    else
        snprintf(out, size, "%d.%d Mbps", kbps / 1000, (kbps % 1000) / 100);
}

static void refresh_settings_values(void)
{
    char text[RC_MENU_VALUE_MAX];
    int i;

    for (i = 0; i < s_menu.count; i++) {
        switch (s_menu.item[i].id) {
        case SH_ID_RESOLUTION:
            snprintf(text, sizeof(text), "%dx%d", s_settings.stream_width, s_settings.stream_height);
            break;
        case SH_ID_FPS:
            snprintf(text, sizeof(text), "%d fps", s_settings.fps);
            break;
        case SH_ID_BITRATE:
            bitrate_text(s_settings.stream_bitrate_kbps, text, sizeof(text));
            break;
        case SH_ID_SCALER:
            snprintf(text, sizeof(text), "%s", s_settings.hardware_scale ? "RSX" : "SPE cores");
            break;
        case SH_ID_SMOOTHING:
            snprintf(text, sizeof(text), "%s",
                     (s_settings.bilinear_upscale == 0) ? "Sharp"
                   : (s_settings.bilinear_upscale == 1) ? "Smooth" : "Smooth both ways");
            break;
        case SH_ID_DIAGNOSTICS:
            snprintf(text, sizeof(text), "%s", s_settings.diagnostics ? "On" : "Off");
            break;
        default:
            continue;
        }
        rc_menu_set_value(&s_menu, i, text);
    }
}

static void build_settings(void)
{
    int row;

    rc_menu_reset(&s_menu, "Settings", "Left and right change a setting");
    s_cards = 0;

    row = rc_menu_add(&s_menu, SH_ID_RESOLUTION, "Picture size", NULL,
                      "What to ask the console to encode. 1280x720 is this decoder's ceiling");
    rc_menu_set_adjustable(&s_menu, row, 1);
    row = rc_menu_add(&s_menu, SH_ID_FPS, "Frame rate", NULL,
                      "60 is measured good here; 30 is the one to try if the picture breaks up");
    rc_menu_set_adjustable(&s_menu, row, 1);
    row = rc_menu_add(&s_menu, SH_ID_BITRATE, "Bandwidth", NULL,
                      "Asking for more than 20 Mbps makes this decoder drop whole pictures");
    rc_menu_set_adjustable(&s_menu, row, 1);
    row = rc_menu_add(&s_menu, SH_ID_SCALER, "Scaling", NULL,
                      "The RSX does it in 113 us a frame; the SPE cores take 3,204");
    rc_menu_set_adjustable(&s_menu, row, 1);
    row = rc_menu_add(&s_menu, SH_ID_SMOOTHING, "Smoothing", NULL,
                      "Costs nothing on the RSX. Smooth both ways is too slow for 60 fps");
    rc_menu_set_adjustable(&s_menu, row, 1);
    row = rc_menu_add(&s_menu, SH_ID_DIAGNOSTICS, "Diagnostics overlay", NULL,
                      "Frame rate and loss over the picture. Options and Create toggles it mid-stream");
    rc_menu_set_adjustable(&s_menu, row, 1);
    /*
     * NO TYPEFACE ROW, and that is a finding rather than an oversight. The atlas is built once, when
     * the overlay is prepared, which happens before this screen can be reached - so a control here
     * would move a field in the record and change nothing anyone could see until the next launch. A
     * setting that appears to do nothing is worse than an absent one.
     */
    (void)rc_menu_add(&s_menu, SH_ID_BACK, "Done", NULL, "Save these and go back");
    refresh_settings_values();
}

/* Left or right on the selected row. Returns 1 if something changed. */
static int adjust(int delta)
{
    const rc_menu_item *item = rc_menu_selected(&s_menu);

    if (item == NULL || !item->adjustable)
        return 0;

    switch (item->id) {
    case SH_ID_RESOLUTION:   step_resolution(delta); break;
    case SH_ID_FPS:          s_settings.fps = step_list(SH_RATES, SH_COUNT(SH_RATES), s_settings.fps,
                                                      delta); break;
    case SH_ID_BITRATE:      s_settings.stream_bitrate_kbps =
                                 step_list(SH_BITRATES, SH_COUNT(SH_BITRATES),
                                           s_settings.stream_bitrate_kbps, delta); break;
    case SH_ID_SCALER:       s_settings.hardware_scale = !s_settings.hardware_scale; break;
    case SH_ID_SMOOTHING:    s_settings.bilinear_upscale =
                                 (s_settings.bilinear_upscale + (delta > 0 ? 1 : 2)) % 3; break;
    case SH_ID_DIAGNOSTICS:  s_settings.diagnostics = !s_settings.diagnostics; break;
    default:                 return 0;
    }
    refresh_settings_values();
    s_dirty = 1;
    return 1;
}

static void run_settings(void)
{
    int running = 1;

    build_settings();
    s_first_row = 0;
    forget_held();

    while (running) {
        uint32_t edges;

        sysUtilCheckCallback();
        /* The XMB asked us to go. Leave by the ordinary route so main()'s teardown runs - see
         * rc_platform_ps3.h on what happens to a program that does not. */
        if (rc_ps3_exit_requested())
            break;
        edges = take_edges();

        if (edges & HALYARD_PAD_DPAD_UP)
            (void)rc_menu_move(&s_menu, -1);
        if (edges & HALYARD_PAD_DPAD_DOWN)
            (void)rc_menu_move(&s_menu, 1);
        if (edges & HALYARD_PAD_DPAD_LEFT)
            (void)adjust(-1);
        if (edges & HALYARD_PAD_DPAD_RIGHT)
            (void)adjust(1);
        if (edges & s_enter) {
            if (rc_menu_selected_id(&s_menu) == SH_ID_BACK)
                running = 0;
            else
                (void)adjust(1);
        }
        if (edges & s_back)
            running = 0;

        draw(0);
    }

    /*
     * SAVED ON THE WAY OUT, both ways out. Circle is "go back", not "discard" - there is no
     * cancel here to discard to, and a settings screen that silently throws away what was just
     * changed is the more surprising of the two behaviours.
     */
    save_record();
}

/* ------------------------------------------------------------------------------------------------ */

/* Returns 1 if the person asked to leave. */
static int run_options(const char *const *dirs, int dir_count)
{
    int running = 1;
    int quit = 0;

    build_options();
    s_first_row = 0;
    forget_held();

    while (running) {
        uint32_t edges;

        sysUtilCheckCallback();
        /* The XMB asked us to go. Leave by the ordinary route so main()'s teardown runs - see
         * rc_platform_ps3.h on what happens to a program that does not. */
        if (rc_ps3_exit_requested())
            break;
        edges = take_edges();

        if (edges & HALYARD_PAD_DPAD_UP)
            (void)rc_menu_move(&s_menu, -1);
        if (edges & HALYARD_PAD_DPAD_DOWN)
            (void)rc_menu_move(&s_menu, 1);

        if (edges & s_enter) {
            switch (rc_menu_selected_id(&s_menu)) {
            case SH_ID_SEARCH:
                search();
                build_options();
                (void)rc_menu_select_id(&s_menu, SH_ID_SEARCH);
                forget_held();
                break;
            case SH_ID_PAIR_NEW:
                (void)rc_pair_run(NULL, NULL, NULL);
                load_record(dirs, dir_count);
                running = 0;
                break;
            case SH_ID_SETTINGS:
                run_settings();
                build_options();
                (void)rc_menu_select_id(&s_menu, SH_ID_SETTINGS);
                forget_held();
                break;
            case SH_ID_QUIT:
                quit = 1;
                running = 0;
                break;
            default:
                running = 0;
                break;
            }
        }
        if ((edges & s_back) || (edges & HALYARD_PAD_OPTIONS))
            running = 0;

        if (running)
            draw(0);
    }
    return quit;
}

rc_shell_action rc_shell_run(const char *const *dirs, int dir_count)
{
    rc_shell_action action = RC_SHELL_QUIT;
    int running = 1;

    rc_log("shell: opening\n");
    if (!rc_pad_open())
        rc_log("shell: no pad - the menu cannot be driven\n");

    /*
     * The overlay may already be prepared, in which case this is a no-op. Asked for anyway: the shell
     * is the first thing on the screen and has nothing to draw with until it exists.
     */
    rc_overlay_set(1);
    if (!rc_overlay_prepared()) {
        /*
         * NOTHING TO DRAW ON. Said rather than returned silently - a shell that cannot draw looks
         * exactly like a shell that was never called, and the difference is the whole diagnosis.
         */
        rc_log("shell: the overlay is not prepared - going straight to the connect path\n");
        return RC_SHELL_CONNECT;
    }

    read_enter_button();
    /*
     * A BASELINE BEFORE THE MENU HAS DRAWN ANYTHING, and its pair is taken at close. rc_thermal.h is
     * explicit that this costs about 14 ms a read and must never be called from a frame path; these are
     * the two calls it describes, one on either side of everything being measured. The question it
     * answers is the one that decides how far this shell is allowed to go: whether a menu that redraws
     * the screen sixty times a second is a thing somebody's living room can hear.
     */
    rc_thermal_reset(&s_thermal);
    rc_thermal_sample(&s_thermal);
    rc_wave_open();
    s_accent = rc_wave_accent();
    load_record(dirs, dir_count);

    /*
     * ASKED ONCE ON THE WAY IN. Without this the home screen opens saying "paired" and "not seen" about
     * consoles that are sitting there awake, and the only way to find out otherwise is to go into the
     * options and ask - which is a thing to do about a screen whose whole job is to tell you.
     *
     * It costs the broadcast's deadline before the first card appears, and `search` puts "Searching for
     * consoles" on the television first, so the wait is something happening rather than a blank screen.
     */
    search();
    build_home();
    forget_held();

    while (running) {
        uint32_t edges;
        int id;

        sysUtilCheckCallback();
        /* The XMB asked us to go. Leave by the ordinary route so main()'s teardown runs - see
         * rc_platform_ps3.h on what happens to a program that does not. */
        if (rc_ps3_exit_requested())
            break;
        edges = take_edges();

        /* A row of cards moves sideways. Up and down are accepted too, because somebody will press
         * them and doing nothing at all reads as the menu being stuck. */
        if (edges & (HALYARD_PAD_DPAD_LEFT | HALYARD_PAD_DPAD_UP))
            (void)rc_menu_move(&s_menu, -1);
        if (edges & (HALYARD_PAD_DPAD_RIGHT | HALYARD_PAD_DPAD_DOWN))
            (void)rc_menu_move(&s_menu, 1);

        id = rc_menu_selected_id(&s_menu);

        if (edges & s_enter) {
            if (id >= SH_ID_PAIRED_BASE && id < SH_ID_PAIRED_BASE + HALYARD_PAIRING_MAX_CONSOLES) {
                /*
                 * CHOSEN, WRITTEN DOWN, AND ONLY THEN CONNECTED. rc_connect reads the file rather than
                 * taking an argument, so the choice has to reach the file before this returns -
                 * otherwise picking the second console streams from the first.
                 */
                if (s_set.selected != id - SH_ID_PAIRED_BASE) {
                    halyard_pairing_set_select(&s_set, id - SH_ID_PAIRED_BASE);
                    s_dirty = 1;
                }
                save_record();
                action = RC_SHELL_CONNECT;
                running = 0;
            } else if (id >= SH_ID_CONSOLE_BASE && id < SH_ID_CONSOLE_BASE + RC_DISCOVER_MAX) {
                int found = id - SH_ID_CONSOLE_BASE;

                /* Its address and its name are already known, so the one question a broadcast can
                 * answer is not asked again. */
                (void)rc_pair_run(s_found.console[found].address, s_found.console[found].host_name,
                                    s_found.console[found].host_id);
                load_record(dirs, dir_count);
                build_home();
                forget_held();
            } else if (id == SH_ID_PAIR_NEW) {
                (void)rc_pair_run(NULL, NULL, NULL);
                load_record(dirs, dir_count);
                build_home();
                forget_held();
            }
        }

        /*
         * TRIANGLE FORGETS, and only on a console this PS3 actually has keys for. On any other card it
         * does nothing at all rather than the nearest destructive thing.
         */
        if ((edges & HALYARD_PAD_TRIANGLE) && id >= SH_ID_PAIRED_BASE &&
            id < SH_ID_PAIRED_BASE + HALYARD_PAIRING_MAX_CONSOLES && s_set.count > 0) {
            (void)confirm_forget(id - SH_ID_PAIRED_BASE);
            build_home();
            forget_held();
        }

        /* START opens everything the front screen deliberately does not show. */
        if (edges & HALYARD_PAD_OPTIONS) {
            if (run_options(dirs, dir_count))
                running = 0;
            build_home();
            forget_held();
        }

        if (running)
            draw(s_set.count > 0);
    }

    if (s_intervals > 0u && s_frames > 0u) {
        unsigned mean = (unsigned)(s_sum_frame / s_intervals);
        unsigned n = s_layer_builds ? s_layer_builds : 1u;

        rc_thermal_sample(&s_thermal);
        rc_log("shell: %u frame(s) at %dx%d - %u us a frame (worst %u) = %u fps\n",
               s_frames, s_scr_w, s_scr_h, mean, s_worst_frame_us,
               mean > 0u ? 1000000u / mean : 0u);
        rc_log("shell:   of which draw %u us: background %u, drawing %u, to video memory %u,"
               " waiting for the flip %u\n",
               (unsigned)(s_sum_draw / s_frames), (unsigned)(s_sum_wave / s_frames),
               (unsigned)(s_sum_ui / s_frames), (unsigned)(s_sum_vram / s_frames),
               (unsigned)(s_sum_wait / s_frames));
        rc_log("shell:   worst single drawing pass %u us, worst copy %u us\n",
               s_ui_worst_us, s_vram_worst_us);
        rc_log("shell:   of the drawing: background rows %u us, compositing the interface %u us\n",
               (unsigned)((s_ticks_rows * 1000000u) / rc_tick_hz() / s_frames),
               (unsigned)((s_ticks_comp * 1000000u) / rc_tick_hz() / s_frames));
        rc_log("shell:   the interface layer was rebuilt %u time(s), %u us each\n",
               s_layer_builds, s_layer_builds ? (unsigned)(s_sum_layer / s_layer_builds) : 0u);
        rc_log("shell:   inside a rebuild: header %u, cards %u (glow %u, card shapes %u),"
               " hints %u; text everywhere %u\n",
               (unsigned)(s_sum_header / n), (unsigned)(s_sum_cards / n),
               (unsigned)(s_sum_glow / n), (unsigned)(s_sum_shape / n),
               (unsigned)(s_sum_hints / n), (unsigned)(s_sum_text / n));
        if (s_thermal.available)
            rc_log("shell:   Cell %u.%u C on the way in, %u.%u C on the way out; RSX %u.%u -> %u.%u\n",
                   s_thermal.cell_first / 10u, s_thermal.cell_first % 10u,
                   s_thermal.cell_last / 10u, s_thermal.cell_last % 10u,
                   s_thermal.rsx_first / 10u, s_thermal.rsx_first % 10u,
                   s_thermal.rsx_last / 10u, s_thermal.rsx_last % 10u);
    }
    rc_log("shell: closing - %s\n", action == RC_SHELL_CONNECT ? "connecting" : "quitting");
    return action;
}
