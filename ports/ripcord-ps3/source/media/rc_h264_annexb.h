/*
 * ripcord-ps3 - Annex-B splitting and access-unit / picture-boundary tracking.
 *
 * TWO LAYERS, ONE FILE, BECAUSE THE LOWER ONE IS USELESS ALONE.
 *
 * `rc_h264_annexb` walks a byte buffer and yields NAL units. `rc_h264_au` takes those units, absorbs the
 * parameter sets, parses the slice headers, and answers the only question the layer above actually has:
 * where does one picture end and the next begin. Splitting without that answer just moves the problem.
 *
 * ZERO COPY, DELIBERATELY. A NAL unit is reported as a pointer into the caller's buffer and a length.
 * Nothing is copied, nothing is allocated, and the iterator holds no buffer of its own. The demuxer above
 * already has the whole frame in main memory; copying it again to hand out slices would be pure cost, and
 * on the target the cost lands where it hurts - an SPE has 256 KB of local store and no cache, so the
 * decoder DMAs slice bytes in from exactly these pointers. Same reasoning as the on-the-fly emulation-
 * prevention handling in rc_h264_bits.h, for the same machine.
 *
 * The buffer must therefore outlive the iteration, and must not be the receive buffer another thread is
 * still writing into.
 *
 * TRAILING ZEROS ARE NOT PART OF THE NAL UNIT, and trimming them is not cosmetic. Three separate things
 * put zero bytes between the end of one NAL and the next start code: `trailing_zero_8bits` (sec 7.4.1),
 * the `zero_byte` that makes a four-byte start code out of a three-byte one, and `cabac_zero_words` -
 * which matter here specifically, because this console encodes with CABAC and the padding is therefore a
 * live case rather than a theoretical one. One rule handles all three: after locating the bytes between
 * two start codes, walk back over any zeros. A unit left empty by that is not a NAL unit at all and is
 * skipped, counted rather than hidden, because a run of them means the buffer is damaged.
 *
 * WHERE AN ACCESS UNIT STARTS IS NOT WHERE ITS PICTURE STARTS. sec 7.4.1.2.3 puts an access delimiter,
 * the parameter sets and any SEI *before* the first slice of the picture they describe - so a caller
 * assembling byte ranges must open the new unit at that leading non-VCL NAL, not at the slice. The two
 * flags in `rc_h264_au_nal` are therefore separate and both are needed: `begins_access_unit` marks the
 * first NAL of the unit whatever its type, and `begins_picture` marks the slice that starts the primary
 * coded picture. For this console's stream, which sends no delimiters and repeats its parameter sets
 * rarely, the two usually coincide - which is exactly why they must not be conflated in code.
 *
 * BOUNDARIES COME FROM THE BITSTREAM, NOT FROM THE FRAMING. The 9297 framing layer carries a frame index
 * that is constant across a frame's fragments (docs/protocol/ps5-av-stream.md), and ripcord-3ds groups on
 * it. It is almost certainly right. It is also the demuxer's own opinion about its own output, so a
 * demuxer bug is invisible to it, and a client that trusts it cannot tell a lost fragment from a short
 * picture. sec 7.4.1.2.4 answers from the slice headers instead, which is the independent check - and the
 * two disagreeing is a finding worth having rather than a discrepancy to reconcile quietly.
 */

#ifndef RC_H264_ANNEXB_H
#define RC_H264_ANNEXB_H

#include "rc_h264_params.h"

#include <stddef.h>
#include <stdint.h>

/*
 * Parameter-set ids are bounded by the standard: sps_id is 0..31 and pps_id is 0..255 (sec 7.4.2). Both
 * tables are held in full rather than as a cache of the ones seen, because a partial table turns a
 * perfectly legal stream into a decode failure, and the whole structure is around twenty kilobytes -
 * nothing against 256 MB of XDR. h264_test.c prints the exact figure rather than this comment asserting
 * one that can drift.
 *
 * It is large enough to matter on a *stack*, though. Put an rc_h264_au in static or heap storage; PPU
 * thread stacks are sized per thread, and ripcord-3ds has a file that exists because its main thread had
 * 32 KB.
 */
#define RC_H264_MAX_SPS 32u
#define RC_H264_MAX_PPS 256u

/* A NAL unit located in the caller's buffer. `start` points at the header byte; `payload` at the byte
 * after it, which is what every parser in rc_h264_params.h expects. */
typedef struct {
    const uint8_t *start;
    size_t size;              /* header byte + RBSP, trailing zeros already removed */
    const uint8_t *payload;   /* start + 1 */
    size_t payload_size;      /* size - 1 */
    unsigned type;            /* nal_unit_type - one of RC_H264_NAL_* */
    unsigned ref_idc;         /* nal_ref_idc */
    int forbidden_zero;       /* sec 7.4.1 says this bit shall be zero, so a set bit is corruption */
} rc_h264_nal;

typedef struct {
    const uint8_t *data;
    size_t size;
    size_t pos;
    uint32_t empty_units;     /* start codes with nothing between them - a damage signal, not an error */
} rc_h264_annexb;

void rc_h264_annexb_init(rc_h264_annexb *it, const uint8_t *data, size_t size);

/* Yields the next NAL unit. Returns 1 and fills `out`, or 0 once the buffer is exhausted. Leading bytes
 * before the first start code are skipped, as are both start-code forms, so a buffer that begins
 * mid-stream costs nothing extra. */
int rc_h264_annexb_next(rc_h264_annexb *it, rc_h264_nal *out);

typedef enum {
    RC_H264_AU_ERROR = 0,     /* malformed, or a refusal - see below */
    RC_H264_AU_NEED_PARAMS,   /* well-formed slice naming a parameter set not yet seen */
    RC_H264_AU_NON_VCL,       /* a parameter set was absorbed, or a NAL with no picture in it */
    RC_H264_AU_SLICE          /* a coded slice; `out` says whether it opened a picture */
} rc_h264_au_result;

typedef struct {
    int begins_access_unit;   /* first NAL of a new access unit, whatever its type */
    int begins_picture;       /* slices only: starts a new primary coded picture (sec 7.4.1.2.4) */
    rc_h264_slice_header slice;
    const rc_h264_sps *sps;   /* the parameter sets in force, pointing into the tracker's tables */
    const rc_h264_pps *pps;
} rc_h264_au_nal;

typedef struct {
    rc_h264_sps sps[RC_H264_MAX_SPS];
    rc_h264_pps pps[RC_H264_MAX_PPS];
    uint8_t sps_valid[RC_H264_MAX_SPS];
    uint8_t pps_valid[RC_H264_MAX_PPS];

    rc_h264_slice_header prev;
    int have_prev;
    int started;              /* anything fed at all */
    int seen_slice;           /* a slice has arrived since the current access unit opened */

    /* Diagnostics. ripcord-3ds spent twelve hardware runs on a fault a printed number would have found
     * in one, so these are counted from the start rather than added when something goes wrong. */
    uint32_t access_units;
    uint32_t pictures;
    uint32_t slices;
    uint32_t dropped_no_params;
} rc_h264_au;

void rc_h264_au_init(rc_h264_au *au);

/*
 * Feeds one NAL unit, in decode order. `out` is always fully written, whatever the result.
 *
 * REFUSED, following the same pattern as the FMO refusal in rc_h264_params.h - a stream shape this
 * console demonstrably does not send, whose support would be a page of grammar defending a case that
 * cannot occur:
 *   - Data partitioning (nal_unit_type 2, 3 or 4). Extended profile only, and the measurement says Main.
 *     Partitions B and C carry no slice header at all, so the boundary rule cannot see them and grouping
 *     would silently go wrong rather than fail. If this fires, the stream is not what
 *     docs/protocol/ps5-av-stream.md describes, and that is the finding.
 *
 * A slice naming an unseen parameter set is RC_H264_AU_NEED_PARAMS, not an error: a client joining a
 * stream, or one that lost the fragment carrying the parameter sets, sees exactly this and should drop
 * slices until they arrive. No state is invented for such a slice - in particular `prev` is left alone,
 * so grouping resumes on the first slice that does parse, which will differ from the stale `prev` in
 * frame_num and so read as a new picture. That is the right answer for the wrong reason, and is recorded
 * here so nobody later mistakes it for a guarantee.
 */
rc_h264_au_result rc_h264_au_feed(rc_h264_au *au, const rc_h264_nal *nal, rc_h264_au_nal *out);

#endif /* RC_H264_ANNEXB_H */
