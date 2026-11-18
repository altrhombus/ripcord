/*
 * ripcord-ps3 - H.264 RBSP bit reader (Exp-Golomb, emulation-prevention aware).
 *
 * The bottom of this port's H.264 syntax reading: the parameter-set, slice-header and access-unit
 * parsers above it are all written against this. Decoding is the console's own decoder's job (cellVdec)
 * or openh264's; these parsers read what a caller needs to know about a stream before handing it over.
 *
 * EMULATION PREVENTION IS HANDLED HERE, ON THE FLY, AND THAT IS A DELIBERATE CHOICE.
 *
 * An encoder may not emit 0x000000, 0x000001, 0x000002 or 0x000003 inside a NAL payload, because the
 * first two are start codes. It therefore inserts a 0x03 after any 00 00 that would otherwise be
 * followed by one of those, and a decoder removes it again. Copying the payload into a scratch buffer
 * with the 0x03 bytes stripped would be fine for a 30-byte parameter set.
 *
 * A slice is not 30 bytes, and a slice header is read out of every one. Reading through the bytes in
 * place costs one counter and one branch per byte boundary and needs no scratch buffer. The
 * parameter-set parsers above get the same reader for free.
 *
 * ERRORS ARE STICKY AND THE VALUE IS ALWAYS DEFINED. Every read returns 1 on success and 0 on running
 * off the end of the buffer, and once a read has failed every subsequent one fails too. Callers may
 * therefore parse a whole structure and check once at the end rather than after every field, which is
 * what the parsers here do. On failure the out-parameter is set to zero rather than left alone:
 * -Wmaybe-uninitialized does not see through a function boundary.
 *
 * BIT ORDER. H.264 is most-significant-bit-first within each byte (ITU-T H.264 sec 7.2), which is the
 * opposite of the little-endian habits most of this port's other parsers have. Read u(1) first from bit
 * 7, not bit 0.
 */

#ifndef RC_H264_BITS_H
#define RC_H264_BITS_H

#include <stddef.h>
#include <stdint.h>

typedef struct {
    const uint8_t *data;   /* NAL payload, start code and one-byte NAL header already removed */
    size_t size;
    size_t byte;           /* index of the byte bits are currently being drawn from */
    unsigned bit;          /* 0..7, bits already consumed of data[byte], MSB first */
    unsigned zeros;        /* consecutive 0x00 bytes ending at the previous byte - EPB detection */
    size_t consumed;       /* payload bits read, excluding any emulation-prevention byte skipped */
    int failed;            /* sticky */
} rc_h264_bits;

/*
 * `payload` is the NAL unit with its start code and its one-byte header (forbidden_zero/nal_ref_idc/
 * nal_unit_type) already stripped. The reader does not look at the header, because the two callers that
 * care about nal_unit_type have already branched on it to decide which parser to run.
 */
void rc_h264_bits_init(rc_h264_bits *br, const uint8_t *payload, size_t size);

/* u(n), 1 <= n <= 32. */
int rc_h264_bits_u(rc_h264_bits *br, unsigned n, uint32_t *out);

/* u(1), as an int, because every caller of it is a flag and `if (flag)` reads better than `if (v == 1)`. */
int rc_h264_bits_flag(rc_h264_bits *br, int *out);

/*
 * ue(v) - unsigned Exp-Golomb. The leading-zero run is capped at 32: a corrupt or misaligned buffer
 * otherwise walks to the end of it counting zeros, and on a slice-sized payload that is a measurable
 * amount of work to reach a conclusion the first sixteen bits had already made inevitable.
 */
int rc_h264_bits_ue(rc_h264_bits *br, uint32_t *out);

/* se(v) - signed Exp-Golomb, the usual zig-zag mapping of ue(v). */
int rc_h264_bits_se(rc_h264_bits *br, int32_t *out);

/* 1 while no read has run off the end. */
int rc_h264_bits_ok(const rc_h264_bits *br);

/*
 * Bits consumed so far, counting only real payload bits - emulation-prevention bytes skipped along the
 * way are not included. Used by the tests to prove the skipping happened; a decoder does not need it.
 */
size_t rc_h264_bits_consumed(const rc_h264_bits *br);

#endif /* RC_H264_BITS_H */
