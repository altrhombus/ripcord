/*
 * ripcord-3ds - Opus decode to NDSP.
 *
 * THE FORMAT IS FIXED AND THE SPEC IS EXPLICIT ABOUT IT (docs/protocol/ps5-remoteplay-v1-spec.md §6):
 * Opus, 48 kHz, stereo, 480 samples per frame (10 ms), CELT-only fullband. Packets are type 0x03 and a
 * constant 268 bytes on this firmware.
 *
 * ONE UNIT PER FRAME, AND ONLY THE FIRST. Each packet's payload packs `total_units` equal-size units back
 * to back: unit 0 is the source Opus frame and the rest are independently encoded copies of the same
 * 10 ms, for loss concealment. The spec records what happens if you hand the decoder the lot - Opus/CELT
 * sizes its per-band bit budget from the packet BYTE LENGTH, so the redundant units get read as
 * high-frequency coefficients and every band above ~5 kHz is corrupted, audible as "underwater". That was
 * confirmed empirically on the wire: decoding just the first unit lifts L/R correlation from 0.13 to 0.58.
 *
 * stream_demux already does the strip - ingest_audio computes unit_size = payload / total_units and hands
 * up only unit 0 - so this module receives a clean ~80-byte frame and must not second-guess it.
 *
 * ON NDSP AND WHY THE OUTPUT BUFFERS ARE WHAT THEY ARE. NDSP plays from a queue of ndspWaveBuf entries in
 * LINEAR memory, and reads them by DMA rather than through the ARM11 cache, so each one has to be flushed
 * after it is written. A ring of buffers is needed because a wave buffer cannot be refilled while the
 * hardware is playing it; the count is a latency-versus-underrun trade, not a magic number.
 *
 * ON THE CORE THIS RUNS ON: the receive thread, deliberately. Putting Opus on core 3 was the plan, and
 * 3dbrew says core 3 is BASE-memregion only while core 2 needs exheader flag 0x2000 - flags that belong
 * to the Homebrew Launcher host, not to us. Threading the frame scale onto core 2 hard-locked the console
 * three times (see halyard_pairing_file.h). Audio is ~1% of a frame's work; it does not need a core, and
 * this port has already paid for finding out what happens when that assumption is made casually.
 */
#ifndef RC_AUDIO_H
#define RC_AUDIO_H

#include <stddef.h>
#include <stdint.h>

/* Fixed by the protocol - see the header comment. Not configurable, because the console does not vary it. */
#define RC_AUDIO_SAMPLE_RATE 48000
#define RC_AUDIO_CHANNELS    2
#define RC_AUDIO_FRAME_SAMPLES 480

/*
 * Ring capacity in samples per channel, and the depth the rate loop steers towards. The ring decouples
 * Opus's 10 ms frames from NDSP's wave buffers - see rc_audio.c for why tying them together did not work.
 */
#define RC_AUDIO_RING_SAMPLES 8192

/*
 * How much audio one NDSP buffer holds, and the ring level the rate loop steers towards.
 *
 * THE TARGET IS DERIVED FROM THE BUFFER SIZE, NOT PICKED. The first version set a 30 ms target against
 * 33 ms buffers, so every refill left the ring 160 samples short and the next buffer arrived starved -
 * 1,448 times in a 60-second run, which is what the residual clicking was. A ring that cannot cover one
 * refill cannot sustain playback, whatever else is tuned.
 *
 * Two buffers' worth: one to hand over immediately, one still in reserve when it is taken.
 */
#define RC_AUDIO_WAVE_SAMPLES 960                             /* 20 ms; ~4 DSP mixer frames */
#define RC_AUDIO_TARGET_SAMPLES (RC_AUDIO_WAVE_SAMPLES * 2)   /* 40 ms */

typedef struct {
    int ready;
    long frames_decoded;
    long decode_errors;
    long queue_full;     /* frames dropped because the ring had no room - see the .c */
    /*
     * Queue OCCUPANCY, which is the thing that actually determines audio latency - the buffer count is
     * only a ceiling. Each queued frame is 10 ms behind the picture, so peak occupancy is peak lag.
     */
    int depth_peak;              /* peak ring occupancy, in samples */
    long depth_sum, depth_samples;
    long starved;                /* times a wave buffer came due with too little audio to fill it */
    int first_error;     /* the first libopus error code, for diagnosis */
    int rate_trim_ppm;   /* last playback-rate correction, parts per million - see rc_audio.c */
} rc_audio;

/*
 * Brings up NDSP and the Opus decoder. Returns 1 on success, 0 having logged why - a failure here is not
 * fatal to a session, it just means no sound.
 */
int rc_audio_init(rc_audio *out);

/* Decodes one Opus frame (unit 0 of an audio packet) and queues it for playback. */
void rc_audio_submit(rc_audio *audio, const uint8_t *frame, size_t length);

void rc_audio_exit(rc_audio *audio);

#endif /* RC_AUDIO_H */
