/*
 * ripcord-3ds - stream_header self-test.
 *
 * No captured A/V vector exists in the dirty room (no capture pairs A/V traffic with usable stream keys -
 * same gap fec_test.c's header documents for FEC), and HalyardStreamHeader.cs itself has no direct unit
 * test on the .NET side either - it is only exercised indirectly via HalyardStreamDemuxer's tests. So this
 * is a round-trip self-test (build, then parse, then compare fields) against the bit layout in
 * stream_header.h, transcribed from HalyardStreamHeader.cs's TryParse.
 *
 * THIS BUILDS FOR THE HOST, NOT THE 3DS - see tests/Makefile.
 */
#include "../source/stream/stream_header.h"

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

static void check_round_trip(const char *label, const stream_header *in)
{
    uint8_t packet[STREAM_HEADER_LENGTH];
    stream_header out;

    CHECK(stream_header_build(in, packet), "%s: build failed", label);
    CHECK(stream_header_parse(packet, sizeof(packet), &out), "%s: parse failed", label);

    CHECK(out.type == in->type, "%s: type mismatch", label);
    CHECK(out.has_extended_header == in->has_extended_header, "%s: has_extended_header mismatch", label);
    CHECK(out.packet_index == in->packet_index, "%s: packet_index mismatch", label);
    CHECK(out.frame_index == in->frame_index, "%s: frame_index mismatch", label);
    CHECK(out.unit_index == in->unit_index, "%s: unit_index mismatch (%d != %d)", label, out.unit_index, in->unit_index);
    CHECK(out.total_units == in->total_units, "%s: total_units mismatch (%d != %d)", label, out.total_units, in->total_units);
    CHECK(out.parity_units == in->parity_units, "%s: parity_units mismatch (%d != %d)", label, out.parity_units, in->parity_units);
    CHECK(out.codec == in->codec, "%s: codec mismatch", label);
    CHECK(out.key_position == in->key_position, "%s: key_position mismatch", label);

    /* Tag region must always come back zeroed from build() - it is not part of the struct. */
    CHECK(packet[STREAM_HEADER_TAG_OFFSET] == 0 && packet[STREAM_HEADER_TAG_OFFSET + 1] == 0 &&
          packet[STREAM_HEADER_TAG_OFFSET + 2] == 0 && packet[STREAM_HEADER_TAG_OFFSET + 3] == 0,
        "%s: tag region not zeroed", label);
}

static void test_video_round_trips(void)
{
    stream_header h;

    memset(&h, 0, sizeof(h));
    h.type = STREAM_HEADER_TYPE_VIDEO;
    h.has_extended_header = 0;
    h.packet_index = 1;
    h.frame_index = 7;
    h.unit_index = 0;
    h.total_units = 12;
    h.parity_units = 4;
    h.codec = 1;
    h.key_position = 0x1000;
    check_round_trip("video basic", &h);

    /* Max values for each bitfield: unit_index 11 bits, total_units-1 11 bits, parity_units 10 bits. */
    h.has_extended_header = 1;
    h.packet_index = 0xffff;
    h.frame_index = 0xffff;
    h.unit_index = 0x7ff;
    h.total_units = 0x800;
    h.parity_units = 0x3ff;
    h.codec = 0xff;
    h.key_position = 0xffffffffu;
    check_round_trip("video max fields", &h);

    /* A parity unit: unit_index at/above source_units. */
    h.has_extended_header = 0;
    h.unit_index = 8;
    h.total_units = 12;
    h.parity_units = 4; /* source_units = 8, so unit_index 8 is the first parity unit */
    CHECK(stream_header_source_units(&h) == 8, "source_units mismatch");
    CHECK(stream_header_is_parity_unit(&h), "unit_index 8 should be a parity unit");
    h.unit_index = 7;
    CHECK(!stream_header_is_parity_unit(&h), "unit_index 7 should be a source unit");
}

static void test_audio_round_trips(void)
{
    stream_header h;

    memset(&h, 0, sizeof(h));
    h.type = STREAM_HEADER_TYPE_AUDIO;
    h.has_extended_header = 0;
    h.packet_index = 42;
    h.frame_index = 99;
    h.unit_index = 0;
    h.total_units = 2;
    h.parity_units = 0x1234; /* raw diagnostic field, not decomposed - see stream_header_parse */
    h.codec = 5;
    h.key_position = 0xdeadbeefu;
    check_round_trip("audio basic", &h);

    /* Max values: unit_index/total_units-1 are byte-wide, parity_units (raw) is 16 bits. */
    h.has_extended_header = 1;
    h.unit_index = 0xff;
    h.total_units = 0x100;
    h.parity_units = 0xffff;
    check_round_trip("audio max fields", &h);
}

static void test_payload_offset(void)
{
    stream_header h;

    memset(&h, 0, sizeof(h));
    h.type = STREAM_HEADER_TYPE_VIDEO;
    h.has_extended_header = 0;
    CHECK(stream_header_payload_offset(&h) == 21, "video no ext: expected 18+3");
    h.has_extended_header = 1;
    CHECK(stream_header_payload_offset(&h) == 24, "video with ext: expected 18+3+3");

    h.type = STREAM_HEADER_TYPE_AUDIO;
    h.has_extended_header = 0;
    CHECK(stream_header_payload_offset(&h) == 20, "audio no ext: expected 18+2");
    h.has_extended_header = 1;
    CHECK(stream_header_payload_offset(&h) == 23, "audio with ext: expected 18+2+3");
}

static void test_rejects_short_packet_and_bad_type(void)
{
    uint8_t packet[STREAM_HEADER_LENGTH] = { 0 };
    stream_header out, h;

    CHECK(!stream_header_parse(packet, STREAM_HEADER_LENGTH - 1, &out), "parse should reject short packet");

    memset(&h, 0, sizeof(h));
    h.type = 0x0f; /* neither video nor audio */
    h.total_units = 1;
    CHECK(!stream_header_build(&h, packet), "build should reject an unknown type");

    h.type = STREAM_HEADER_TYPE_VIDEO;
    h.unit_index = 0x800; /* one past the 11-bit max */
    h.total_units = 1;
    CHECK(!stream_header_build(&h, packet), "build should reject an out-of-range video unit_index");
}

int main(void)
{
    test_video_round_trips();
    test_audio_round_trips();
    test_payload_offset();
    test_rejects_short_packet_and_bad_type();

    printf("\n%d passed, %d failed\n", g_passed, g_failed);
    return g_failed == 0 ? 0 : 1;
}
