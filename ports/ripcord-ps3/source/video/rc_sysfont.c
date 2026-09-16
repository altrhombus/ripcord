/* See rc_sysfont.h - especially the note on coverage and on why the drawn font stays. */
#include "rc_sysfont.h"

#include <font/font.h>
#include <font/fontFT.h>
#include <font/fontset.h>
#include <sysmodule/sysmodule.h>

#include <stdio.h>
#include <string.h>

/*
 * PSL1GHT's font.h declares this as `fontontSetScalePixel` - a typo in the header, against an export
 * that is spelled correctly in the library. Declared here so the call links; changing the SDK header
 * would be a change nobody else building this tree would have.
 */
extern s32 fontSetScalePixel(font *f, f32 w, f32 h);

/*
 * The cache the font library is told it may use. cellFont wants a buffer for glyph expansion and a
 * separate one for the renderer; both are sized from the SDK's own sample values rather than derived,
 * because nothing here knows what the library does with them and guessing smaller is how a font
 * library starts failing on the character you did not test.
 */
static uint32_t s_file_cache[1024 * 16];
static uint8_t  s_renderer_buf[1024 * 512] __attribute__((aligned(16)));

static const fontLibrary *s_lib;
static fontRenderer s_renderer;
static font s_font;
static int s_ready;
static int s_modules;
static char s_status[96] = "not tried";
static int s_ascent;
static float s_size;

static int fail(const char *step, int rc)
{
    snprintf(s_status, sizeof(s_status), "%s refused (0x%08X)", step, (unsigned)rc);
    return 0;
}

int rc_sysfont_open(float pixels)
{
    fontConfig config;
    fontLibraryConfigFT ftconfig;
    fontRendererConfigFT rconfig;
    fontType type;
    fontHorizontalLayout layout;
    s32 rc;

    if (s_ready)
        return 1;

    /*
     * THREE MODULES, AND THE ORDER MATTERS. FREETYPE underpins FONTFT which underpins FONT; loading
     * them the other way round returns success and then fails at the first glyph.
     */
    if (sysModuleLoad(SYSMODULE_FREETYPE) != 0)
        return fail("SYSMODULE_FREETYPE", 0);
    if (sysModuleLoad(SYSMODULE_FONTFT) != 0)
        return fail("SYSMODULE_FONTFT", 0);
    if (sysModuleLoad(SYSMODULE_FONT) != 0)
        return fail("SYSMODULE_FONT", 0);
    s_modules = 1;

    memset(&config, 0, sizeof(config));
    config.fileCache.buffer = s_file_cache;
    config.fileCache.size = sizeof(s_file_cache);
    config.userFontEntryMax = 0;
    config.userFontEntries = NULL;
    config.flags = 0;
    rc = fontInit(&config);
    if (rc != 0)
        return fail("fontInit", rc);

    fontLibraryConfigFT_initialize(&ftconfig);
    rc = fontInitLibraryFreeType(&ftconfig, &s_lib);
    if (rc != 0)
        return fail("fontInitLibraryFreeType", rc);

    memset(&rconfig, 0, sizeof(rconfig));
    rconfig.bufferingPolicy.buffer = s_renderer_buf;
    rconfig.bufferingPolicy.initSize = sizeof(s_renderer_buf);
    rconfig.bufferingPolicy.maxSize = sizeof(s_renderer_buf);
    rconfig.bufferingPolicy.expandSize = 0;
    rconfig.bufferingPolicy.resetSize = 0;
    rc = fontCreateRenderer(s_lib, (fontRendererConfig *)&rconfig, &s_renderer);
    if (rc != 0)
        return fail("fontCreateRenderer", rc);

    /* New Rodin latin - the XMB's own face. The map selects the character map within the set; 0 is the
     * default one and is what every sample uses. */
    type.type = FONT_TYPE_NEWRODIN_GOTHIC_LATIN_SET;
    type.map = 0u;
    rc = fontOpenFontset(s_lib, &type, &s_font);
    if (rc != 0)
        return fail("fontOpenFontset", rc);

    rc = fontBindRenderer(&s_font, &s_renderer);
    if (rc != 0)
        return fail("fontBindRenderer", rc);

    rc = fontSetScalePixel(&s_font, pixels, pixels);
    if (rc != 0)
        return fail("fontSetScalePixel", rc);

    /* The ascent is asked for rather than assumed to be some fraction of the em, because it is not: a
     * face's baseline sits where the face says it does. */
    if (fontGetHorizontalLayout(&s_font, &layout) == 0)
        s_ascent = (int)(layout.baseLineY + 0.5f);
    else
        s_ascent = (int)(pixels * 0.8f);

    s_size = pixels;
    s_ready = 1;
    snprintf(s_status, sizeof(s_status), "ready (New Rodin, %d px, ascent %d)",
             (int)pixels, s_ascent);
    return 1;
}

const char *rc_sysfont_status(void)
{
    return s_status;
}

int rc_sysfont_ascent(void)
{
    return s_ascent;
}

/*
 * Changes the em size on the open face. One font instance rather than one per size: the library
 * rescales on demand and a second instance would double the cache for a panel that uses exactly two
 * sizes. The ascent moves with it, which is why it is re-asked rather than scaled arithmetically - a
 * face's baseline is where the face says it is, at every size.
 */
void rc_sysfont_set_size(float pixels)
{
    fontHorizontalLayout layout;

    if (!s_ready || pixels == s_size)
        return;
    if (fontSetScalePixel(&s_font, pixels, pixels) != 0)
        return;
    s_size = pixels;
    if (fontGetHorizontalLayout(&s_font, &layout) == 0)
        s_ascent = (int)(layout.baseLineY + 0.5f);
}

int rc_sysfont_render(unsigned char *cov, int cov_w, int cov_h, int x, int baseline,
                      const char *text)
{
    fontRenderSurface surface;
    float pen = (float)x;
    int i;

    if (!s_ready || text == NULL)
        return 0;

    /*
     * A NULL buffer MEASURES instead of drawing. Right-aligning or centring a proportional run needs
     * its width before it is placed, and asking the metrics for it is the only way to know - there is
     * no per-glyph width table to add up the way there is for the drawn font.
     */
    if (cov != NULL)
        fontRenderSurfaceInit(&surface, cov, cov_w, 1, cov_w, cov_h);

    for (i = 0; text[i] != '\0'; i++) {
        fontGlyphMetrics metrics;
        u32 code = (u32)(unsigned char)text[i];

        if (cov != NULL) {
            if (fontRenderCharGlyphImageHorizontal(&s_font, code, &surface, pen, (float)baseline,
                                                   &metrics, NULL) != 0)
                continue;
        } else if (fontGetCharGlyphMetrics(&s_font, code, &metrics) != 0) {
            continue;
        }
        pen += metrics.horizontal.advance;
    }

    return (int)(pen - (float)x + 0.5f);
}
