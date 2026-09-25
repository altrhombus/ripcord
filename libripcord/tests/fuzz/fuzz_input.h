/*
 * libripcord fuzzing - splitting one fuzzer input into several datagrams.
 *
 * Most of what these harnesses exercise is stateful across packets: a reassembler holding a first
 * fragment, a demuxer halfway through a frame. A single-buffer harness can never reach the state a
 * second packet finds, so each input is read as a sequence of records, each a 2-byte big-endian length
 * followed by that many bytes. A length running past the end is truncated to what remains rather than
 * rejected, so every input the fuzzer can produce means something and none is wasted on a framing error.
 */
#ifndef RC_FUZZ_INPUT_H
#define RC_FUZZ_INPUT_H

#include <stddef.h>
#include <stdint.h>

typedef struct {
    const uint8_t *data;
    size_t remaining;
} rc_fuzz_input;

static inline void rc_fuzz_input_init(rc_fuzz_input *in, const uint8_t *data, size_t size)
{
    in->data = data;
    in->remaining = size;
}

/* Returns 1 and points *out at the next record, 0 when the input is exhausted. */
static inline int rc_fuzz_next(rc_fuzz_input *in, const uint8_t **out, size_t *out_length)
{
    size_t length;

    if (in->remaining < 2)
        return 0;
    length = ((size_t)in->data[0] << 8) | (size_t)in->data[1];
    in->data += 2;
    in->remaining -= 2;
    if (length > in->remaining)
        length = in->remaining;
    *out = in->data;
    *out_length = length;
    in->data += length;
    in->remaining -= length;
    return 1;
}

/* Reads every byte of a region a parser handed back, so AddressSanitizer checks the bounds it claimed
 * rather than only the ones the parser itself touched. The volatile sink keeps the loop from being
 * optimised away. */
static inline void rc_fuzz_touch(const uint8_t *data, size_t length)
{
    static volatile uint8_t sink;
    size_t i;

    for (i = 0; i < length; i++)
        sink = (uint8_t)(sink ^ data[i]);
}

#endif /* RC_FUZZ_INPUT_H */
