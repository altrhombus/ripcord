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
static int s_ascent;
static float s_size;

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

static void note_metrics(void)
{
    /*
     * The ascent is asked for rather than taken as a fraction of the em, because it is not one: a
     * face's baseline sits where the face says it does, and at every size. FreeType reports it in
     * 26.6 fixed point, hence the shift.
     */
    s_ascent = (int)(s_face->size->metrics.ascender >> 6);
}

int rc_sysfont_open(float pixels)
{
    unsigned i;
    int rc;

    if (s_ready)
        return 1;

    rc = FT_Init_FreeType(&s_lib);
    if (rc != 0)
        return fail("FT_Init_FreeType", rc);

    for (i = 0u; i < sizeof(kFaces) / sizeof(kFaces[0]); i++) {
        rc = FT_New_Face(s_lib, kFaces[i].path, 0, &s_face);
        if (rc == 0) {
            rc = FT_Set_Pixel_Sizes(s_face, 0, (FT_UInt)pixels);
            if (rc == 0) {
                s_size = pixels;
                note_metrics();
                s_ready = 1;
                snprintf(s_status, sizeof(s_status), "ready - %s, %d px, ascent %d",
                         kFaces[i].what, (int)pixels, s_ascent);
                return 1;
            }
            FT_Done_Face(s_face);
            s_face = NULL;
        }
    }

    FT_Done_FreeType(s_lib);
    s_lib = NULL;
    return fail("FT_New_Face (no system face opened)", rc);
}

const char *rc_sysfont_status(void)
{
    return s_status;
}

int rc_sysfont_ascent(void)
{
    return s_ascent;
}

void rc_sysfont_set_size(float pixels)
{
    if (!s_ready || pixels == s_size)
        return;
    if (FT_Set_Pixel_Sizes(s_face, 0, (FT_UInt)pixels) != 0)
        return;
    s_size = pixels;
    note_metrics();
}

int rc_sysfont_render(unsigned char *cov, int cov_w, int cov_h, int x, int baseline,
                      const char *text)
{
    int pen = x;
    int i;

    if (!s_ready || text == NULL)
        return 0;

    for (i = 0; text[i] != '\0'; i++) {
        FT_GlyphSlot g;
        int gx, gy;

        if (FT_Load_Char(s_face, (FT_ULong)(unsigned char)text[i], FT_LOAD_RENDER) != 0)
            continue;
        g = s_face->glyph;

        /*
         * A NULL buffer MEASURES instead of drawing. Right-aligning or centring a proportional run
         * needs its width before it is placed, and there is no per-glyph width table to add up the way
         * there is for the drawn font - the face has to be asked.
         */
        if (cov != NULL && g->bitmap.buffer != NULL) {
            int ox = pen + g->bitmap_left;
            int oy = baseline - g->bitmap_top;

            for (gy = 0; gy < (int)g->bitmap.rows; gy++) {
                const unsigned char *src = g->bitmap.buffer + (size_t)gy * (size_t)g->bitmap.pitch;
                int py = oy + gy;

                if (py < 0 || py >= cov_h)
                    continue;
                for (gx = 0; gx < (int)g->bitmap.width; gx++) {
                    int px = ox + gx;

                    if (px < 0 || px >= cov_w)
                        continue;
                    /*
                     * Taken as the MAXIMUM rather than assigned. Glyphs in a run can overlap by a pixel
                     * where one's bearing reaches under its neighbour, and overwriting there would cut
                     * a notch out of whichever was drawn first.
                     */
                    if (src[gx] > cov[(size_t)py * (size_t)cov_w + (size_t)px])
                        cov[(size_t)py * (size_t)cov_w + (size_t)px] = src[gx];
                }
            }
        }

        /* 26.6 fixed point, like every metric FreeType reports. */
        pen += (int)(g->advance.x >> 6);
    }

    return pen - x;
}
