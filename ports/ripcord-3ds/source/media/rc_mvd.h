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
 * ANNEX-B IN, ONE NAL UNIT AT A TIME. `mvdstdProcessVideoFrame` takes a single NAL unit with its start
 * code already stripped, so rc_mvd_decode_frame() splits the demuxer's whole-frame Annex-B buffer on
 * 00 00 00 01 boundaries and feeds each unit in turn. That split is why the parameter sets from
 * STREAM_INFO matter: they arrive as their own NAL units prepended to the first IDR, and MVD answers
 * MVD_STATUS_PARAMSET to them - a "success, but no picture yet" that must not be treated as a failure.
 *
 * THE INPUT BUFFER MUST BE LINEAR AND FLUSHED. MVD reads through physical addresses, so each NAL unit is
 * copied into a linearAlloc'd staging buffer and GSPGPU_FlushDataCache'd before the call. Feeding it a
 * pointer into an ordinary heap or stack buffer produces silence or garbage rather than an error.
 *
 * UNVERIFIED ON HARDWARE. Everything above the decoder has now been confirmed against a real PS5, but
 * this file has never run: there is no MVD block on a build machine and no way to emulate one honestly.
 * Treat the output-dimension handling in particular as provisional - see the .c on why the example's
 * 240x400 framebuffer geometry is transposed relative to the 400x240 screen, and what that means for a
 * 640x360 source.
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
     * How many times ProcessNALUnit returned each status. This distinguishes the one thing never yet
     * checked: OK (0x17000) means the unit was consumed, FRAMEREADY (0x17003) means a picture actually
     * exists to render. This port has been calling RenderVideoFrame on both, following the devkitPro
     * example - and if FRAMEREADY never occurs, every render has been asking for a frame the decoder
     * does not have, which would explain a successful render producing no output.
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
int rc_mvd_init(rc_mvd *out, int input_width, int input_height);

/*
 * Decodes one whole Annex-B frame, splitting it into NAL units and rendering directly into the top
 * screen's current framebuffer. Returns 1 if a picture was produced, 0 otherwise (parameter sets only,
 * or an error - check `decode_errors`).
 *
 * Renders straight to the framebuffer rather than to an intermediate buffer, which is what the official
 * example does and avoids an ARM11-side rotate of every frame. The caller still owns the swap.
 */
int rc_mvd_decode_frame(rc_mvd *mvd, const uint8_t *annexb, size_t length, int is_keyframe);

/*
 * Tells the decoder that video was lost. Until the next keyframe arrives, rc_mvd_decode_frame() will
 * skip whatever it is handed and count it in `frames_skipped`.
 *
 * This is correctness before it is economy. H.264 inter-frames are differences against earlier frames,
 * so once one is missing every frame that references it is undecodable - feeding them to MVD produces a
 * guaranteed error per frame, and on the first hardware run that was most of them. Skipping also removes
 * the CPU cost of decoding frames that could never have produced a picture, which matters on a handheld
 * where that cost comes out of the same thread as the network receive loop.
 */
void rc_mvd_signal_loss(rc_mvd *mvd);

/* Attaches a profile so the decode and scale stages are timed alongside the network ones. Optional -
 * NULL, or never calling this, simply leaves those stages unmeasured. */
void rc_mvd_set_profile(rc_profile *profile);

/*
 * Switches to the next candidate output geometry and reports it. Returns the new candidate's index.
 *
 * WHY THIS EXISTS. Getting a decoded frame onto the screen has now cost many hardware runs, each testing
 * a single guess about MVD's output behaviour, and the remaining unknowns are all in the same small
 * space: whether the config's dimensions are in the picture's orientation or the framebuffer's
 * transposed one, whether input cropping makes a differently-sized output legal, and whether the
 * framebuffer is the only target that works. Those combine into a handful of configurations, and
 * testing them one build at a time is a poor use of a person standing in front of a console. This walks
 * them in a single session instead.
 */
int rc_mvd_next_candidate(rc_mvd *mvd);

/*
 * 1 if the current configuration has MVD write the framebuffer directly, 0 if it renders into our own
 * buffer for the CPU to scale.
 *
 * The caller needs this to decide whether to call gfxFlushBuffers before presenting. That flush pushes
 * the ARM11's cache out over the framebuffer, which is required when the CPU drew the frame and
 * DESTRUCTIVE when MVD did - it overwrites the hardware's output with whatever stale lines the CPU
 * happened to hold, which is black. That is a picture rendered successfully and then erased a
 * microsecond later, with nothing in any log to say so.
 */
int rc_mvd_renders_to_screen(const rc_mvd *mvd);

/* Shuts MVD down. Safe on an uninitialised or already-closed instance. */
void rc_mvd_exit(rc_mvd *mvd);

#endif /* RC_MVD_H */
