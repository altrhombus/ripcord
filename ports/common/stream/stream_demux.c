#include "stream_demux.h"

#include <string.h>

static int passthrough_open_packet(void *ctx, uint8_t *packet, size_t packet_length,
    uint32_t key_position, int payload_offset)
{
    (void)ctx;
    (void)packet;
    (void)packet_length;
    (void)key_position;
    (void)payload_offset;
    return 1;
}

stream_demux_crypto stream_demux_passthrough_crypto(void)
{
    stream_demux_crypto c;
    c.open_packet = passthrough_open_packet;
    c.ctx = NULL;
    return c;
}

static int packet_crypto_open_packet(void *ctx, uint8_t *packet, size_t packet_length,
    uint32_t key_position, int payload_offset)
{
    stream_packet_crypto *crypto = (stream_packet_crypto *)ctx;

    if (!stream_packet_crypto_verify(crypto, key_position, packet, packet_length, STREAM_HEADER_TAG_OFFSET, 0))
        return 0;
    stream_packet_crypto_crypt_payload(crypto, key_position, packet + payload_offset,
        packet_length - (size_t)payload_offset);
    return 1;
}

stream_demux_crypto stream_demux_packet_crypto(stream_packet_crypto *crypto)
{
    stream_demux_crypto c;
    c.open_packet = packet_crypto_open_packet;
    c.ctx = crypto;
    return c;
}

void stream_demux_init(stream_demux *demux, stream_demux_crypto crypto, stream_demux_sink sink)
{
    memset(demux, 0, sizeof(*demux));
    demux->crypto = crypto;
    demux->sink = sink;
    demux->frame_index = -1;
}

/*
 * Classify the out-of-band parameter sets as HEVC or H.264 - ported from
 * HalyardStreamDemuxer.LooksLikeHevcParameterSets. The parameter sets are the one place the two codecs
 * cannot be confused: H.264 carries a 1-byte NAL header whose 5-bit type is 7 (SPS) or 8 (PPS); HEVC
 * carries 2 bytes whose 6-bit type is 32 (VPS), 33 (SPS) or 34 (PPS) - values H.264 cannot express. Slice
 * headers are NOT safe to classify this way (an ordinary H.264 non-IDR slice byte can read as a valid
 * HEVC type), so this only ever runs over the parameter sets themselves.
 */
static int looks_like_hevc_parameter_sets(const uint8_t *header, size_t length)
{
    size_t i;

    for (i = 0; i + 2 < length; i++) {
        size_t payload;
        uint8_t b0, b1;
        int hevc_type;
        int hevc_header_valid;

        if (header[i] != 0 || header[i + 1] != 0)
            continue;

        if (header[i + 2] == 0x01) {
            payload = i + 3;
        } else if (header[i + 2] == 0x00 && i + 3 < length && header[i + 3] == 0x01) {
            payload = i + 4;
        } else {
            continue;
        }

        if (payload + 1 >= length)
            break;

        b0 = header[payload];
        b1 = header[payload + 1];

        /* Validate the whole 2-byte HEVC header (forbidden_zero clear, nuh_layer_id 0,
         * nuh_temporal_id_plus1 == 1) - a bare 6-bit-type check alone lets through H.264 bytes like 0x41. */
        hevc_type = (b0 >> 1) & 0x3f;
        hevc_header_valid = (b0 & 0x80) == 0 && (b0 & 0x01) == 0 && b1 == 0x01;
        if (hevc_header_valid && (hevc_type == 32 || hevc_type == 33 || hevc_type == 34))
            return 1;

        if ((b0 & 0x80) == 0 && ((b0 & 0x1f) == 7 || (b0 & 0x1f) == 8))
            return 0;

        i = payload;
    }

    return 0;
}

void stream_demux_set_video_header(stream_demux *demux, const uint8_t *header, size_t length)
{
    size_t copy_length;

    if (length == 0)
        return;

    copy_length = length < sizeof(demux->video_header) ? length : sizeof(demux->video_header);
    memcpy(demux->video_header, header, copy_length);
    demux->video_header_length = copy_length;
    demux->video_is_hevc = looks_like_hevc_parameter_sets(demux->video_header, demux->video_header_length);
}

/* Copies the packet, verifies + decrypts it in place through the crypto seam, and hands back the mutable
 * buffer plus its length. `opened` must hold at least STREAM_PACKET_CRYPTO_MAX_PACKET bytes. */
static int try_open_media(stream_demux *demux, const stream_header *header, const uint8_t *packet,
    size_t packet_length, uint8_t *opened, size_t *opened_length)
{
    if (packet_length > STREAM_PACKET_CRYPTO_MAX_PACKET)
        return 0;

    memcpy(opened, packet, packet_length);
    *opened_length = packet_length;

    if (demux->crypto.open_packet(demux->crypto.ctx, opened, packet_length, header->key_position,
            stream_header_payload_offset(header))) {
        return 1;
    }

    demux->auth_failures++;
    return 0;
}

/* Append to the assembly buffer. Unlike the reference's Array.Resize, this port never allocates: an
 * append that would overflow STREAM_DEMUX_ASSEMBLY_CAPACITY marks the frame overflowed (flush_video_frame
 * then drops it rather than emitting a truncated, corrupt Annex-B stream) instead of growing. */
static void append_to_assembly(stream_demux *demux, const uint8_t *data, size_t length)
{
    if (demux->assembly_overflowed)
        return;
    if (demux->assembly_length + length > sizeof(demux->assembly)) {
        demux->assembly_overflowed = 1;
        return;
    }
    memcpy(demux->assembly + demux->assembly_length, data, length);
    demux->assembly_length += length;
}

/* On a frame-index change, report whole frames that went missing between the frame just finished and
 * new_frame_index (forward jumps only; a backward index is an out-of-order straggler). Per-frame slice
 * loss is reported from flush_video_frame after FEC has had its chance. */
static void check_for_frame_gap(stream_demux *demux, int new_frame_index)
{
    int expected = (demux->frame_index + 1) & 0xffff;
    int forward_gap = (new_frame_index - expected) & 0xffff;

    if (forward_gap > 0 && forward_gap < 0x8000 && demux->sink.video_loss_detected) {
        demux->sink.video_loss_detected(demux->sink.userdata, expected, (new_frame_index - 1) & 0xffff);
    }
}

/* Size the slot buffer for a new frame from its first-arriving unit (which fixes the common padded unit
 * size). Leaves frame_allocated false on invalid or oversized geometry so the frame is skipped rather than
 * risking a buffer overflow from console-controlled wire fields (see stream_demux.h's header comment). */
static void allocate_frame(stream_demux *demux, const stream_header *header, int data_size,
    const uint8_t *payload, size_t payload_length)
{
    int source, fec, slots, padded, stride;
    size_t buf_needed;

    demux->frame_allocated = 0;

    source = stream_header_source_units(header);
    fec = header->parity_units > 1 ? header->parity_units : 1;
    slots = source + fec;
    if (source <= 0 || slots > FEC_MAX_TOTAL_UNITS)
        return;

    /* Source units carry a 2-byte size-extension ADDED to the transmitted size to get the common coded
     * unit length all units share for FEC; parity units are already exactly that length. Both arrival
     * orders land on the same value, which is why this can key off whichever unit comes first. */
    padded = data_size;
    if (header->unit_index < source && payload_length >= STREAM_DEMUX_VIDEO_UNIT_PREFIX_LENGTH) {
        padded += (int)(((uint32_t)payload[0] << 8) | payload[1]);
    }
    if (padded <= 0)
        return;

    stride = (padded + 0xf) & ~0xf;
    if (stride > STREAM_DEMUX_MAX_UNIT_STRIDE)
        return;
    buf_needed = (size_t)slots * (size_t)stride;
    if (buf_needed > sizeof(demux->slot_buf))
        return;

    memset(demux->slot_buf, 0, buf_needed);
    memset(demux->slot_present, 0, (size_t)slots);
    memset(demux->slot_data_size, 0, (size_t)slots * sizeof(demux->slot_data_size[0]));

    demux->source_expected = source;
    demux->fec_expected = fec;
    demux->fec_actual = header->parity_units;
    demux->unit_padded_size = padded;
    demux->unit_stride = stride;
    demux->source_received = 0;
    demux->fec_received = 0;
    demux->frame_allocated = 1;
}

/* Copy one decrypted unit into its slot (indexed by unit index, so arrival order is irrelevant). */
static void place_unit(stream_demux *demux, const stream_header *header, int data_size,
    const uint8_t *payload, size_t payload_length)
{
    int idx = header->unit_index;

    if (idx < 0 || idx >= demux->source_expected + demux->fec_expected || demux->slot_present[idx] ||
        data_size > demux->unit_padded_size) {
        return;
    }

    memcpy(demux->slot_buf + (size_t)idx * (size_t)demux->unit_stride, payload, payload_length);
    demux->slot_present[idx] = 1;
    demux->slot_data_size[idx] = data_size;
    if (idx < demux->source_expected)
        demux->source_received++;
    else
        demux->fec_received++;
}

/* Whether the first present source unit's slice is an IDR (H.264 NAL type 5) or an HEVC IRAP picture.
 * Scans past the 2-byte unit prefix and the Annex-B start code (3- or 4-byte) to the NAL header byte. */
static int first_source_slice_is_idr(const stream_demux *demux)
{
    int i;

    /*
     * Defence in depth, NOT a fix for anything observed. flush_video_frame already refuses to call this
     * when frame_allocated is clear, so these values are sound by the time we get here. The check is
     * cheap and this function indexes slot_buf by console-controlled geometry, which is the class of
     * thing worth being unconditionally safe about - but it should not be mistaken for a diagnosis.
     */
    if (!demux->frame_allocated || demux->unit_stride <= 0
        || demux->source_expected <= 0 || demux->source_expected > FEC_MAX_TOTAL_UNITS) {
        return 0;
    }

    for (i = 0; i < demux->source_expected; i++) {
        const uint8_t *slice;
        int slice_length;
        int p;

        if (!demux->slot_present[i] || demux->slot_data_size[i] <= STREAM_DEMUX_VIDEO_UNIT_PREFIX_LENGTH)
            continue;

        slice = demux->slot_buf + (size_t)i * (size_t)demux->unit_stride + STREAM_DEMUX_VIDEO_UNIT_PREFIX_LENGTH;
        slice_length = demux->slot_data_size[i] - STREAM_DEMUX_VIDEO_UNIT_PREFIX_LENGTH;

        p = 0;
        while (p < slice_length && slice[p] == 0)
            p++;

        if (p < slice_length && slice[p] == 0x01 && p + 1 < slice_length) {
            uint8_t nal = slice[p + 1];

            if (demux->video_is_hevc) {
                int hevc_type = (nal >> 1) & 0x3f;
                return hevc_type >= 16 && hevc_type <= 21;
            }
            return (nal & 0x1f) == 5;
        }

        return 0; /* no parseable start code - treat as non-key */
    }

    return 0;
}

static void flush_video_frame(stream_demux *demux)
{
    long expected_units, received_units;
    int is_key;
    int incomplete = 0;
    int i;

    if (!demux->frame_allocated || demux->source_expected == 0) {
        demux->frame_allocated = 0;
        return;
    }

    /* Record wire loss for congestion feedback before FEC - FEC recovery doesn't change what the network
     * actually dropped. fec_actual (not the min-1 slot count) so a frame with no parity isn't false loss. */
    expected_units = demux->source_expected + demux->fec_actual;
    received_units = demux->source_received + demux->fec_received;
    demux->stat_units_received += received_units;
    if (expected_units > received_units)
        demux->stat_units_lost += expected_units - received_units;

    /* Recover missing source units from the parity units when enough total units survived. */
    if (demux->source_received < demux->source_expected &&
        demux->source_received + demux->fec_received >= demux->source_expected &&
        fec_reed_solomon_decode(demux->slot_buf, (size_t)demux->unit_padded_size, (size_t)demux->unit_stride,
            demux->source_expected, demux->fec_expected, demux->slot_present)) {
        for (i = 0; i < demux->source_expected; i++) {
            int off, padding;

            if (demux->slot_present[i])
                continue;

            off = i * demux->unit_stride;
            padding = (int)(((uint32_t)demux->slot_buf[off] << 8) | demux->slot_buf[off + 1]);
            if (padding < demux->unit_padded_size) {
                demux->slot_data_size[i] = demux->unit_padded_size - padding;
                demux->slot_present[i] = 1;
            }
        }
    }

    /* A frame is a keyframe iff its slices are IDR NAL units - drives both the keyframe flag and the
     * SPS/PPS re-send below. */
    is_key = first_source_slice_is_idr(demux);

    demux->assembly_length = 0;
    demux->assembly_overflowed = 0;
    if (is_key && demux->video_header_length > 0)
        append_to_assembly(demux, demux->video_header, demux->video_header_length);

    for (i = 0; i < demux->source_expected; i++) {
        int size;

        if (!demux->slot_present[i]) {
            incomplete = 1;
            continue;
        }

        size = demux->slot_data_size[i];
        if (size <= STREAM_DEMUX_VIDEO_UNIT_PREFIX_LENGTH)
            continue;

        append_to_assembly(demux,
            demux->slot_buf + (size_t)i * (size_t)demux->unit_stride + STREAM_DEMUX_VIDEO_UNIT_PREFIX_LENGTH,
            (size_t)(size - STREAM_DEMUX_VIDEO_UNIT_PREFIX_LENGTH));
    }

    demux->frame_allocated = 0;

    /* Slices lost that FEC could not recover (or an assembly that overflowed this port's static capacity)
     * - ask the console for a fresh IDR so the picture recovers fast rather than accumulating corruption. */
    if ((incomplete || demux->assembly_overflowed) && demux->sink.video_loss_detected)
        demux->sink.video_loss_detected(demux->sink.userdata, demux->frame_index, demux->frame_index);

    if (demux->assembly_length == 0 || demux->assembly_overflowed)
        return;

    if (demux->sink.video_frame_ready) {
        demux->sink.video_frame_ready(demux->sink.userdata, demux->assembly, demux->assembly_length, is_key);
    }
}

static void ingest_video(stream_demux *demux, const stream_header *header, const uint8_t *packet,
    size_t packet_length)
{
    int payload_offset = stream_header_payload_offset(header);
    uint8_t opened[STREAM_PACKET_CRYPTO_MAX_PACKET];
    size_t opened_length;
    int data_size;
    const uint8_t *payload;

    if (packet_length <= (size_t)payload_offset)
        return;
    if (!try_open_media(demux, header, packet, packet_length, opened, &opened_length))
        return;

    data_size = (int)(opened_length - (size_t)payload_offset);
    if (data_size <= 0)
        return;
    payload = opened + payload_offset;

    if ((int)header->frame_index != demux->frame_index) {
        /* A stale unit from an already-finished (older) frame - drop it rather than restarting
         * reassembly. */
        int diff = ((int)header->frame_index - demux->frame_index) & 0xffff;
        if (demux->frame_index >= 0 && diff >= 0x8000)
            return;

        if (demux->frame_index >= 0) {
            check_for_frame_gap(demux, header->frame_index);
            flush_video_frame(demux);
        }

        demux->frame_index = header->frame_index;
        allocate_frame(demux, header, data_size, payload, (size_t)data_size);
    }

    if (demux->frame_allocated)
        place_unit(demux, header, data_size, payload, (size_t)data_size);
}

static void ingest_audio(stream_demux *demux, const stream_header *header, const uint8_t *packet,
    size_t packet_length)
{
    int payload_offset = stream_header_payload_offset(header);
    uint8_t opened[STREAM_PACKET_CRYPTO_MAX_PACKET];
    size_t opened_length;
    int payload_length, units, unit_size;

    if (packet_length <= (size_t)payload_offset || header->codec != STREAM_DEMUX_OPUS_CODEC)
        return;
    if (!try_open_media(demux, header, packet, packet_length, opened, &opened_length))
        return;

    /* The audio payload packs total_units equal-size units back to back: unit 0 is the source Opus frame,
     * the rest are redundant copies of the same 10ms for loss concealment. Feeding the whole payload to
     * the decoder corrupts every band above ~5kHz (Opus/CELT sizes its bit budget from the packet length),
     * so only unit 0 is ever emitted here. Using the redundant units for loss concealment is a later
     * refinement, same as the .NET reference. */
    payload_length = (int)(opened_length - (size_t)payload_offset);
    units = header->total_units > 1 ? header->total_units : 1;
    unit_size = payload_length / units;
    if (unit_size <= 0)
        return;

    if (demux->sink.audio_frame_ready)
        demux->sink.audio_frame_ready(demux->sink.userdata, opened + payload_offset, (size_t)unit_size);
}

void stream_demux_ingest(stream_demux *demux, const uint8_t *packet, size_t packet_length)
{
    stream_header header;

    if (!stream_header_parse(packet, packet_length, &header))
        return;

    if (header.type == STREAM_HEADER_TYPE_VIDEO) {
        ingest_video(demux, &header, packet, packet_length);
    } else if (header.type == STREAM_HEADER_TYPE_AUDIO) {
        ingest_audio(demux, &header, packet, packet_length);
    } else if (demux->sink.control_packet_received) {
        int payload_offset = stream_header_payload_offset(&header);
        size_t copy_from = (size_t)payload_offset < packet_length ? (size_t)payload_offset : packet_length;
        demux->sink.control_packet_received(demux->sink.userdata, &header, packet + copy_from,
            packet_length - copy_from);
    }
}

void stream_demux_take_packet_stats(stream_demux *demux, long *out_received, long *out_lost)
{
    if (out_received)
        *out_received = demux->stat_units_received;
    if (out_lost)
        *out_lost = demux->stat_units_lost;
    demux->stat_units_received = 0;
    demux->stat_units_lost = 0;
}
