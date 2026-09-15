#include "rc_audio_ps3.h"

#include <audio/audio.h>
#include <opus.h>

#include <string.h>

#include "platform/rc_platform.h"

static OpusDecoder *s_decoder;
static audioPortConfig s_config;
static u32 s_port;
static int s_open;
static rc_audio_stats s_stats;

/* Sample ring, interleaved, one float per sample. Written by the decoder, read by the port filler. */
static float s_ring[RC_AUDIO_RING_SAMPLES * RC_AUDIO_CHANNELS];
static unsigned s_head;      /* next sample-pair to read  */
static unsigned s_tail;      /* next sample-pair to write */
static unsigned s_fill;      /* pairs currently held      */

/* Where we are writing in the hardware's block ring. */
static unsigned s_write_block;

static float s_scratch[RC_AUDIO_FRAME_SAMPLES * 6 * RC_AUDIO_CHANNELS];

/*
 * PSL1GHT declares audioPortConfig.readIndex as "index of currently read block", while the syscall it
 * wraps returns an ADDRESS holding that index. Which one arrives is settled by looking at it rather than
 * by picking: a block index is less than numBlocks (8, 16 or 32), and an address is not. Recorded in the
 * stats so the answer is in the log rather than in this comment.
 */
static unsigned current_read_block(void)
{
    if (s_stats.read_index_is_address)
        return (unsigned)(*(volatile u64 *)(uintptr_t)s_config.readIndex % s_config.numBlocks);
    return (unsigned)(s_config.readIndex % s_config.numBlocks);
}

int rc_audio_init(void)
{
    audioPortParam param;
    int err = 0;

    memset(&s_stats, 0, sizeof(s_stats));
    s_head = s_tail = s_fill = 0;
    s_write_block = 0;

    if (audioInit() != 0) {
        s_stats.last_error = -1;
        return 0;
    }

    memset(&param, 0, sizeof(param));
    param.numChannels = AUDIO_PORT_2CH;
    param.numBlocks = AUDIO_BLOCK_8;
    param.attrib = 0;
    param.level = 1.0f;

    if (audioPortOpen(&param, &s_port) != 0) {
        s_stats.last_error = -2;
        (void)audioQuit();
        return 0;
    }
    if (audioGetPortConfig(s_port, &s_config) != 0) {
        s_stats.last_error = -3;
        (void)audioPortClose(s_port);
        (void)audioQuit();
        return 0;
    }

    /* See current_read_block(). */
    s_stats.read_index_is_address = (s_config.readIndex >= (u32)s_config.numBlocks) ? 1 : 0;

    if (audioPortStart(s_port) != 0) {
        s_stats.last_error = -4;
        (void)audioPortClose(s_port);
        (void)audioQuit();
        return 0;
    }

    s_decoder = opus_decoder_create(RC_AUDIO_SAMPLE_RATE, RC_AUDIO_CHANNELS, &err);
    if (s_decoder == NULL) {
        s_stats.last_error = err;
        (void)audioPortStop(s_port);
        (void)audioPortClose(s_port);
        (void)audioQuit();
        return 0;
    }

    s_open = 1;
    s_stats.ready = 1;
    return 1;
}

void rc_audio_submit(const uint8_t *frame, size_t length)
{
    int samples;
    int i;

    if (!s_open || frame == NULL || length == 0u)
        return;

    s_stats.frames_in++;

    samples = opus_decode_float(s_decoder, frame, (opus_int32)length, s_scratch,
                                (int)(sizeof(s_scratch) / (sizeof(float) * RC_AUDIO_CHANNELS)), 0);
    if (samples <= 0) {
        s_stats.decode_errors++;
        s_stats.last_error = samples;
        return;
    }
    s_stats.frames_decoded++;

    for (i = 0; i < samples; i++) {
        if (s_fill == RC_AUDIO_RING_SAMPLES) {
            /* Dropped rather than blocked. Audio that cannot be played on time is not worth stalling the
             * receive loop for - and the loop is what empties the ring. */
            s_stats.ring_overflows++;
            break;
        }
        s_ring[s_tail * RC_AUDIO_CHANNELS + 0] = s_scratch[i * RC_AUDIO_CHANNELS + 0];
        s_ring[s_tail * RC_AUDIO_CHANNELS + 1] = s_scratch[i * RC_AUDIO_CHANNELS + 1];
        s_tail = (s_tail + 1u) % RC_AUDIO_RING_SAMPLES;
        s_fill++;
    }
    if (s_fill > s_stats.worst_ring)
        s_stats.worst_ring = s_fill;
}

void rc_audio_service(void)
{
    unsigned read_block;
    unsigned guard;

    if (!s_open)
        return;

    read_block = current_read_block();

    /*
     * Keep the hardware fed without running into the block it is reading. Two blocks of headroom: one
     * being played, one it is about to take.
     */
    for (guard = 0; guard < s_config.numBlocks; guard++) {
        unsigned ahead = (s_write_block + (unsigned)s_config.numBlocks - read_block)
                         % (unsigned)s_config.numBlocks;
        float *block;
        unsigned i;

        if (ahead >= (unsigned)s_config.numBlocks - 2u)
            break;

        /* Only write a whole block; a partial one would be a click. */
        if (s_fill < AUDIO_BLOCK_SAMPLES && s_stats.blocks_written > 0u)
            break;

        block = (float *)(uintptr_t)(s_config.audioDataStart
                + s_write_block * (unsigned)s_config.channelCount
                  * AUDIO_BLOCK_SAMPLES * (unsigned)sizeof(float));

        if (s_fill >= AUDIO_BLOCK_SAMPLES) {
            for (i = 0; i < AUDIO_BLOCK_SAMPLES; i++) {
                block[i * RC_AUDIO_CHANNELS + 0] = s_ring[s_head * RC_AUDIO_CHANNELS + 0];
                block[i * RC_AUDIO_CHANNELS + 1] = s_ring[s_head * RC_AUDIO_CHANNELS + 1];
                s_head = (s_head + 1u) % RC_AUDIO_RING_SAMPLES;
                s_fill--;
            }
        } else {
            /* Nothing to play yet. Silence keeps the ring moving rather than repeating the last block,
             * and it is counted because it is the one fault a listener can hear. */
            memset(block, 0, AUDIO_BLOCK_SAMPLES * RC_AUDIO_CHANNELS * sizeof(float));
            s_stats.silence_written++;
        }

        s_write_block = (s_write_block + 1u) % (unsigned)s_config.numBlocks;
        s_stats.blocks_written++;
    }
}

void rc_audio_stats_get(rc_audio_stats *out)
{
    if (out != NULL)
        *out = s_stats;
}

void rc_audio_shutdown(void)
{
    if (!s_open)
        return;
    s_open = 0;

    (void)audioPortStop(s_port);
    (void)audioPortClose(s_port);
    (void)audioQuit();
    if (s_decoder != NULL) {
        opus_decoder_destroy(s_decoder);
        s_decoder = NULL;
    }
}
