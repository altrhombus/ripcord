/*
 * ripcord-3ds - stream_demux self-test.
 *
 * Uses the passthrough crypto seam (stream_demux_passthrough_crypto - identity, matching the .NET
 * reference's own PassthroughHalyardSessionCrypto) so packets can be built directly in plaintext; this
 * exercises framing, frame reassembly, FEC recovery and audio/control routing exactly the way the .NET
 * reference's HalyardStreamDemuxer*Tests.cs files do, end to end against synthetic packets rather than a
 * captured session (no capture in the dirty room pairs A/V traffic with usable stream keys).
 *
 * THIS BUILDS FOR THE HOST, NOT THE 3DS - see tests/Makefile.
 */
#include "../stream/stream_demux.h"
#include "../stream/fec_galois.h"

#include <stdio.h>
#include <string.h>

static int g_passed;
static int g_failed;

#define CHECK(cond, ...) do { \
    if (cond) { \
        g_passed++; \
    } else { \
        g_failed++; \
        printf("FAIL %s:%d: ", __FILE__, __LINE__); \
        printf(__VA_ARGS__); \
        printf("\n"); \
    } \
} while (0)

/* A large static instance rather than a stack local - stream_demux is a few hundred KB (see
 * stream_demux.h), sized for a 3DS's generous RAM but not something to put on any thread's stack. */
static stream_demux g_demux;

#define CAPTURE_CAPACITY (256 * 1024)

typedef struct {
    int video_frame_count;
    uint8_t video_data[CAPTURE_CAPACITY];
    size_t video_length;
    int video_is_key;

    int audio_frame_count;
    uint8_t audio_data[4096];
    size_t audio_length;

    int loss_count;
    int loss_first;
    int loss_last;

    int control_count;
    uint8_t control_type;
    uint8_t control_data[256];
    size_t control_length;
} capture_state;

static void on_video_frame(void *userdata, const uint8_t *data, size_t length, int is_keyframe)
{
    capture_state *state = (capture_state *)userdata;
    size_t copy_len = length < sizeof(state->video_data) ? length : sizeof(state->video_data);

    state->video_frame_count++;
    memcpy(state->video_data, data, copy_len);
    state->video_length = length;
    state->video_is_key = is_keyframe;
}

static void on_audio_frame(void *userdata, const uint8_t *data, size_t length)
{
    capture_state *state = (capture_state *)userdata;
    size_t copy_len = length < sizeof(state->audio_data) ? length : sizeof(state->audio_data);

    state->audio_frame_count++;
    memcpy(state->audio_data, data, copy_len);
    state->audio_length = length;
}

static void on_loss(void *userdata, int first_frame_index, int last_frame_index)
{
    capture_state *state = (capture_state *)userdata;

    state->loss_count++;
    state->loss_first = first_frame_index;
    state->loss_last = last_frame_index;
}

static void on_control(void *userdata, const stream_header *header, const uint8_t *data, size_t length)
{
    capture_state *state = (capture_state *)userdata;
    size_t copy_len = length < sizeof(state->control_data) ? length : sizeof(state->control_data);

    state->control_count++;
    state->control_type = header->type;
    memcpy(state->control_data, data, copy_len);
    state->control_length = length;
}

static void reset_capture(capture_state *state)
{
    memset(state, 0, sizeof(*state));
}

static stream_demux_sink make_sink(capture_state *state)
{
    stream_demux_sink sink;
    memset(&sink, 0, sizeof(sink));
    sink.userdata = state;
    sink.video_frame_ready = on_video_frame;
    sink.audio_frame_ready = on_audio_frame;
    sink.video_loss_detected = on_loss;
    sink.control_packet_received = on_control;
    return sink;
}

static size_t build_video_packet(uint8_t *out, size_t out_capacity, int frame_index, int unit_index,
    int total_units, int parity_units, uint8_t codec, uint16_t extra_padding,
    const uint8_t *slice, size_t slice_length)
{
    stream_header h;
    size_t offset;

    memset(&h, 0, sizeof(h));
    h.type = STREAM_HEADER_TYPE_VIDEO;
    h.has_extended_header = 0;
    h.packet_index = (uint16_t)unit_index;
    h.frame_index = (uint16_t)frame_index;
    h.unit_index = unit_index;
    h.total_units = total_units;
    h.parity_units = parity_units;
    h.codec = codec;
    h.key_position = 0;

    if (!stream_header_build(&h, out))
        return 0;

    /* Bytes 18..20: the wire's own video prefix (size_extension u16 + adaptive_stream_index), sent in the
     * clear ahead of stream_header_payload_offset() - unused by passthrough crypto, so zero-filled here.
     * The demuxer's own 2-byte FEC size-extension field (extra_padding) starts only at payload_offset. */
    offset = STREAM_HEADER_LENGTH;
    out[offset++] = 0;
    out[offset++] = 0;
    out[offset++] = 0;
    out[offset++] = (uint8_t)(extra_padding >> 8);
    out[offset++] = (uint8_t)(extra_padding & 0xff);
    if (offset + slice_length > out_capacity)
        return 0;
    memcpy(out + offset, slice, slice_length);
    return offset + slice_length;
}

static size_t build_audio_packet(uint8_t *out, size_t out_capacity, int frame_index, int total_units,
    const uint8_t *payload, size_t payload_length)
{
    stream_header h;

    memset(&h, 0, sizeof(h));
    h.type = STREAM_HEADER_TYPE_AUDIO;
    h.has_extended_header = 0;
    h.packet_index = (uint16_t)frame_index;
    h.frame_index = (uint16_t)frame_index;
    h.unit_index = 0;
    h.total_units = total_units;
    h.parity_units = 0;
    h.codec = STREAM_DEMUX_OPUS_CODEC;
    h.key_position = 0;

    if (!stream_header_build(&h, out))
        return 0;

    /* Bytes 18..19: audio's own wire prefix (an "unknown" byte plus a haptics indicator), sent in the
     * clear ahead of stream_header_payload_offset() - unused by passthrough crypto, so zero-filled here. */
    if ((size_t)STREAM_HEADER_LENGTH + 2 + payload_length > out_capacity)
        return 0;
    out[STREAM_HEADER_LENGTH] = 0;
    out[STREAM_HEADER_LENGTH + 1] = 0;
    memcpy(out + STREAM_HEADER_LENGTH + 2, payload, payload_length);
    return (size_t)STREAM_HEADER_LENGTH + 2 + payload_length;
}

static void test_single_unit_keyframe(void)
{
    capture_state state;
    static const uint8_t sps_pps[] = { 0x00, 0x00, 0x00, 0x01, 0x67, 0xaa, 0xbb }; /* h264 SPS (type 7) */
    static const uint8_t idr_slice[] = { 0x00, 0x00, 0x00, 0x01, 0x65, 0xde, 0xad, 0xbe, 0xef };
    uint8_t packet[64];
    size_t packet_length;

    reset_capture(&state);
    stream_demux_init(&g_demux, stream_demux_passthrough_crypto(), make_sink(&state));
    stream_demux_set_video_header(&g_demux, sps_pps, sizeof(sps_pps));

    packet_length = build_video_packet(packet, sizeof(packet), 0, 0, 1, 0, 0, 0, idr_slice, sizeof(idr_slice));
    CHECK(packet_length > 0, "failed to build frame 0 packet");
    stream_demux_ingest(&g_demux, packet, packet_length);
    CHECK(state.video_frame_count == 0, "frame should not flush until the next frame_index arrives");

    /* First unit of frame 1 forces frame 0 to flush. */
    packet_length = build_video_packet(packet, sizeof(packet), 1, 0, 1, 0, 0, 0, idr_slice, sizeof(idr_slice));
    stream_demux_ingest(&g_demux, packet, packet_length);

    CHECK(state.video_frame_count == 1, "expected exactly one flushed frame, got %d", state.video_frame_count);
    CHECK(state.video_is_key, "frame 0 should be a keyframe");
    CHECK(state.video_length == sizeof(sps_pps) + sizeof(idr_slice),
        "assembled length mismatch: got %zu", state.video_length);
    CHECK(memcmp(state.video_data, sps_pps, sizeof(sps_pps)) == 0, "SPS/PPS prefix mismatch");
    CHECK(memcmp(state.video_data + sizeof(sps_pps), idr_slice, sizeof(idr_slice)) == 0, "slice bytes mismatch");
    CHECK(state.loss_count == 0, "no loss expected for two consecutive complete frames");
}

/*
 * A FRAME WITH MORE UNITS THAN THE OLD CAP ALLOWED.
 *
 * Until STREAM_DEMUX_MAX_UNITS_PER_FRAME existed, a frame wanting more than FEC_MAX_TOTAL_UNITS slots was
 * refused outright, so no test ever built one and no console ever delivered one this code assembled.
 * Raising the cap made them reachable for the first time, and on hardware the first thing they did was
 * decode to black at 15 Mbps while the same build at 8 Mbps was perfect - the difference being frames of
 * about 90 units against about 21.
 *
 * 96 source units, each carrying a distinct byte pattern, checked back in order. If the assembly walks
 * its slots wrongly past 64 this fails on the bytes rather than on the count.
 */
static void test_frame_with_more_units_than_the_old_cap(void)
{
    capture_state state;
    static uint8_t packet[256];
    static uint8_t expect[96 * 8];
    int units = 96;
    int i;

    reset_capture(&state);
    stream_demux_init(&g_demux, stream_demux_passthrough_crypto(), make_sink(&state));

    for (i = 0; i < units; i++) {
        uint8_t slice[8];
        size_t n;
        int b;

        for (b = 0; b < 8; b++)
            slice[b] = (uint8_t)(i * 8 + b);
        memcpy(expect + (size_t)i * 8u, slice, sizeof(slice));

        n = build_video_packet(packet, sizeof(packet), 0, i, units, 0, 0, 0, slice, sizeof(slice));
        CHECK(n > 0, "failed to build unit %d of a %d-unit frame", i, units);
        stream_demux_ingest(&g_demux, packet, n);
    }

    /* The first unit of the next frame flushes frame 0. */
    {
        uint8_t slice[8];
        size_t n;

        memset(slice, 0, sizeof(slice));
        n = build_video_packet(packet, sizeof(packet), 1, 0, 1, 0, 0, 0, slice, sizeof(slice));
        stream_demux_ingest(&g_demux, packet, n);
    }

    CHECK(state.video_frame_count == 1, "expected one flushed frame, got %d", state.video_frame_count);
    CHECK(state.video_length == (size_t)units * 8u,
        "a %d-unit frame should assemble to %d bytes, got %zu", units, units * 8, state.video_length);
    CHECK(memcmp(state.video_data, expect, (size_t)units * 8u) == 0,
        "the assembled bytes of a %d-unit frame do not match what was sent", units);
    CHECK(state.loss_count == 0, "a complete frame should report no loss");
}

static void test_fec_recovers_dropped_source_units(void)
{
    const int k = 4, m = 2, total = k + m;
    const size_t unit_size = 32; /* includes the 2-byte prefix */
    const size_t stride = 32;
    uint8_t frame_buf[6 * 32];
    uint8_t slices[4][30]; /* unit_size - 2-byte prefix */
    uint8_t expected_assembly[4 * 30];
    size_t expected_length = 0;
    capture_state state;
    uint8_t packet[64];
    size_t packet_length;
    int u;

    fec_galois_init();
    memset(frame_buf, 0, sizeof(frame_buf));
    for (u = 0; u < k; u++) {
        int t;
        /* [2-byte prefix = 0 (no extra padding, unit_size already the coded length)][deterministic slice] */
        frame_buf[(size_t)u * stride] = 0;
        frame_buf[(size_t)u * stride + 1] = 0;
        for (t = 0; t < 30; t++) {
            uint8_t b = (uint8_t)(0x10 * u + t);
            slices[u][t] = b;
            frame_buf[(size_t)u * stride + 2 + (size_t)t] = b;
        }
        memcpy(expected_assembly + expected_length, slices[u], sizeof(slices[u]));
        expected_length += sizeof(slices[u]);
    }
    CHECK(fec_reed_solomon_encode(frame_buf, unit_size, stride, k, m), "fixture encode failed");

    reset_capture(&state);
    stream_demux_init(&g_demux, stream_demux_passthrough_crypto(), make_sink(&state));

    /* Drop source units 0 and 2 - send the surviving 2 source + 2 parity units (parity_units=m on the
     * wire; total_units = k+m). */
    for (u = 0; u < total; u++) {
        if (u == 0 || u == 2)
            continue; /* dropped on the wire */
        packet_length = build_video_packet(packet, sizeof(packet), 5, u, total, m, 0, 0,
            frame_buf + (size_t)u * stride + 2, unit_size - 2);
        CHECK(packet_length > 0, "failed to build unit %d packet", u);
        stream_demux_ingest(&g_demux, packet, packet_length);
    }

    /* Force the flush with the next frame's first unit. */
    packet_length = build_video_packet(packet, sizeof(packet), 6, 0, 1, 0, 0, 0, (const uint8_t *)"x", 1);
    stream_demux_ingest(&g_demux, packet, packet_length);

    CHECK(state.video_frame_count == 1, "expected frame 5 to flush, got %d frames", state.video_frame_count);
    CHECK(!state.video_is_key, "frame has no parameter sets configured, should not be flagged as key");
    CHECK(state.loss_count == 0, "FEC should have fully recovered the frame - no loss expected");
    CHECK(state.video_length == expected_length,
        "recovered assembly length mismatch: got %zu want %zu", state.video_length, expected_length);
    CHECK(memcmp(state.video_data, expected_assembly, expected_length) == 0,
        "recovered assembly bytes do not match the original source units");
}

static void test_incomplete_frame_reports_loss_but_still_emits(void)
{
    capture_state state;
    static const uint8_t slice0[] = { 0x00, 0x00, 0x00, 0x01, 0x41, 0x01, 0x02, 0x03 };
    uint8_t packet[64];
    size_t packet_length;

    reset_capture(&state);
    stream_demux_init(&g_demux, stream_demux_passthrough_crypto(), make_sink(&state));

    /* Frame 10: two source units, no parity - only unit 0 ever arrives. */
    packet_length = build_video_packet(packet, sizeof(packet), 10, 0, 2, 0, 0, 0, slice0, sizeof(slice0));
    stream_demux_ingest(&g_demux, packet, packet_length);

    /* Frame 11 flushes frame 10. */
    packet_length = build_video_packet(packet, sizeof(packet), 11, 0, 1, 0, 0, 0, slice0, sizeof(slice0));
    stream_demux_ingest(&g_demux, packet, packet_length);

    CHECK(state.video_frame_count == 1, "frame 10 should still be emitted with what arrived");
    CHECK(state.loss_count == 1, "expected exactly one loss report for the incomplete frame, got %d",
        state.loss_count);
    CHECK(state.loss_first == 10 && state.loss_last == 10, "loss range should be (10,10), got (%d,%d)",
        state.loss_first, state.loss_last);
}

static void test_frame_index_gap_reports_missing_frames(void)
{
    capture_state state;
    static const uint8_t slice0[] = { 0x00, 0x00, 0x00, 0x01, 0x41, 0x01 };
    uint8_t packet[64];
    size_t packet_length;

    reset_capture(&state);
    stream_demux_init(&g_demux, stream_demux_passthrough_crypto(), make_sink(&state));

    packet_length = build_video_packet(packet, sizeof(packet), 20, 0, 1, 0, 0, 0, slice0, sizeof(slice0));
    stream_demux_ingest(&g_demux, packet, packet_length);

    /* Frame index jumps 20 -> 23: frames 21 and 22 went missing entirely. */
    packet_length = build_video_packet(packet, sizeof(packet), 23, 0, 1, 0, 0, 0, slice0, sizeof(slice0));
    stream_demux_ingest(&g_demux, packet, packet_length);

    CHECK(state.loss_count == 1, "expected exactly one gap report, got %d", state.loss_count);
    CHECK(state.loss_first == 21 && state.loss_last == 22, "gap range should be (21,22), got (%d,%d)",
        state.loss_first, state.loss_last);
}

static void test_audio_strips_redundant_units(void)
{
    capture_state state;
    static const uint8_t payload[] = {
        1, 2, 3, 4, 5, 6, 7, 8,        /* unit 0: the real Opus frame */
        9, 9, 9, 9, 9, 9, 9, 9,        /* unit 1: redundant copy */
        7, 7, 7, 7, 7, 7, 7, 7,        /* unit 2: redundant copy */
    };
    uint8_t packet[64];
    size_t packet_length;

    reset_capture(&state);
    stream_demux_init(&g_demux, stream_demux_passthrough_crypto(), make_sink(&state));

    packet_length = build_audio_packet(packet, sizeof(packet), 0, 3, payload, sizeof(payload));
    CHECK(packet_length > 0, "failed to build audio packet");
    stream_demux_ingest(&g_demux, packet, packet_length);

    CHECK(state.audio_frame_count == 1, "expected one audio frame, got %d", state.audio_frame_count);
    CHECK(state.audio_length == 8, "expected only unit 0 (8 bytes), got %zu", state.audio_length);
    CHECK(memcmp(state.audio_data, payload, 8) == 0, "unit 0 bytes mismatch");
}

static void test_control_packet_passthrough(void)
{
    capture_state state;
    uint8_t packet[STREAM_HEADER_LENGTH + 2 + 4];
    static const uint8_t body[] = { 0xaa, 0xbb, 0xcc, 0xdd };

    reset_capture(&state);
    stream_demux_init(&g_demux, stream_demux_passthrough_crypto(), make_sink(&state));

    memset(packet, 0, sizeof(packet));
    packet[0] = 0x05; /* congestion, base-type byte per spec - not video (2) or audio (3) */
    /* stream_header_payload_offset() is IsVideo ? 3 : 2 regardless of the exact non-video type, so the
     * payload starts 2 bytes after the base header here too. */
    memcpy(packet + STREAM_HEADER_LENGTH + 2, body, sizeof(body));

    stream_demux_ingest(&g_demux, packet, sizeof(packet));

    CHECK(state.control_count == 1, "expected one control callback, got %d", state.control_count);
    CHECK(state.control_type == 0x05, "control type mismatch");
    CHECK(state.control_length == sizeof(body), "control payload length mismatch: got %zu", state.control_length);
    CHECK(memcmp(state.control_data, body, sizeof(body)) == 0, "control payload bytes mismatch");
}

int main(void)
{
    test_single_unit_keyframe();
    test_frame_with_more_units_than_the_old_cap();
    test_fec_recovers_dropped_source_units();
    test_incomplete_frame_reports_loss_but_still_emits();
    test_frame_index_gap_reports_missing_frames();
    test_audio_strips_redundant_units();
    test_control_packet_passthrough();

    printf("\n%d passed, %d failed\n", g_passed, g_failed);
    return g_failed == 0 ? 0 : 1;
}
