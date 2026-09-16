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
/*
 * SIXTEEN, because eight was a 30 fps number. At 60 fps the console delivers an access unit every 16 ms
 * and the decoder answers AUDONE on its own schedule; b180 at 720p60 hit the ceiling constantly - 489
 * submissions refused in thirty seconds against four units lost on the network - and every refusal is a
 * frame the decoder never sees.
 */
#define RC_VDEC_AU_SLOTS 16

/*
 * How long a submission may wait for the decoder's four-deep command queue - ONE FRAME at 60 fps.
 *
 * 6 ms was a number picked for being obviously small. The trade is one-sided and it argues for a whole
 * frame: giving up costs that frame AND breaks the reference chain, so everything after it is wrong
 * until a keyframe arrives, while waiting costs at most the frame. Dropping is only better if the
 * decoder is permanently behind, and it is not - b189 refused 11 submissions in 1,772.
 *
 * The refusals cluster on big transitions, where the frames are large and the decoder takes longest,
 * which is exactly where a broken chain is most visible. That is the residual blockiness.
 *
 * Still bounded, because this hardware punishes an unbounded wait, and the rings in front of and behind
 * this absorb a frame's delay without dropping anything.
 */
#define RC_VDEC_SUBMIT_WAIT_MS 16
/* Matches RC_FRAME_SLOT_BYTES in rc_connect.c, and for the same reason - a 1080p keyframe does not fit
 * in 96 KB with any margin worth having. */
#define RC_VDEC_AU_BYTES (256 * 1024)
/*
 * SIZED FOR THE WIDEST FORMAT, WHICH IS RGB AND NOT YUV.
 *
 * This was 1920*1088*3/2 - a YUV420 picture - and b188 asked the decoder for ARGB32 without resizing it.
 * Packed RGB is four bytes a pixel against YUV420's one and a half, so at 1280x720 vdecGetPicture wrote
 * 3,686,400 bytes into a 3,133,440 byte slot: half a megabyte past the end of every picture, into the
 * next slot and beyond. The console locked up immediately and stayed locked.
 *
 * The guard below did not catch it because it tested the YUV size too. Both are fixed together; a bound
 * that does not know which format it is bounding is not a bound.
 */
#define RC_VDEC_PICTURE_BYTES (1920 * 1088 * 4)
#define RC_VDEC_MEM_ALIGN (1024u * 1024u)

static uint8_t s_au[RC_VDEC_AU_SLOTS][RC_VDEC_AU_BYTES] __attribute__((aligned(128)));

/*
 * Two output buffers, so the decoder can be filling one while the caller is still blitting the other.
 * One would mean dropping a picture whenever the blit and the decode overlap, which at 30 fps they will.
 */
/*
 * FOUR PICTURES, NOT TWO, AND A RING RATHER THAN A SINGLE PUBLISHED SLOT.
 *
 * b185 decoded 1,788 frames with zero errors and delivered 960 of them. Nothing was dropped by the
 * decoder; they were overwritten here. Two buffers with one "ready" index means a picture announced
 * while the previous is still unconsumed replaces it, and at 60 fps announcements arrive in bursts, so
 * slightly under half never reached the screen.
 *
 * A ring lets the decoder run ahead a few pictures and lets the consumer take everything waiting. Four
 * costs 12.5 MB against two's 6.3, which this console has and a half-rate stream is not worth.
 */
#define RC_VDEC_PICTURE_SLOTS 4
static uint8_t s_picture[RC_VDEC_PICTURE_SLOTS][RC_VDEC_PICTURE_BYTES] __attribute__((aligned(128)));

static u32 s_handle;
static void *s_memory;
static int s_open;

static rc_decode_picture_fn s_sink;
static void *s_sink_ctx;
static rc_decode_picture_rgb_fn s_rgb_sink;
static void *s_rgb_sink_ctx;
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
static int s_clamp_safe;
static volatile int s_chain_broken;

/*
 * IS WHAT WE SUBMIT STILL AN ACCESS UNIT?
 *
 * At 8 Mbps this port decodes 873 of 875 frames perfectly; at 15 Mbps, with the same profile, the same
 * level and the same nine reference frames, every picture comes back black. The only thing that changes
 * is frame size - about 21 units a frame against about 90 - and the demuxer's assembly of a 96-unit
 * frame is byte-exact on the host, so the bytes leave it intact.
 *
 * That leaves what happens between there and the decoder. An access unit must begin with an Annex-B
 * start code; counting the ones that do not is the difference between "we are feeding it rubbish" and
 * "it is being fed correctly and still produces black", and no amount of reasoning separates those.
 */
static unsigned s_au_bad_start;
static unsigned s_au_largest;

/*
 * WHAT IS INSIDE THE ACCESS UNITS, since their outsides are provably fine.
 *
 * b172: every one of 893 units began with a start code, the largest was 112 KB against a 256 KB buffer,
 * the profile, level and reference count matched the stream that decodes perfectly at a lower bitrate,
 * and all 893 pictures came back black. The bytes are correct and the decoder still cannot use them, so
 * the remaining difference has to be in the structure of the unit rather than its size.
 *
 * Slice count is the candidate worth counting first: a frame split into many slices is a different shape
 * of work from one split into few, hardware decoders have limits there that software ones do not, and
 * nothing in this port has ever looked.
 */
static unsigned s_au_max_nals;
static unsigned s_au_max_slices;
/* The MOST RECENT count as well as the highest. b176 could not show the console responding because the
 * maximum is sticky: the stream thinned from 15 Mbps to 2.4 and the reported slice count stayed at its
 * opening value forever. A high-water mark answers "was it ever bad"; this answers "is it bad now". */
static unsigned s_au_last_slices;

/*
 * WHY A FRAME WAS LOST, counted apart. b180 reported 489 "errors" at 60 fps, which could equally have
 * been the ring refusing submissions, the decoder refusing them, or collection failing afterwards -
 * three faults with three different fixes and one counter between them.
 */
static unsigned s_drop_ring_full;
static unsigned s_drop_submit;
static unsigned s_drop_collect;
static unsigned s_drop_too_large;
static int s_drop_submit_error;
static int s_num_spus;
static unsigned s_submit_waits;


static unsigned s_first_nal_seen;
static unsigned s_first_nal_types;   /* first four NAL types of the first unit, packed one per byte */

/* The picture ring: the callback fills at the tail, the caller drains from the head. */
static volatile unsigned s_pic_head;
static volatile unsigned s_pic_tail;
static volatile int s_pic_w[RC_VDEC_PICTURE_SLOTS];
static volatile int s_pic_h[RC_VDEC_PICTURE_SLOTS];
static unsigned s_pic_overwritten;

static uint32_t s_callback_opd[2] __attribute__((aligned(8)));

/*
 * THE DECODER TOPS OUT AT LEVEL 4.2, AND WHETHER THAT MATTERS DEPENDS ON THE DECODED PICTURE BUFFER.
 *
 * b145's sweep found vdecQueryAttr accepts exactly the level_idc values 10..42, so 4.2 is the highest
 * configuration cellVdec offers. A decoder opened below what a stream declares does not complain - it
 * accepts every access unit, reports no error, and produces uniformly black pictures, which is the
 * signature b163 found at 720p and b169 found again at 1080p.
 *
 * Lowering the declared level is sound ONLY when the content genuinely fits the lower one, and the
 * binding constraint is not the macroblock rate. b170 got that wrong: 1080p30 is 244,800 macroblocks a
 * second against 4.2's ceiling of 522,240 and 8,160 a frame against 8,704, so on throughput it fits
 * easily - and it still decoded black, because level also bounds the DECODED PICTURE BUFFER and the
 * console uses NINE reference frames.
 *
 * Nine frames is 32,400 macroblocks of DPB at 720p, inside level 4.0's 32,768, which is exactly why the
 * console declares 4.0 there and why 720p works. At 1080p the same nine frames need 73,440 against
 * 4.2's 34,816. The console is not padding its declared level; it is asking for what it uses, and this
 * hardware cannot be configured to provide it.
 *
 * So the clamp stays, and it is guarded by the thing that actually decides: it fires only when the
 * stream's own max_num_ref_frames fits the target level's DPB at the stream's own resolution. At 1080p
 * against this console it will not fire, which is the correct answer rather than a missing feature.
 */
#define RC_VDEC_MAX_LEVEL 42

/* Reference frames level 4.2's DPB holds at a given frame size. Table A-1's MaxDpbMbs for 4.2. */
#define RC_VDEC_MAX_DPB_MBS 34816u

static int clamp_is_safe(const rc_h264_sps *sps)
{
    unsigned mbs;
    unsigned frames;

    if (sps->coded_width == 0u || sps->coded_height == 0u)
        return 0;

    mbs = (sps->coded_width / 16u) * (sps->coded_height / 16u);
    if (mbs == 0u)
        return 0;

    frames = RC_VDEC_MAX_DPB_MBS / mbs;
    if (frames > 16u)
        frames = 16u;                      /* the format's own ceiling */
    return sps->max_num_ref_frames <= frames;
}

static void clamp_level(uint8_t *au, size_t length)
{
    rc_h264_annexb it;
    rc_h264_nal nal;

    rc_h264_annexb_init(&it, au, length);
    while (rc_h264_annexb_next(&it, &nal)) {
        rc_h264_sps sps;
        uint8_t *level_byte;

        if (nal.type != 7 || nal.payload_size < 3u)   /* 7 is an SPS */
            continue;
        if (!rc_h264_sps_parse(nal.payload, nal.payload_size, &sps))
            continue;

        if (s_sps_profile < 0) {
            s_sps_profile = (int)nal.payload[0];
            s_sps_level = (int)nal.payload[2];
            s_sps_max_ref = (int)sps.max_num_ref_frames;
            s_clamp_safe = clamp_is_safe(&sps);
        }

        if (nal.payload[2] <= RC_VDEC_MAX_LEVEL || !clamp_is_safe(&sps))
            continue;

        /* payload is profile_idc, constraint flags, level_idc - the third byte. The cast is of a pointer
         * into this file's own copy of the access unit. No coded data is touched. */
        level_byte = (uint8_t *)(uintptr_t)(nal.payload + 2);
        *level_byte = RC_VDEC_MAX_LEVEL;
        s_level_clamped++;
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
        /* Against the size of the format actually being requested - see RC_VDEC_PICTURE_BYTES. */
        {
            long needed = (long)w * (long)h * ((s_rgb_sink != NULL) ? 4 : 3) / ((s_rgb_sink != NULL) ? 1 : 2);

            if (w <= 0 || h <= 0 || needed > RC_VDEC_PICTURE_BYTES) {
                s_drop_too_large++;
                return 0;
            }
        }

        /*
         * Never the buffer the caller is still reading. b160's version reduced to "always slot 0", so
         * the second buffer was never used and the consumer could be blitting the very bytes being
         * overwritten. If a picture is published, write the other one.
         */
        /* The tail of the ring. If it has caught the head, the OLDEST waiting picture goes - the
         * newest is the one closest to live - and it is counted rather than lost in silence. */
        if (s_pic_tail - s_pic_head >= (unsigned)RC_VDEC_PICTURE_SLOTS) {
            s_pic_head++;
            s_pic_overwritten++;
        }
        slot = (int)(s_pic_tail % (unsigned)RC_VDEC_PICTURE_SLOTS);

        memset(&format, 0, sizeof(format));
        /* Whichever sink is set decides the format; the decoder does the colour conversion when it can,
         * which is the whole reason for the RGB path. */
        format.format_type = (s_rgb_sink != NULL) ? VDEC_PICFMT_ARGB32 : VDEC_PICFMT_YUV420P;
        format.color_matrix = VDEC_COLOR_MATRIX_BT709;
        format.alpha = 0xff;

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
        s_pic_w[slot] = w;
        s_pic_h[slot] = h;

        /*
         * THE BARRIER IS THE POINT. This runs on the library's thread and the consumer runs on the
         * decode thread; PowerPC is weakly ordered, so without it the consumer can observe the published
         * index before the picture data it refers to. b160 blitted 431 pictures onto a black screen with
         * every counter in the path reporting success, which is what that looks like.
         */
        __lwsync();
        s_pic_tail++;        /* published last, and only after the data is visible */
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

void rc_decode_vdec_set_rgb_sink(rc_decode_picture_rgb_fn fn, void *ctx)
{
    s_rgb_sink = fn;
    s_rgb_sink_ctx = ctx;
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
    if ((long)width * (long)height * 4 > RC_VDEC_PICTURE_BYTES)
        return 0;

    if (s_open)
        return 1;

    s_au_submitted = 0;
    s_au_done = 0;
    s_pic_head = 0;
    s_pic_tail = 0;
    s_pic_overwritten = 0;
    s_seq_done = 0;
    s_cb_luma_max = 0;
    s_cb_pictures = 0;
    s_sps_profile = -1;
    s_sps_level = -1;
    s_sps_max_ref = -1;
    s_level_clamped = 0;
    s_clamp_safe = 0;
    s_au_bad_start = 0;
    s_au_largest = 0;
    s_au_max_nals = 0;
    s_au_max_slices = 0;
    s_au_last_slices = 0;
    s_drop_ring_full = 0;
    s_drop_submit = 0;
    s_drop_collect = 0;
    s_drop_too_large = 0;
    s_drop_submit_error = 0;
    s_submit_waits = 0;
    s_chain_broken = 0;
    s_first_nal_types = 0;
    s_first_nal_seen = 0;

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
     * TWO SPEs, because one decodes about 41 frames a second and 60 are arriving.
     *
     * b183 measured it rather than inferred it: of 1,747 submissions at 720p60, 1,373 came back BUSY -
     * 907 rescued by waiting and 466 given up on - and 1,225 pictures came out. The decoder's command
     * queue being four deep is why BUSY appears at all; being saturated is why waiting stops helping.
     *
     * One SPE was chosen when the stream was 30 fps and nothing had measured what it could take. Six are
     * initialised, the colour converter holds four and never releases them, so a second was sitting idle
     * the whole time.
     */
    config.num_spus = 2;
    s_num_spus = (int)config.num_spus;

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

int rc_decode_vdec_num_spus(void)
{
    return s_num_spus;
}

unsigned rc_decode_vdec_mem_size(void)
{
    return s_mem_size;
}

int rc_decode_vdec_sps_profile(void) { return s_sps_profile; }
int rc_decode_vdec_sps_level(void)   { return s_sps_level; }
int rc_decode_vdec_sps_max_ref(void) { return s_sps_max_ref; }
unsigned rc_decode_vdec_level_clamped(void) { return s_level_clamped; }
int rc_decode_vdec_clamp_safe(void) { return s_clamp_safe; }
unsigned rc_decode_vdec_au_bad_start(void) { return s_au_bad_start; }
unsigned rc_decode_vdec_au_largest(void) { return s_au_largest; }
unsigned rc_decode_vdec_au_max_nals(void) { return s_au_max_nals; }
unsigned rc_decode_vdec_au_max_slices(void) { return s_au_max_slices; }
unsigned rc_decode_vdec_au_last_slices(void) { return s_au_last_slices; }
unsigned rc_decode_vdec_drop_ring_full(void) { return s_drop_ring_full; }
unsigned rc_decode_vdec_drop_submit(void) { return s_drop_submit; }
int rc_decode_vdec_drop_submit_error(void) { return s_drop_submit_error; }
unsigned rc_decode_vdec_submit_waits(void) { return s_submit_waits; }
unsigned rc_decode_vdec_drop_collect(void) { return s_drop_collect + s_drop_too_large; }
unsigned rc_decode_vdec_pictures_overwritten(void) { return s_pic_overwritten; }

int rc_decode_vdec_take_chain_broken(void)
{
    int broken = s_chain_broken;

    s_chain_broken = 0;
    return broken;
}
unsigned rc_decode_vdec_first_nal_types(void) { return s_first_nal_types; }

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

            if (length > s_au_largest)
                s_au_largest = (unsigned)length;

            {
                rc_h264_annexb scan;
                rc_h264_nal one;
                unsigned nals = 0;
                unsigned slices = 0;

                rc_h264_annexb_init(&scan, s_au[slot], length);
                while (rc_h264_annexb_next(&scan, &one)) {
                    /* Only for the first access unit, and only its first four NALs. The guard is on the
                     * unit rather than on the packed value, which b173 got wrong: testing the value
                     * meant the second NAL was never recorded once the first had made it non-zero. */
                    if (s_first_nal_seen == 0u && nals < 4u)
                        s_first_nal_types |= ((unsigned)one.type & 0xffu) << (8u * (3u - nals));
                    nals++;
                    if (one.type == 1u || one.type == 5u)   /* coded slices */
                        slices++;
                }
                s_first_nal_seen = 1u;
                if (nals > s_au_max_nals)
                    s_au_max_nals = nals;
                s_au_last_slices = slices;
                if (slices > s_au_max_slices)
                    s_au_max_slices = slices;
            }
            if (length < 4u
                || !((access_unit[0] == 0 && access_unit[1] == 0 && access_unit[2] == 0
                      && access_unit[3] == 1)
                     || (access_unit[0] == 0 && access_unit[1] == 0 && access_unit[2] == 1)))
                s_au_bad_start++;
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
            {
                /*
                 * BUSY IS BACK-PRESSURE, NOT A FAILURE, and b182 is what treating it as one costs: 468
                 * submissions refused in thirty seconds at 60 fps, with the ring never full and five
                 * units lost on the network. The decoder's own command queue is four deep - b145 read
                 * cmd_depth from vdecQueryAttr - so at 16 ms a frame it is routinely still working when
                 * the next one arrives.
                 *
                 * The offline probe has always retried here; the live path counted and dropped, and
                 * every drop is a frame the decoder never sees and a reference chain to repair. A few
                 * milliseconds of waiting costs nothing against a 16 ms frame, and this thread's only
                 * other job - handing over a finished picture - happens further down the same call.
                 */
                s32 rc = vdecDecodeAu(s_handle, VDEC_DECODER_MODE_NORMAL, &info);
                int waited = 0;

                while (rc == (s32)VDEC_ERROR_BUSY && waited < RC_VDEC_SUBMIT_WAIT_MS) {
                    rc_sleep_ms(1u);
                    waited++;
                    rc = vdecDecodeAu(s_handle, VDEC_DECODER_MODE_NORMAL, &info);
                }
                if (waited > 0)
                    s_submit_waits++;

                if (rc == 0) {
                    s_au_submitted++;
                } else {
                    s_drop_submit++;
                    s_drop_submit_error = (int)rc;
                    stats->errors++;
                    s_chain_broken = 1;
                }
            }
            stats->decode_ticks += rc_tick() - t0;
        } else {
            /*
             * The ring is full: the decoder has not finished with any slot. Dropped rather than waited
             * on, because the caller is the decode thread and blocking it stops the very deliveries that
             * free a slot - but a dropped access unit breaks the reference chain exactly as packet loss
             * does, so the stream needs a keyframe to recover and nothing else here will ask for one.
             */
            s_drop_ring_full++;
            stats->errors++;
            s_chain_broken = 1;
        }
    }

    /*
     * EVERYTHING the decoder has finished, not just the newest, handed to the sink on this thread.
     *
     * b185 is why this is a loop. 1,788 frames decoded without a single error and 960 reached the
     * screen: announcements arrive in bursts at 60 fps, and taking one per call left the rest to be
     * overwritten. Draining costs a blit apiece - about 5 ms - which fits because the decoder only ever
     * runs a few pictures ahead.
     */
    while (s_pic_head != s_pic_tail) {
        unsigned slot = s_pic_head % (unsigned)RC_VDEC_PICTURE_SLOTS;
        const uint8_t *y;
        const uint8_t *u;
        const uint8_t *v;
        int w, h;

        /* Pairs with the __lwsync in the callback: the tail was published after the data, so do not
         * read the data before observing the tail. */
        __lwsync();
        w = s_pic_w[slot];
        h = s_pic_h[slot];
        y = s_picture[slot];
        u = y + (size_t)w * (size_t)h;
        v = u + (size_t)(w / 2) * (size_t)(h / 2);

        /*
         * Sampled before it is handed on, because b160 proved every counter in this pipeline can report
         * success while the pixels are zeros. A grid down the middle tells a picture from a black
         * rectangle.
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
        if (s_rgb_sink != NULL)
            s_rgb_sink(s_rgb_sink_ctx, y, w * 4, w, h);
        else if (s_sink != NULL)
            s_sink(s_sink_ctx, y, u, v, w, w / 2, w, h);

        s_pic_head++;
        delivered = 1;
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
