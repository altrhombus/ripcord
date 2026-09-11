/*
 * ripcord-ps3 - H.264 RBSP bit reader. See rc_h264_bits.h for why emulation prevention is handled here
 * rather than by copying the payload into a stripped scratch buffer.
 */
#include "rc_h264_bits.h"

/* ue(v) leading-zero cap - see the header. 32 is generous: the largest legal ue(v) is 2^32-2. */
#define RC_H264_MAX_LEADING_ZEROS 32u

void rc_h264_bits_init(rc_h264_bits *br, const uint8_t *payload, size_t size)
{
    br->data = payload;
    br->size = (payload != NULL) ? size : 0u;
    br->byte = 0u;
    br->bit = 0u;
    br->zeros = 0u;
    br->consumed = 0u;
    br->failed = 0;
}

int rc_h264_bits_ok(const rc_h264_bits *br)
{
    return br->failed == 0;
}

size_t rc_h264_bits_consumed(const rc_h264_bits *br)
{
    return br->consumed;
}

/*
 * Finish the current byte and step to the next one, skipping an emulation-prevention byte if this is
 * where one lands.
 *
 * The rule (ITU-T H.264 sec 7.4.1.1) is that a 0x03 immediately following two 0x00 bytes was inserted by
 * the encoder and is not part of the RBSP. `zeros` counts consecutive 0x00 bytes *consumed*, so it is
 * updated from the byte being left behind, not the one being entered - getting that backwards makes the
 * reader skip a legitimate 0x03 that merely happens to follow one zero byte.
 */
static void rc_h264_bits_advance(rc_h264_bits *br)
{
    br->zeros = (br->data[br->byte] == 0x00u) ? (br->zeros + 1u) : 0u;
    br->byte++;
    br->bit = 0u;

    if (br->zeros >= 2u && br->byte < br->size && br->data[br->byte] == 0x03u) {
        br->byte++;
        br->zeros = 0u;
    }
}

int rc_h264_bits_u(rc_h264_bits *br, unsigned n, uint32_t *out)
{
    uint32_t v = 0u;
    unsigned i;

    *out = 0u;
    if (br->failed || n == 0u || n > 32u) {
        br->failed = 1;
        return 0;
    }

    for (i = 0u; i < n; i++) {
        unsigned bitval;

        if (br->byte >= br->size) {
            br->failed = 1;
            return 0;
        }

        bitval = ((unsigned)br->data[br->byte] >> (7u - br->bit)) & 1u;
        v = (v << 1) | bitval;
        br->bit++;
        br->consumed++;

        if (br->bit == 8u) {
            rc_h264_bits_advance(br);
        }
    }

    *out = v;
    return 1;
}

int rc_h264_bits_flag(rc_h264_bits *br, int *out)
{
    uint32_t v;

    *out = 0;
    if (!rc_h264_bits_u(br, 1u, &v)) {
        return 0;
    }

    *out = (int)v;
    return 1;
}

int rc_h264_bits_ue(rc_h264_bits *br, uint32_t *out)
{
    unsigned leading = 0u;
    uint32_t bit;
    uint32_t suffix;

    *out = 0u;
    if (br->failed) {
        return 0;
    }

    for (;;) {
        if (!rc_h264_bits_u(br, 1u, &bit)) {
            return 0;
        }
        if (bit != 0u) {
            break;
        }
        leading++;
        if (leading > RC_H264_MAX_LEADING_ZEROS) {
            br->failed = 1;
            return 0;
        }
    }

    if (leading == 0u) {
        return 1; /* *out is already 0 */
    }

    /* codeNum = 2^leading - 1 + read(leading). leading == 32 would overflow the shift, so it is capped
     * one short of that above; 2^32-1 is not a representable codeNum anyway. */
    if (leading >= 32u) {
        br->failed = 1;
        return 0;
    }

    if (!rc_h264_bits_u(br, leading, &suffix)) {
        return 0;
    }

    *out = ((uint32_t)1u << leading) - 1u + suffix;
    return 1;
}

int rc_h264_bits_se(rc_h264_bits *br, int32_t *out)
{
    uint32_t k;

    *out = 0;
    if (!rc_h264_bits_ue(br, &k)) {
        return 0;
    }

    /* sec 9.1.1: codeNum 1,2,3,4 -> +1,-1,+2,-2. Odd maps positive, even negative.
     * The halves are computed in uint32_t and narrowed once, so the -2^31 edge cannot trap on the
     * negation of a signed minimum. */
    if ((k & 1u) != 0u) {
        *out = (int32_t)((k + 1u) / 2u);
    } else {
        *out = -(int32_t)(k / 2u);
    }
    return 1;
}
