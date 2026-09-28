/*
 * ripcord-3ds - Phase 5 Takion transport self-test.
 *
 * Known-answer vectors are full captured packets (Takion header + SCTP chunk together) lifted from
 * Ripcord.Protocol.Halyard.Takion's own test suite (TakionTests.cs, TakionDataChunkTests.cs,
 * TakionReliabilityTests.cs), which in turn lifted them from a real captured handshake
 * (captures/rudp_control_setup.pcapng - see docs/protocol/ps5-remoteplay-v1-spec.md sec8). Parsed as
 * whole packets via takion_message_parse() before handing the chunk portion to the chunk-specific
 * parser, the same way real traffic would arrive, rather than hand-slicing the chunk bytes out of the
 * hex literal here (a manual slice is exactly the kind of transcription step that would go unnoticed if
 * it were wrong).
 *
 * INIT_ACK/COOKIE_ECHO/COOKIE_ACK and the DATA continuation fragment have no captured vector available -
 * see their own header comments for exactly what is and isn't confirmed - so those are checked by
 * round-trip (build, then parse, then compare fields) instead.
 *
 * THIS BUILDS FOR THE HOST, NOT THE 3DS - see tests/Makefile.
 */
#include "../takion/takion_data_chunk.h"
#include "../takion/takion_handshake.h"
#include "../takion/takion_message.h"
#include "../takion/takion_reassembler.h"
#include "../takion/takion_sack_chunk.h"

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

static int hex_nibble(char c)
{
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

static size_t from_hex(const char *text, uint8_t *out, size_t capacity)
{
    size_t length = strlen(text);
    size_t i;

    if (length % 2 != 0 || length / 2 > capacity)
        return (size_t)-1;
    for (i = 0; i < length; i += 2) {
        int hi = hex_nibble(text[i]);
        int lo = hex_nibble(text[i + 1]);
        if (hi < 0 || lo < 0)
            return (size_t)-1;
        out[i / 2] = (uint8_t)((hi << 4) | lo);
    }
    return length / 2;
}

/* ---- takion_message: the 13-byte header ---- */

static void test_message_header(void)
{
    takion_message_header header;
    uint8_t buf[64];
    size_t n;
    static const uint8_t chunk[4] = { 0xaa, 0xbb, 0xcc, 0xdd };
    takion_message_header out;
    const uint8_t *out_chunk;
    size_t out_chunk_length;

    header.base_type = TAKION_BASE_TYPE_CONTROL;
    header.verification_tag = 0x00b18ccf;
    header.gmac_tag = 0;
    header.key_position = 0;

    n = takion_message_build(&header, chunk, sizeof(chunk), buf, sizeof(buf));
    CHECK(n == TAKION_HEADER_SIZE + sizeof(chunk), "header+chunk should be 17 bytes, got %u", (unsigned)n);
    CHECK(buf[0] == 0x00 && buf[1] == 0x00 && buf[2] == 0xb1 && buf[3] == 0x8c && buf[4] == 0xcf,
        "header bytes did not match the expected base_type/verification_tag layout");

    n = takion_message_parse(buf, n, &out, &out_chunk, &out_chunk_length);
    CHECK(n == TAKION_HEADER_SIZE, "parse should consume exactly the 13-byte header");
    CHECK(out.base_type == header.base_type && out.verification_tag == header.verification_tag,
        "parsed header fields did not round-trip");
    CHECK(out_chunk_length == sizeof(chunk) && memcmp(out_chunk, chunk, sizeof(chunk)) == 0,
        "parsed chunk bytes did not round-trip");

    n = takion_message_parse(buf, 5, &out, &out_chunk, &out_chunk_length);
    CHECK(n == 0, "a buffer shorter than the 13-byte header must report 0, not a partial header");
}

/* ---- takion_handshake: INIT confirmed by vector; INIT_ACK/COOKIE_ECHO/COOKIE_ACK by round-trip ---- */

static void test_handshake_init_vector(void)
{
    /* TakionTests.cs: handshake.BuildInit(0x00004823) == this exact hex - a client's first INIT. */
    static const char expect_hex[] =
        "000000000000000000000000000100001400004823000190000064006400004823";
    uint8_t expect[64];
    size_t expect_len = from_hex(expect_hex, expect, sizeof(expect));

    uint8_t chunk[64];
    size_t chunk_len = takion_build_init(0x00004823u, chunk, sizeof(chunk));
    takion_message_header header;
    uint8_t packet[64];
    size_t packet_len;
    uint32_t parsed_tag;

    header.base_type = TAKION_BASE_TYPE_CONTROL;
    header.verification_tag = 0; /* the client does not know the server's tag yet */
    header.gmac_tag = 0;
    header.key_position = 0;
    packet_len = takion_message_build(&header, chunk, chunk_len, packet, sizeof(packet));

    CHECK(packet_len == expect_len && memcmp(packet, expect, expect_len) == 0,
        "a from-scratch INIT packet did not match the captured vector");

    CHECK(takion_chunk_type(chunk, chunk_len) == (int)TAKION_CHUNK_INIT, "chunk type should be INIT");
    CHECK(takion_parse_init(chunk, chunk_len, &parsed_tag) == 1 && parsed_tag == 0x00004823u,
        "parsing our own built INIT should recover the same initiate_tag");
}

static void test_handshake_init_ack_and_cookie(void)
{
    static const uint8_t cookie[TAKION_COOKIE_SIZE] = {
        0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f, 0x10,
        0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x1b, 0x1c, 0x1d, 0x1e, 0x1f, 0x20
    };
    uint8_t buf[128];
    size_t n;
    uint32_t server_tag, initial_tsn;
    uint8_t recovered_cookie[TAKION_COOKIE_SIZE];

    n = takion_build_init_ack(0x00b18ccfu, 0x00b18ccfu, cookie, buf, sizeof(buf));
    CHECK(n == 4 + 16 + TAKION_COOKIE_SIZE, "INIT_ACK chunk length mismatch, got %u", (unsigned)n);
    CHECK(takion_chunk_type(buf, n) == (int)TAKION_CHUNK_INIT_ACK, "chunk type should be INIT_ACK");

    CHECK(takion_parse_init_ack(buf, n, &server_tag, &initial_tsn, recovered_cookie) == 1,
        "parsing our own built INIT_ACK should succeed");
    CHECK(server_tag == 0x00b18ccfu && initial_tsn == 0x00b18ccfu, "INIT_ACK tag/tsn did not round-trip");
    CHECK(memcmp(recovered_cookie, cookie, sizeof(cookie)) == 0, "INIT_ACK cookie did not round-trip");

    {
        uint8_t echo[64];
        size_t echo_len = takion_build_cookie_echo(cookie, echo, sizeof(echo));
        uint8_t recovered_echo_cookie[TAKION_COOKIE_SIZE];

        CHECK(echo_len == 4 + TAKION_COOKIE_SIZE, "COOKIE_ECHO length mismatch");
        CHECK(takion_parse_cookie_echo(echo, echo_len, recovered_echo_cookie) == 1
            && memcmp(recovered_echo_cookie, cookie, sizeof(cookie)) == 0,
            "COOKIE_ECHO cookie did not round-trip");
    }

    {
        uint8_t ack[16];
        size_t ack_len = takion_build_cookie_ack(ack, sizeof(ack));

        CHECK(ack_len == 4, "COOKIE_ACK should be exactly 4 bytes");
        CHECK(takion_is_cookie_ack(ack, ack_len) == 1, "our own built COOKIE_ACK should self-recognise");
        CHECK(takion_is_cookie_ack(buf, n) == 0, "an INIT_ACK chunk must not be mistaken for COOKIE_ACK");
    }
}

/* ---- takion_data_chunk: three real captured DATA packets, plus a build round trip ---- */

static void check_data_packet(const char *label, const char *hex,
                              uint32_t expect_seq, unsigned expect_channel, const char *expect_payload_hex)
{
    uint8_t packet[128];
    size_t packet_len = from_hex(hex, packet, sizeof(packet));
    takion_message_header header;
    const uint8_t *chunk;
    size_t chunk_length;
    uint32_t seq;
    unsigned channel;
    int ending;
    const uint8_t *payload;
    size_t payload_length;
    uint8_t expect_payload[64];
    size_t expect_payload_length = from_hex(expect_payload_hex, expect_payload, sizeof(expect_payload));

    CHECK(takion_message_parse(packet, packet_len, &header, &chunk, &chunk_length) == TAKION_HEADER_SIZE,
        "%s: failed to parse the Takion header", label);
    CHECK(header.base_type == TAKION_BASE_TYPE_CONTROL, "%s: base_type should be control", label);

    CHECK(takion_data_parse_first(chunk, chunk_length, &seq, &channel, &ending, &payload, &payload_length) == 1,
        "%s: failed to parse as a first-fragment DATA chunk", label);
    CHECK(seq == expect_seq, "%s: seq_num mismatch: got 0x%08x, want 0x%08x", label, seq, expect_seq);
    CHECK(channel == expect_channel, "%s: channel mismatch: got 0x%04x, want 0x%04x", label, channel, expect_channel);
    CHECK(ending == 1, "%s: ending bit should be set (every captured example is a single-fragment message)", label);
    CHECK(payload_length == expect_payload_length && memcmp(payload, expect_payload, expect_payload_length) == 0,
        "%s: payload mismatch", label);
}

static void test_data_chunk_vectors(void)
{
    /* TakionDataChunkTests.cs's three captured packets. */
    check_data_packet("DATA1 (PROTOCOL_VERSION_REQUEST)",
        "0000b18ccf000000000000000000010014000048230015000000081ffa01020809",
        0x00004823u, TAKION_CHANNEL_PROTOCOL_VERSION, "081ffa01020809");
    check_data_packet("DATA2 (PROTOCOL_VERSION_ACK)",
        "000000482300000000000000000001001400b18ccf000000000008208202020809",
        0x00b18ccfu, TAKION_CHANNEL_SERVER_REPLY, "08208202020809");
    check_data_packet("DATA3 (BANDWIDTH_PROBE)",
        "0000b18ccf000000000000000000010017000048250008000000080c7206080012020801",
        0x00004825u, TAKION_CHANNEL_BANDWIDTH, "080c7206080012020801");
}

static void test_data_chunk_build_matches_vector(void)
{
    /* Rebuilding DATA1 from its known fields should reproduce the captured packet byte-for-byte -
     * TakionDataChunkTests.cs's own build round trip. */
    static const char expect_hex[] =
        "0000b18ccf000000000000000000010014000048230015000000081ffa01020809";
    uint8_t expect[64];
    size_t expect_len = from_hex(expect_hex, expect, sizeof(expect));

    uint8_t payload[16];
    size_t payload_len = from_hex("081ffa01020809", payload, sizeof(payload));
    uint8_t chunk[64];
    size_t chunk_len = takion_data_build_first(0x00004823u, TAKION_CHANNEL_PROTOCOL_VERSION, 1,
        payload, payload_len, chunk, sizeof(chunk));

    takion_message_header header;
    uint8_t packet[64];
    size_t packet_len;

    header.base_type = TAKION_BASE_TYPE_CONTROL;
    header.verification_tag = 0x00b18ccfu;
    header.gmac_tag = 0;
    header.key_position = 0;
    packet_len = takion_message_build(&header, chunk, chunk_len, packet, sizeof(packet));

    CHECK(packet_len == expect_len && memcmp(packet, expect, expect_len) == 0,
        "a from-scratch DATA1 packet did not match the captured vector");
}

static void test_data_chunk_continuation_round_trip(void)
{
    /* No captured continuation-fragment vector exists (see the header comment) - self-consistency only.
     * The CHANNEL assertions below are not decoration: this port shipped continuations with the channel
     * zero-filled, which put every fragment after the first on channel 0 - the console's own channel -
     * and a round-trip test that ignored the field was exactly why nothing caught it. */
    static const uint8_t payload[5] = { 0x11, 0x22, 0x33, 0x44, 0x55 };
    uint8_t buf[32];
    size_t n = takion_data_build_continuation(0x00004826u, TAKION_CHANNEL_SESSION, 1,
                                              payload, sizeof(payload), buf, sizeof(buf));
    uint32_t seq;
    unsigned channel = 0xffffu;
    int ending;
    const uint8_t *out_payload;
    size_t out_length;

    CHECK(n > 0, "building a continuation fragment should succeed");
    CHECK(takion_data_parse_continuation(buf, n, &seq, &channel, &ending, &out_payload, &out_length) == 1,
        "parsing our own built continuation fragment should succeed");
    CHECK(seq == 0x00004826u, "continuation seq_num did not round-trip");
    CHECK(channel == TAKION_CHANNEL_SESSION, "continuation CHANNEL did not round-trip");
    CHECK(ending == 1, "continuation ending bit did not round-trip");
    CHECK(out_length == sizeof(payload) && memcmp(out_payload, payload, sizeof(payload)) == 0,
        "continuation payload did not round-trip");

    /* The channel must land at the same offset as in a first fragment (value offset 4), which is the
     * single fact the old layout got wrong. Chunk header is 4 bytes, so that is buf[8..9]. */
    CHECK(buf[8] == 0x00 && buf[9] == 0x01,
        "continuation channel is not at value offset 4 - the layout regression is back");
}

/* ---- takion_sack_chunk: two real captured SACK packets, plus a build round trip ---- */

static void test_sack_vectors(void)
{
    takion_message_header header;
    const uint8_t *chunk;
    size_t chunk_length;
    takion_sack_info info;

    /* TakionReliabilityTests.cs Build(): cumulativeTsnAck == tag, both 0x00004823 in this vector. */
    {
        static const char expect_hex[] =
            "0000004823000000000000000003000010000048230001900000000000";
        uint8_t expect[64];
        size_t expect_len = from_hex(expect_hex, expect, sizeof(expect));
        uint8_t chunk_buf[32];
        size_t chunk_len = takion_sack_build(0x00004823u, 0x00019000u, chunk_buf, sizeof(chunk_buf));
        uint8_t packet[64];
        size_t packet_len;

        header.base_type = TAKION_BASE_TYPE_CONTROL;
        header.verification_tag = 0x00004823u;
        header.gmac_tag = 0;
        header.key_position = 0;
        packet_len = takion_message_build(&header, chunk_buf, chunk_len, packet, sizeof(packet));

        CHECK(packet_len == expect_len && memcmp(packet, expect, expect_len) == 0,
            "a from-scratch SACK packet did not match the captured build vector");
    }

    /* TakionReliabilityTests.cs Parse(): the server's SACK of the client's DATA. */
    {
        static const char hex[] =
            "0000b18ccf00000000000000000300001000b18cd00001900000000000";
        uint8_t packet[64];
        size_t packet_len = from_hex(hex, packet, sizeof(packet));

        CHECK(takion_message_parse(packet, packet_len, &header, &chunk, &chunk_length) == TAKION_HEADER_SIZE,
            "failed to parse the Takion header for the captured SACK");
        CHECK(takion_sack_parse(chunk, chunk_length, &info) == 1, "failed to parse the captured SACK chunk");
        CHECK(info.cumulative_tsn_ack == 0x00b18cd0u,
            "cumulative_tsn_ack mismatch: got 0x%08x", info.cumulative_tsn_ack);
        CHECK(info.a_rwnd == 0x00019000u, "a_rwnd mismatch: got 0x%08x", info.a_rwnd);
        CHECK(info.gap_ack_block_count == 0 && info.dup_tsn_count == 0,
            "a clean-LAN SACK should carry no gap/dup blocks");
    }
}

/* ---- takion_reassembler ---- */

static void test_reassembler(void)
{
    takion_reassembler r;
    static const uint8_t whole[4] = { 0xde, 0xad, 0xbe, 0xef };
    static const uint8_t part1[3] = { 0x01, 0x02, 0x03 };
    static const uint8_t part2[3] = { 0x04, 0x05, 0x06 };
    const uint8_t *message;
    size_t length;
    unsigned channel;

    takion_reassembler_init(&r);

    /* A single-fragment message completes immediately. */
    CHECK(takion_reassembler_first(&r, TAKION_CHANNEL_SESSION, whole, sizeof(whole), 1, &message, &length) == 1,
        "an ending first-fragment should complete immediately");
    CHECK(length == sizeof(whole) && memcmp(message, whole, sizeof(whole)) == 0,
        "single-fragment message content mismatch");

    /*
     * THE LIFETIME, not the content - and it is the half that actually broke.
     *
     * Both headers promise the returned pointer aims at the reassembler's own buffer and stays valid
     * until the next call. The single-fragment path did not: it handed back the caller's `payload`
     * pointer, which in takion_channel_poll is a LOCAL receive buffer, so a complete message under
     * about a kilobyte - the common case - was valid only until poll's frame went away.
     *
     * Content comparison could never catch that; the bytes are identical either way, right up until
     * something else uses the stack. So this asserts on provenance: the message must come from inside
     * the reassembler, and must NOT be the pointer that was passed in.
     */
    CHECK(message != whole,
        "a completed message must not alias the caller's buffer - that buffer is poll's stack frame");
    CHECK(message >= r.buffer && message + length <= r.buffer + sizeof(r.buffer),
        "a completed message must live inside the reassembler, as both headers promise");

    /* The same promise for the multi-fragment path, which always kept it. */

    /* Two fragments concatenate on the ending bit. */
    CHECK(takion_reassembler_first(&r, TAKION_CHANNEL_BANDWIDTH, part1, sizeof(part1), 0, &message, &length) == 0,
        "a non-ending first-fragment should report incomplete");
    CHECK(takion_reassembler_continue(&r, part2, sizeof(part2), 1, &channel, &message, &length) == 1,
        "the continuation with the ending bit should complete the message");
    CHECK(channel == TAKION_CHANNEL_BANDWIDTH, "reassembled message should report the first fragment's channel");
    CHECK(length == sizeof(part1) + sizeof(part2), "reassembled length mismatch");
    CHECK(message >= r.buffer && message + length <= r.buffer + sizeof(r.buffer),
        "a reassembled message must live inside the reassembler too");
    CHECK(memcmp(message, part1, sizeof(part1)) == 0 && memcmp(message + sizeof(part1), part2, sizeof(part2)) == 0,
        "reassembled content mismatch");

    /* Error cases: a continuation with nothing pending, and a first-fragment while one is pending. */
    CHECK(takion_reassembler_continue(&r, part2, sizeof(part2), 1, &channel, &message, &length) == -1,
        "a continuation with no message in flight must be rejected");

    takion_reassembler_first(&r, TAKION_CHANNEL_SESSION, part1, sizeof(part1), 0, &message, &length);
    CHECK(takion_reassembler_first(&r, TAKION_CHANNEL_SESSION, part1, sizeof(part1), 0, &message, &length) == -1,
        "a first-fragment while one is already pending must be rejected");
}

int main(void)
{
    test_message_header();
    test_handshake_init_vector();
    test_handshake_init_ack_and_cookie();
    test_data_chunk_vectors();
    test_data_chunk_build_matches_vector();
    test_data_chunk_continuation_round_trip();
    test_sack_vectors();
    test_reassembler();

    printf("\n%d passed, %d failed\n", g_passed, g_failed);
    return g_failed == 0 ? 0 : 1;
}
