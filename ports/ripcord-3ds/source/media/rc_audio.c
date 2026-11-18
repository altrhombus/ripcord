/* See rc_audio.h for the format, the one-unit rule and why this runs on the receive thread. */

#include "rc_audio.h"

#include "util/rc_log.h"

#include <3ds.h>
#include <opus/opus.h>

#include <string.h>

/*
 * A RING OF PCM, DRAINED INTO A FEW LARGE WAVE BUFFERS - not one wave buffer per Opus frame.
 *
 * 3dbrew's DSP_Memory_Region page documents a channel's queue as "Buffer[4]": a channel can hold four
 * queued buffers. One ndspWaveBuf per 10 ms Opus frame played, but clicked, and neither a deeper queue
 * nor a rate trim could fix it:
 *
 *   8 buffers  -> 44 drops/min at  80 ms lag
 *   16 buffers -> 25 drops/min at 160 ms lag, depth pinned at the ceiling
 *   + rate loop, +0.94%  -> depth 5.3, still 22 drops
 *   + integrating loop, +3.0% (clamped) -> depth 4.1, still 28 drops
 *
 * At +3% NDSP was asked to consume 49,440 samples a second while we supplied 47,760, and the queue still
 * filled. No clock mismatch can do that; the buffers had no slot to go into.
 *
 * So decode into a ring and refill a handful of large wave buffers from it. Arrival is not steady -
 * audio packets come in alongside video, and a keyframe burst delivers several frames at once - and the
 * ring absorbs that. The rate loop steers ring occupancy, because long-run drift between the
 * console's clock and the DSP's is real; it is not what causes the clicks.
 *
 * RC_AUDIO_WAVE_SAMPLES and the ring target live in the header - the target is derived from the buffer
 * size on purpose, see there.
 *
 * THREE BUFFERS, BECAUSE THE HARDWARE HAS FOUR SLOTS. Three leaves a slot spare so a refill never races
 * the one currently playing.
 *
 * 20 ms each. The same page gives "160 samples per audio frame" as the mixer's granularity, i.e. ~4.9 ms
 * at the DSP's native 32728 Hz - so a 20 ms buffer is about four mixer frames, comfortably above the unit
 * NDSP schedules in. 10 ms buffers are barely two. 33 ms works, but buys latency for no benefit.
 */
#define RC_AUDIO_WAVE_BUFFERS 3
#define RC_AUDIO_WAVE_BYTES (RC_AUDIO_WAVE_SAMPLES * RC_AUDIO_CHANNELS * (int)sizeof(int16_t))

#define RC_AUDIO_CHANNEL 0

/* The rate loop's target, RC_AUDIO_TARGET_SAMPLES, lives in the header (callers report against it). */
#define RATE_ADJUST_INTERVAL 25       /* frames between corrections - 250 ms, far slower than the drift */
/*
 * Integral gain: fraction of the depth error ADDED to the running trim each interval. Small because it
 * accumulates - at 250 ms per step this reaches a 1.3% offset in a couple of seconds without overshoot.
 */
#define RATE_ADJUST_GAIN 0.01f
/*
 * +/-0.5%, AND THE LIMIT IS AN AUDIBILITY BUDGET, NOT A CONTROL PARAMETER. A rate trim may only ever be
 * inaudible. Widened to 3% the loop pinned there, and 3% is about half a semitone: hardware reported the
 * audio drifting out of tune. 0.5% is roughly a twelfth of a semitone and ample for genuine clock drift
 * - the one run where the loop settled rather than saturating measured +2460 ppm.
 *
 * If drift genuinely exceeds this, the answer is to drop or duplicate a frame occasionally, not to bend
 * the pitch.
 */
#define RATE_ADJUST_LIMIT 0.005f

static int s_since_adjust;
static float s_trim;   /* running playback-rate correction; integrates, see rc_audio_submit */

static OpusDecoder *s_decoder;
static ndspWaveBuf s_wave[RC_AUDIO_WAVE_BUFFERS];
static int16_t *s_pcm;                 /* linear: the wave buffers NDSP reads by DMA */
static int16_t s_ring[RC_AUDIO_RING_SAMPLES * RC_AUDIO_CHANNELS];  /* plain RAM: the CPU-side queue */
static int s_ring_write, s_ring_read, s_ring_fill;
static int s_ndsp_up;

int rc_audio_init(rc_audio *out)
{
    int err = 0;
    int i;

    if (out == NULL)
        return 0;
    memset(out, 0, sizeof(*out));

    if (R_FAILED(ndspInit())) {
        /* Missing DSP firmware is the usual cause and is a property of the console, not of this code:
         * NDSP needs a dsp1.bin dumped from the handheld. Worth naming, because "no audio" with no
         * explanation sends people looking in the wrong place. */
        rc_log("\x1b[33mNOTE\x1b[0m ndspInit failed - no audio. Is dsp1.bin dumped to the SD card?\n");
        return 0;
    }
    s_ndsp_up = 1;

    ndspSetOutputMode(NDSP_OUTPUT_STEREO);
    /*
     * POLYPHASE, the highest-quality mode NDSP offers. The 3DS DSP runs natively at 32728 Hz, so this
     * 48 kHz channel is resampled down by a ratio of 0.68 on every sample - the widest conversion in the
     * pipeline. NONE was nearest-neighbour decimation and audibly wrong; LINEAR is a two-tap
     * approximation on a 2:3 ratio, which is where "slightly out of tune" comes from.
     */
    ndspChnSetInterp(RC_AUDIO_CHANNEL, NDSP_INTERP_POLYPHASE);
    ndspChnSetRate(RC_AUDIO_CHANNEL, (float)RC_AUDIO_SAMPLE_RATE);
    ndspChnSetFormat(RC_AUDIO_CHANNEL, NDSP_FORMAT_STEREO_PCM16);

    /* DMA'd by the DSP, so linear memory and an explicit flush after every write. */
    s_pcm = (int16_t *)linearAlloc((size_t)RC_AUDIO_WAVE_BYTES * RC_AUDIO_WAVE_BUFFERS);
    if (s_pcm == NULL) {
        rc_log("\x1b[33mNOTE\x1b[0m no linear memory for audio buffers - no audio\n");
        ndspExit();
        s_ndsp_up = 0;
        return 0;
    }
    memset(s_pcm, 0, (size_t)RC_AUDIO_WAVE_BYTES * RC_AUDIO_WAVE_BUFFERS);
    memset(s_ring, 0, sizeof(s_ring));
    s_ring_write = s_ring_read = s_ring_fill = 0;

    memset(s_wave, 0, sizeof(s_wave));
    for (i = 0; i < RC_AUDIO_WAVE_BUFFERS; i++) {
        s_wave[i].data_vaddr = s_pcm + (size_t)i * RC_AUDIO_WAVE_SAMPLES * RC_AUDIO_CHANNELS;
        s_wave[i].nsamples = RC_AUDIO_WAVE_SAMPLES;
        s_wave[i].status = NDSP_WBUF_DONE;   /* free from the start */
    }

    s_decoder = opus_decoder_create(RC_AUDIO_SAMPLE_RATE, RC_AUDIO_CHANNELS, &err);
    if (s_decoder == NULL || err != OPUS_OK) {
        rc_log("\x1b[33mNOTE\x1b[0m opus_decoder_create failed (%d) - no audio\n", err);
        linearFree(s_pcm);
        s_pcm = NULL;
        ndspExit();
        s_ndsp_up = 0;
        return 0;
    }

    s_since_adjust = 0;
    s_trim = 0.0f;
    out->ready = 1;
    rc_log("audio: Opus %d Hz stereo -> %d ms ring -> %d x %d ms NDSP buffers\n",
        RC_AUDIO_SAMPLE_RATE, RC_AUDIO_RING_SAMPLES * 1000 / RC_AUDIO_SAMPLE_RATE,
        RC_AUDIO_WAVE_BUFFERS, RC_AUDIO_WAVE_SAMPLES * 1000 / RC_AUDIO_SAMPLE_RATE);
    return 1;
}

/* Copies `samples` frames out of the ring into `dst`, wrapping. Caller has checked availability. */
static void ring_read(int16_t *dst, int samples)
{
    int first = RC_AUDIO_RING_SAMPLES - s_ring_read;

    if (first > samples)
        first = samples;
    memcpy(dst, s_ring + (size_t)s_ring_read * RC_AUDIO_CHANNELS,
           (size_t)first * RC_AUDIO_CHANNELS * sizeof(int16_t));
    if (samples > first) {
        memcpy(dst + (size_t)first * RC_AUDIO_CHANNELS, s_ring,
               (size_t)(samples - first) * RC_AUDIO_CHANNELS * sizeof(int16_t));
    }
    s_ring_read = (s_ring_read + samples) % RC_AUDIO_RING_SAMPLES;
    s_ring_fill -= samples;
}

/*
 * Refill any finished wave buffer from the ring. Called after every decode, so NDSP is topped up at the
 * rate audio arrives rather than on a timer of its own.
 *
 * A buffer that comes due with less than a full load in the ring is left alone rather than queued short:
 * a partial buffer is a gap in the output, which is a click. Counted as `starved`, which is the honest
 * name for it and distinguishes it from the ring overflowing.
 */
static void service_wave_buffers(rc_audio *audio)
{
    int i;

    for (i = 0; i < RC_AUDIO_WAVE_BUFFERS; i++) {
        if (s_wave[i].status != NDSP_WBUF_DONE && s_wave[i].status != NDSP_WBUF_FREE)
            continue;
        if (s_ring_fill < RC_AUDIO_WAVE_SAMPLES) {
            audio->starved++;
            return;
        }
        ring_read((int16_t *)s_wave[i].data_vaddr, RC_AUDIO_WAVE_SAMPLES);
        s_wave[i].nsamples = RC_AUDIO_WAVE_SAMPLES;
        DSP_FlushDataCache(s_wave[i].data_vaddr, (u32)RC_AUDIO_WAVE_BYTES);
        ndspChnWaveBufAdd(RC_AUDIO_CHANNEL, &s_wave[i]);
    }
}

void rc_audio_submit(rc_audio *audio, const uint8_t *frame, size_t length)
{
    int16_t decoded_pcm[RC_AUDIO_FRAME_SAMPLES * RC_AUDIO_CHANNELS];
    int decoded, first;

    if (audio == NULL || !audio->ready || frame == NULL || length == 0)
        return;

    if (RC_AUDIO_RING_SAMPLES - s_ring_fill < RC_AUDIO_FRAME_SAMPLES) {
        /* Ring full: we are ahead of the DSP by the whole ring. Drop rather than overwrite audio that
         * has not played yet - and let the rate loop below pull us back. */
        audio->queue_full++;
        service_wave_buffers(audio);
        return;
    }

    /*
     * length is the SOURCE unit only - see rc_audio.h. Passing the whole packet payload here is the
     * documented way to corrupt everything above ~5 kHz, and it would look like a codec bug rather than a
     * framing one.
     */
    decoded = opus_decode(s_decoder, frame, (opus_int32)length,
                          decoded_pcm, RC_AUDIO_FRAME_SAMPLES, 0);
    if (decoded <= 0) {
        audio->decode_errors++;
        if (audio->first_error == 0)
            audio->first_error = decoded;
        return;
    }

    /* Into the ring, wrapping. */
    first = RC_AUDIO_RING_SAMPLES - s_ring_write;
    if (first > decoded)
        first = decoded;
    memcpy(s_ring + (size_t)s_ring_write * RC_AUDIO_CHANNELS, decoded_pcm,
           (size_t)first * RC_AUDIO_CHANNELS * sizeof(int16_t));
    if (decoded > first) {
        memcpy(s_ring, decoded_pcm + (size_t)first * RC_AUDIO_CHANNELS,
               (size_t)(decoded - first) * RC_AUDIO_CHANNELS * sizeof(int16_t));
    }
    s_ring_write = (s_ring_write + decoded) % RC_AUDIO_RING_SAMPLES;
    s_ring_fill += decoded;
    audio->frames_decoded++;

    service_wave_buffers(audio);

    audio->depth_sum += s_ring_fill;
    audio->depth_samples++;
    if (s_ring_fill > audio->depth_peak)
        audio->depth_peak = s_ring_fill;

    /*
     * The rate loop, steering RING OCCUPANCY against long-run drift between the console's clock and the
     * DSP's. It integrates: a proportional-only version could not hold a steady offset at zero error and
     * settled at whatever error produced the trim it needed, reading the same +9375 ppm across runs and
     * across a tripled clamp.
     */
    if (++s_since_adjust >= RATE_ADJUST_INTERVAL) {
        float error = (float)(s_ring_fill - RC_AUDIO_TARGET_SAMPLES) / (float)RC_AUDIO_RING_SAMPLES;

        s_since_adjust = 0;
        s_trim += error * RATE_ADJUST_GAIN;
        if (s_trim > RATE_ADJUST_LIMIT)
            s_trim = RATE_ADJUST_LIMIT;
        if (s_trim < -RATE_ADJUST_LIMIT)
            s_trim = -RATE_ADJUST_LIMIT;
        ndspChnSetRate(RC_AUDIO_CHANNEL, (float)RC_AUDIO_SAMPLE_RATE * (1.0f + s_trim));
        audio->rate_trim_ppm = (int)(s_trim * 1000000.0f);
    }
}

void rc_audio_exit(rc_audio *audio)
{
    if (audio != NULL)
        audio->ready = 0;

    if (s_decoder != NULL) {
        opus_decoder_destroy(s_decoder);
        s_decoder = NULL;
    }
    if (s_ndsp_up) {
        ndspChnWaveBufClear(RC_AUDIO_CHANNEL);
        ndspExit();
        s_ndsp_up = 0;
    }
    if (s_pcm != NULL) {
        linearFree(s_pcm);
        s_pcm = NULL;
    }
}
