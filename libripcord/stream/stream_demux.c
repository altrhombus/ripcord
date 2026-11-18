#include "stream_demux.h"

#include <string.h>

static int passthrough_open_packet(void *ctx, uint8_t *packet, size_t packet_length,
    uint32_t keystream_offset, int payload_offset)
{
    (void)ctx;
    (void)packet;
    (void)packet_length;
    (void)keystream_offset;
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
    uint32_t keystream_offset, int payload_offset)
{
    stream_packet_crypto *crypto = (stream_packet_crypto *)ctx;

    if (!stream_packet_crypto_verify(crypto, keystream_offset, packet, packet_length, STREAM_HEADER_TAG_OFFSET, 0))
        return 0;
    stream_packet_crypto_crypt_payload(crypto, keystream_offset, packet + payload_offset,
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

size_t stream_demux_struct_size(void)
{
    return sizeof(stream_demux);
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

    if (demux->crypto.open_packet(demux->crypto.ctx, opened, packet_length, header->keystream_offset,
            stream_header_payload_offset(header))) {
        return 1;
    }

    demux->auth_failures++;
    return 0;
}

/* Append to the assembly buffer. Unlike the reference's Array.Resize, this core never allocates: an
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

/* Start a new frame from whichever of its units arrives first, which fixes the coded length (spec sec6.2).
 * Leaves frame_open clear on impossible or oversized geometry so the frame is skipped rather than risking
 * a buffer overflow from console-controlled wire fields (see stream_demux.h's header comment). */
static void open_frame(stream_demux *demux, const stream_header *header, int data_size,
    const uint8_t *payload, size_t payload_length)
{
    int source, parity, unit_count, coded_length;
    size_t bytes;

    demux->frame_open = 0;

    source = stream_header_source_units(header);
    parity = header->parity_units; /* as the header says; 0 means no parity and nothing to recover with */
    unit_count = source + parity;
    if (source <= 0 || parity < 0)
        return;
    if (unit_count > STREAM_DEMUX_MAX_UNITS_PER_FRAME) {
        /* Counted, not just refused. A frame that wants more units than this build provides is a
         * configuration answer - raise STREAM_DEMUX_MAX_UNITS_PER_FRAME - and uncounted it looks
         * exactly like a network that had lost everything. */
        demux->stat_frames_too_many_units++;
        return;
    }

    /* A source unit's 2-byte size-extension, added to its own length, gives the coded length; a parity
     * unit is the coded length already. So either kind of unit, arriving first, gives the same answer. */
    coded_length = data_size;
    if (header->unit_index < source && payload_length >= STREAM_DEMUX_VIDEO_UNIT_PREFIX_LENGTH) {
        coded_length += (int)(((uint32_t)payload[0] << 8) | payload[1]);
    }
    if (coded_length <= 0 || coded_length > STREAM_DEMUX_MAX_CODED_LENGTH)
        return;
    bytes = (size_t)unit_count * (size_t)coded_length;
    if (bytes > sizeof(demux->units))
        return;

    memset(demux->units, 0, bytes);
    memset(demux->arrived, 0, (size_t)unit_count);
    memset(demux->arrived_length, 0, (size_t)unit_count * sizeof(demux->arrived_length[0]));

    demux->source_count = source;
    demux->parity_count = parity;
    demux->coded_length = coded_length;
    demux->source_arrived = 0;
    demux->parity_arrived = 0;
    demux->frame_open = 1;
}

/* Copy one decrypted unit to its place in the frame, by unit index. */
static void store_unit(stream_demux *demux, const stream_header *header, int data_size,
    const uint8_t *payload, size_t payload_length)
{
    int index = header->unit_index;

    if (index < 0 || index >= demux->source_count + demux->parity_count || demux->arrived[index] ||
        data_size > demux->coded_length) {
        return;
    }

    memcpy(demux->units + (size_t)index * (size_t)demux->coded_length, payload, payload_length);
    demux->arrived[index] = 1;
    demux->arrived_length[index] = data_size;
    if (index < demux->source_count)
        demux->source_arrived++;
    else
        demux->parity_arrived++;
}
/* Whether the first present source unit's slice is an IDR (H.264 NAL type 5) or an HEVC IRAP picture.
 * Scans past the 2-byte unit prefix and the Annex-B start code (3- or 4-byte) to the NAL header byte. */
static int first_source_slice_is_idr(const stream_demux *demux)
{
    int i;

    /*
     * Defence in depth, NOT a fix for anything observed. flush_video_frame already refuses to call this
     * when frame_open is clear, so these values are sound by the time we get here. The check is
     * cheap and this function indexes units by console-controlled geometry, which is the class of
     * thing worth being unconditionally safe about - but it should not be mistaken for a diagnosis.
     */
    if (!demux->frame_open || demux->coded_length <= 0
        || demux->source_count <= 0
        || demux->source_count > STREAM_DEMUX_MAX_UNITS_PER_FRAME) {
        return 0;
    }

    for (i = 0; i < demux->source_count; i++) {
        const uint8_t *slice;
        int slice_length;
        int p;

        if (!demux->arrived[i] || demux->arrived_length[i] <= STREAM_DEMUX_VIDEO_UNIT_PREFIX_LENGTH)
            continue;

        slice = demux->units + (size_t)i * (size_t)demux->coded_length + STREAM_DEMUX_VIDEO_UNIT_PREFIX_LENGTH;
        slice_length = demux->arrived_length[i] - STREAM_DEMUX_VIDEO_UNIT_PREFIX_LENGTH;

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

    if (!demux->frame_open || demux->source_count == 0) {
        demux->frame_open = 0;
        return;
    }

    /* Record wire loss for the loss report before FEC - FEC recovery doesn't change what the network
     * actually dropped. */
    expected_units = demux->source_count + demux->parity_count;
    received_units = demux->source_arrived + demux->parity_arrived;
    demux->stat_units_received += received_units;
    if (expected_units > received_units)
        demux->stat_units_lost += expected_units - received_units;

    /*
     * Recover missing source units from the parity units when enough units survived. Recovery is bounded
     * by what the Reed-Solomon module can hold, a smaller number than the units a frame may occupy; a
     * group too large is left alone, and the frame is still assembled from whatever arrived.
     */
    if (demux->source_count + demux->parity_count <= FEC_MAX_TOTAL_UNITS &&
        demux->source_arrived < demux->source_count &&
        demux->source_arrived + demux->parity_arrived >= demux->source_count &&
        fec_reed_solomon_recover_source(demux->units, (size_t)demux->coded_length,
            demux->source_count, demux->parity_count, demux->arrived)) {
        /* A recovered source unit's own size-extension, now restored, gives back its length. */
        for (i = 0; i < demux->source_count; i++) {
            size_t offset;
            int extension;

            if (demux->arrived[i])
                continue;

            offset = (size_t)i * (size_t)demux->coded_length;
            extension = (int)(((uint32_t)demux->units[offset] << 8) | demux->units[offset + 1]);
            if (extension < demux->coded_length) {
                demux->arrived_length[i] = demux->coded_length - extension;
                demux->arrived[i] = 1;
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

    for (i = 0; i < demux->source_count; i++) {
        int size;

        if (!demux->arrived[i]) {
            incomplete = 1;
            continue;
        }

        size = demux->arrived_length[i];
        if (size <= STREAM_DEMUX_VIDEO_UNIT_PREFIX_LENGTH)
            continue;

        append_to_assembly(demux,
            demux->units + (size_t)i * (size_t)demux->coded_length + STREAM_DEMUX_VIDEO_UNIT_PREFIX_LENGTH,
            (size_t)(size - STREAM_DEMUX_VIDEO_UNIT_PREFIX_LENGTH));
    }

    demux->frame_open = 0;

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
        open_frame(demux, header, data_size, payload, (size_t)data_size);
    }

    if (demux->frame_open)
        store_unit(demux, header, data_size, payload, (size_t)data_size);
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
     * so only unit 0 is ever emitted here. The redundant units are not used for loss concealment, same
     * as the .NET reference. */
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

int stream_demux_video_is_hevc(const stream_demux *demux)
{
    return (demux != NULL) ? demux->video_is_hevc : 0;
}
