#include "rc_vdec_probe.h"

#include <codec/vdec.h>
#include <ppu-asm.h>
#include <sysmodule/sysmodule.h>

#include <string.h>

/*
 * The sweep. 0..255 covers any plausible encoding of the value - a level_idc, a small enum, or a packed
 * profile/level byte - without this file having to assume which of those it is. The point is to come back
 * with the set the console accepts, not to confirm a guess.
 *
 * vdecQueryAttr only asks how much memory a configuration would need. It allocates nothing, starts
 * nothing and can be called before any decoder exists, which is what makes a sweep reasonable here.
 */
#define RC_VDEC_SWEEP_MAX 256

int rc_vdec_probe(rc_vdec_probe_result *out)
{
    int level;

    if (out == NULL)
        return 0;

    memset(out, 0, sizeof(*out));
    out->first_accepted = -1;
    out->last_accepted = -1;
    out->largest_mem_level = -1;

    out->module_load = (int)sysModuleLoad(SYSMODULE_VDEC_H264);
    if (out->module_load != 0)
        return 0;

    for (level = 0; level < RC_VDEC_SWEEP_MAX; level++) {
        vdecType type;
        vdecAttr attr;
        s32 rc;

        memset(&type, 0, sizeof(type));
        memset(&attr, 0, sizeof(attr));
        type.codec_type = VDEC_CODEC_TYPE_H264;
        type.profile_level = (u32)level;

        out->queried++;
        rc = vdecQueryAttr(&type, &attr);
        if (rc != 0) {
            out->last_error = (int)rc;
            continue;
        }

        out->accepted++;
        if (out->level_count < RC_VDEC_LEVELS_MAX)
            out->levels[out->level_count++] = level;
        if (out->first_accepted < 0) {
            out->first_accepted = level;
            out->first_mem_size = (unsigned)attr.mem_size;
            out->cmd_depth = (unsigned)attr.cmd_depth;
            out->ver_major = (unsigned)attr.ver_major;
            out->ver_minor = (unsigned)attr.ver_minor;
        }
        out->last_accepted = level;
        if ((unsigned)attr.mem_size > out->largest_mem_size) {
            out->largest_mem_size = (unsigned)attr.mem_size;
            out->largest_mem_level = level;
        }
    }

    return 1;
}

/* ---------------------------------------------------------------------------------------------------
 * The decode test. See rc_vdec_probe.h for why it is offline and why it hashes.
 * ------------------------------------------------------------------------------------------------ */

#include "rc_decode_probe.h"
#include "rc_h264_annexb.h"
#include "platform/rc_platform.h"

#include <malloc.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>

#define FNV64_OFFSET 0xcbf29ce484222325ULL

/* Implemented in rc_decode_probe.cpp and shared deliberately: comparing two decoders is only meaningful
 * if the same bytes go through the same hash. */
extern uint64_t rc_decode_probe_hash_plane(const uint8_t *plane, int stride, int width, int height,
                                           uint64_t seed);

/*
 * vdecQueryAttr says how much memory the decoder wants; the ALIGNMENT it wants is stated nowhere in the
 * SDK headers. 1 MB is used on the reasoning that an over-aligned buffer cannot be wrong and the waste is
 * irrelevant against a 10-57 MB allocation. [X] - not confirmed against the console.
 */
#define RC_VDEC_MEM_ALIGN (1024u * 1024u)

/* One 1080p YUV420 picture - more than either the capture or the 720p stream needs. Static, because this
 * build caps a stack frame at 8 KB. */
#define RC_VDEC_PICTURE_BYTES (1920 * 1088 * 3 / 2)
/*
 * 128-BYTE ALIGNED, AND THAT IS THE POINT OF b159.
 *
 * b158 localised the disagreement with cellVdec completely. ffmpeg and openh264 decode this capture to
 * bit-identical luma, so the reference is sound and vdec is the odd one out; vdec matches it exactly
 * across columns 0..631 of every row and differs only in columns 632..639, by a large positive spike at
 * column 632 that decays rightward, while a uniform row matches perfectly.
 *
 * 632 is 640 - 8, and 640 is 5 * 128. This hardware moves data in 128-byte units, so a destination that
 * is not itself 128-byte aligned puts every row's final chunk across an alignment boundary - which is
 * exactly where the corruption is. The probe checked that the decoder's output was tightly packed and
 * never checked what it was being written INTO; a plain static array gets 8 or 16 byte alignment.
 *
 * [X] - this explains the position and shape of the fault exactly, but the fix is unconfirmed until a
 * run says the two decoders now agree.
 */
static uint8_t s_picture[RC_VDEC_PICTURE_BYTES] __attribute__((aligned(128)));

static rc_vdec_decode_result *s_out;
static int s_want_rgb;
static rc_vdec_log_fn s_log;
static volatile int s_seq_done;
static volatile int s_au_done;   /* access units the decoder has finished reading */

/*
 * A CENSUS OF WHAT THE DECODER SAYS, because up to b150 this probe could not tell several very different
 * failures apart. last_error was written both by the decoder's own ERROR callback and by our submit being
 * refused, and the refusal came last, so whatever the decoder had said was overwritten. Worse, a PICOUT
 * whose vdecGetPicture failed returned early without counting anything, which reads exactly like a PICOUT
 * that never arrived.
 *
 * These are counted separately and none of them is inferred from another.
 */
static volatile int s_cb_audone;
static volatile int s_cb_picout;
static volatile int s_cb_seqdone;
static volatile int s_cb_error;
static volatile int s_cb_other;
static volatile unsigned s_cb_other_type;
static volatile int s_decoder_error;    /* what the ERROR callback said, never our own refusals */
static volatile int s_picitem_fail;     /* vdecGetPicItem refused */
static volatile int s_getpicture_fail;  /* vdecGetPicture refused */
static volatile int s_getpicture_error;
static uint64_t s_reference_y;
static uint64_t s_reference_u;

/*
 * THE CALLBACK'S DESCRIPTOR, AND WHY IT IS NOT JUST THE FUNCTION'S ADDRESS.
 *
 * b149 fed 16 access units, finished none, and never saw a single callback of any kind. The decoder was
 * branching to nothing: vdecClosure.fn was being given the address of the ELFv1 function descriptor GCC
 * emits, whose fields are 64-bit, while the firmware reads a descriptor of two 32-BIT words. Its own PRX
 * import stubs show the shape plainly - lwz r0,0(r12) for the entry and lwz r2,4(r12) for the TOC. Given
 * a 64-bit descriptor it reads the high half of the entry pointer, which for a 32-bit address is zero.
 *
 * PSL1GHT supplies __build_opd32 for precisely this, so this is the SDK's own answer rather than a
 * second guess at one: it copies the 64-bit entry and TOC down into a two-word 32-bit descriptor and
 * returns its address.
 */
static uint32_t s_callback_opd[2] __attribute__((aligned(8)));

/* Formats through the same one-line hook the steps use - see rc_vdec_probe.h for why findings are
 * written as they happen rather than summarised at the end. */
static void logf_line(const char *fmt, ...)
{
    char line[160];
    va_list ap;

    if (s_log == NULL)
        return;
    va_start(ap, fmt);
    (void)vsnprintf(line, sizeof(line), fmt, ap);
    va_end(ap);
    s_log(line);
}

/*
 * Written BEFORE the call it names, not after. See rc_vdec_probe.h: a step recorded only on success
 * names nothing when the call hangs, which is the one case worth instrumenting.
 */
static void step(rc_vdec_decode_result *out, int which, const char *what)
{
    out->last_step = which;
    if (s_log != NULL)
        s_log(what);
}

/*
 * The library calls this on its own PPU thread. The picture is collected here rather than by signalling
 * another thread, because vdecGetPicture is the way to collect a picture the decoder is announcing and
 * it is announcing it now - handing the job elsewhere would only add a buffer whose lifetime nobody
 * has established yet.
 */
static u32 vdec_callback(u32 handle, u32 msgtype, u32 msgdata, u32 arg)
{
    (void)arg;

    if (s_out == NULL)
        return 0;

    if (msgtype == VDEC_CALLBACK_PICOUT) {
        s_cb_picout++;
        vdecPictureFormat format;
        u32 item_addr = 0;
        s32 rc;

        /*
         * THE DECODER'S OWN DIMENSIONS, not the SPS's. The capture's SPS says 640x368 and openh264
         * reports 640x360 because it applies the frame cropping - so hashing at the SPS size would
         * compare two different pictures and call them different decoders.
         */
        if (vdecGetPicItem(handle, &item_addr) != 0 || item_addr == 0) {
            s_picitem_fail++;
        } else {
            const vdecPicture *pic = (const vdecPicture *)(uintptr_t)item_addr;

            s_out->picture_size = (unsigned)pic->picture_size;
            s_out->picture_attr = (unsigned)pic->attr;
            s_out->picture_status = (unsigned)pic->status;
            if (pic->codec_specific_addr != 0) {
                const vdecH264Info *info = (const vdecH264Info *)(uintptr_t)pic->codec_specific_addr;

                s_out->width = (int)info->width;
                s_out->height = (int)info->height;
            }
        }

        memset(&format, 0, sizeof(format));
        format.format_type = s_want_rgb ? VDEC_PICFMT_ARGB32 : VDEC_PICFMT_YUV420P;
        format.color_matrix = VDEC_COLOR_MATRIX_BT709;
        format.alpha = 0xff;

        rc = vdecGetPicture(handle, &format, s_picture);
        if (s_want_rgb) {
            /* The question is only whether it is accepted and filled; correctness against openh264 is a
             * different comparison and needs a converter this probe does not have. */
            s_out->rgb_requested = 1;
            s_out->rgb_accepted = (rc == 0);
            if (rc == 0) {
                /* s_out->width/height, not w/h: those are scoped to the GetPicItem block above. */
                size_t n = (size_t)s_out->width * (size_t)s_out->height * 4u;
                size_t at;

                s_out->rgb_min = 255u;
                s_out->rgb_max = 0u;
                for (at = 0; at < n; at += 997u) {   /* a prime stride, so no channel is favoured */
                    if (s_picture[at] < s_out->rgb_min)
                        s_out->rgb_min = s_picture[at];
                    if (s_picture[at] > s_out->rgb_max)
                        s_out->rgb_max = s_picture[at];
                }
            }
        }
        if (rc != 0) {
            /* Counted, not merged into last_error - a refused collection is a different fact from a
             * refused submission, and b150 could not tell them apart. */
            s_getpicture_fail++;
            s_getpicture_error = (int)rc;
            return 0;
        }

        s_out->pictures_out++;
        if (s_out->hashes < (int)(sizeof(s_out->hash) / sizeof(s_out->hash[0]))
            && s_out->width > 0 && s_out->height > 0) {
            int w = s_out->width;
            int h = s_out->height;
            size_t luma = (size_t)w * (size_t)h;
            size_t chroma = (size_t)(w / 2) * (size_t)(h / 2);
            uint64_t hash;

            /* Taken to be tightly packed, Y then U then V. [X] - if it pads its rows the hashes simply
             * will not match openh264's, and that mismatch is itself the finding. */
            hash = rc_decode_probe_hash_plane(s_picture, w, w, h, FNV64_OFFSET);
            hash = rc_decode_probe_hash_plane(s_picture + luma, w / 2, w / 2, h / 2, hash);
            hash = rc_decode_probe_hash_plane(s_picture + luma + chroma, w / 2, w / 2, h / 2, hash);
            /*
             * STORED HERE, PRINTED BY THE FEEDING THREAD. The hashes still have to be written as they
             * land rather than summarised at the end - b147 lost a whole run's findings to a hang after
             * the decode - but this runs on the library's own thread, and ps3_log formats, writes a file
             * and sends a datagram. None of that has ever run anywhere but the main thread on this port,
             * and a callback that has never once been invoked is not the place to find out whether it
             * can. The loop drains this after every submission.
             */
            /*
             * First picture only: the same planes hashed separately, and the chroma hashed at both the
             * visible-height and coded-height offsets. Whichever of those matches openh264's U says
             * where this decoder actually puts its planes - which is a fact to be read off a run rather
             * than reasoned about from the header, since nothing states it.
             */
            if (s_out->hashes == 0) {
                int padded = (h + 15) & ~15;

                s_out->buffer_addr = (unsigned)(uintptr_t)s_picture;
                s_out->padded_height = padded;
                s_out->hash_y = rc_decode_probe_hash_plane(s_picture, w, w, h, FNV64_OFFSET);
                memcpy(s_out->first_luma, s_picture, sizeof(s_out->first_luma));
                memcpy(s_out->second_row_luma, s_picture + w, sizeof(s_out->second_row_luma));

                /*
                 * Diffed against openh264's own first picture, at this decoder's tightly packed stride -
                 * picture_size of 353280 is exactly 640*368*1.5, so stride is the width. Where the two
                 * first differ is the finding; four builds of hashes never got closer than "not equal".
                 */
                s_out->diff_first_offset = -1;
                if (rc_decode_reference_width == w && rc_decode_reference_height == h) {
                    long i;
                    long total = (long)w * (long)h;
                    int last_diff_row = -1;

                    s_out->diff_valid = 1;
                    s_out->diff_total = total;
                    for (i = 0; i < total; i++) {
                        if (s_picture[i] != rc_decode_reference_luma[i]) {
                            if (s_out->diff_first_offset < 0) {
                                int k;

                                s_out->diff_first_offset = i;
                                s_out->diff_first_row = (int)(i / w);
                                s_out->diff_first_col = (int)(i % w);
                                for (k = 0; k < 8 && i + k < total; k++) {
                                    s_out->diff_reference[k] = rc_decode_reference_luma[i + k];
                                    s_out->diff_actual[k] = s_picture[i + k];
                                }
                            }
                            if (s_out->diff_bytes == 0) {
                                s_out->diff_min_col = (int)(i % w);
                                s_out->diff_max_col = (int)(i % w);
                                s_out->diff_min_row = (int)(i / w);
                            }
                            {
                                int col = (int)(i % w);
                                int row = (int)(i / w);

                                if (col < s_out->diff_min_col)
                                    s_out->diff_min_col = col;
                                if (col > s_out->diff_max_col)
                                    s_out->diff_max_col = col;
                                s_out->diff_max_row = row;
                                if (col >= w - 16)
                                    s_out->diff_in_last_16_cols++;
                                if (row != last_diff_row) {
                                    s_out->diff_rows_affected++;
                                    last_diff_row = row;
                                }
                            }
                            {
                                int delta = (int)s_picture[i] - (int)rc_decode_reference_luma[i];

                                if (delta < 0)
                                    delta = -delta;
                                if (delta > s_out->diff_max_delta)
                                    s_out->diff_max_delta = delta;
                                s_out->diff_delta_sum += delta;
                                if (delta > 4)
                                    s_out->diff_over_4++;
                            }
                            s_out->diff_bytes++;
                        }
                    }

                    /* Three rows with content in them, 16 columns ending at the picture's right edge. */
                    {
                        int want[3];
                        int k;

                        want[0] = h / 3;
                        want[1] = h / 2;
                        want[2] = (h * 5) / 6;
                        for (k = 0; k < 3; k++) {
                            const uint8_t *ref = rc_decode_reference_luma + (size_t)want[k] * (size_t)w
                                                 + (size_t)(w - 16);
                            const uint8_t *act = s_picture + (size_t)want[k] * (size_t)w
                                                 + (size_t)(w - 16);

                            memcpy(s_out->edge_ref[k], ref, 16);
                            memcpy(s_out->edge_act[k], act, 16);
                            s_out->edge_row[k] = want[k];
                        }
                        s_out->edge_rows = 3;
                    }

                    /* Does vdec's last 8 appear anywhere else in the reference's same row? */
                    s_out->edge_found_at = -1;
                    if (s_out->diff_first_row >= 0 && s_out->diff_first_row < h) {
                        const uint8_t *ref_row = rc_decode_reference_luma
                                                 + (size_t)s_out->diff_first_row * (size_t)w;
                        const uint8_t *act_edge = s_picture
                                                  + (size_t)s_out->diff_first_row * (size_t)w
                                                  + (size_t)(w - 8);
                        int at;

                        for (at = 0; at + 8 <= w; at++) {
                            if (memcmp(ref_row + at, act_edge, 8) == 0) {
                                s_out->edge_found_at = at;
                                break;
                            }
                        }
                    }

                    /*
                     * FIND THE STRIDE BY FINDING A ROW. The reference's row 1 is 640 known bytes; where
                     * they occur in this decoder's plane is where its row 1 starts, and that offset is
                     * its stride. This asks the buffer instead of asking me.
                     */
                    if (s_out->diff_bytes > 0 && h > 1) {
                        int candidate;

                        for (candidate = 1; candidate <= 2048; candidate++) {
                            if ((size_t)candidate + (size_t)w > sizeof(s_picture))
                                break;
                            if (memcmp(s_picture + candidate,
                                       rc_decode_reference_luma + w, (size_t)w) == 0) {
                                s_out->found_row_stride = candidate;
                                break;
                            }
                        }
                    }
                }

                /*
                 * FIND THE STRIDE BY REPRODUCING A KNOWN ANSWER. openh264 decoded this same picture and
                 * its luma hash is the reference; whichever stride makes this decoder's luma hash the
                 * same is this decoder's stride. Nothing states it, and every question about where the
                 * chroma starts depends on it.
                 *
                 * The range is generous on purpose - openh264 itself chose 704 for a 640-wide picture,
                 * so "a bit more than the width" is the shape to expect, but the point of a sweep is not
                 * to confirm what was expected.
                 */
                if (s_reference_y != 0u) {
                    int candidate;

                    for (candidate = w; candidate <= 2048; candidate += 16) {
                        if ((size_t)candidate * (size_t)h > sizeof(s_picture))
                            break;
                        if (rc_decode_probe_hash_plane(s_picture, candidate, w, h, FNV64_OFFSET)
                                == s_reference_y) {
                            s_out->matched_stride = candidate;
                            break;
                        }
                    }
                }

                /*
                 * With the stride known, where the chroma begins is the same question asked again: how
                 * many luma rows precede it. Swept against openh264's U for the same reason.
                 */
                if (s_out->matched_stride > 0 && s_reference_u != 0u) {
                    int rows;
                    int stride = s_out->matched_stride;

                    for (rows = h; rows <= 2048; rows++) {
                        size_t offset = (size_t)stride * (size_t)rows;

                        if (offset + (size_t)(stride / 2) * (size_t)(h / 2) > sizeof(s_picture))
                            break;
                        if (rc_decode_probe_hash_plane(s_picture + offset, stride / 2, w / 2, h / 2,
                                                       FNV64_OFFSET) == s_reference_u) {
                            s_out->matched_luma_rows = rows;
                            break;
                        }
                    }
                }

                {
                    size_t luma_padded = (size_t)w * (size_t)padded;
                    size_t chroma_visible = (size_t)(w / 2) * (size_t)(h / 2);
                    size_t chroma_padded = (size_t)(w / 2) * (size_t)(padded / 2);

                    s_out->hash_u_at_visible =
                        rc_decode_probe_hash_plane(s_picture + luma, w / 2, w / 2, h / 2, FNV64_OFFSET);
                    s_out->hash_v_at_visible =
                        rc_decode_probe_hash_plane(s_picture + luma + chroma_visible,
                                                   w / 2, w / 2, h / 2, FNV64_OFFSET);
                    s_out->hash_u_at_padded =
                        rc_decode_probe_hash_plane(s_picture + luma_padded, w / 2, w / 2, h / 2,
                                                   FNV64_OFFSET);
                    s_out->hash_v_at_padded =
                        rc_decode_probe_hash_plane(s_picture + luma_padded + chroma_padded,
                                                   w / 2, w / 2, h / 2, FNV64_OFFSET);
                }
            }

            s_out->hash[s_out->hashes++] = hash;
        }
    } else if (msgtype == VDEC_CALLBACK_AUDONE) {
        s_cb_audone++;
        /* A queue slot has freed. The queue is only cmd_depth deep - b145 reported 4 - which is why
         * this has to be counted rather than ignored. */
        s_au_done++;
    } else if (msgtype == VDEC_CALLBACK_SEQDONE) {
        /* vdecEndSequence finishes here, not when it returns. b147 called vdecClose straight after
         * EndSequence and the console stopped in Close. */
        s_cb_seqdone++;
        s_seq_done = 1;
    } else if (msgtype == VDEC_CALLBACK_ERROR) {
        s_cb_error++;
        s_decoder_error = (int)msgdata;
    } else {
        s_cb_other++;
        s_cb_other_type = msgtype;
    }

    return 0;
}

/*
 * An access unit is a contiguous run of the file, so its start is the start code in front of its first
 * NAL. rc_h264_nal.start points at the header byte, which is just after that code.
 */
/* Prints whatever the callback has stored since the last call. Runs on the feeding thread. */
static void drain_hashes(rc_vdec_decode_result *out, int *printed)
{
    while (*printed < out->hashes) {
        logf_line("       frame %2d  0x%016llx  (%dx%d)",
                  *printed, (unsigned long long)out->hash[*printed], out->width, out->height);
        (*printed)++;
    }
}

static const uint8_t *start_code_of(const uint8_t *file, const uint8_t *nal_start)
{
    if ((size_t)(nal_start - file) >= 4u
        && nal_start[-4] == 0 && nal_start[-3] == 0 && nal_start[-2] == 0 && nal_start[-1] == 1)
        return nal_start - 4;
    if ((size_t)(nal_start - file) >= 3u
        && nal_start[-3] == 0 && nal_start[-2] == 0 && nal_start[-1] == 1)
        return nal_start - 3;
    return nal_start;
}

/*
 * THE QUEUE IS FOUR DEEP, AND THAT IS THE WHOLE STORY OF b148.
 *
 * vdecQueryAttr reports cmd_depth - 4 on this console - and vdecDecodeAu is asynchronous: an access unit
 * occupies a slot until the decoder says AUDONE. b148 submitted them as fast as the parser produced them,
 * so the fifth was refused, the loop broke, and the run reported five access units, no pictures and no
 * error. The missing error was a separate bug; the missing flow control is this one.
 *
 * So this waits for a slot before submitting, and treats a BUSY refusal as back-pressure to retry rather
 * than a failure to give up on. Both waits are bounded, because this hardware punishes an unbounded one.
 */
static s32 feed_au(u32 handle, const uint8_t *begin, const uint8_t *end,
                   rc_vdec_decode_result *out, unsigned depth)
{
    vdecAU info;
    uint64_t t0;
    s32 rc;
    int waited = 0;

    if (end <= begin)
        return 0;

    /* 200 ms, not 2000: if the decoder is not consuming, seventeen two-second waits turn a failed run
     * into half a minute of nothing. A working decoder frees a slot far inside this. */
    while ((out->aus_fed - s_au_done) >= (int)depth && waited < 200) {
        rc_sleep_ms(1u);
        waited++;
    }

    memset(&info, 0, sizeof(info));
    info.packet_addr = (u32)(uintptr_t)begin;
    info.packet_size = (u32)(size_t)(end - begin);
    info.pts.low = VDEC_TS_INVALID;
    info.pts.hi = VDEC_TS_INVALID;
    info.dts.low = VDEC_TS_INVALID;
    info.dts.hi = VDEC_TS_INVALID;

    t0 = rc_tick();
    rc = vdecDecodeAu(handle, VDEC_DECODER_MODE_NORMAL, &info);
    while (rc == (s32)VDEC_ERROR_BUSY && waited < 200) {
        rc_sleep_ms(1u);
        waited++;
        rc = vdecDecodeAu(handle, VDEC_DECODER_MODE_NORMAL, &info);
    }
    out->decode_ticks += rc_tick() - t0;

    if (rc == 0)
        out->aus_fed++;
    return rc;
}

int rc_vdec_decode_probe(const char *path, int level, int max_frames, rc_vdec_log_fn log,
                         uint64_t reference_y, uint64_t reference_u, int want_rgb,
                         rc_vdec_decode_result *out)
{
    vdecType type;
    vdecAttr attr;
    vdecConfig config;
    vdecClosure closure;
    rc_h264_annexb it;
    rc_h264_nal nal;
    /* 19,320 bytes - rc_h264_annexb.h says plainly that this belongs in static or heap and not on a
     * PPU thread stack, and this build caps a frame at 8 KB for exactly that reason. */
    static rc_h264_au au;
    rc_h264_au_nal piece;
    const uint8_t *au_begin = NULL;
    uint8_t *file = NULL;
    void *decoder_mem = NULL;
    size_t file_size = 0;
    u32 handle = 0;
    int printed = 0;
    FILE *f;
    s32 rc;
    int ok = 0;

    if (out == NULL)
        return 0;
    memset(out, 0, sizeof(*out));
    out->level = level;
    s_log = log;
    s_reference_y = reference_y;
    s_reference_u = reference_u;
    s_want_rgb = want_rgb;
    s_au_done = 0;
    s_seq_done = 0;
    s_cb_audone = 0;
    s_cb_picout = 0;
    s_cb_seqdone = 0;
    s_cb_error = 0;
    s_cb_other = 0;
    s_cb_other_type = 0;
    s_decoder_error = 0;
    s_picitem_fail = 0;
    s_getpicture_fail = 0;
    s_getpicture_error = 0;

    /* ---- the capture ---- */
    step(out, RC_VDEC_STEP_READ_FILE, "       .. reading the capture");
    f = fopen(path, "rb");
    if (f == NULL)
        return 0;
    (void)fseek(f, 0, SEEK_END);
    {
        long n = ftell(f);

        if (n <= 0) {
            fclose(f);
            return 0;
        }
        file_size = (size_t)n;
    }
    (void)fseek(f, 0, SEEK_SET);
    file = (uint8_t *)malloc(file_size);
    if (file == NULL) {
        fclose(f);
        return 0;
    }
    if (fread(file, 1, file_size, f) != file_size) {
        free(file);
        fclose(f);
        return 0;
    }
    fclose(f);

    /* ---- what this level costs ---- */
    step(out, RC_VDEC_STEP_QUERY_ATTR, "       .. vdecQueryAttr");
    memset(&type, 0, sizeof(type));
    memset(&attr, 0, sizeof(attr));
    type.codec_type = VDEC_CODEC_TYPE_H264;
    type.profile_level = (u32)level;
    rc = vdecQueryAttr(&type, &attr);
    if (rc != 0) {
        out->last_error = (int)rc;
        free(file);
        return 0;
    }
    out->mem_size = (unsigned)attr.mem_size;

    step(out, RC_VDEC_STEP_ALLOC, "       .. allocating the decoder's memory");
    decoder_mem = memalign(RC_VDEC_MEM_ALIGN, attr.mem_size);
    if (decoder_mem == NULL) {
        free(file);
        return 0;
    }

    /* ---- open ---- */
    step(out, RC_VDEC_STEP_OPEN, "       .. vdecOpen  <- if the log stops here, this is the call");
    memset(&config, 0, sizeof(config));
    config.mem_addr = (u32)(uintptr_t)decoder_mem;
    config.mem_size = attr.mem_size;
    config.ppu_thread_prio = 1000;
    config.ppu_thread_stack_size = 256u * 1024u;
    config.spu_thread_prio = 200;   /* [X] - a plausible mid priority, not a confirmed one */
    config.num_spus = 1;            /* one is what is spare; the other five convert colour */

    /* On this ABI a function name already denotes its descriptor's address, so this casts that address
     * and not code. [X] - if the library wants something else, it will fail at open rather than subtly. */
    memset(&closure, 0, sizeof(closure));
    closure.fn = (u32)__build_opd32(vdec_callback, s_callback_opd);
    closure.arg = 0;
    logf_line("       .. callback descriptor at 0x%08X (entry 0x%08X, toc 0x%08X)",
              (unsigned)closure.fn, (unsigned)s_callback_opd[0], (unsigned)s_callback_opd[1]);

    s_out = out;
    rc = vdecOpen(&type, &config, &closure, &handle);
    if (rc != 0) {
        out->last_error = (int)rc;
        s_out = NULL;
        free(decoder_mem);
        free(file);
        return 0;
    }
    out->opened = 1;

    step(out, RC_VDEC_STEP_START_SEQUENCE, "       .. vdecStartSequence");
    rc = vdecStartSequence(handle);
    if (rc != 0) {
        out->last_error = (int)rc;
        goto close_out;
    }

    /*
     * THE WHOLE CAPTURE STAYS RESIDENT and every access unit points into it. vdecDecodeAu takes an
     * address the decoder reads asynchronously, so a buffer reused between calls would need the AUDONE
     * callback to say when it was free again. Keeping the file mapped stops that question arising at all.
     */
    step(out, RC_VDEC_STEP_DECODE_AU, "       .. feeding access units");
    rc_h264_annexb_init(&it, file, file_size);
    rc_h264_au_init(&au);
    printed = 0;
    while (out->pictures_out < max_frames && rc_h264_annexb_next(&it, &nal)) {
        drain_hashes(out, &printed);
        (void)rc_h264_au_feed(&au, &nal, &piece);

        if (piece.begins_access_unit) {
            const uint8_t *here = start_code_of(file, nal.start);

            if (au_begin != NULL) {
                /* feed_au's OWN return. b148 assigned a stale `rc` here, so the one error that stopped
                 * the run was reported as zero and the run looked like a success that produced nothing. */
                s32 frc = feed_au(handle, au_begin, here, out, attr.cmd_depth);

                if (frc != 0) {
                    out->last_error = (int)frc;
                    logf_line("       vdecDecodeAu refused access unit %d with 0x%08X"
                              " (%d submitted, %d finished)",
                              out->aus_fed, (unsigned)frc, out->aus_fed, s_au_done);
                    break;
                }
            }
            au_begin = here;
        }
    }
    /* The last access unit has no successor to close it. */
    if (au_begin != NULL && out->pictures_out < max_frames)
        (void)feed_au(handle, au_begin, file + file_size, out, attr.cmd_depth);

    /* A bounded moment for the decoder to finish announcing. Bounded, because a wait that cannot end is
     * how this port lost three sessions. */
    {
        int waited = 0;

        while (out->pictures_out < max_frames && waited < 2000) {
            drain_hashes(out, &printed);
            rc_sleep_ms(5u);
            waited += 5;
        }
        drain_hashes(out, &printed);
    }

    step(out, RC_VDEC_STEP_END_SEQUENCE, "       .. vdecEndSequence");
    logf_line("       .. %d access unit(s) submitted, %d finished, %d picture(s) so far",
              out->aus_fed, s_au_done, out->pictures_out);
    s_seq_done = 0;
    (void)vdecEndSequence(handle);
    {
        int waited = 0;

        while (!s_seq_done && waited < 2000) {
            rc_sleep_ms(5u);
            waited += 5;
        }
        logf_line("       .. sequence ended: SEQDONE %s after %d ms",
                  s_seq_done ? "arrived" : "DID NOT ARRIVE", waited);
        drain_hashes(out, &printed);
    }
    ok = (out->pictures_out > 0);

close_out:
    /*
     * ONLY IF THE DECODER SAID IT WAS FINISHED. b147 reached vdecClose and the console stopped there,
     * with EndSequence called immediately before it - and EndSequence completes on the SEQDONE callback
     * rather than on return, so Close was being asked to tear down a sequence still running.
     *
     * When SEQDONE has not arrived, Close is skipped rather than attempted. That follows the decision
     * already recorded at the foot of rc_spu_yuv.c for the same hardware and the same class of problem:
     * this runs as the process exits, lv2 reclaims a process's resources when it does, and cleanup that
     * cannot hang is worth more here than cleanup that is complete. A hang costs a reboot and the rest
     * of the log.
     */
    if (s_seq_done) {
        step(out, RC_VDEC_STEP_CLOSE, "       .. vdecClose");
        (void)vdecClose(handle);
    } else {
        logf_line("       .. SKIPPING vdecClose - the decoder never said the sequence ended, and"
                  " b147 hung in exactly this call");
    }
    s_out = NULL;
    free(decoder_mem);
    free(file);
    logf_line("       callbacks: AUDONE %d, PICOUT %d, SEQDONE %d, ERROR %d, other %d (type %u)",
              s_cb_audone, s_cb_picout, s_cb_seqdone, s_cb_error, s_cb_other,
              (unsigned)s_cb_other_type);
    logf_line("       decoder's own error 0x%08X; GetPicItem refused %d, GetPicture refused %d (0x%08X)",
              (unsigned)s_decoder_error, s_picitem_fail, s_getpicture_fail,
              (unsigned)s_getpicture_error);
    step(out, RC_VDEC_STEP_DONE, "       .. done");
    return ok;
}
