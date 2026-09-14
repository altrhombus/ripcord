/*
 * ripcord-ps3 - see rc_decode_probe.h for what this establishes and why the hashes are the point.
 *
 * C++ because openh264's decoder API is a C++ interface (ISVCDecoder is an abstract class with virtual
 * methods, not a C handle). It is the only C++ in this port, it is confined to this file, and everything
 * it exposes is extern "C" so the rest of the port stays C99.
 */
#include "rc_decode_probe.h"

extern "C" {
#include "rc_h264_annexb.h"
#include "platform/rc_platform.h"
}

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "codec_api.h"

#define FNV64_OFFSET 0xcbf29ce484222325ULL
#define FNV64_PRIME  0x100000001b3ULL

/*
 * The stream is read whole. It is ~2 MB and the console has 256 MB; streaming it would add a buffering
 * layer whose bugs would be indistinguishable from decoder bugs, which is the opposite of what a probe
 * is for.
 */
#define RC_DECODE_PROBE_MAX_BYTES (4u * 1024u * 1024u)

extern "C" uint64_t rc_decode_probe_hash_plane(const uint8_t *plane, int stride, int width, int height,
                                               uint64_t seed)
{
    uint64_t h = seed;
    int y;

    /*
     * ROW BY ROW, width bytes per row, ignoring the stride padding. The decoder hands back planes with
     * whatever stride suits it; the reference decode on the development machine is a plain file with no
     * padding at all. Hashing the logical pixels rather than the buffer is what makes the two comparable,
     * and hashing the padding instead would produce a stable, reproducible, meaningless number.
     */
    for (y = 0; y < height; y++) {
        const uint8_t *row = plane + (size_t)y * (size_t)stride;
        int x;
        for (x = 0; x < width; x++) {
            h ^= (uint64_t)row[x];
            h *= FNV64_PRIME;
        }
    }
    return h;
}

extern "C" int rc_decode_probe(const char *path, int max_frames, rc_decode_probe_result *out)
{
    ISVCDecoder *dec = 0;
    SDecodingParam param;
    uint8_t *buf = 0;
    size_t len = 0;
    FILE *f = 0;
    rc_h264_annexb it;
    rc_h264_nal nal;
    uint64_t t0;
    int ok = 0;

    memset(out, 0, sizeof(*out));

    f = fopen(path, "rb");
    if (f == 0)
        return 0;

    buf = (uint8_t *)malloc(RC_DECODE_PROBE_MAX_BYTES);
    if (buf == 0) { fclose(f); return 0; }

    len = fread(buf, 1, RC_DECODE_PROBE_MAX_BYTES, f);
    fclose(f);
    if (len == 0) { free(buf); return 0; }

    out->opened = 1;
    out->bytes = len;

    if (WelsCreateDecoder(&dec) != 0 || dec == 0) { free(buf); return 0; }

    memset(&param, 0, sizeof(param));
    param.sVideoProperty.eVideoBsType = VIDEO_BITSTREAM_AVC;
    if (dec->Initialize(&param) != 0) {
        WelsDestroyDecoder(dec);
        free(buf);
        return 0;
    }
    out->initialised = 1;

    /*
     * Our splitter, not openh264's. rc_h264_annexb yields NAL units as pointers into this buffer with no
     * copying - the same zero-copy shape the real client needs, because the decoder will eventually DMA
     * slice bytes straight from these pointers into an SPE's local store.
     */
    rc_h264_annexb_init(&it, buf, len);

    while (out->frames_out < max_frames && rc_h264_annexb_next(&it, &nal)) {
        uint8_t *planes[3] = { 0, 0, 0 };
        SBufferInfo info;
        int32_t rc;

        /*
         * openh264 wants the start code; rc_h264_annexb hands back the NAL from its header byte onward,
         * having stepped over the code. Rather than copy every NAL to prepend one, the pointer is walked
         * back three bytes to the 0x000001 that is still sitting in the buffer.
         *
         * Three is right for both forms. A four-byte 00 00 00 01 backed up by three lands on 00 00 01,
         * which is an equally valid start code - so this does not need to know which form the splitter
         * just consumed. The guard is for a buffer that begins mid-stream with no code at all in front
         * of the first NAL, which rc_h264_annexb explicitly tolerates.
         */
        const uint8_t *with_start;
        int32_t feed_len;

        if (nal.start < buf + 3)
            continue;

        with_start = nal.start - 3;
        feed_len = (int32_t)nal.size + 3;

        memset(&info, 0, sizeof(info));

        /*
         * The timed region is this call and nothing else. Reading the file, splitting NALs and hashing
         * are all outside it: the question is what the PPE's H.264 decode costs, not what this probe
         * costs.
         */
        t0 = rc_tick();
        rc = dec->DecodeFrameNoDelay(with_start, feed_len, planes, &info);
        out->decode_ticks += rc_tick() - t0;
        out->nals_fed++;

        if (rc != 0) {
            out->last_error = rc;
            continue;
        }

        if (info.iBufferStatus == 1 && planes[0] != 0) {
            const SBufferInfo *bi = &info;
            int w = bi->UsrData.sSystemBuffer.iWidth;
            int h = bi->UsrData.sSystemBuffer.iHeight;
            int ys = bi->UsrData.sSystemBuffer.iStride[0];
            int cs = bi->UsrData.sSystemBuffer.iStride[1];
            uint64_t hash;

            out->width = w;
            out->height = h;
            out->frames_out++;

            /* Only the first few are hashed; see RC_DECODE_PROBE_FRAMES. */
            if (out->hashes >= RC_DECODE_PROBE_FRAMES) {
                ok = 1;
                continue;
            }
            t0 = rc_tick();

            /* Y, then U, then V - the order the reference file stores them in. One running hash across
             * all three planes, so a mismatch in any of them shows up in one number. */
            hash = rc_decode_probe_hash_plane(planes[0], ys, w, h, FNV64_OFFSET);
            hash = rc_decode_probe_hash_plane(planes[1], cs, w / 2, h / 2, hash);
            hash = rc_decode_probe_hash_plane(planes[2], cs, w / 2, h / 2, hash);

            out->hash[out->hashes++] = hash;
            out->hash_ticks += rc_tick() - t0;
            ok = 1;
        }
    }

    dec->Uninitialize();
    WelsDestroyDecoder(dec);
    free(buf);
    return ok;
}

/* ---- the live decoder ------------------------------------------------------------------------ */

/*
 * One decoder, held open across frames. See the header: re-creating it per frame would break every
 * inter-frame, because they reference the pictures before them.
 */
static ISVCDecoder *g_live = 0;

extern "C" int rc_decode_live_open(void)
{
    SDecodingParam param;

    if (g_live != 0)
        return 1;
    if (WelsCreateDecoder(&g_live) != 0 || g_live == 0) {
        g_live = 0;
        return 0;
    }

    memset(&param, 0, sizeof(param));
    param.sVideoProperty.eVideoBsType = VIDEO_BITSTREAM_AVC;
    if (g_live->Initialize(&param) != 0) {
        WelsDestroyDecoder(g_live);
        g_live = 0;
        return 0;
    }
    return 1;
}

extern "C" int rc_decode_live_feed(const uint8_t *access_unit, size_t length,
                                   rc_decode_live_stats *stats)
{
    unsigned char *planes[3] = { 0, 0, 0 };
    SBufferInfo info;
    uint64_t t0;
    int rc;

    if (g_live == 0 || access_unit == 0 || length == 0 || stats == 0)
        return 0;

    memset(&info, 0, sizeof(info));
    stats->frames_in++;

    /*
     * Fed whole, not split into NALs. stream_demux already emits a complete access unit with the video
     * header prepended where one is needed, and DecodeFrameNoDelay accepts that directly - splitting it
     * again here would only risk disagreeing with the demuxer about where a picture begins.
     *
     * The timed region is this call alone. The question is what the PPE's decode costs, not what the
     * surrounding bookkeeping costs.
     */
    t0 = rc_tick();
    rc = g_live->DecodeFrameNoDelay(access_unit, (int)length, planes, &info);
    stats->decode_ticks += rc_tick() - t0;

    if (rc != 0) {
        stats->last_error = rc;
        stats->errors++;
        return 0;
    }

    if (info.iBufferStatus == 1 && planes[0] != 0) {
        stats->width = info.UsrData.sSystemBuffer.iWidth;
        stats->height = info.UsrData.sSystemBuffer.iHeight;
        stats->pictures_out++;
        return 1;
    }
    return 0;
}

extern "C" void rc_decode_live_close(void)
{
    if (g_live != 0) {
        g_live->Uninitialize();
        WelsDestroyDecoder(g_live);
        g_live = 0;
    }
}
