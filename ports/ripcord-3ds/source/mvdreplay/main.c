/*
 * ripcord-3ds - offline MVD replay: feed a known-good H.264 file to the decoder, with no console.
 *
 * WHY THIS EXISTS. Twelve hardware runs were spent on a grey picture, each one costing a full connect
 * flow against a real PS5 - discovery, session, senkusha, Takion, ECDH - to test one hypothesis about a
 * decoder. Then `dumpvideo=1` wrote the elementary stream to SD, ffmpeg decoded it on a PC, and it was
 * the PS5 home screen in full colour with legible text. That settled the fault: the console's stream is
 * good, this port's demuxer is good, and MVD alone turns a valid bitstream into a flat grey field.
 *
 * Once the input is known-good, the console is dead weight. This program reads that same video.264 off
 * the SD card and drives rc_mvd with it directly. No network, no keys, no session that times out, no
 * packet loss, and - the part that matters most - EVERY RUN IS IDENTICAL, so a change in the picture is
 * a change we made rather than a change in what the console happened to be showing.
 *
 *     ffmpeg -i video.264 frame%03d.png     # what the decoder is SUPPOSED to produce
 *
 * ACCESS UNITS, NOT NAL UNITS. The stream carries one slice per MTU: the first keyframe in our capture
 * is 21 IDR NAL units, and 2,213 NAL units make only 941 pictures. A picture therefore ends where the
 * next one begins, and the marker for that is `first_mb_in_slice == 0` in a slice header - not the
 * arrival of an SPS, which in this stream appears only 20 times in 941 pictures. Grouping on parameter
 * sets would put twenty pictures' worth of slices into one access unit and is exactly the sort of
 * plausible-but-wrong rule that has already cost this investigation several rounds.
 *
 * first_mb_in_slice is the first field of the slice header and is coded ue(v), so the value is zero
 * precisely when the byte after the NAL header has its top bit set.
 */
#include "../media/rc_mvd.h"
#include "util/rc_log.h"
#include "../util/rc_profile.h"
#include "util/rc_program_dir.h"

#include <3ds.h>

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* The capture the dump produces is 2 MB; give the reader room for a larger one without a realloc. */
#define REPLAY_CAPACITY (4u * 1024u * 1024u)

/* Static, not a local: the main thread's stack is 32 KB. */
static uint8_t s_stream[REPLAY_CAPACITY];
static size_t s_stream_length;
static rc_mvd s_mvd;
static rc_profile s_profile;
static uint64_t s_run_started;

/* Annex-B start code at `pos`? Returns its length (3 or 4), else 0. */
static size_t start_code_at(const uint8_t *d, size_t length, size_t pos)
{
    if (pos + 3 <= length && d[pos] == 0 && d[pos + 1] == 0 && d[pos + 2] == 1)
        return 3;
    if (pos + 4 <= length && d[pos] == 0 && d[pos + 1] == 0 && d[pos + 2] == 0 && d[pos + 3] == 1)
        return 4;
    return 0;
}

/* Does this NAL begin a new picture? True for a slice whose first_mb_in_slice is zero. */
static int starts_picture(const uint8_t *nal, size_t length)
{
    unsigned type;

    if (length < 2)
        return 0;
    type = (unsigned)(nal[0] & 0x1Fu);
    if (type != 1u && type != 5u)
        return 0;
    return (nal[1] & 0x80u) != 0u;
}

/*
 * Does this access unit contain an IDR slice (NAL type 5)?
 *
 * rc_mvd_init arms s_awaiting_keyframe, and rc_mvd_decode_frame drops every access unit until one
 * arrives marked as a keyframe. The first cut of this program passed 0 unconditionally, so every unit
 * was skipped and the screen stayed blank - no picture and no grey, which reads as "the replay is
 * broken" rather than "the replay is working and told you nothing". The connect flow gets this flag from
 * the demuxer; here it has to be read out of the bitstream.
 */
static int unit_is_keyframe(const uint8_t *unit, size_t length)
{
    size_t pos = 0;

    while (pos < length) {
        size_t sc = start_code_at(unit, length, pos);

        if (sc == 0) {
            pos++;
            continue;
        }
        if (pos + sc < length && (unit[pos + sc] & 0x1Fu) == 5u)
            return 1;
        pos += sc;
    }
    return 0;
}

/*
 * TWO FILES, SWITCHED AT RUNTIME - the CABAC/CAVLC A/B.
 *
 * The console encodes Main profile with entropy_coding_mode_flag = 1, i.e. CABAC. MVD is a fixed-function
 * block and whether it implements CABAC at all is a property of the silicon, not something this code can
 * configure - so it is the first thing to establish and it had never been checked.
 *
 * video_cavlc.264 is the same pictures re-encoded as Constrained Baseline: CAVLC, level 3.1, refs=1, no
 * B-frames, slices sized near the MTU like the console's. The ONLY meaningful difference is the entropy
 * coder. Switching between them without re-initialising MVD keeps the decoder in the same warm state
 * that produces "clean after a rewind, then blurring", so the comparison is like for like.
 *
 *     ffmpeg -i video.264 -c:v libx264 -profile:v baseline -level 3.1 -refs 1 -bf 0 \
 *            -b:v 700k -x264-params slice-max-size=1200 video_cavlc.264
 */
static const char *const kStreams[] = { "video.264", "video_cavlc.264" };
static int s_stream_index;
static const char *s_argv0;

/*
 * INTEGRITY CHECK ON LOAD - because a corrupted capture looked like a decoder bug for several rounds.
 *
 * The first capture this tool was fed had 332 frame_num discontinuities across 941 pictures: 35% of its
 * pictures referenced a frame that was not in the file, because the dump was written to SD on the
 * receive thread and the resulting stall cost 2168 packets. ffmpeg conceals gaps silently, so it decoded
 * and looked perfect on a PC, and was trusted as known-good input while the decoder was blamed.
 *
 * frame_num increments by one per reference picture; a jump means a picture is missing. Counting them
 * takes milliseconds and turns "is my test input sound?" from an assumption into a printed number.
 */
typedef struct { const uint8_t *b; size_t len; size_t pos; } bitreader;

static unsigned br_u1(bitreader *r)
{
    unsigned v;
    if ((r->pos >> 3) >= r->len)
        return 0;
    v = (unsigned)((r->b[r->pos >> 3] >> (7 - (r->pos & 7))) & 1u);
    r->pos++;
    return v;
}

static unsigned br_u(bitreader *r, unsigned n)
{
    unsigned v = 0;
    while (n-- > 0)
        v = (v << 1) | br_u1(r);
    return v;
}

static unsigned br_ue(bitreader *r)
{
    unsigned z = 0;
    while (z < 32u && br_u1(r) == 0u)
        z++;
    return (1u << z) - 1u + br_u(r, z);
}

/* Strips emulation-prevention bytes into `out`, returning the length written. */
static size_t strip_rbsp(const uint8_t *in, size_t len, uint8_t *out, size_t out_max)
{
    size_t i = 0, n = 0;

    while (i < len && n < out_max) {
        if (i + 2 < len && in[i] == 0 && in[i + 1] == 0 && in[i + 2] == 3) {
            out[n++] = in[i];
            if (n < out_max)
                out[n++] = in[i + 1];
            i += 3;
        } else {
            out[n++] = in[i++];
        }
    }
    return n;
}

static void report_stream_integrity(void)
{
    uint8_t rbsp[64];
    size_t pos = 0;
    unsigned log2_max_frame_num = 0;
    long pictures = 0, gaps = 0;
    int have_prev = 0;
    unsigned prev = 0;

    while (pos < s_stream_length) {
        size_t sc = start_code_at(s_stream, s_stream_length, pos);
        size_t nal_start, next;
        unsigned type;

        if (sc == 0) {
            pos++;
            continue;
        }
        nal_start = pos + sc;
        if (nal_start >= s_stream_length)
            break;
        type = (unsigned)(s_stream[nal_start] & 0x1Fu);

        next = nal_start;
        while (next < s_stream_length && start_code_at(s_stream, s_stream_length, next) == 0)
            next++;

        if (type == 7u) {
            bitreader r;
            size_t n = strip_rbsp(s_stream + nal_start + 1,
                                  (next - nal_start - 1 < sizeof(rbsp)) ? next - nal_start - 1
                                                                        : sizeof(rbsp),
                                  rbsp, sizeof(rbsp));
            r.b = rbsp; r.len = n; r.pos = 0;
            (void)br_u(&r, 8); (void)br_u(&r, 8); (void)br_u(&r, 8); /* profile, constraints, level */
            (void)br_ue(&r);                                          /* sps id */
            log2_max_frame_num = br_ue(&r) + 4u;
        } else if ((type == 1u || type == 5u) && log2_max_frame_num > 0u) {
            bitreader r;
            size_t n = strip_rbsp(s_stream + nal_start + 1,
                                  (next - nal_start - 1 < sizeof(rbsp)) ? next - nal_start - 1
                                                                        : sizeof(rbsp),
                                  rbsp, sizeof(rbsp));
            r.b = rbsp; r.len = n; r.pos = 0;
            if (br_ue(&r) == 0u) { /* first_mb_in_slice == 0 -> a new picture */
                unsigned fn;
                (void)br_ue(&r); (void)br_ue(&r);  /* slice_type, pps id */
                fn = br_u(&r, log2_max_frame_num);
                pictures++;
                if (have_prev && type != 5u) {
                    unsigned expect = (prev + 1u) & ((1u << log2_max_frame_num) - 1u);
                    if (fn != expect && fn != prev)
                        gaps++;
                }
                prev = fn;
                have_prev = 1;
            }
        }
        pos = next;
    }

    rc_log("  integrity: %ld picture(s), %s%ld frame_num gap(s)\x1b[0m%s\n", pictures,
        gaps == 0 ? "\x1b[32m" : "\x1b[31m", gaps,
        gaps == 0 ? " - stream is intact" : " - THIS CAPTURE IS DAMAGED, decoder faults may be its fault");
}

static size_t load_stream(const char *argv0)
{
    char path[512];
    FILE *f;
    size_t got;

    rc_program_dir(argv0, path, sizeof(path));
    strncat(path, kStreams[s_stream_index], sizeof(path) - strlen(path) - 1);

    f = fopen(path, "rb");
    if (f == NULL) {
        rc_log("\x1b[31mFAIL\x1b[0m could not open %s\n", path);
        rc_log("        produce one with dumpvideo=1 in pairing.txt, then copy it beside this .3dsx\n");
        return 0;
    }
    got = fread(s_stream, 1, sizeof(s_stream), f);
    fclose(f);

    rc_log("loaded \x1b[36m%s\x1b[0m (%u KB, slot %d)\n", kStreams[s_stream_index],
        (unsigned)(got / 1024u), s_stream_index);
    return got;
}

/*
 * ONE ACCESS UNIT PER CALL, driven from the main loop at vblank rate.
 *
 * The first cut decoded all 941 pictures in one batch and swapped buffers once at the end. A pass takes
 * 18.8 seconds, so every blit landed in the back buffer and the screen sat unchanged throughout - which
 * looked like "the decoder produced nothing" when in fact it had produced 940 pictures. Stepping one
 * unit per frame makes this a player: what is on screen is the unit just decoded.
 */
static size_t s_cursor;
static long s_units, s_pictures;
static long s_flat_frames, s_first_good = -1;
static unsigned s_detail_min = 100, s_detail_max;

/* Finds the extent of the access unit starting at `from`. Returns its end offset. */
static size_t unit_end_from(size_t from)
{
    size_t pos = from;
    size_t pending = (size_t)-1;
    int have_slice = 0;

    while (pos < s_stream_length) {
        size_t sc = start_code_at(s_stream, s_stream_length, pos);
        size_t nal_start, next;
        unsigned type;
        int is_vcl;

        if (sc == 0) {
            pos++;
            continue;
        }
        nal_start = pos + sc;
        if (nal_start >= s_stream_length)
            break;

        type = (unsigned)(s_stream[nal_start] & 0x1Fu);
        is_vcl = (type == 1u || type == 5u);

        if (is_vcl && have_slice && starts_picture(s_stream + nal_start, s_stream_length - nal_start))
            return (pending != (size_t)-1) ? pending : pos;

        if (is_vcl) {
            have_slice = 1;
            pending = (size_t)-1;
        } else if (pending == (size_t)-1) {
            pending = pos;
        }

        next = nal_start;
        while (next < s_stream_length && start_code_at(s_stream, s_stream_length, next) == 0)
            next++;
        pos = next;
    }
    return s_stream_length;
}

/* Decodes the next access unit. Returns 0 at end of stream. */
static int replay_step(void)
{
    size_t end;

    /* A zero-length stream must not be steppable, or the caller loops on end-of-stream forever. */
    if (s_stream_length == 0 || s_cursor >= s_stream_length)
        return 0;

    end = unit_end_from(s_cursor);
    if (end <= s_cursor)
        return 0;

    if (rc_mvd_decode_frame(&s_mvd, s_stream + s_cursor, end - s_cursor,
                            unit_is_keyframe(s_stream + s_cursor, end - s_cursor))) {
        unsigned detail = rc_mvd_last_detail();

        s_pictures++;
        if (detail > s_detail_max) s_detail_max = detail;
        if (detail < s_detail_min) s_detail_min = detail;
        if (detail < 25u)
            s_flat_frames++;
        else if (s_first_good < 0)
            s_first_good = s_pictures;
    }
    s_units++;
    s_cursor = end;
    return 1;
}

static void replay_rewind(void)
{
    s_cursor = 0;
    s_units = 0;
    s_pictures = 0;
    s_flat_frames = 0;
    s_first_good = -1;
    s_detail_min = 100;
    s_detail_max = 0;
    rc_profile_reset(&s_profile);
    rc_log("rewound to the start of the stream\n");
}

int main(int argc, char **argv)
{
    const char *argv0 = argc > 0 ? argv[0] : NULL;

    int paused = 0;

    osSetSpeedupEnable(true);

    /* Same split as the connect probe: MVD owns the RGB565 top screen, text goes on the bottom. */
    gfxInit(GSP_RGB565_OES, GSP_BGR8_OES, false);
    consoleInit(GFX_BOTTOM, NULL);

    s_argv0 = argv0;
    rc_log_open(argv0, "mvdreplay.log");
    rc_log("ripcord-3ds MVD replay - no console, no network, same bytes every run\n");
    rc_log("(hold Y while launching to load %s instead)\n", kStreams[1]);
    rc_log("----------------------------------------------------------------------\n");
    rc_profile_calibrate();

    /*
     * THE STREAM IS CHOSEN AT STARTUP, NOT SWITCHED AT RUNTIME.
     *
     * There was a Y key that swapped files mid-run. Both forms of it crashed: handing a warm decoder a
     * new sequence parameter set took the process down, and so did tearing MVD down and re-initialising
     * it to avoid that. Four crash dumps share an identical register context - same PC across builds
     * whose .text differs by ~1.5 KB, lr = 0, only the faulted data address changing - which is code
     * that does not move when this binary does. That is the MVD system module dying, not this program,
     * and no amount of care on this side makes mvdstdExit/mvdstdInit cycling safe.
     *
     * Selecting before mvdstdInit is called sidesteps it completely: hold Y while launching for the
     * second stream. One decoder lifetime per process, which is what the block appears to expect.
     */
    hidScanInput();
    if (hidKeysHeld() & KEY_Y)
        s_stream_index = 1;

    s_stream_length = load_stream(argv0);
    if (s_stream_length == 0)
        goto idle;
    report_stream_integrity();

    /*
     * The dump is 640x360 because that is what the connect flow negotiated when it was written. If a
     * capture at another resolution is ever replayed this needs to read the SPS instead; for now it is
     * stated rather than guessed, so a mismatch is obvious rather than mysterious.
     */
    rc_mvd_set_profile(&s_profile);
    rc_mvd_set_widescreen(1);
    rc_mvd_set_smoothing(1);
    if (!rc_mvd_init(&s_mvd, 640, 360, 0)) {
        rc_log("\x1b[31mFAIL\x1b[0m rc_mvd_init\n");
        goto idle;
    }

    rc_log("\nA = rewind, X = pause/resume, START = exit\n\n");
    s_run_started = svcGetSystemTick();

    while (aptMainLoop()) {
        u32 down;

        hidScanInput();
        down = hidKeysDown();
        if (down & KEY_START)
            break;
        if (down & KEY_A)
            replay_rewind();
        if (down & KEY_X) {
            paused = !paused;
            rc_log("%s at unit %ld\n", paused ? "paused" : "resumed", s_units);
        }

        if (!paused && !replay_step()) {
            rc_log("end of stream [%s]: %ld unit(s) -> %ld picture(s)\n",
                kStreams[s_stream_index], s_units, s_pictures);
            rc_log("  picture quality: %ld flat of %ld frame(s), first good at %ld, "
                   "detail %u%%..%u%%\n",
                s_flat_frames, s_pictures, s_first_good, s_detail_min, s_detail_max);
            replay_rewind();
        }

        gfxFlushBuffers();
        gfxSwapBuffers();
        gspWaitForVBlank();
    }

    rc_log("\nMVD: %ld picture(s), %ld NAL unit(s) fed, %ld process error(s), %ld render error(s)\n",
        s_mvd.frames_rendered, s_mvd.nal_units_fed, s_mvd.process_errors, s_mvd.render_errors);
    rc_profile_report(&s_profile,
        (unsigned)((svcGetSystemTick() - s_run_started) / (uint64_t)CPU_TICKS_PER_MSEC));
    rc_mvd_exit(&s_mvd);

idle:
    rc_log("\nPress START to exit.\n");
    while (aptMainLoop()) {
        hidScanInput();
        if (hidKeysDown() & KEY_START)
            break;
        gfxFlushBuffers();
        gfxSwapBuffers();
        gspWaitForVBlank();
    }

    rc_log_close();
    gfxExit();
    return 0;
}
