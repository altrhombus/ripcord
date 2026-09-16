/*
 * ripcord-ps3 - the console's own typeface, read as a font file rather than through cellFont.
 *
 * WHY NOT cellFont. The obvious route is fontOpenFontset with FONT_TYPE_NEWRODIN_GOTHIC_LATIN_SET,
 * which asks the firmware for the same face the XMB uses. It refuses to produce a renderer on this
 * console through PSL1GHT's bindings - six hardware runs eliminated the interface revision, the
 * renderer's buffering policy, the callback descriptors and a missing module, and all fifteen
 * combinations returned an identical 0x80540002. DECODE.md records it in full.
 *
 * WHAT THIS DOES INSTEAD. The faces are ordinary TrueType files in /dev_flash/data/font, and b255
 * confirmed a packaged homebrew can read them: 38,388 bytes and a 00 01 00 00 magic, through both the
 * lv2 syscalls and newlib. SCE-PS3-RD-R-LATIN.TTF is Rodin Regular, which is what the menus are set
 * in. The FreeType portlib parses it. Nothing is redistributed - the file stays on the console and is
 * read at runtime exactly as cellFont would have read it - and FreeType has a reference and a tutorial
 * where cellFont has a header with a typo in it.
 *
 * COVERAGE, NOT PIXELS. FT_LOAD_RENDER gives an 8-bit antialiased bitmap, which the caller composites
 * in whatever colour it wants. That is what makes the antialiasing real, and it is why the overlay's
 * panel lives in main memory: blending needs the destination read, and a Cell read from RSX memory is
 * roughly two orders of magnitude slower than a write.
 *
 * EVERY GLYPH IS RASTERISED ONCE, AT START-UP, AND NEVER AGAIN. b256 did it the obvious way - call
 * FT_LOAD_RENDER whenever a character is wanted - and cost the session: a 1,483 ms stall inside one
 * panel rebuild, 3,237 of 3,534 frames dropped for overrun, 299 keyframes requested and 2 fps on
 * screen. Three things compounded.
 *
 *   The rebuild runs on the DECODE THREAD, which also drains the socket and feeds the decoder, so a
 *   stall there is not slow drawing - it is a stopped pipeline.
 *
 *   Measuring a run rasterised it. Right-aligned text is measured and then drawn, so every one of
 *   those glyphs was rendered twice and half the work thrown away.
 *
 *   And the panel uses two sizes, so FT_Set_Pixel_Sizes was called several times per rebuild, which
 *   rescales the face underneath all of it.
 *
 * So the printable ASCII is rendered into an atlas at each size when the overlay opens - before the
 * stream starts, off the decode thread - and everything after that is a memcpy and a table lookup.
 * Drawing does not touch FreeType at all.
 */
#include "rc_sysfont.h"

#include <ft2build.h>
#include FT_FREETYPE_H

#include <stdio.h>
#include <string.h>

static FT_Library s_lib;
static FT_Face s_face;
static int s_ready;
static char s_status[112] = "not tried";
static const char *s_face_name = "?";

/*
 * THE ATLAS. Two sizes, printable ASCII, rendered once. A glyph is its coverage plus the four numbers
 * needed to place it: where its box sits relative to the pen and baseline, and how far the pen moves
 * afterwards. All of them are what FreeType reported at the size it was rendered for - none is derived
 * from another, because a face's metrics are its own at every size.
 */
#define RC_SF_FIRST   32
#define RC_SF_LAST    126
#define RC_SF_COUNT   (RC_SF_LAST - RC_SF_FIRST + 1)
#define RC_SF_SIZES   2
#define RC_SF_ARENA   (192u * 1024u)

typedef struct {
    short w, h;
    short left, top;
    short advance;
    unsigned int at;     /* offset into the size's arena */
} rc_sf_glyph;

static rc_sf_glyph s_glyph[RC_SF_SIZES][RC_SF_COUNT];
static unsigned char s_arena[RC_SF_SIZES][RC_SF_ARENA];
static unsigned s_arena_used[RC_SF_SIZES];
static int s_px[RC_SF_SIZES];
/*
 * THE WIDEST DIGIT'S ADVANCE, per size, which is what makes proportional numerals hold still.
 *
 * A proportional face gives '1' a narrower advance than '8', so a figure that ticks from 11 to 88
 * changes width and drags everything after it - which is the whole reason the panel was setting
 * numbers in a second, monospaced face. It does not have to: a digit drawn into a fixed cell the width
 * of the widest digit, centred in it, is a tabular figure, and real faces ship exactly this as a
 * separate set. Synthesising it costs one number per size and lets the panel be set in one typeface.
 */
static short s_digit_cell[RC_SF_SIZES];
static int s_ascent_px[RC_SF_SIZES];
static int s_slot;           /* which size rc_sysfont_set_size selected */
static int s_tabular;        /* digits in fixed cells - see s_digit_cell */
static int s_sizes_built;

static int fail(const char *step, int rc)
{
    snprintf(s_status, sizeof(s_status), "%s refused (%d)", step, rc);
    return 0;
}

/*
 * The faces worth having, in order. Rodin Regular first because it IS the XMB's Latin face; New Rodin
 * JP after it because that set carries Latin too and is present on firmware where the Latin-only file
 * might not be. Swept rather than assumed for the reason the fontset sweep existed: which files a given
 * firmware carries is not knowable from here, and a missing one opens exactly like a corrupt one.
 */
static const struct {
    const char *what;
    const char *path;
} kFaces[] = {
    { "Rodin Regular",     "/dev_flash/data/font/SCE-PS3-RD-R-LATIN.TTF" },
    { "New Rodin (JP set)", "/dev_flash/data/font/SCE-PS3-NR-R-JPN.TTF" },
    { "Seurat Regular",    "/dev_flash/data/font/SCE-PS3-SR-R-LATIN.TTF" },
};

/*
 * Renders the printable ASCII at one size into that size's arena. Called only from rc_sysfont_open, so
 * it happens during session setup rather than in the middle of a frame.
 */
static int build_size(int slot, int pixels)
{
    unsigned code;

    if (FT_Set_Pixel_Sizes(s_face, 0, (FT_UInt)pixels) != 0)
        return 0;

    s_px[slot] = pixels;
    /* Asked for rather than taken as a fraction of the em, because it is not one: a face's baseline
     * sits where the face says it does, at every size. 26.6 fixed point, hence the shift. */
    s_ascent_px[slot] = (int)(s_face->size->metrics.ascender >> 6);
    s_arena_used[slot] = 0u;

    for (code = RC_SF_FIRST; code <= RC_SF_LAST; code++) {
        rc_sf_glyph *g = &s_glyph[slot][code - RC_SF_FIRST];
        FT_GlyphSlot slotp;
        unsigned need;
        int row;

        memset(g, 0, sizeof(*g));
        if (FT_Load_Char(s_face, (FT_ULong)code, FT_LOAD_RENDER) != 0)
            continue;
        slotp = s_face->glyph;

        g->advance = (short)(slotp->advance.x >> 6);
        g->left = (short)slotp->bitmap_left;
        g->top = (short)slotp->bitmap_top;
        g->w = (short)slotp->bitmap.width;
        g->h = (short)slotp->bitmap.rows;

        need = (unsigned)g->w * (unsigned)g->h;
        if (need == 0u || slotp->bitmap.buffer == NULL)
            continue;
        if (s_arena_used[slot] + need > RC_SF_ARENA) {
            /* Out of arena. The glyph keeps its advance so the run still spaces correctly and simply
             * draws nothing - a gap is a better failure than a wrong layout. */
            g->w = 0;
            g->h = 0;
            continue;
        }

        g->at = s_arena_used[slot];
        for (row = 0; row < g->h; row++) {
            memcpy(s_arena[slot] + g->at + (size_t)row * (size_t)g->w,
                   slotp->bitmap.buffer + (size_t)row * (size_t)slotp->bitmap.pitch,
                   (size_t)g->w);
        }
        s_arena_used[slot] += need;
    }

    {
        unsigned d;

        s_digit_cell[slot] = 0;
        for (d = (unsigned)'0'; d <= (unsigned)'9'; d++) {
            short a = s_glyph[slot][d - RC_SF_FIRST].advance;

            if (a > s_digit_cell[slot])
                s_digit_cell[slot] = a;
        }
    }
    return 1;
}

int rc_sysfont_open(float body_px, float heading_px)
{
    unsigned i;
    int rc = -1;

    if (s_ready)
        return 1;

    rc = FT_Init_FreeType(&s_lib);
    if (rc != 0)
        return fail("FT_Init_FreeType", rc);

    for (i = 0u; i < sizeof(kFaces) / sizeof(kFaces[0]); i++) {
        rc = FT_New_Face(s_lib, kFaces[i].path, 0, &s_face);
        if (rc == 0) {
            s_face_name = kFaces[i].what;
            break;
        }
    }
    if (s_face == NULL) {
        FT_Done_FreeType(s_lib);
        s_lib = NULL;
        return fail("FT_New_Face (no system face opened)", rc);
    }

    /*
     * The two sizes the panel uses. They are built here rather than on demand because on demand means
     * on the decode thread, and that is what b256 cost a session to establish.
     */
    /*
     * BOTH SIZES ARE GIVEN, not one and a ratio. The caller derives them from the display - a panel
     * designed at 1080p is set at two thirds of that on a 720p screen - and a multiplier here would
     * have quietly ignored the second of them. b265 built its atlas at a hardcoded 26 and 37 while the
     * layout asked for 24 and 34, which happened to work at 1080p and would have left the type at full
     * size on every smaller display.
     */
    if (!build_size(0, (int)body_px) || !build_size(1, (int)heading_px)) {
        FT_Done_Face(s_face);
        s_face = NULL;
        FT_Done_FreeType(s_lib);
        s_lib = NULL;
        return fail("FT_Set_Pixel_Sizes while building the atlas", 0);
    }
    s_sizes_built = 2;
    s_slot = 0;

    /*
     * FreeType is finished with. Everything after this is a table lookup and a memcpy, so the face and
     * the library are closed rather than left open holding their caches - a megabyte of arena for the
     * glyphs is the whole cost of the overlay's text from here on.
     */
    FT_Done_Face(s_face);
    s_face = NULL;
    FT_Done_FreeType(s_lib);
    s_lib = NULL;

    s_ready = 1;
    snprintf(s_status, sizeof(s_status),
             "ready - %s, %d and %d px, ascent %d, atlas %u+%u bytes",
             s_face_name, s_px[0], s_px[1], s_ascent_px[0],
             s_arena_used[0], s_arena_used[1]);
    return 1;
}

const char *rc_sysfont_status(void)
{
    return s_status;
}

int rc_sysfont_ascent(void)
{
    return s_ready ? s_ascent_px[s_slot] : 0;
}

/*
 * Digits in fixed cells, for a column of figures that must not shuffle as the figures change. Sticky
 * like the size, because measuring and drawing are two calls and they have to agree.
 */
void rc_sysfont_set_tabular(int on)
{
    s_tabular = on;
}

void rc_sysfont_set_size(float pixels)
{
    int want;

    if (!s_ready)
        return;
    /* Nearest of the two built, rather than rebuilding: a size that was not prepared cannot be drawn
     * without going back to FreeType, which is the thing this exists to avoid. */
    want = ((int)pixels >= (s_px[0] + s_px[1]) / 2) ? 1 : 0;
    if (want < s_sizes_built)
        s_slot = want;
}

int rc_sysfont_render(unsigned char *cov, int cov_w, int cov_h, int x, int baseline,
                      const char *text)
{
    int pen = x;
    int i;

    if (!s_ready || text == NULL)
        return 0;

    for (i = 0; text[i] != '\0'; i++) {
        unsigned char c = (unsigned char)text[i];
        const rc_sf_glyph *g;
        int gx, gy;

        if (c < RC_SF_FIRST || c > RC_SF_LAST)
            c = '?';
        g = &s_glyph[s_slot][c - RC_SF_FIRST];

        /*
         * A NULL buffer MEASURES, and measuring is now free. In b256 it went through FT_LOAD_RENDER
         * like everything else, so every right-aligned run was rasterised twice and half of it thrown
         * away - which is half of why a rebuild took 1,483 ms.
         */
        {
            int cell = 0;
            int pad = 0;

            if (s_tabular && c >= '0' && c <= '9') {
                cell = s_digit_cell[s_slot];
                /* Centred in its cell rather than left-aligned: a narrow 1 hugging the left of a wide
                 * cell reads as a gap in the number, which is worse than the jitter this replaces. */
                pad = (cell - g->advance) / 2;
            }

        if (cov != NULL && g->w > 0) {
            int ox = pen + pad + g->left;
            int oy = baseline - g->top;

            for (gy = 0; gy < g->h; gy++) {
                const unsigned char *src = s_arena[s_slot] + g->at + (size_t)gy * (size_t)g->w;
                int py = oy + gy;
                unsigned char *dst;

                if (py < 0 || py >= cov_h)
                    continue;
                dst = cov + (size_t)py * (size_t)cov_w;
                for (gx = 0; gx < g->w; gx++) {
                    int px = ox + gx;

                    if (px < 0 || px >= cov_w)
                        continue;
                    /*
                     * The MAXIMUM rather than an assignment. Glyphs in a run overlap by a pixel where
                     * one's bearing reaches under its neighbour, and overwriting there cuts a notch out
                     * of whichever was drawn first.
                     */
                    if (src[gx] > dst[px])
                        dst[px] = src[gx];
                }
            }
        }
            pen += (cell > 0) ? cell : g->advance;
        }
    }

    return pen - x;
}
