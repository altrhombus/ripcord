/*
 * ripcord-ps3 - does openh264 decode on the PPE, and does it produce the right pixels?
 *
 * Step 7's first hardware question. Everything established so far about this decoder was established
 * somewhere else: it compiles for the PPE, it links to a PowerPC64 executable, and it decodes this
 * project's own capture bit-exactly on big-endian PowerPC64 under qemu. None of that is the PPE. qemu is
 * a different processor model, glibc is not newlib, and a Linux process is not 256 MB of XDR with a
 * hypervisor underneath.
 *
 * So this reads the capture off the console's disk, runs it through openh264, and hashes each decoded
 * frame with FNV-1a - the same hash, over the same bytes in the same order, that the development machine
 * computed from the reference decode. Matching hashes mean the PS3 produced pixel-identical output to a
 * known-good decode of the same stream. Differing hashes localise the failure to a frame number.
 *
 * THE NAL SPLITTING IS OURS, NOT OPENH264'S, and that is deliberate. rc_h264_annexb.c has 142 host
 * checks behind it and is what the real client will use to find access-unit boundaries in a live stream;
 * feeding openh264 through it here exercises the seam that will exist later rather than a convenient one
 * that will not.
 */
#ifndef RC_DECODE_PROBE_H
#define RC_DECODE_PROBE_H

#include <stddef.h>
#include <stdint.h>

/* The implementation is C++ - openh264's ISVCDecoder is an abstract class, not a C handle - and every
 * caller in this port is C99. Without this the two disagree about linkage and the compiler says so. */
#ifdef __cplusplus
extern "C" {
#endif

/* How many frames get hashed. Correctness needs a handful; throughput needs hundreds, and hashing all of
 * them would measure the hash. */
#define RC_DECODE_PROBE_FRAMES 8

typedef struct {
    int      opened;                              /* the stream was found and read            */
    size_t   bytes;                               /* how much of it                           */
    int      initialised;                         /* openh264 accepted Initialize()           */
    int      nals_fed;                            /* NAL units handed to the decoder          */
    int      frames_out;                          /* frames it produced                       */
    int      width, height;                       /* as the decoder reported them             */
    uint64_t hash[RC_DECODE_PROBE_FRAMES];        /* FNV-1a 64 per frame, in output order     */
    int      hashes;                              /* how many of the above are filled         */
    int32_t  last_error;                          /* whatever failed, as openh264 reported it */

    /*
     * TIMING, in rc_tick() units, and the two are reported separately because one of them is not the
     * decoder. FNV-1a over 345,600 bytes a frame is 345,600 dependent multiplies in scalar C, which on
     * an in-order PPE is not a rounding error - folding it into the decode time would flatter or
     * slander the decoder depending on how many frames were hashed. `decode_ticks` excludes it.
     */
    uint64_t decode_ticks;
    uint64_t hash_ticks;

    /*
     * THE FIRST FRAME, SPLIT BY PLANE. The combined hash above answers "do these two decoders agree"
     * with one number, which is right until they disagree - then it cannot say whether the luma differs
     * or only the chroma's position does. cellVdec and openh264 disagreed on b151 while both reported
     * 640x360, and the difference between "wrong pixels" and "right pixels at the wrong offset" is the
     * whole diagnosis.
     */
    uint64_t hash_y, hash_u, hash_v;
    int      stride_y, stride_uv;

    /*
     * THE ACTUAL BYTES, because the hashes have stopped being able to say anything useful. b153 settled
     * the layout question - vdec's picture_size of 353280 is exactly 640*368*1.5, so its planes are
     * tightly packed at stride 640 - and then showed that no stride at all reproduces openh264's luma.
     * Two conformant decoders differing on pixel VALUES is a different claim from differing on where
     * those pixels sit, and sixteen bytes distinguishes "a scaled or offset version of the same picture"
     * from "a different picture" at a glance, which no hash can.
     */
    uint8_t  first_luma[16];
    uint8_t  second_row_luma[16];
} rc_decode_probe_result;

/*
 * Decodes up to RC_DECODE_PROBE_FRAMES frames from the Annex-B stream at `path`. Returns 1 if the
 * decoder produced at least one frame, 0 otherwise; `out` is filled either way, because a run that
 * produced nothing still has to say how far it got.
 */
int rc_decode_probe(const char *path, int max_frames, rc_decode_probe_result *out);

/*
 * THE FIRST PICTURE'S LUMA, KEPT WHOLE, so another decoder can be diffed against it rather than
 * hashed against it.
 *
 * b154 printed sixteen bytes from each decoder and they were identical - 0x18, limited-range black -
 * because the capture opens on a black frame, so the sample could not distinguish anything. A hash says
 * only "not equal" and a corner sample says only "equal here". Neither locates a disagreement.
 *
 * Stored packed at `rc_decode_reference_width` bytes per row, whatever stride the decoder used, so a
 * consumer compares pixels and not padding.
 */
#define RC_DECODE_REFERENCE_MAX (1280 * 720)
extern uint8_t rc_decode_reference_luma[RC_DECODE_REFERENCE_MAX];
extern int rc_decode_reference_width;
extern int rc_decode_reference_height;

/*
 * THE LIVE DECODER, as opposed to the file probe above.
 *
 * rc_decode_probe answers "can this hardware decode H.264, and how fast" from a capture on disk. This
 * answers the different question the stream asks: can it decode frames as they arrive, in order, from a
 * decoder that stays open across them. A frame handed to a decoder that was re-created for it would
 * decode nothing useful - inter-frames reference the pictures before them, which is the whole point of
 * the format and the reason a per-frame open cannot work.
 *
 * Frames are whole access units in Annex-B form, exactly as stream_demux emits them.
 */
typedef struct {
    int  frames_in;        /* access units handed to the decoder */
    int  pictures_out;     /* pictures it produced - not the same number, and the gap is informative */
    int  width;
    int  height;
    int  last_error;       /* openh264's own return from the last failing call, 0 if none */
    int  errors;
    uint64_t decode_ticks; /* decode calls only - not the demux, not the copy */

    /*
     * A SAMPLE OF WHAT WAS ACTUALLY HANDED OVER. b160 decoded 431 pictures with no errors, converted 435
     * frames on the SPEs, blitted 431 and flipped 431 - and the screen stayed black. Every counter in
     * the path said it worked, because every counter was counting calls rather than content. These are
     * the darkest and brightest luma seen in the delivered pictures: a picture of zeros is black, and a
     * counter cannot tell you that.
     */
    unsigned luma_min, luma_max;
} rc_decode_live_stats;

/* Opens a decoder that stays open. Returns 1 on success. */
int rc_decode_live_open(void);

/* Feeds one whole access unit. Returns 1 if a picture came out. Statistics accumulate into `stats`,
 * which the caller owns and may read at any time. */
int rc_decode_live_feed(const uint8_t *access_unit, size_t length, rc_decode_live_stats *stats);

/*
 * Called with each decoded picture, inside rc_decode_live_feed, while openh264 still owns the planes.
 * They are valid for the duration of the call only - the decoder reuses them - so a consumer either
 * uses them now or copies them. Using them now is what the display does.
 */
typedef void (*rc_decode_picture_fn)(void *ctx, const unsigned char *y, const unsigned char *u,
                                     const unsigned char *v, int y_stride, int uv_stride,
                                     int width, int height);

void rc_decode_live_set_sink(rc_decode_picture_fn fn, void *ctx);

void rc_decode_live_close(void);

/*
 * WHICH DECODER IS ACTUALLY RUNNING, and the two behind the seam.
 *
 * The console's own H.264 decoder (cellVdec, on the SPEs) is the one to use and openh264 on the PPE is
 * the fallback, following the seam-and-stub pattern CLAUDE.md describes for the crypto: the rest of the
 * pipeline neither knows nor cares which answered.
 *
 * The choice is made at open time and reported, because a silent fallback to a decoder that manages
 * 4 fps would look exactly like the hardware one being slow.
 *
 * Call rc_decode_live_hint() before opening when the stream's size is known - it decides which H.264
 * level the hardware decoder is opened at, and therefore how much memory it reserves.
 */
typedef enum {
    RC_DECODE_BACKEND_NONE = 0,
    RC_DECODE_BACKEND_VDEC,      /* cellVdec, on the SPEs */
    RC_DECODE_BACKEND_OPENH264   /* openh264, on the PPE */
} rc_decode_backend;

void rc_decode_live_hint(int width, int height);
rc_decode_backend rc_decode_live_backend(void);
const char *rc_decode_live_backend_name(void);

/* The openh264 backend, reached through the seam above rather than directly. */
int  rc_decode_openh264_open(void);
void rc_decode_openh264_set_sink(rc_decode_picture_fn fn, void *ctx);
int  rc_decode_openh264_feed(const uint8_t *access_unit, size_t length, rc_decode_live_stats *stats);
void rc_decode_openh264_close(void);

/* The hash the caller compares against. Exposed so the development machine can compute it identically. */
uint64_t rc_decode_probe_hash_plane(const uint8_t *plane, int stride, int width, int height,
                                    uint64_t seed);

#ifdef __cplusplus
}
#endif

#endif /* RC_DECODE_PROBE_H */
