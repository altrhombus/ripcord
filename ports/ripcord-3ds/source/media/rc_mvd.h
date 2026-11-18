/*
 * ripcord-3ds - the MVD hardware H.264 decoder.
 *
 * MVD is the New 3DS's video decode block, the one piece of hardware that makes this project possible at
 * all: software H.264 on an ARM11 at 804 MHz is not a real option. It is reached through libctru's
 * `mvd:STD` service, and this file is a thin wrapper over the sequence the official devkitPro MVD example
 * proves works - init, configure, feed NAL units, render.
 *
 * NEW 3DS ONLY, AND THAT IS NOT A SOFT REQUIREMENT. The original 3DS has no MVD block. rc_mvd_init()
 * checks and refuses rather than failing later in a way that looks like a decode bug.
 *
 * ANNEX-B IN, ONE NAL UNIT AT A TIME. rc_mvd_decode_frame() splits the demuxer's whole-frame Annex-B
 * buffer on its start codes and feeds each unit in turn. That split is why the parameter sets from
 * STREAM_INFO matter: they arrive as their own NAL units prepended to the first IDR, and MVD answers
 * MVD_STATUS_PARAMSET to them - a "success, but no picture yet" that must not be treated as a failure.
 *
 * THE INPUT BUFFER MUST BE LINEAR AND FLUSHED. MVD reads through physical addresses, so each NAL unit is
 * copied into an aligned linear staging buffer and GSPGPU_FlushDataCache'd before the call. Feeding it a
 * pointer into an ordinary heap or stack buffer produces silence or garbage rather than an error.
 *
 * VERIFIED ON HARDWARE: MVD has decoded video from a real PS5 on a real console, and six defects were
 * found in this path by building an offline replay harness and measuring. The .c records them.
 */
#ifndef RC_MVD_H
#define RC_MVD_H

#include <stddef.h>
#include <stdint.h>

#include "../util/rc_profile.h"

typedef struct {
    int ready;              /* 0 until init succeeds; every other entry point is a no-op until then */
    int input_width;
    int input_height;
    long frames_rendered;
    long nal_units_fed;
    long param_sets;        /* parameter sets and other non-picture units MVD accepted */
    long process_errors;    /* mvdstdProcessVideoFrame rejected the unit */
    long oversized_units;   /* never offered to MVD - larger than the staging buffer */
    /*
     * How many times ProcessNALUnit returned each status. OK (0x17000) means the unit was consumed,
     * FRAMEREADY (0x17003) means a picture actually exists to render.
     */
    long status_ok;         /* 0x17000 */
    long status_frameready; /* 0x17003 */
    long status_nalucproc;  /* 0x17007 */
    long status_other;
    long render_errors;     /* the unit decoded but mvdstdRenderVideoFrame failed */
    long frames_skipped;    /* not offered to MVD - see rc_mvd_decode_frame's keyframe gating */
    unsigned first_process_error; /* the FIRST distinct code of each kind, not the last: the last is */
    unsigned first_render_error;  /* whatever happened to fail most recently, which is rarely the cause */
} rc_mvd;

/*
 * Brings up MVD for H.264 decode at `input_width` x `input_height`. Returns 1 on success, 0 on failure
 * (not a New 3DS, service unavailable, or no linear memory), having left `out` unusable but safe to
 * pass to the other calls.
 */
int rc_mvd_init(rc_mvd *out, int input_width, int input_height, int rgb565);

/*
 * Decodes one whole Annex-B frame: splits it into NAL units, feeds them to MVD and renders the picture
 * into this module's own output buffer. Without a scale thread the picture is also scaled into the top
 * screen's current framebuffer here; with one it is handed to the worker. Returns 1 if a picture was
 * produced, 0 otherwise (parameter sets only, skipped while awaiting a keyframe, or an error - check
 * `process_errors` and `render_errors`). The caller owns the swap; see rc_mvd_scale_begin.
 */
int rc_mvd_decode_frame(rc_mvd *mvd, const uint8_t *annexb, size_t length, int is_keyframe);

/*
 * Tells the decoder that video was lost. If skip-until-keyframe is enabled (see
 * rc_mvd_set_skip_until_keyframe; default off), rc_mvd_decode_frame() then skips whatever it is handed
 * until the next keyframe arrives and counts it in `frames_skipped`. Otherwise this does nothing.
 *
 * H.264 inter-frames are differences against earlier frames, so once one is missing every frame that
 * references it cannot be decoded properly until the next IDR.
 */
void rc_mvd_signal_loss(rc_mvd *mvd);

/* Whether a loss should drop every frame until the next keyframe. Default off - see the .c for the
 * hardware measurement that made 'correct' the wrong trade here. */
void rc_mvd_set_skip_until_keyframe(int enabled);

/* 800x240 top-screen mode. 640-wide source then UPSCALES horizontally instead of losing 61% of its
 * columns, which is the single biggest lever on text legibility. See the .c. Call before streaming. */
void rc_mvd_set_widescreen(int enabled);

/*
 * Starts the scale worker on a spare core, given the mask from rc_profile_probe_cores. Returns 1 if the
 * scale now runs off the receive thread, 0 if it stayed inline (which still works, just slower).
 */
int rc_mvd_start_scale_thread(unsigned core_mask);

/*
 * Scale-and-present, split so the caller never blocks on it:
 *   rc_mvd_scale_begin    - 1 if a new picture was handed to the scaler (or drawn inline), else 0
 *   rc_mvd_scale_complete - 1 once the framebuffer is written and flushed; the caller then swaps
 * Call at DISPLAY rate, not decode rate - the decoder outruns a 60 Hz screen whenever it is keeping up.
 */
int rc_mvd_scale_begin(rc_mvd *mvd);

/*
 * Percentage of the last blitted frame that differs from its own mean luminance. A real picture is
 * mostly detail (typically 40-80%); a cleared buffer with motion painted into it is a few percent, and
 * crucially still shows a large min-to-max SPREAD - which is why spread was the wrong thing to measure.
 */
unsigned rc_mvd_last_detail(void);
int rc_mvd_scale_complete(void);

/* Average the two source rows the vertical squeeze straddles, rather than picking one. */
void rc_mvd_set_smoothing(int enabled);

/* Attaches a profile so the decode and scale stages are timed alongside the network ones. Optional -
 * NULL, or never calling this, simply leaves those stages unmeasured. */
void rc_mvd_set_profile(rc_profile *profile);


/* Shuts MVD down. Safe on an uninitialised or already-closed instance. */
void rc_mvd_exit(rc_mvd *mvd);

#endif /* RC_MVD_H */
