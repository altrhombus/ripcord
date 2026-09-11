/*
 * ripcord-ps3 - H.264 parameter sets and slice headers (ITU-T H.264 sec 7.3.2.1, 7.3.2.2, 7.3.3).
 *
 * WHAT THIS IS FOR, AND WHERE IT STOPS.
 *
 * Two jobs, and neither of them is decoding. The first is telling a decoder how to configure itself -
 * resolution, entropy mode, reference-frame count. The second is finding picture boundaries in a stream
 * that carries one slice per MTU, which is what the demuxer above needs before it can hand anything to a
 * decoder at all.
 *
 * So the slice-header parser deliberately stops after `redundant_pic_cnt`. Everything up to that point is
 * what sec 7.4.1.2.4 uses to decide whether a slice begins a new primary coded picture; everything after
 * it - reference list modification, prediction weights, the marking process - is decoder state that only
 * the decoder can use. Parsing further would be writing decoder internals under a parser's name, and this
 * file would then be the wrong place to look for either. The stopping point is documented rather than
 * incidental, and `rc_h264_slice_header_parse` says what it did not read.
 *
 * MEASURED, NOT ASSUMED. docs/protocol/ps5-av-stream.md records what the console actually sends, parsed
 * out of two decrypted dumps: Main profile, CABAC, progressive, I and P slices only, one slice group,
 * 4:2:0, and one SPS and one PPS for a whole session. This file is written against the standard rather
 * than against that measurement - a parser that only handles what we happened to see is a parser that
 * fails silently the first time a firmware changes - with two deliberate exceptions where refusing is
 * better than guessing, both marked REFUSED below and both following the house pattern set by
 * "refuse an unobserved ECDH curve".
 *
 * NOT A CONFORMANCE DECODER. Syntax is read, semantics are mostly not checked. A stream that is
 * syntactically well-formed but semantically nonsense parses cleanly here and falls over later, which is
 * the right division: this layer's failures should mean "these bytes are not an H.264 parameter set",
 * not "this parameter set is unwise".
 */

#ifndef RC_H264_PARAMS_H
#define RC_H264_PARAMS_H

#include <stddef.h>
#include <stdint.h>

/* nal_unit_type values this port cares about (sec 7.4.1). */
#define RC_H264_NAL_SLICE      1u
#define RC_H264_NAL_IDR_SLICE  5u
#define RC_H264_NAL_SEI        6u
#define RC_H264_NAL_SPS        7u
#define RC_H264_NAL_PPS        8u
#define RC_H264_NAL_AUD        9u

/* slice_type % 5 (sec 7.4.3). The stream measured carries only I and P. */
#define RC_H264_SLICE_P   0u
#define RC_H264_SLICE_B   1u
#define RC_H264_SLICE_I   2u
#define RC_H264_SLICE_SP  3u
#define RC_H264_SLICE_SI  4u

typedef struct {
    uint32_t profile_idc;
    uint32_t constraint_flags;        /* the whole constraint-flags and reserved-bits byte, as sent */
    uint32_t level_idc;
    uint32_t sps_id;

    uint32_t chroma_format_idc;       /* 1 == 4:2:0. Defaults to 1 when the profile has no chroma block */
    int separate_colour_plane_flag;
    uint32_t bit_depth_luma;          /* already +8 */
    uint32_t bit_depth_chroma;        /* already +8 */

    uint32_t log2_max_frame_num;      /* already +4 - the width of frame_num in a slice header */
    uint32_t pic_order_cnt_type;
    uint32_t log2_max_pic_order_cnt_lsb;  /* already +4; meaningful when pic_order_cnt_type == 0 */
    int delta_pic_order_always_zero_flag; /* meaningful when pic_order_cnt_type == 1 */
    uint32_t num_ref_frames_in_pic_order_cnt_cycle;

    uint32_t max_num_ref_frames;
    int gaps_in_frame_num_value_allowed_flag;

    uint32_t pic_width_in_mbs;        /* already +1 */
    uint32_t pic_height_in_map_units; /* already +1 */
    int frame_mbs_only_flag;
    int mb_adaptive_frame_field_flag;
    int direct_8x8_inference_flag;

    int frame_cropping_flag;
    uint32_t crop_left, crop_right, crop_top, crop_bottom;  /* in crop units, not pixels */
    int vui_parameters_present_flag;

    /* Derived, in luma samples, before cropping is applied. A decoder allocates this; the display size
     * is smaller when frame_cropping_flag is set, which is how 640x368 coded carries 640x360 shown. */
    uint32_t coded_width;
    uint32_t coded_height;
} rc_h264_sps;

typedef struct {
    uint32_t pps_id;
    uint32_t sps_id;
    int entropy_coding_mode_flag;     /* 1 == CABAC. Measured 1 on this console */
    int bottom_field_pic_order_in_frame_present_flag;
    uint32_t num_slice_groups_minus1;
    uint32_t num_ref_idx_l0_default_active_minus1;
    uint32_t num_ref_idx_l1_default_active_minus1;
    int weighted_pred_flag;
    uint32_t weighted_bipred_idc;
    int32_t pic_init_qp;              /* already +26 */
    int32_t pic_init_qs;              /* already +26 */
    int32_t chroma_qp_index_offset;
    int deblocking_filter_control_present_flag;
    int constrained_intra_pred_flag;
    int redundant_pic_cnt_present_flag;
} rc_h264_pps;

typedef struct {
    uint32_t first_mb_in_slice;
    uint32_t slice_type;              /* as coded, 0..9 */
    uint32_t slice_type_base;         /* slice_type % 5 - one of RC_H264_SLICE_* */
    uint32_t pps_id;
    uint32_t frame_num;

    int field_pic_flag;
    int bottom_field_flag;

    int is_idr;                       /* from nal_unit_type, not from the header itself */
    uint32_t nal_ref_idc;
    uint32_t idr_pic_id;              /* meaningful when is_idr */

    uint32_t pic_order_cnt_lsb;       /* pic_order_cnt_type == 0 */
    int32_t delta_pic_order_cnt_bottom;
    int32_t delta_pic_order_cnt[2];   /* pic_order_cnt_type == 1 */

    uint32_t redundant_pic_cnt;
} rc_h264_slice_header;

/*
 * All three take the NAL payload with its start code and one-byte header already removed, and return 1
 * on success or 0 on a malformed or truncated one. Output structs are fully zeroed first, so a failed
 * parse leaves defined values rather than whatever the caller's stack held.
 *
 * REFUSED, both in the PPS parser:
 *   - num_slice_groups_minus1 != 0 (FMO). Main profile forbids it, the measurement confirms 0, and the
 *     slice-group map syntax is a page of grammar for a feature this stream cannot contain. If this ever
 *     fires, the stream is not what docs/protocol/ps5-av-stream.md describes and that is the finding.
 *   - A PPS whose sps_id does not match the SPS handed to the slice parser, which is a caller error
 *     rather than a stream one, but is cheaper to catch here than to debug as a wrong frame_num width.
 */
int rc_h264_sps_parse(const uint8_t *payload, size_t size, rc_h264_sps *out);
int rc_h264_pps_parse(const uint8_t *payload, size_t size, rc_h264_pps *out);

/*
 * The slice header needs both parameter sets: frame_num's width and the POC syntax come from the SPS,
 * and several fields are present only because of a PPS flag. `nal_ref_idc` and `nal_unit_type` come from
 * the NAL header byte the caller already stripped.
 *
 * Reads as far as redundant_pic_cnt and no further - see the file comment. Everything after that in
 * sec 7.3.3 is left unread, so this cannot be used to drive a decoder on its own, and is not meant to.
 */
int rc_h264_slice_header_parse(const uint8_t *payload, size_t size,
                               uint32_t nal_ref_idc, uint32_t nal_unit_type,
                               const rc_h264_sps *sps, const rc_h264_pps *pps,
                               rc_h264_slice_header *out);

/*
 * Does `slice` begin a new primary coded picture, given the previous slice in decode order?
 *
 * `prev` may be NULL, which answers yes - the first slice of a stream always begins a picture.
 *
 * ripcord-3ds's mvdreplay uses the cheap rule, `first_mb_in_slice == 0`, and documents why it is right
 * for this stream. It is not right in general: a picture whose slices are coded out of order (ASO) has a
 * later slice with first_mb_in_slice == 0, and two consecutive pictures could in principle both start at
 * zero without the rule being able to tell where one ended. sec 7.4.1.2.4 lists the real conditions, and
 * this implements the subset of them that the fields above can answer. The two rules agree on the
 * measured stream; keeping the fuller one means they will also agree on a stream we have not measured,
 * or disagree loudly rather than quietly.
 */
int rc_h264_slice_begins_new_picture(const rc_h264_slice_header *slice,
                                     const rc_h264_slice_header *prev,
                                     const rc_h264_sps *sps);

#endif /* RC_H264_PARAMS_H */
