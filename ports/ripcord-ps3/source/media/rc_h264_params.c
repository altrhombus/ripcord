/*
 * ripcord-ps3 - H.264 parameter sets and slice headers. See rc_h264_params.h for scope, for where the
 * slice parser deliberately stops, and for the two REFUSED cases.
 */
#include "rc_h264_params.h"
#include "rc_h264_bits.h"

#include <string.h>

/* Profiles whose SPS carries the chroma_format_idc block (sec 7.3.2.1.1). Main (77) is not among them,
 * which is why the measured stream's SPS goes straight from seq_parameter_set_id to log2_max_frame_num. */
static int rc_h264_profile_has_chroma_block(uint32_t profile_idc)
{
    switch (profile_idc) {
    case 100u: case 110u: case 122u: case 244u:
    case 44u:  case 83u:  case 86u:  case 118u:
    case 128u: case 138u: case 139u: case 134u: case 135u:
        return 1;
    default:
        return 0;
    }
}

/*
 * scaling_list (sec 7.3.2.1.1.1). The values are not retained: this parser exists to locate fields, and
 * a scaling list only matters to a transform stage that does not live here. It must still be *consumed*
 * correctly or every field after it is read at the wrong bit offset - which is the failure mode that
 * makes a half-implemented SPS parser worse than none.
 */
static int rc_h264_skip_scaling_list(rc_h264_bits *br, unsigned size)
{
    int32_t last_scale = 8;
    int32_t next_scale = 8;
    unsigned j;

    for (j = 0u; j < size; j++) {
        if (next_scale != 0) {
            int32_t delta;
            if (!rc_h264_bits_se(br, &delta)) {
                return 0;
            }
            next_scale = (last_scale + delta + 256) % 256;
        }
        last_scale = (next_scale == 0) ? last_scale : next_scale;
    }
    return 1;
}

int rc_h264_sps_parse(const uint8_t *payload, size_t size, rc_h264_sps *out)
{
    rc_h264_bits br;
    uint32_t v;
    int flag;

    memset(out, 0, sizeof(*out));
    rc_h264_bits_init(&br, payload, size);

    if (!rc_h264_bits_u(&br, 8u, &out->profile_idc)) { return 0; }
    if (!rc_h264_bits_u(&br, 8u, &out->constraint_flags)) { return 0; }
    if (!rc_h264_bits_u(&br, 8u, &out->level_idc)) { return 0; }
    if (!rc_h264_bits_ue(&br, &out->sps_id)) { return 0; }

    /* Defaults for the profiles that omit the block entirely (sec 7.4.2.1.1). */
    out->chroma_format_idc = 1u;
    out->bit_depth_luma = 8u;
    out->bit_depth_chroma = 8u;

    if (rc_h264_profile_has_chroma_block(out->profile_idc)) {
        if (!rc_h264_bits_ue(&br, &out->chroma_format_idc)) { return 0; }
        if (out->chroma_format_idc == 3u) {
            if (!rc_h264_bits_flag(&br, &out->separate_colour_plane_flag)) { return 0; }
        }
        if (!rc_h264_bits_ue(&br, &v)) { return 0; }
        out->bit_depth_luma = 8u + v;
        if (!rc_h264_bits_ue(&br, &v)) { return 0; }
        out->bit_depth_chroma = 8u + v;
        if (!rc_h264_bits_flag(&br, &flag)) { return 0; }   /* qpprime_y_zero_transform_bypass_flag */

        if (!rc_h264_bits_flag(&br, &flag)) { return 0; }   /* seq_scaling_matrix_present_flag */
        if (flag) {
            unsigned count = (out->chroma_format_idc != 3u) ? 8u : 12u;
            unsigned i;
            for (i = 0u; i < count; i++) {
                int present;
                if (!rc_h264_bits_flag(&br, &present)) { return 0; }
                if (present && !rc_h264_skip_scaling_list(&br, (i < 6u) ? 16u : 64u)) { return 0; }
            }
        }
    }

    if (!rc_h264_bits_ue(&br, &v)) { return 0; }
    out->log2_max_frame_num = 4u + v;

    if (!rc_h264_bits_ue(&br, &out->pic_order_cnt_type)) { return 0; }
    if (out->pic_order_cnt_type == 0u) {
        if (!rc_h264_bits_ue(&br, &v)) { return 0; }
        out->log2_max_pic_order_cnt_lsb = 4u + v;
    } else if (out->pic_order_cnt_type == 1u) {
        uint32_t i;
        int32_t ignored;
        if (!rc_h264_bits_flag(&br, &out->delta_pic_order_always_zero_flag)) { return 0; }
        if (!rc_h264_bits_se(&br, &ignored)) { return 0; }   /* offset_for_non_ref_pic */
        if (!rc_h264_bits_se(&br, &ignored)) { return 0; }   /* offset_for_top_to_bottom_field */
        if (!rc_h264_bits_ue(&br, &out->num_ref_frames_in_pic_order_cnt_cycle)) { return 0; }
        /* The cycle itself is decoder state, but must be consumed - see rc_h264_skip_scaling_list. */
        for (i = 0u; i < out->num_ref_frames_in_pic_order_cnt_cycle; i++) {
            if (!rc_h264_bits_se(&br, &ignored)) { return 0; }
        }
    } else if (out->pic_order_cnt_type != 2u) {
        return 0;   /* only 0, 1 and 2 exist */
    }

    if (!rc_h264_bits_ue(&br, &out->max_num_ref_frames)) { return 0; }
    if (!rc_h264_bits_flag(&br, &out->gaps_in_frame_num_value_allowed_flag)) { return 0; }

    if (!rc_h264_bits_ue(&br, &v)) { return 0; }
    out->pic_width_in_mbs = v + 1u;
    if (!rc_h264_bits_ue(&br, &v)) { return 0; }
    out->pic_height_in_map_units = v + 1u;

    if (!rc_h264_bits_flag(&br, &out->frame_mbs_only_flag)) { return 0; }
    if (!out->frame_mbs_only_flag) {
        if (!rc_h264_bits_flag(&br, &out->mb_adaptive_frame_field_flag)) { return 0; }
    }
    if (!rc_h264_bits_flag(&br, &out->direct_8x8_inference_flag)) { return 0; }

    if (!rc_h264_bits_flag(&br, &out->frame_cropping_flag)) { return 0; }
    if (out->frame_cropping_flag) {
        if (!rc_h264_bits_ue(&br, &out->crop_left)) { return 0; }
        if (!rc_h264_bits_ue(&br, &out->crop_right)) { return 0; }
        if (!rc_h264_bits_ue(&br, &out->crop_top)) { return 0; }
        if (!rc_h264_bits_ue(&br, &out->crop_bottom)) { return 0; }
    }

    if (!rc_h264_bits_flag(&br, &out->vui_parameters_present_flag)) { return 0; }
    /* VUI is not parsed. It carries timing and bitstream-restriction hints a decoder can run without,
     * and its HRD sub-structures are long. Nothing below needs it; if frame rate is ever wanted from the
     * stream rather than from the launch spec, this is where it would come from. */

    /* sec 7.4.2.1.1: a frame is 2 map units tall when frame_mbs_only_flag is 0. */
    out->coded_width = out->pic_width_in_mbs * 16u;
    out->coded_height = (2u - (uint32_t)out->frame_mbs_only_flag) * out->pic_height_in_map_units * 16u;

    return rc_h264_bits_ok(&br);
}

int rc_h264_pps_parse(const uint8_t *payload, size_t size, rc_h264_pps *out)
{
    rc_h264_bits br;
    int32_t sv;

    memset(out, 0, sizeof(*out));
    rc_h264_bits_init(&br, payload, size);

    if (!rc_h264_bits_ue(&br, &out->pps_id)) { return 0; }
    if (!rc_h264_bits_ue(&br, &out->sps_id)) { return 0; }
    if (!rc_h264_bits_flag(&br, &out->entropy_coding_mode_flag)) { return 0; }
    if (!rc_h264_bits_flag(&br, &out->bottom_field_pic_order_in_frame_present_flag)) { return 0; }
    if (!rc_h264_bits_ue(&br, &out->num_slice_groups_minus1)) { return 0; }

    /* REFUSED - see the header. Main profile forbids FMO, the measurement says 0, and the slice-group
     * map grammar is a page of syntax for something this stream cannot contain. Refusing is the same
     * choice this project made for an unobserved ECDH curve: a loud stop beats a quiet guess. */
    if (out->num_slice_groups_minus1 != 0u) {
        return 0;
    }

    if (!rc_h264_bits_ue(&br, &out->num_ref_idx_l0_default_active_minus1)) { return 0; }
    if (!rc_h264_bits_ue(&br, &out->num_ref_idx_l1_default_active_minus1)) { return 0; }
    if (!rc_h264_bits_flag(&br, &out->weighted_pred_flag)) { return 0; }
    if (!rc_h264_bits_u(&br, 2u, &out->weighted_bipred_idc)) { return 0; }

    if (!rc_h264_bits_se(&br, &sv)) { return 0; }
    out->pic_init_qp = 26 + sv;
    if (!rc_h264_bits_se(&br, &sv)) { return 0; }
    out->pic_init_qs = 26 + sv;
    if (!rc_h264_bits_se(&br, &out->chroma_qp_index_offset)) { return 0; }

    if (!rc_h264_bits_flag(&br, &out->deblocking_filter_control_present_flag)) { return 0; }
    if (!rc_h264_bits_flag(&br, &out->constrained_intra_pred_flag)) { return 0; }
    if (!rc_h264_bits_flag(&br, &out->redundant_pic_cnt_present_flag)) { return 0; }

    /* The optional tail (transform_8x8_mode_flag, a second scaling matrix, second_chroma_qp_index_offset)
     * is present only when more RBSP data follows. Detecting that needs more_rbsp_data(), which means
     * finding the rbsp_stop_one_bit from the end of the buffer. Nothing here needs those fields - the
     * measured stream is Main, which has no 8x8 transform at all - so the parse ends here rather than
     * carrying a trailing-bits scan for three values no caller reads. */

    return rc_h264_bits_ok(&br);
}

int rc_h264_slice_header_parse(const uint8_t *payload, size_t size,
                               uint32_t nal_ref_idc, uint32_t nal_unit_type,
                               const rc_h264_sps *sps, const rc_h264_pps *pps,
                               rc_h264_slice_header *out)
{
    rc_h264_bits br;
    uint32_t v;

    memset(out, 0, sizeof(*out));

    if (sps == NULL || pps == NULL) {
        return 0;
    }
    /* REFUSED - a caller mismatch, caught here because the symptom otherwise is a frame_num read at the
     * wrong width, which looks like stream corruption rather than like the wiring mistake it is. */
    if (pps->sps_id != sps->sps_id) {
        return 0;
    }

    out->is_idr = (nal_unit_type == RC_H264_NAL_IDR_SLICE) ? 1 : 0;
    out->nal_ref_idc = nal_ref_idc;

    rc_h264_bits_init(&br, payload, size);

    if (!rc_h264_bits_ue(&br, &out->first_mb_in_slice)) { return 0; }
    if (!rc_h264_bits_ue(&br, &out->slice_type)) { return 0; }
    if (out->slice_type > 9u) { return 0; }
    out->slice_type_base = out->slice_type % 5u;
    if (!rc_h264_bits_ue(&br, &out->pps_id)) { return 0; }

    if (sps->separate_colour_plane_flag) {
        if (!rc_h264_bits_u(&br, 2u, &v)) { return 0; }   /* colour_plane_id */
    }

    if (!rc_h264_bits_u(&br, sps->log2_max_frame_num, &out->frame_num)) { return 0; }

    if (!sps->frame_mbs_only_flag) {
        if (!rc_h264_bits_flag(&br, &out->field_pic_flag)) { return 0; }
        if (out->field_pic_flag) {
            if (!rc_h264_bits_flag(&br, &out->bottom_field_flag)) { return 0; }
        }
    }

    if (out->is_idr) {
        if (!rc_h264_bits_ue(&br, &out->idr_pic_id)) { return 0; }
    }

    if (sps->pic_order_cnt_type == 0u) {
        if (!rc_h264_bits_u(&br, sps->log2_max_pic_order_cnt_lsb, &out->pic_order_cnt_lsb)) { return 0; }
        if (pps->bottom_field_pic_order_in_frame_present_flag && !out->field_pic_flag) {
            if (!rc_h264_bits_se(&br, &out->delta_pic_order_cnt_bottom)) { return 0; }
        }
    } else if (sps->pic_order_cnt_type == 1u && !sps->delta_pic_order_always_zero_flag) {
        if (!rc_h264_bits_se(&br, &out->delta_pic_order_cnt[0])) { return 0; }
        if (pps->bottom_field_pic_order_in_frame_present_flag && !out->field_pic_flag) {
            if (!rc_h264_bits_se(&br, &out->delta_pic_order_cnt[1])) { return 0; }
        }
    }

    if (pps->redundant_pic_cnt_present_flag) {
        if (!rc_h264_bits_ue(&br, &out->redundant_pic_cnt)) { return 0; }
    }

    /* STOPS HERE, deliberately - see rc_h264_params.h. */
    return rc_h264_bits_ok(&br);
}

int rc_h264_slice_begins_new_picture(const rc_h264_slice_header *slice,
                                     const rc_h264_slice_header *prev,
                                     const rc_h264_sps *sps)
{
    if (prev == NULL) {
        return 1;
    }

    /* sec 7.4.1.2.4, restricted to the fields this parser reads. Each of these differing is sufficient
     * on its own; none of them differing means the slice continues the current picture. */
    if (slice->frame_num != prev->frame_num) { return 1; }
    if (slice->pps_id != prev->pps_id) { return 1; }
    if (slice->field_pic_flag != prev->field_pic_flag) { return 1; }
    if (slice->field_pic_flag && slice->bottom_field_flag != prev->bottom_field_flag) { return 1; }
    if ((slice->nal_ref_idc == 0u) != (prev->nal_ref_idc == 0u)) { return 1; }
    if (slice->is_idr != prev->is_idr) { return 1; }
    if (slice->is_idr && prev->is_idr && slice->idr_pic_id != prev->idr_pic_id) { return 1; }

    if (sps != NULL && sps->pic_order_cnt_type == 0u) {
        if (slice->pic_order_cnt_lsb != prev->pic_order_cnt_lsb) { return 1; }
        if (slice->delta_pic_order_cnt_bottom != prev->delta_pic_order_cnt_bottom) { return 1; }
    }
    if (sps != NULL && sps->pic_order_cnt_type == 1u) {
        if (slice->delta_pic_order_cnt[0] != prev->delta_pic_order_cnt[0]) { return 1; }
        if (slice->delta_pic_order_cnt[1] != prev->delta_pic_order_cnt[1]) { return 1; }
    }

    return 0;
}
