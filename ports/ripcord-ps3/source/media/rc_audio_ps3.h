/*
 * ripcord-ps3 - Opus decode and playback.
 *
 * WHAT THE CONSOLE SENDS. 48 kHz stereo Opus in 10 ms frames - 480 samples per channel each, ~100 a
 * second. Those numbers are not this port's: src/Ripcord.Media.Audio/OpusAudioDecoder.cs fixes the rate
 * and ripcord-3ds reaches the same frame size independently. A thirty-second run carries 2,997 of them.
 *
 * WHAT THE CONSOLE WANTS. PSL1GHT's audio port takes interleaved 32-bit FLOAT in fixed blocks of 256
 * samples per channel, in a ring of 8, 16 or 32 blocks that the hardware walks continuously. So there
 * are two mismatches to absorb: 480 against 256, and "whenever a packet arrives" against "every 5.33 ms,
 * forever". A sample ring between the decoder and the port absorbs both.
 *
 * ON THE THREAD THIS RUNS ON. The receive thread, which is also where the demuxer hands audio over.
 * That is the choice ripcord-3ds made and the reasoning carries: Opus is about 1% of a frame's work, it
 * does not need a thread, and a casually added thread has locked this console up three times. The
 * port's ring holds 42 ms at 8 blocks, and the receive loop comes round far faster than that, so it
 * can be topped up without being paced precisely.
 *
 * NO EVENT QUEUE, for the same reason. audioCreateNotifyEventQueue is the textbook way to pace writes,
 * and it needs a thread to block on it.
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
 * HOW MUCH AUDIO SITS AHEAD OF THE SPEAKER, and why almost none of it is worth reclaiming.
 *
 * The first clean run held about 93 ms between the decoder and the speaker - six blocks of hardware lead
 * at 32 ms plus a ring reaching 61 - and underran exactly once, on the first block, before anything had
 * been decoded.
 *
 * b179 cut the lead to four blocks and trimmed the ring to 20 ms whenever it drifted past, which
 * bounded latency at 40 ms and discarded 167,488 samples in 251 trims - three and a half seconds of a
 * thirty second run, 11.6% of the audio, and it stuttered throughout.
 *
 * THE DEPTH IS THE JITTER BUFFER. Audio arrives in network bursts, so the ring fluctuates by design; a
 * trim set near the normal fluctuation discards the buffering that absorbs the bursts.
 *
 * So the lead is six blocks, and the trim is a runaway guard rather than a target: it fires only past
 * 100 ms, which normal jitter does not reach and genuine clock drift eventually would. The trims are
 * counted because a trim that fires regularly is the signal that the two clocks disagree.
 */
#define RC_AUDIO_BLOCKS_AHEAD  6u
#define RC_AUDIO_TARGET_SAMPLES 4800u   /* 100 ms - a ceiling, not a target */

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
