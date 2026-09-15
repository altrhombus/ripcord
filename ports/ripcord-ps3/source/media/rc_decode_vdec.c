#include "rc_decode_vdec.h"

#include <codec/vdec.h>
#include <ppu-asm.h>
#include <ppu_intrinsics.h>
#include <sysmodule/sysmodule.h>

#include <malloc.h>
#include <string.h>

#include "platform/rc_platform.h"
#include "rc_h264_annexb.h"
#include "rc_h264_params.h"

/*
 * Sizes. The access-unit ring matches the frame queue in rc_connect.c: eight whole frames, each with
 * room for the largest this stream has produced with margin. Every one is 128-byte aligned - see the
 * header for what happens otherwise.
 */
#define RC_VDEC_AU_SLOTS 8
/* Matches RC_FRAME_SLOT_BYTES in rc_connect.c, and for the same reason - a 1080p keyframe does not fit
 * in 96 KB with any margin worth having. */
#define RC_VDEC_AU_BYTES (256 * 1024)
#define RC_VDEC_PICTURE_BYTES (1920 * 1088 * 3 / 2)
#define RC_VDEC_MEM_ALIGN (1024u * 1024u)

static uint8_t s_au[RC_VDEC_AU_SLOTS][RC_VDEC_AU_BYTES] __attribute__((aligned(128)));

/*
 * Two output buffers, so the decoder can be filling one while the caller is still blitting the other.
 * One would mean dropping a picture whenever the blit and the decode overlap, which at 30 fps they will.
 */
static uint8_t s_picture[2][RC_VDEC_PICTURE_BYTES] __attribute__((aligned(128)));

static u32 s_handle;
static void *s_memory;
static int s_open;

static rc_decode_picture_fn s_sink;
static void *s_sink_ctx;
static rc_decode_live_stats *s_stats;

/* The access-unit ring. The decoder consumes in order, so AUDONE frees the oldest. */
static volatile int s_au_submitted;
static volatile int s_au_done;
static volatile int s_seq_done;

/*
 * SAMPLED WHERE THE DECODER WROTE IT, not where the consumer read it.
 *
 * b161 delivered 444 pictures whose luma was 0..0, with vdecGetPicture returning success every time and
 * the dimensions arriving correctly through the same handoff. That splits into two very different
 * faults - the decoder not writing this buffer, or the buffer not being visible to the reading thread -
 * and one sample taken here, immediately after the call, separates them. If this is non-zero and the
 * consumer still sees zeros, it is visibility; if this is zero too, the write never landed here.
 */
static volatile unsigned s_cb_luma_max;
static volatile unsigned s_cb_pictures;

/* The first access unit submitted, and where pictures are written - both to be read rather than assumed. */
static uint8_t s_first_au[8];
static unsigned s_first_au_len;
static unsigned s_picture_addr;
static int s_level;
static unsigned s_mem_size;

/* What the stream says about itself, read from the first SPS that arrives. */
static int s_sps_profile = -1;
static int s_sps_level = -1;
static int s_sps_max_ref = -1;
static unsigned s_level_clamped;

/* The picture handoff: the callback writes `s_fill` and publishes it, the caller consumes it. */
static volatile int s_ready = -1;   /* index of a finished picture, -1 if none */
static volatile int s_fill;
static volatile int s_ready_w, s_ready_h;

static uint32_t s_callback_opd[2] __attribute__((aligned(8)));

/*
 * THE DECODER TOPS OUT AT LEVEL 4.2 AND THE CONSOLE DECLARES 5.0.
 *
 * b145's sweep found vdecQueryAttr accepts exactly the level_idc values 10..42, so 4.2 is the highest
 * configuration cellVdec offers. b169's 1080p stream declares level 5.0 in its SPS, and a decoder opened
 * below what a stream declares does not complain - it accepts every access unit, reports no error, and
 * produces uniformly black pictures. That is the same signature b163 showed at 720p.
 *
 * The declared level is not what the content needs. 1080p30 is 244,800 macroblocks a second against
 * level 4.2's ceiling of 522,240, and 8,160 macroblocks a frame against 8,704 - it fits 4.2 with room
 * spare. The console is simply declaring more headroom than it uses.
 *
 * So the declared level is lowered to 42 in the SPS before the access unit is submitted. This is a claim
 * about the bitstream, not a change to it: no coded data is touched, only the number the decoder gates
 * on.
 *
 * WHERE THIS CAN GO WRONG, AND IT IS NOT HYPOTHETICAL. Level also bounds the decoded picture buffer, and
 * that IS a real constraint rather than a formality: 4.2 allows 34,816 macroblocks of DPB, which at
 * 8,160 per frame is four reference frames, where 5.0 allows thirteen. A stream using more than four
 * would decode wrongly rather than not at all. max_num_ref_frames is read from the same SPS and logged
 * beside the level for exactly that reason - if the picture comes back corrupt rather than black, that
 * number says why, and this clamp is the thing to remove.
 */
#define RC_VDEC_MAX_LEVEL 42

static void clamp_level(uint8_t *au, size_t length)
{
    rc_h264_annexb it;
    rc_h264_nal nal;

    rc_h264_annexb_init(&it, au, length);
    while (rc_h264_annexb_next(&it, &nal)) {
        uint8_t *level_byte;

        if (nal.type != 7 || nal.payload_size < 3u)   /* 7 is an SPS */
            continue;

        if (s_sps_profile < 0) {
            rc_h264_sps sps;

            s_sps_profile = (int)nal.payload[0];
            s_sps_level = (int)nal.payload[2];
            if (rc_h264_sps_parse(nal.payload, nal.payload_size, &sps))
                s_sps_max_ref = (int)sps.max_num_ref_frames;
        }

        /* payload is profile_idc, constraint_set flags, level_idc - the third byte. The cast is of a
         * pointer into the caller's own copy of the access unit, which this function owns. */
        level_byte = (uint8_t *)(uintptr_t)(nal.payload + 2);
        if (*level_byte > RC_VDEC_MAX_LEVEL) {
            *level_byte = RC_VDEC_MAX_LEVEL;
            s_level_clamped++;
        }
    }
}

static u32 vdec_callback(u32 handle, u32 msgtype, u32 msgdata, u32 arg)
{
    (void)arg;
    (void)msgdata;

    if (msgtype == VDEC_CALLBACK_AUDONE) {
        s_au_done++;
        return 0;
    }

    if (msgtype == VDEC_CALLBACK_PICOUT) {
        vdecPictureFormat format;
        u32 item_addr = 0;
        int w = 0, h = 0;
        int slot;

        if (vdecGetPicItem(handle, &item_addr) == 0 && item_addr != 0) {
            const vdecPicture *pic = (const vdecPicture *)(uintptr_t)item_addr;

            if (pic->codec_specific_addr != 0) {
                const vdecH264Info *info = (const vdecH264Info *)(uintptr_t)pic->codec_specific_addr;

                w = (int)info->width;
                h = (int)info->height;
            }
        }
        if (w <= 0 || h <= 0 || (long)w * (long)h * 3 / 2 > RC_VDEC_PICTURE_BYTES)
            return 0;

        /*
         * Never the buffer the caller is still reading. b160's version reduced to "always slot 0", so
         * the second buffer was never used and the consumer could be blitting the very bytes being
         * overwritten. If a picture is published, write the other one.
         */
        slot = (s_ready == 0) ? 1 : 0;

        memset(&format, 0, sizeof(format));
        format.format_type = VDEC_PICFMT_YUV420P;
        format.color_matrix = VDEC_COLOR_MATRIX_BT709;
        format.alpha = 0;

        if (vdecGetPicture(handle, &format, s_picture[slot]) != 0) {
            if (s_stats != NULL)
                s_stats->errors++;
            return 0;
        }

        {
            /*
             * A GRID OVER THE WHOLE PICTURE, not one row across the middle. b162's sample was a single
             * row and its being zero was read as "the buffer is empty" - which assumes that row has
             * content in it. Sixty-four rows by sixty-four columns cannot all be black in a game's
             * start screen, so this answers the question the row could not.
             */
            const uint8_t *py = s_picture[slot];
            int gy, gx;

            for (gy = 0; gy < 64; gy++) {
                size_t base = (size_t)(gy * (h / 64)) * (size_t)w;

                for (gx = 0; gx < 64; gx++) {
                    unsigned sample = py[base + (size_t)(gx * (w / 64))];

                    if (sample > s_cb_luma_max)
                        s_cb_luma_max = sample;
                }
            }
            s_cb_pictures++;
        }

        s_picture_addr = (unsigned)(uintptr_t)s_picture[slot];
        s_fill = slot;
        s_ready_w = w;
        s_ready_h = h;

        /*
         * THE BARRIER IS THE POINT. This runs on the library's thread and the consumer runs on the
         * decode thread; PowerPC is weakly ordered, so without it the consumer can observe the published
         * index before the picture data it refers to. b160 blitted 431 pictures onto a black screen with
         * every counter in the path reporting success, which is what that looks like.
         */
        __lwsync();
        s_ready = slot;      /* published last, and only after the data is visible */
        return 0;
    }

    if (msgtype == VDEC_CALLBACK_SEQDONE) {
        s_seq_done = 1;
        return 0;
    }

    if (msgtype == VDEC_CALLBACK_ERROR && s_stats != NULL) {
        s_stats->last_error = (int)msgdata;
        s_stats->errors++;
    }
    return 0;
}

void rc_decode_vdec_set_sink(rc_decode_picture_fn fn, void *ctx)
{
    s_sink = fn;
    s_sink_ctx = ctx;
}

int rc_decode_vdec_open(int width, int height)
{
    vdecType type;
    vdecAttr attr;
    vdecConfig config;
    vdecClosure closure;

    /*
     * The size is no longer what picks the level - see below - but it is still checked, because a
     * picture larger than the output buffers would be written past their end.
     */
    if ((long)width * (long)height * 3 / 2 > RC_VDEC_PICTURE_BYTES)
        return 0;

    if (s_open)
        return 1;

    s_au_submitted = 0;
    s_au_done = 0;
    s_ready = -1;
    s_fill = 0;
    s_seq_done = 0;
    s_cb_luma_max = 0;
    s_cb_pictures = 0;
    s_sps_profile = -1;
    s_sps_level = -1;
    s_sps_max_ref = -1;
    s_level_clamped = 0;

    if (sysModuleLoad(SYSMODULE_VDEC_H264) != 0)
        return 0;

    memset(&type, 0, sizeof(type));
    memset(&attr, 0, sizeof(attr));
    type.codec_type = VDEC_CODEC_TYPE_H264;
    /*
     * THE HIGHEST LEVEL THE CONSOLE ACCEPTS, AND NOT ONE CHOSEN FROM THE RESOLUTION.
     *
     * b163 is why. The decoder was opened at level 31 for a 1280x720 stream, on the reasoning that 3.1
     * is the level for 720p - and the stream's own SPS says 0x28, level 4.0. H.264 levels constrain
     * bitrate and frame rate as well as picture size, so a resolution cannot pick one. Given a
     * configuration below what the stream needs, the decoder accepted every access unit, reported no
     * error at all, and produced 889 uniformly black pictures. The offline capture really was level 31,
     * which is exactly why the probe worked and the live path did not.
     *
     * 42 is the highest of the thirteen values b145's sweep found the console accepts, and a decoder
     * opened high decodes anything below it. The cost is memory reserved up front - level 40 asked for
     * 57 MB against level 31's 33 MB - which is worth paying to never make this mistake again from a
     * number that looked like it could be inferred.
     */
    type.profile_level = 42u;

    if (vdecQueryAttr(&type, &attr) != 0)
        return 0;
    s_level = (int)type.profile_level;
    s_mem_size = (unsigned)attr.mem_size;

    s_memory = memalign(RC_VDEC_MEM_ALIGN, attr.mem_size);
    if (s_memory == NULL)
        return 0;

    memset(&config, 0, sizeof(config));
    config.mem_addr = (u32)(uintptr_t)s_memory;
    config.mem_size = attr.mem_size;
    config.ppu_thread_prio = 1000;
    config.ppu_thread_stack_size = 256u * 1024u;
    config.spu_thread_prio = 200;
    /*
     * One SPE. rc_spu_yuv.h explains why the colour converter takes four rather than five: this group is
     * created once and never released, so the SPE the decoder needs has to be left free from the start.
     */
    config.num_spus = 1;

    memset(&closure, 0, sizeof(closure));
    /* A 32-bit {entry, toc} descriptor, not GCC's 64-bit one - see rc_vdec_probe.c for what the
     * difference cost. */
    closure.fn = (u32)__build_opd32(vdec_callback, s_callback_opd);
    closure.arg = 0;

    if (vdecOpen(&type, &config, &closure, &s_handle) != 0) {
        free(s_memory);
        s_memory = NULL;
        return 0;
    }
    if (vdecStartSequence(s_handle) != 0) {
        (void)vdecClose(s_handle);
        free(s_memory);
        s_memory = NULL;
        return 0;
    }

    s_open = 1;
    return 1;
}

unsigned rc_decode_vdec_callback_luma_max(void)
{
    return s_cb_luma_max;
}

unsigned rc_decode_vdec_callback_pictures(void)
{
    return s_cb_pictures;
}

int rc_decode_vdec_level(void)
{
    return s_level;
}

unsigned rc_decode_vdec_mem_size(void)
{
    return s_mem_size;
}

int rc_decode_vdec_sps_profile(void) { return s_sps_profile; }
int rc_decode_vdec_sps_level(void)   { return s_sps_level; }
int rc_decode_vdec_sps_max_ref(void) { return s_sps_max_ref; }
unsigned rc_decode_vdec_level_clamped(void) { return s_level_clamped; }

unsigned rc_decode_vdec_picture_addr(void)
{
    return s_picture_addr;
}

unsigned rc_decode_vdec_first_au(uint8_t out[8])
{
    memcpy(out, s_first_au, sizeof(s_first_au));
    return s_first_au_len;
}

int rc_decode_vdec_feed(const uint8_t *access_unit, size_t length, rc_decode_live_stats *stats)
{
    int delivered = 0;

    if (!s_open || stats == NULL)
        return 0;
    s_stats = stats;

    if (access_unit != NULL && length > 0u && length <= RC_VDEC_AU_BYTES) {
        int in_flight = s_au_submitted - s_au_done;

        /*
         * Dropped rather than waited on. The caller is the decode thread, and blocking it would stop it
         * delivering the pictures whose completion is what frees a slot. A dropped access unit costs one
         * frame and the next keyframe repairs it; a stalled thread costs the stream.
         */
        if (in_flight < RC_VDEC_AU_SLOTS) {
            int slot = s_au_submitted % RC_VDEC_AU_SLOTS;
            vdecAU info;
            uint64_t t0;

            memcpy(s_au[slot], access_unit, length);
            clamp_level(s_au[slot], length);
            if (s_first_au_len == 0u) {
                memcpy(s_first_au, access_unit, sizeof(s_first_au));
                s_first_au_len = (unsigned)length;
            }

            memset(&info, 0, sizeof(info));
            info.packet_addr = (u32)(uintptr_t)s_au[slot];
            info.packet_size = (u32)length;
            info.pts.low = VDEC_TS_INVALID;
            info.pts.hi = VDEC_TS_INVALID;
            info.dts.low = VDEC_TS_INVALID;
            info.dts.hi = VDEC_TS_INVALID;

            stats->frames_in++;
            t0 = rc_tick();
            if (vdecDecodeAu(s_handle, VDEC_DECODER_MODE_NORMAL, &info) == 0)
                s_au_submitted++;
            else
                stats->errors++;
            stats->decode_ticks += rc_tick() - t0;
        } else {
            stats->errors++;
        }
    }

    /* Whatever the decoder has finished, handed to the sink on this thread. */
    {
        int ready = s_ready;

        if (ready >= 0) {
            int w, h;

            /* Pairs with the __lwsync in the callback: the index was published after the data, so do
             * not read the data before observing the index. */
            __lwsync();
            w = s_ready_w;
            h = s_ready_h;
            const uint8_t *y = s_picture[ready];
            const uint8_t *u = y + (size_t)w * (size_t)h;
            const uint8_t *v = u + (size_t)(w / 2) * (size_t)(h / 2);

            /*
             * Sampled before it is handed on, because b160 proved that every counter in this pipeline
             * can report success while the pixels are zeros. A few hundred samples down the middle of
             * the picture is enough to tell a picture from a black rectangle.
             */
            {
                int i;

                for (i = 0; i < 256; i++) {
                    size_t at = ((size_t)h / 2u) * (size_t)w + (size_t)(i * (w / 256));
                    unsigned sample = y[at];

                    if (stats->pictures_out == 0 && i == 0) {
                        stats->luma_min = sample;
                        stats->luma_max = sample;
                    }
                    if (sample < stats->luma_min)
                        stats->luma_min = sample;
                    if (sample > stats->luma_max)
                        stats->luma_max = sample;
                }
            }

            stats->width = w;
            stats->height = h;
            stats->pictures_out++;
            if (s_sink != NULL)
                s_sink(s_sink_ctx, y, u, v, w, w / 2, w, h);
            s_ready = -1;
            delivered = 1;
        }
    }
    return delivered;
}

void rc_decode_vdec_close(void)
{
    if (!s_open)
        return;

    /*
     * EndSequence completes on a callback rather than on return, and vdecClose before that callback
     * arrives is what hung the console in b147. Bounded, and Close is skipped rather than risked if the
     * decoder never says it finished - lv2 reclaims the process's resources at exit, and cleanup that
     * cannot hang is worth more here than cleanup that is complete.
     */
    s_seq_done = 0;
    (void)vdecEndSequence(s_handle);
    {
        int waited = 0;

        while (!s_seq_done && waited < 1000) {
            rc_sleep_ms(5u);
            waited += 5;
        }
    }
    if (s_seq_done)
        (void)vdecClose(s_handle);

    free(s_memory);
    s_memory = NULL;
    s_open = 0;
    s_sink = NULL;
    s_stats = NULL;
}
