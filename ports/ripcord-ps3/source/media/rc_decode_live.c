/*
 * ripcord-ps3 - which decoder the live path uses, and the fallback when the first one will not open.
 *
 * The seam is the one in rc_decode_probe.h and the shape is the one CLAUDE.md describes for the crypto:
 * two implementations behind one interface, chosen once, with the rest of the pipeline unaware of which
 * answered. cellVdec on the SPEs is the decoder; openh264 on the PPE is what happens if it will not open.
 *
 * WHY A FALLBACK AT ALL, given cellVdec was validated against two other decoders before it was wired in:
 * the validation says its OUTPUT is correct, not that it will open on every machine, every firmware and
 * every stream size. Opening reserves 33 MB at level 31 and needs an SPE free. openh264 is already built
 * and already worked; keeping it costs a branch.
 *
 * The choice is reported rather than silent, because a quiet fall back to the decoder that manages 4 fps
 * at 720p would look exactly like the hardware one being slow, and that is a bad afternoon.
 */
#include "rc_decode_probe.h"
#include "rc_decode_vdec.h"

static rc_decode_backend s_backend;
static int s_hint_width = 1280;
static int s_hint_height = 720;
static rc_decode_picture_fn s_sink;
static void *s_sink_ctx;
static rc_decode_picture_rgb_fn s_rgb_sink;
static void *s_rgb_sink_ctx;

void rc_decode_live_hint(int width, int height)
{
    if (width > 0 && height > 0) {
        s_hint_width = width;
        s_hint_height = height;
    }
}

rc_decode_backend rc_decode_live_backend(void)
{
    return s_backend;
}

const char *rc_decode_live_backend_name(void)
{
    switch (s_backend) {
    case RC_DECODE_BACKEND_VDEC:     return "cellVdec (SPE)";
    case RC_DECODE_BACKEND_OPENH264: return "openh264 (PPE)";
    default:                         return "none";
    }
}

/*
 * Only the hardware decoder can produce packed RGB, so this is forwarded to it and never to openh264 -
 * which is why it is set BEFORE opening: the format is chosen at open, and the fallback ignores it.
 */
void rc_decode_live_set_rgb_sink(rc_decode_picture_rgb_fn fn, void *ctx)
{
    s_rgb_sink = fn;
    s_rgb_sink_ctx = ctx;
    rc_decode_vdec_set_rgb_sink(fn, ctx);
}

int rc_decode_live_open(void)
{
    rc_decode_vdec_set_rgb_sink(s_rgb_sink, s_rgb_sink_ctx);
    if (rc_decode_vdec_open(s_hint_width, s_hint_height)) {
        s_backend = RC_DECODE_BACKEND_VDEC;
        if (s_sink != NULL)
            rc_decode_vdec_set_sink(s_sink, s_sink_ctx);
        return 1;
    }
    if (rc_decode_openh264_open()) {
        s_backend = RC_DECODE_BACKEND_OPENH264;
        if (s_sink != NULL)
            rc_decode_openh264_set_sink(s_sink, s_sink_ctx);
        return 1;
    }
    s_backend = RC_DECODE_BACKEND_NONE;
    return 0;
}

/* Remembered as well as forwarded: the port sets the sink after opening, but nothing promises that. */
void rc_decode_live_set_sink(rc_decode_picture_fn fn, void *ctx)
{
    s_sink = fn;
    s_sink_ctx = ctx;
    if (s_backend == RC_DECODE_BACKEND_VDEC)
        rc_decode_vdec_set_sink(fn, ctx);
    else if (s_backend == RC_DECODE_BACKEND_OPENH264)
        rc_decode_openh264_set_sink(fn, ctx);
}

int rc_decode_live_feed(const uint8_t *access_unit, size_t length, rc_decode_live_stats *stats)
{
    if (s_backend == RC_DECODE_BACKEND_VDEC)
        return rc_decode_vdec_feed(access_unit, length, stats);
    if (s_backend == RC_DECODE_BACKEND_OPENH264)
        return rc_decode_openh264_feed(access_unit, length, stats);
    return 0;
}

void rc_decode_live_close(void)
{
    if (s_backend == RC_DECODE_BACKEND_VDEC)
        rc_decode_vdec_close();
    else if (s_backend == RC_DECODE_BACKEND_OPENH264)
        rc_decode_openh264_close();
    s_backend = RC_DECODE_BACKEND_NONE;
}
