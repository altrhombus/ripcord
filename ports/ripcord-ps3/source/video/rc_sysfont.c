/* See rc_sysfont.h - especially the note on coverage and on why the drawn font stays. */
#include "rc_sysfont.h"

#include <font/font.h>
#include <font/fontFT.h>
#include <font/fontset.h>
#include <sysmodule/sysmodule.h>

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <ppu-asm.h>

/*
 * PSL1GHT's font.h declares this as `fontontSetScalePixel` - a typo in the header, against an export
 * that is spelled correctly in the library. Declared here so the call links; changing the SDK header
 * would be a change nobody else building this tree would have.
 */
extern s32 fontSetScalePixel(font *f, f32 w, f32 h);

/*
 * THE REVISIONED ENTRY POINTS, and the plain ones are why b241 saw no new font.
 *
 * fontInit already does this for the base library - it asks the stub for its revision flags and calls
 * fontInitializeWithRevision. PSL1GHT provides no such wrapper for the FreeType half, so the obvious
 * fontInitLibraryFreeType gets called instead, with no revision at all, and the firmware answers
 * 0x80540002. Both halves have to be told which revision of the interface they are being called
 * through, and the flags come from the stubs rather than from a constant anyone here could write down.
 */
extern void fontFTGetStubRevisionFlags(u64 *revisionFlags);
extern void fontGetStubRevisionFlags(u64 *revisionFlags);
extern s32 fontInitializeWithRevision(u64 revision, fontConfig *config);
extern s32 fontEnd(void);
extern s32 fontEndLibrary(const fontLibrary *lib);
extern s32 fontInitLibraryFreeTypeWithRevision(u64 revision, fontLibraryConfigFT *config,
                                               const fontLibrary **lib);

/*
 * THE ALLOCATOR THE FONT LIBRARY CALLS BACK INTO, and it needs 32-BIT DESCRIPTORS.
 *
 * cellFont is a PRX. A function pointer handed to it is called from 32-bit code, and GCC's ELFv1
 * descriptor is 64-bit - the same mismatch that made vdecClosure.fn silently never fire in b149, which
 * took four builds to find because nothing reports it. It is written down here rather than rediscovered
 * a third time: ANY callback given to a firmware library on this platform needs __build_opd32.
 *
 * The callbacks themselves are the C library's, because the font library's appetite is its own business
 * and a fixed arena would only move the failure to whichever glyph overran it.
 */
static uint32_t s_opd_malloc[2] __attribute__((aligned(8)));
static uint32_t s_opd_free[2] __attribute__((aligned(8)));
static uint32_t s_opd_realloc[2] __attribute__((aligned(8)));
static uint32_t s_opd_calloc[2] __attribute__((aligned(8)));

static void *font_malloc(void *object, u32 size)
{
    (void)object;
    return malloc(size);
}

static void font_free(void *object, void *ptr)
{
    (void)object;
    free(ptr);
}

static void *font_realloc(void *object, void *p, u32 size)
{
    (void)object;
    return realloc(p, size);
}

static void *font_calloc(void *object, u32 num, u32 size)
{
    (void)object;
    return calloc(num, size);
}

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
static char s_policy[48] = "?";
static char s_face[32] = "?";
static char s_revmode[16] = "?";

static int fail(const char *step, int rc)
{
    snprintf(s_status, sizeof(s_status), "%s refused (0x%08X)", step, (unsigned)rc);
    return 0;
}

/*
 * ONE ATTEMPT AT THE WHOLE CHAIN, at a given interface revision.
 *
 * It is the whole chain rather than one call because the mismatch does not surface where it is made.
 * PSL1GHT's fontInit initialises with the BASE font stub's revision alone and never mentions the
 * FreeType stub, so the font system comes up without FreeType declared - and the FT library then
 * initialises happily on its own revision, and the refusal lands two calls later at
 * fontCreateRenderer with 0x80540002 and a buffering policy that had nothing to do with it. b248 swept
 * all five policies and was refused five times, which is what said the policy was never the question.
 *
 * So the revision is swept instead, and each attempt is torn down before the next: initialising twice
 * without an intervening fontEnd is its own refusal, and would hide the answer behind a second fault.
 */
static int attempt(u64 revision, float pixels)
{
    fontConfig config;
    fontLibraryConfigFT ftconfig;
    fontRendererConfigFT rconfig;
    fontType type;
    fontHorizontalLayout layout;
    s32 rc;

    /*
     * THREE MODULES, AND THE ORDER MATTERS. FREETYPE underpins FONTFT which underpins FONT; loading
     * them the other way round returns success and then fails at the first glyph.
     */
    if (!s_modules) {
        if (sysModuleLoad(SYSMODULE_FREETYPE) != 0)
            return fail("SYSMODULE_FREETYPE", 0);
        if (sysModuleLoad(SYSMODULE_FONTFT) != 0)
            return fail("SYSMODULE_FONTFT", 0);
        if (sysModuleLoad(SYSMODULE_FONT) != 0)
            return fail("SYSMODULE_FONT", 0);
        s_modules = 1;
    }

    memset(&config, 0, sizeof(config));
    config.fileCache.buffer = s_file_cache;
    config.fileCache.size = sizeof(s_file_cache);
    config.userFontEntryMax = 0;
    config.userFontEntries = NULL;
    config.flags = 0;
    rc = fontInitializeWithRevision(revision, &config);
    if (rc != 0)
        return fail("fontInitializeWithRevision", rc);

    fontLibraryConfigFT_initialize(&ftconfig);
    ftconfig.memoryIF.object = NULL;
    ftconfig.memoryIF.malloc_func =
        (fontMallocCallback)(uintptr_t)(u32)__build_opd32(font_malloc, s_opd_malloc);
    ftconfig.memoryIF.free_func =
        (fontFreeCallback)(uintptr_t)(u32)__build_opd32(font_free, s_opd_free);
    ftconfig.memoryIF.realloc_func =
        (fontReallocCallback)(uintptr_t)(u32)__build_opd32(font_realloc, s_opd_realloc);
    ftconfig.memoryIF.calloc_func =
        (fontCallocCallback)(uintptr_t)(u32)__build_opd32(font_calloc, s_opd_calloc);

    /* The same revision the font system came up under - a library initialised against a different one
     * than the system that will be asked to render through it is exactly the mismatch above. */
    rc = fontInitLibraryFreeTypeWithRevision(revision, &ftconfig, &s_lib);
    if (rc != 0)
        return fail("fontInitLibraryFreeTypeWithRevision", rc);

    /*
     * THE RENDERER'S BUFFERING POLICY IS SWEPT, NOT GUESSED.
     *
     * b246 got past the library and was refused here with the same 0x80540002, and the policy is five
     * numbers with no documented relationship between them - whether the buffer may be supplied or must
     * be allocated, whether expandSize may be zero, whether maxSize may equal initSize. That is four or
     * five plausible shapes and, taken one per build, four or five hardware runs to walk.
     *
     * So they are all tried here and the one that is accepted is reported, exactly as rsxInit's sizes
     * and vdecQueryAttr's levels were swept rather than reasoned about. The cost is a few refused calls
     * during start-up; the alternative is a week of single-hypothesis builds.
     */
    {
        static const struct {
            const char *what;
            int own_buffer;
            u32 init, max, expand, reset;
        } kPolicies[] = {
            { "library-allocated, expanding",  0, 512u * 1024u, 2048u * 1024u, 128u * 1024u,
              512u * 1024u },
            { "library-allocated, fixed",      0, 512u * 1024u,  512u * 1024u, 0u, 0u },
            { "library-allocated, all zero",   0, 0u, 0u, 0u, 0u },
            { "caller-supplied, fixed",        1, sizeof(s_renderer_buf), sizeof(s_renderer_buf), 0u,
              0u },
            { "caller-supplied, expanding",    1, sizeof(s_renderer_buf), sizeof(s_renderer_buf),
              64u * 1024u, 128u * 1024u },
        };
        unsigned i;

        rc = -1;
        for (i = 0u; i < sizeof(kPolicies) / sizeof(kPolicies[0]); i++) {
            memset(&rconfig, 0, sizeof(rconfig));
            rconfig.bufferingPolicy.buffer = kPolicies[i].own_buffer ? s_renderer_buf : NULL;
            rconfig.bufferingPolicy.initSize = kPolicies[i].init;
            rconfig.bufferingPolicy.maxSize = kPolicies[i].max;
            rconfig.bufferingPolicy.expandSize = kPolicies[i].expand;
            rconfig.bufferingPolicy.resetSize = kPolicies[i].reset;

            rc = fontCreateRenderer(s_lib, (fontRendererConfig *)&rconfig, &s_renderer);
            if (rc == 0) {
                snprintf(s_policy, sizeof(s_policy), "%s", kPolicies[i].what);
                break;
            }
        }
        if (rc != 0)
            return fail("fontCreateRenderer (every policy refused)", rc);
    }

    /*
     * AND THE FONTSET IS SWEPT FOR THE SAME REASON. New Rodin latin is the XMB's own face and the one
     * worth having, but which sets a given firmware actually carries is not something this can know,
     * and a missing set refuses exactly like a malformed argument does. The preferred one is tried
     * first and the rest are fallbacks in descending order of how much they look like the menus.
     */
    {
        static const struct {
            const char *what;
            u32 type;
        } kSets[] = {
            { "New Rodin gothic latin", FONT_TYPE_NEWRODIN_GOTHIC_LATIN_SET },
            { "New Rodin gothic JP",    FONT_TYPE_NEWRODIN_GOTHIC_JP_SET },
            { "Rodin sans serif",       FONT_TYPE_RODIN_SANS_SERIF_LATIN },
            { "Matisse serif",          FONT_TYPE_MATISSE_SERIF_LATIN },
        };
        unsigned i;

        rc = -1;
        for (i = 0u; i < sizeof(kSets) / sizeof(kSets[0]); i++) {
            type.type = kSets[i].type;
            type.map = 0u;
            rc = fontOpenFontset(s_lib, &type, &s_font);
            if (rc == 0) {
                snprintf(s_face, sizeof(s_face), "%s", kSets[i].what);
                break;
            }
        }
        if (rc != 0)
            return fail("fontOpenFontset (every fontset refused)", rc);
    }

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
    return 1;
}

int rc_sysfont_open(float pixels)
{
    u64 base_rev = 0ull, ft_rev = 0ull;
    unsigned m;

    if (s_ready)
        return 1;

    fontGetStubRevisionFlags(&base_rev);
    fontFTGetStubRevisionFlags(&ft_rev);

    {
        /*
         * The OR is tried first because it is what the API's shape implies - a system that will be
         * asked to render through FreeType should be told so at initialisation. The others follow
         * because that is an inference rather than anything documented, and being wrong about it
         * should cost a refused call rather than another trip to the console.
         */
        const u64 modes[3] = { base_rev | ft_rev, ft_rev, base_rev };
        static const char *const names[3] = { "base|FT", "FT only", "base only" };

        for (m = 0u; m < 3u; m++) {
            if (attempt(modes[m], pixels)) {
                snprintf(s_revmode, sizeof(s_revmode), "%s", names[m]);
                snprintf(s_status, sizeof(s_status), "ready - %s, %d px, ascent %d, %s, rev %s",
                         s_face, (int)pixels, s_ascent, s_policy, s_revmode);
                return 1;
            }
            /* Torn down before the next: initialising twice without this is its own refusal, and
             * would hide the answer behind a second fault. */
            if (s_lib != NULL) {
                (void)fontEndLibrary(s_lib);
                s_lib = NULL;
            }
            (void)fontEnd();
        }
    }
    return 0;
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
