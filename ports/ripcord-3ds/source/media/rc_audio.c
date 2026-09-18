/* See rc_audio.h for the format, the one-unit rule and why this runs on the receive thread. */

#include "rc_audio.h"

#include "util/rc_log.h"

#include <3ds.h>
#include <opus/opus.h>

#include <string.h>

/*
 * A RING OF PCM, DRAINED INTO A FEW LARGE WAVE BUFFERS - not one wave buffer per Opus frame.
 *
 * The first design gave each 10 ms Opus frame its own ndspWaveBuf. It played, but clicked, and the
 * numbers eventually showed why the obvious fixes could not work:
 *
 *   8 buffers  -> 44 drops/min at  80 ms lag
 *   16 buffers -> 25 drops/min at 160 ms lag, depth pinned at the ceiling
 *   + rate loop, +0.94%  -> depth 5.3, still 22 drops
 *   + integrating loop, +3.0% (clamped) -> depth 4.1, still 28 drops
 *
 * The last one is the proof. At +3% we ask NDSP to consume 49,440 samples a second while supplying
 * 47,760 - three and a half percent MORE than we produce - and the queue still fills. No clock mismatch
 * can do that.
 *
 * 3dbrew's DSP_Memory_Region page names the cause outright: a channel's queue is "Buffer[4]". Sixteen
 * buffers were being handed to hardware with four slots, so most of them were never going to be accepted
 * however fast the DSP played. The audio path had been built around the codec's frame size instead of
 * the hardware's, and every fix attempted above was tuning around a structure that could not work.
 *
 * So decode into a ring and refill a handful of large wave buffers from it. Opus's 480-sample frames and
 * NDSP's scheduling are now independent: arrival lumpiness is absorbed by the ring, and NDSP sees a few
 * steady buffers instead of a hundred small ones. The rate loop stays, steering ring occupancy rather
 * than a buffer count, because long-run drift between the console's clock and the DSP's is real even
 * though it was never the thing causing the clicks.
 *
 * PREVIOUS DESIGN, kept for the record:
 *
 * Eight was tried first and produced 44 dropped frames a minute - a click roughly every 1.4 seconds,
 * audible and correctly reported. The counter was named `underruns`, which pointed at the wrong cause:
 * the drop happens when NO wave buffer is free, i.e. the DSP has not drained one yet. That is the queue
 * being FULL, not starved.
 *
 * The console sends exactly 100 frames a second and the DSP consumes exactly 100 a second, so a steady
 * stream would never fill an 80 ms queue. Arrival is not steady: audio packets come in alongside video,
 * and a keyframe burst delivers several frames at once while the DSP plays on its own clock. The queue
 * has to absorb the burst, not the average.
 *
 * The cost is latency - 160 ms of audio behind the picture, up from 80. For a game that is real, and if
 * it proves objectionable the honest fix is to make arrival less bursty rather than to shrink this back
 * and reintroduce the clicks. Watch queue_full: it should be zero, and a non-zero figure means the queue
 * is still too shallow for how lumpy the receive loop has become.
 *
 * RC_AUDIO_BUFFERS lives in the header: callers need it to interpret the depth figures.
 */
/*
 * RC_AUDIO_WAVE_SAMPLES and the ring target live in the header - the target is derived from the buffer
 * size on purpose, see there.
 *
 * THREE BUFFERS, BECAUSE THE HARDWARE HAS FOUR SLOTS. 3dbrew's DSP_Memory_Region page documents the
 * per-channel queue as "Buffer[4]" - a channel can hold four queued buffers, full stop.
 *
 * The first design queued SIXTEEN. That is four times what the hardware can hold, and it explains every
 * measurement that made no sense: the queue filled regardless of playback rate, and asking NDSP to
 * consume 3.5% more than we supplied changed nothing, because it could not drain what it had no room to
 * accept. Three leaves a slot spare so a refill never races the one currently playing.
 *
 * 20 ms each. The same page gives "160 samples per audio frame" as the mixer's granularity, i.e. ~4.9 ms
 * at the DSP's native 32728 Hz - so a 20 ms buffer is about four mixer frames, comfortably above the unit
 * NDSP schedules in. The original 10 ms buffers were barely two. 33 ms was tried in between and worked,
 * but buys latency for no benefit: what matters is clearing the mixer's granularity, not exceeding it.
 */
#define RC_AUDIO_WAVE_BUFFERS 3
#define RC_AUDIO_WAVE_BYTES (RC_AUDIO_WAVE_SAMPLES * RC_AUDIO_CHANNELS * (int)sizeof(int16_t))

#define RC_AUDIO_CHANNEL 0

/*
 * RC_AUDIO_TARGET_DEPTH lives in the header (callers report against it). 3 frames is enough to ride out
 * one lumpy drain without running dry, and low enough that lip-sync is not visibly wrong.
 */
#define RATE_ADJUST_INTERVAL 25       /* frames between corrections - 250 ms, far slower than the drift */
/*
 * Integral gain: fraction of the depth error ADDED to the running trim each interval. Small because it
 * accumulates - at 250 ms per step this reaches a 1.3% offset in a couple of seconds without overshoot.
 */
#define RATE_ADJUST_GAIN 0.01f
/*
 * +/-0.5%, AND THE LIMIT IS AN AUDIBILITY BUDGET, NOT A CONTROL PARAMETER.
 *
 * This was widened to 3% to give the loop more authority. It promptly pinned there - and 3% is about
 * half a semitone, which is audible as the audio drifting out of tune. That was reported from hardware
 * and it is exactly what the number predicts: the correction became a worse artefact than the thing it
 * was correcting.
 *
 * A rate trim may only ever be inaudible. 0.5% is roughly a twelfth of a semitone, under any reasonable
 * threshold, and it is ample for genuine clock drift - the one run where the loop settled rather than
 * saturating measured +2460 ppm, well inside this.
 *
 * If drift genuinely exceeds this, the answer is to drop or duplicate a frame occasionally, not to bend
 * the pitch. A rare click is a better failure than continuous detuning.
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
     * LINEAR, NOT NONE. The comment here used to say "48 kHz in, 48 kHz out - nothing to do", which is
     * simply wrong: the 3DS DSP runs natively at 32728 Hz, so a 48 kHz channel is resampled DOWN by a
     * third no matter what. NDSP_INTERP_NONE makes that a nearest-neighbour decimation - the cheapest
     * possible resampler on the widest ratio in the path. That is also why the rate loop has real work
     * to do at all.
     */
    /*
     * POLYPHASE, the highest-quality mode NDSP offers. This path resamples 48 kHz down to the DSP's
     * native 32728 Hz - a ratio of 0.68 on every sample, the widest conversion in the pipeline - so the
     * interpolator is not a detail. NONE was nearest-neighbour decimation and audibly wrong; LINEAR is a
     * two-tap approximation on a 2:3 ratio, which is where "slightly out of tune" comes from.
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
     * The rate loop, now steering RING OCCUPANCY. It integrates - a proportional-only version could not
     * hold a steady offset at zero error and settled at whatever error produced the trim it needed,
     * which is why it read the same +9375 ppm across runs and across a tripled clamp.
     *
     * Long-run drift between the console's clock and the DSP's is real and this still corrects it. It is
     * NOT what was causing the clicks: those were 100 tiny wave buffers a second, and the ring above is
     * the fix for that.
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
