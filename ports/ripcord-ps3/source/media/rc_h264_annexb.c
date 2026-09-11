/*
 * ripcord-ps3 - Annex-B splitting and access-unit tracking. See rc_h264_annexb.h for the design, for why
 * nothing is copied, and for the one REFUSED case.
 */
#include "rc_h264_annexb.h"
#include "rc_h264_bits.h"

#include <string.h>

/*
 * Is there a start code at `pos`? Returns its length in `*out_len`, 3 or 4.
 *
 * Adapted from ripcord-3ds's next_start_code() in source/media/rc_mvd.c, which has the ordering right and
 * says why: the three-byte form is tested first, so a four-byte code is never mistaken for a three-byte
 * one preceded by a stray zero. Both forms occur in this stream - the demuxer emits four, and the
 * parameter sets the console copies out of its stream-info packet are not guaranteed to.
 */
static int rc_h264_start_code_at(const uint8_t *d, size_t size, size_t pos, size_t *out_len)
{
    if (pos + 3u <= size && d[pos] == 0x00u && d[pos + 1u] == 0x00u && d[pos + 2u] == 0x01u) {
        *out_len = 3u;
        return 1;
    }
    if (pos + 4u <= size && d[pos] == 0x00u && d[pos + 1u] == 0x00u
        && d[pos + 2u] == 0x00u && d[pos + 3u] == 0x01u) {
        *out_len = 4u;
        return 1;
    }
    return 0;
}

/* Finds the next start code at or after `from`. Returns 1 with its position and length, else 0. */
static int rc_h264_find_start_code(const uint8_t *d, size_t size, size_t from,
                                   size_t *out_pos, size_t *out_len)
{
    size_t i;

    for (i = from; i + 3u <= size; i++) {
        if (d[i] != 0x00u || d[i + 1u] != 0x00u) {
            continue;
        }
        if (rc_h264_start_code_at(d, size, i, out_len)) {
            *out_pos = i;
            return 1;
        }
    }
    return 0;
}

void rc_h264_annexb_init(rc_h264_annexb *it, const uint8_t *data, size_t size)
{
    memset(it, 0, sizeof(*it));
    it->data = data;
    it->size = (data != NULL) ? size : 0u;
}

int rc_h264_annexb_next(rc_h264_annexb *it, rc_h264_nal *out)
{
    memset(out, 0, sizeof(*out));

    for (;;) {
        size_t sc_pos = 0u;
        size_t sc_len = 0u;
        size_t next_pos = 0u;
        size_t next_len = 0u;
        size_t start;
        size_t end;

        if (it->data == NULL
            || !rc_h264_find_start_code(it->data, it->size, it->pos, &sc_pos, &sc_len)) {
            it->pos = it->size;
            return 0;
        }

        start = sc_pos + sc_len;
        if (!rc_h264_find_start_code(it->data, it->size, start, &next_pos, &next_len)) {
            next_pos = it->size;
        }
        it->pos = next_pos;

        /* The zeros between the end of this NAL and the next start code belong to neither - see the
         * header for the three things that put them there. */
        end = next_pos;
        while (end > start && it->data[end - 1u] == 0x00u) {
            end--;
        }

        if (end == start) {
            it->empty_units++;
            continue;
        }

        out->start = it->data + start;
        out->size = end - start;
        out->payload = out->start + 1u;
        out->payload_size = out->size - 1u;
        out->forbidden_zero = ((out->start[0] & 0x80u) != 0u) ? 1 : 0;
        out->ref_idc = ((unsigned)out->start[0] >> 5) & 0x03u;
        out->type = (unsigned)out->start[0] & 0x1fu;
        return 1;
    }
}

void rc_h264_au_init(rc_h264_au *au)
{
    memset(au, 0, sizeof(*au));
}

/*
 * Does a NAL of this type open an access unit when one is already in progress?
 *
 * sec 7.4.1.2.3 fixes the order within an access unit: the delimiter, then the parameter sets and SEI,
 * then the slices. So one of these arriving after a slice can only mean the previous unit ended. The
 * converse does not hold and is not assumed - a slice may open a unit too, which is the common case in
 * this stream because the console sends no delimiters.
 */
static int rc_h264_nal_opens_access_unit(unsigned type)
{
    return type == RC_H264_NAL_AUD
        || type == RC_H264_NAL_SPS
        || type == RC_H264_NAL_PPS
        || type == RC_H264_NAL_SEI;
}

/*
 * Reads pic_parameter_set_id out of a slice header without knowing any parameter set.
 *
 * There is a chicken-and-egg here: the slice header cannot be parsed without the SPS and PPS, because
 * frame_num's width comes from one and several fields exist only because of a flag in the other - but
 * which PPS applies is itself a field of that header. It is resolvable because the first three fields
 * are unconditional ue(v) values with nothing in front of them (sec 7.3.3):
 *
 *     first_mb_in_slice  ue(v)
 *     slice_type         ue(v)
 *     pic_parameter_set_id ue(v)
 *
 * This is the only place the slice grammar is written twice, and it is safe exactly because those three
 * have no conditions attached. Anything past them would be a second, divergent copy of the real parser,
 * and that is how a header ends up read at the wrong bit offset.
 */
static int rc_h264_peek_pps_id(const rc_h264_nal *nal, uint32_t *out_pps_id)
{
    rc_h264_bits br;
    uint32_t ignored;

    rc_h264_bits_init(&br, nal->payload, nal->payload_size);
    if (!rc_h264_bits_ue(&br, &ignored)) { return 0; }   /* first_mb_in_slice */
    if (!rc_h264_bits_ue(&br, &ignored)) { return 0; }   /* slice_type */
    return rc_h264_bits_ue(&br, out_pps_id);
}

rc_h264_au_result rc_h264_au_feed(rc_h264_au *au, const rc_h264_nal *nal, rc_h264_au_nal *out)
{
    int opens;

    memset(out, 0, sizeof(*out));

    if (nal == NULL || nal->start == NULL || nal->size == 0u) {
        return RC_H264_AU_ERROR;
    }
    if (nal->forbidden_zero) {
        return RC_H264_AU_ERROR;
    }

    /* REFUSED: data partitioning. See the header. */
    if (nal->type == 2u || nal->type == 3u || nal->type == 4u) {
        return RC_H264_AU_ERROR;
    }

    /* True when nothing has been fed yet, or when the access unit in progress ended at the last slice.
     * The same test serves both NAL classes; what differs is only whether it is consulted. */
    opens = (!au->started) || au->seen_slice;

    if (nal->type == RC_H264_NAL_SPS) {
        rc_h264_sps sps;

        if (!rc_h264_sps_parse(nal->payload, nal->payload_size, &sps) || sps.sps_id >= RC_H264_MAX_SPS) {
            return RC_H264_AU_ERROR;
        }
        au->sps[sps.sps_id] = sps;
        au->sps_valid[sps.sps_id] = 1u;
        out->sps = &au->sps[sps.sps_id];
    } else if (nal->type == RC_H264_NAL_PPS) {
        rc_h264_pps pps;

        /* A refusal from the PPS parser - FMO - arrives here as an error, and that is the intent: the
         * caller cannot decode such a stream either, so there is nothing softer to report. */
        if (!rc_h264_pps_parse(nal->payload, nal->payload_size, &pps) || pps.pps_id >= RC_H264_MAX_PPS) {
            return RC_H264_AU_ERROR;
        }
        au->pps[pps.pps_id] = pps;
        au->pps_valid[pps.pps_id] = 1u;
        out->pps = &au->pps[pps.pps_id];
        if (pps.sps_id < RC_H264_MAX_SPS && au->sps_valid[pps.sps_id]) {
            out->sps = &au->sps[pps.sps_id];
        }
    }

    if (nal->type != RC_H264_NAL_SLICE && nal->type != RC_H264_NAL_IDR_SLICE) {
        if (rc_h264_nal_opens_access_unit(nal->type) && opens) {
            out->begins_access_unit = 1;
            au->access_units++;
            au->seen_slice = 0;
        }
        au->started = 1;
        return RC_H264_AU_NON_VCL;
    }

    /* A slice. Resolve its parameter sets first - see rc_h264_peek_pps_id for why that is possible at
     * all before the header has been parsed. */
    {
        uint32_t pps_id = 0u;
        const rc_h264_sps *sps;
        const rc_h264_pps *pps;

        if (!rc_h264_peek_pps_id(nal, &pps_id)) {
            return RC_H264_AU_ERROR;
        }
        if (pps_id >= RC_H264_MAX_PPS || !au->pps_valid[pps_id]) {
            au->dropped_no_params++;
            return RC_H264_AU_NEED_PARAMS;
        }

        pps = &au->pps[pps_id];
        if (pps->sps_id >= RC_H264_MAX_SPS || !au->sps_valid[pps->sps_id]) {
            au->dropped_no_params++;
            return RC_H264_AU_NEED_PARAMS;
        }
        sps = &au->sps[pps->sps_id];

        if (!rc_h264_slice_header_parse(nal->payload, nal->payload_size, nal->ref_idc, nal->type,
                                        sps, pps, &out->slice)) {
            return RC_H264_AU_ERROR;
        }

        out->sps = sps;
        out->pps = pps;
        out->begins_picture =
            rc_h264_slice_begins_new_picture(&out->slice, au->have_prev ? &au->prev : NULL, sps);

        if (out->begins_picture) {
            au->pictures++;
            if (opens) {
                out->begins_access_unit = 1;
                au->access_units++;
            }
        }

        au->prev = out->slice;
        au->have_prev = 1;
        au->seen_slice = 1;
        au->started = 1;
        au->slices++;
        return RC_H264_AU_SLICE;
    }
}
