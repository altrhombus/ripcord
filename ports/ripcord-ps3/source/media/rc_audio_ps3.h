/*
 * ripcord-ps3 - Opus decode and playback.
 *
 * WHAT THE CONSOLE SENDS. 48 kHz stereo Opus in 10 ms frames - 480 samples per channel each, ~100 a
 * second. Those numbers are not this port's: src/Ripcord.Media.Audio/OpusAudioDecoder.cs fixes the rate
 * and ripcord-3ds reaches the same frame size independently. The stream carried 2,997 of them in every
 * thirty-second run through this port and discarded all of them until now.
 *
 * WHAT THE CONSOLE WANTS. PSL1GHT's audio port takes interleaved 32-bit FLOAT in fixed blocks of 256
 * samples per channel, in a ring of 8, 16 or 32 blocks that the hardware walks continuously. So there
 * are two mismatches to absorb: 480 against 256, and "whenever a packet arrives" against "every 5.33 ms,
 * forever". A sample ring between the decoder and the port absorbs both.
 *
 * ON THE THREAD THIS RUNS ON. The receive thread, which is also where the demuxer hands audio over.
 * That is the choice ripcord-3ds made and the reasoning carries: Opus is about 1% of a frame's work, it
 * does not need a thread, and this port has already spent three console lockups learning what a casually
 * added thread costs. The port's ring holds 42 ms at 8 blocks, and the receive loop comes round far
 * faster than that, so it can be topped up without being paced precisely.
 *
 * NO EVENT QUEUE, for the same reason. audioCreateNotifyEventQueue exists and is the textbook way to
 * pace writes, and it needs a thread to block on it. Writing whatever blocks are free each time round a
 * loop that already runs every few milliseconds needs neither.
 */
#ifndef RC_AUDIO_PS3_H
#define RC_AUDIO_PS3_H

#include <stddef.h>
#include <stdint.h>

/* Fixed by the protocol - see the header comment. Not configurable; the console does not vary them. */
#define RC_AUDIO_SAMPLE_RATE   48000
#define RC_AUDIO_CHANNELS      2
#define RC_AUDIO_FRAME_SAMPLES 480    /* 10 ms, per channel */

/*
 * Ring capacity in samples per channel. 8,192 is 170 ms, which is far more than the port's own 42 ms of
 * blocks needs - the margin is for the receive loop being busy, not for latency. A ring that cannot
 * cover one refill cannot sustain playback, which is a lesson ripcord-3ds paid for in clicks.
 */
#define RC_AUDIO_RING_SAMPLES  8192

/*
 * HOW MUCH AUDIO IS ALLOWED TO SIT AHEAD OF THE SPEAKER, which is latency and nothing else.
 *
 * Two buffers hold it, and the first clean run measured both: the hardware's block ring was being kept
 * six blocks ahead (1,536 samples, 32 ms) and this file's own ring reached 2,944 samples (61 ms) on top,
 * so about 93 ms of audio existed between the decoder and the speaker. None of it was needed - the run
 * underran exactly once, on the first block, before anything had been decoded at all.
 *
 * The lead is now four blocks rather than six, and the ring is trimmed to 960 samples when it drifts
 * past that. Together they bound it at about 41 ms.
 *
 * WHY TRIM RATHER THAN STEER. ripcord-3ds steers a rate loop toward a depth, which is the better answer
 * and needs a clock relationship this port does not have: there, audio and the DSP share a timebase. Here
 * the ring only grows when the receive loop has been busy, so the depth is a backlog rather than a phase
 * error, and discarding the oldest of it is both correct and audible only once. The trims are counted,
 * because a trim every second would mean the two clocks genuinely disagree and a rate loop is the answer
 * after all.
 */
#define RC_AUDIO_BLOCKS_AHEAD  4u
#define RC_AUDIO_TARGET_SAMPLES 960u   /* 20 ms */

typedef struct {
    int ready;                  /* the port opened and the decoder was created */
    int last_error;             /* whatever failed most recently */

    unsigned frames_in;         /* Opus frames handed over */
    unsigned frames_decoded;
    unsigned decode_errors;

    unsigned blocks_written;    /* blocks handed to the hardware */
    unsigned silence_written;   /* blocks that had to be filled with silence - the audible fault */
    unsigned ring_overflows;    /* decoded audio dropped because the ring was full */
    unsigned worst_ring;        /* deepest the ring ever got, in samples per channel */
    unsigned trims;             /* times the backlog was discarded down to the target */
    unsigned trimmed_samples;

    /* PSL1GHT names audioPortConfig.readIndex an index while cellAudio returns an address. Which it
     * turned out to be is recorded rather than assumed - see rc_audio_ps3.c. */
    int read_index_is_address;
} rc_audio_stats;

/* Opens the audio port and creates the decoder. Returns 1 on success; audio is optional, so a failure
 * here is reported and the stream continues without it. */
int  rc_audio_init(void);

/* One Opus frame, exactly as stream_demux emits it. Decodes into the ring. */
void rc_audio_submit(const uint8_t *frame, size_t length);

/* Moves whatever the ring holds into the hardware's blocks. Call often; it does nothing when there is
 * nothing to do. */
void rc_audio_service(void);

void rc_audio_stats_get(rc_audio_stats *out);
void rc_audio_shutdown(void);

#endif /* RC_AUDIO_PS3_H */
