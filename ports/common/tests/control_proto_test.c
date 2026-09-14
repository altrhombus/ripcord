/*
 * ripcord-3ds - control-plane protobuf known-answer runner (SESSION_REQUEST / SESSION_REPLY).
 *
 * Reads tests/vectors/control-proto.kat, produced by
 *
 *     dotnet run --project tools/Ripcord.ProtocolLab -- vectors
 *
 * Every expected byte string in that file was encoded by Google.Protobuf from
 * docs/protocol/stream_control.proto; every byte string here is produced (or consumed) by the ~200 hand-
 * rolled lines in source/takion/takion_control_proto.c. That is the whole point of this runner: the .NET
 * side gets its wire format from a code generator reading the schema, this port gets it from a human
 * reading the same schema, and only a byte-for-byte comparison catches a field number typed wrong or a
 * varint boundary handled wrong. Neither implementation is checking the other's *understanding* of what
 * the console wants - both could be wrong together about that - but a divergence here is unambiguously a
 * bug in one of them.
 *
 * The vectors deliberately include a launch spec long enough to push the payload's length prefix to a
 * two-byte varint, an absent-optional case, and a reply carrying a nested message this port does not
 * model (extendedInfo) - the last of which exists to prove the parser skips an unknown length-delimited
 * field without losing its place, which is the failure that would otherwise only show up against a
 * console firmware newer than this code.
 *
 * Needs no crypto backend and no hardware. Builds for the host - see tests/Makefile.
 */
#include "../session/halyard_launch_spec.h"
#include "../takion/takion_control_proto.h"
#include "../takion/senkusha_echo.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define MAX_LINE 16384
#define MAX_BLOB 4096

static int g_passed;
static int g_failed;

static int hex_nibble(char c)
{
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

static int parse_hex(const char *text, uint8_t *out, size_t capacity)
{
    size_t length = strlen(text);
    size_t i;

    if (length % 2 != 0 || length / 2 > capacity)
        return -1;
    for (i = 0; i < length; i += 2) {
        int hi = hex_nibble(text[i]);
        int lo = hex_nibble(text[i + 1]);
        if (hi < 0 || lo < 0)
            return -1;
        out[i / 2] = (uint8_t)((hi << 4) | lo);
    }
    return (int)(length / 2);
}

static void to_hex(const uint8_t *data, size_t length, char *out)
{
    static const char kDigits[] = "0123456789abcdef";
    size_t i;
    for (i = 0; i < length; i++) {
        out[i * 2] = kDigits[data[i] >> 4];
        out[i * 2 + 1] = kDigits[data[i] & 0x0f];
    }
    out[length * 2] = '\0';
}

static void check_hex_equal(const char *kind, int line_number, const char *label,
                            const uint8_t *actual, size_t length, const char *expect_hex)
{
    char actual_hex[MAX_BLOB * 2 + 1];

    if (length > MAX_BLOB) {
        g_failed++;
        printf("FAIL %s line %d: %s too long for the runner's buffer\n", kind, line_number, label);
        return;
    }
    to_hex(actual, length, actual_hex);
    if (strcmp(actual_hex, expect_hex) == 0) {
        g_passed++;
    } else {
        g_failed++;
        printf("FAIL %s line %d: %s mismatch\n  got  %s\n  want %s\n",
            kind, line_number, label, actual_hex, expect_hex);
    }
}

static void check_uint_equal(const char *kind, int line_number, const char *label,
                             uint64_t actual, uint64_t expect)
{
    if (actual == expect) {
        g_passed++;
    } else {
        g_failed++;
        printf("FAIL %s line %d: %s mismatch\n  got  %llu\n  want %llu\n",
            kind, line_number, label, (unsigned long long)actual, (unsigned long long)expect);
    }
}

static void fail(const char *kind, int line_number, const char *why)
{
    g_failed++;
    printf("FAIL %s line %d: %s\n", kind, line_number, why);
}

/*
 * "-" stands in for a field whose hex would be empty - a literal empty field is invisible to strtok and
 * silently shifts every later field left by one. On a required field that means present-but-zero-length;
 * on an optional one it means absent. See the .kat header and LabVectors.HexOrDash for both meanings.
 */
static int is_dash(const char *field)
{
    return strcmp(field, "-") == 0;
}

/* Parses a required field's hex, treating the dash sentinel as a legitimate zero-length value. */
static int parse_hex_required(const char *field, uint8_t *out, size_t capacity)
{
    if (is_dash(field))
        return 0;
    return parse_hex(field, out, capacity);
}

/* sessionreq <clientVersion> <sessionKeyHex> <launchSpecHex> <encryptedKeyHex> <pubHex|-> <sigHex|-> <encodedHex> */
static void run_sessionreq(char **fields, int count, int line_number)
{
    static uint8_t session_key[MAX_BLOB];
    static uint8_t launch_spec[MAX_BLOB];
    static uint8_t encrypted_key[MAX_BLOB];
    static uint8_t public_key[MAX_BLOB];
    static uint8_t signature[MAX_BLOB];
    static uint8_t encoded[MAX_BLOB];
    takion_session_request request;
    int session_key_len, launch_spec_len, encrypted_key_len;
    int public_key_len = 0, signature_len = 0;
    size_t written;
    uint32_t type = 0xffffffffu;

    if (count < 8) { fail("sessionreq", line_number, "too few fields"); return; }

    /* A zero-length sessionKey is a real case in the vectors, and it arrives as the dash sentinel
     * rather than as an empty field - see is_dash() for why an empty field cannot survive strtok. */
    session_key_len = parse_hex_required(fields[2], session_key, sizeof(session_key));
    launch_spec_len = parse_hex_required(fields[3], launch_spec, sizeof(launch_spec));
    encrypted_key_len = parse_hex_required(fields[4], encrypted_key, sizeof(encrypted_key));
    if (session_key_len < 0 || launch_spec_len < 0 || encrypted_key_len < 0) {
        fail("sessionreq", line_number, "bad hex in a required field");
        return;
    }
    if (!is_dash(fields[5])) {
        public_key_len = parse_hex(fields[5], public_key, sizeof(public_key));
        if (public_key_len < 0) { fail("sessionreq", line_number, "bad pubkey hex"); return; }
    }
    if (!is_dash(fields[6])) {
        signature_len = parse_hex(fields[6], signature, sizeof(signature));
        if (signature_len < 0) { fail("sessionreq", line_number, "bad signature hex"); return; }
    }

    memset(&request, 0, sizeof(request));
    request.client_version = (uint32_t)strtoul(fields[1], NULL, 10);
    request.session_key = (const char *)session_key;
    request.session_key_length = (size_t)session_key_len;
    request.launch_spec_json = (const char *)launch_spec;
    request.launch_spec_json_length = (size_t)launch_spec_len;
    request.encrypted_key = encrypted_key;
    request.encrypted_key_length = (size_t)encrypted_key_len;
    if (!is_dash(fields[5])) {
        request.ecdh_public_key = public_key;
        request.ecdh_public_key_length = (size_t)public_key_len;
    }
    if (!is_dash(fields[6])) {
        request.ecdh_signature = signature;
        request.ecdh_signature_length = (size_t)signature_len;
    }

    written = takion_control_build_session_request(&request, encoded, sizeof(encoded));
    if (written == 0) { fail("sessionreq", line_number, "build returned 0"); return; }
    check_hex_equal("sessionreq", line_number, "encoded", encoded, written, fields[7]);

    /* The envelope we just built must be dispatchable by our own receiver half. */
    if (!takion_control_peek_type(encoded, written, &type)) {
        fail("sessionreq", line_number, "peek_type failed on our own output");
        return;
    }
    check_uint_equal("sessionreq", line_number, "peekedType", type, TAKION_CONTROL_SESSION_REQUEST);

    /* One byte short must fail rather than truncate: a buffer bound that is off by one shows up here or
     * on the wire, and only one of those is cheap to debug. */
    if (takion_control_build_session_request(&request, encoded, written - 1) != 0) {
        fail("sessionreq", line_number, "build succeeded into an undersized buffer");
    } else {
        g_passed++;
    }
}

/* sessionreply <encodedHex> <serverVersion> <token> <encKeyAccepted> <versionAccepted> <sessionKeyHex>
 *              <serverVersionStringHex|-> <pubHex|-> <sigHex|-> */
static void run_sessionreply(char **fields, int count, int line_number)
{
    static uint8_t encoded[MAX_BLOB];
    takion_session_reply reply;
    int encoded_len;
    uint32_t type = 0xffffffffu;

    if (count < 10) { fail("sessionreply", line_number, "too few fields"); return; }

    encoded_len = parse_hex(fields[1], encoded, sizeof(encoded));
    if (encoded_len <= 0) { fail("sessionreply", line_number, "bad encoded hex"); return; }

    if (!takion_control_peek_type(encoded, (size_t)encoded_len, &type)) {
        fail("sessionreply", line_number, "peek_type failed");
        return;
    }
    check_uint_equal("sessionreply", line_number, "peekedType", type, TAKION_CONTROL_SESSION_REPLY);

    if (!takion_control_parse_session_reply(encoded, (size_t)encoded_len, &reply)) {
        fail("sessionreply", line_number, "parse failed");
        return;
    }

    check_uint_equal("sessionreply", line_number, "serverVersion",
        reply.server_version, strtoul(fields[2], NULL, 10));
    check_uint_equal("sessionreply", line_number, "token",
        reply.token, strtoul(fields[3], NULL, 10));
    check_uint_equal("sessionreply", line_number, "encryptedKeyAccepted",
        (uint64_t)reply.encrypted_key_accepted, strtoul(fields[4], NULL, 10));
    check_uint_equal("sessionreply", line_number, "versionAccepted",
        (uint64_t)reply.version_accepted, strtoul(fields[5], NULL, 10));
    if (is_dash(fields[6])) {
        check_uint_equal("sessionreply", line_number, "sessionKeyLength",
            (uint64_t)reply.session_key_length, 0);
    } else {
        check_hex_equal("sessionreply", line_number, "sessionKey",
            (const uint8_t *)reply.session_key, reply.session_key_length, fields[6]);
    }

    if (is_dash(fields[7])) {
        if (reply.server_version_string != NULL) {
            fail("sessionreply", line_number, "serverVersionString present but expected absent");
        } else {
            g_passed++;
        }
    } else {
        check_hex_equal("sessionreply", line_number, "serverVersionString",
            (const uint8_t *)reply.server_version_string, reply.server_version_string_length, fields[7]);
    }

    if (is_dash(fields[8])) {
        if (reply.has_ecdh) {
            fail("sessionreply", line_number, "has_ecdh set on a reply carrying no key");
        } else {
            g_passed++;
        }
    } else {
        check_hex_equal("sessionreply", line_number, "ecdhPublicKey",
            reply.ecdh_public_key, reply.ecdh_public_key_length, fields[8]);
        check_hex_equal("sessionreply", line_number, "ecdhSignature",
            reply.ecdh_signature, reply.ecdh_signature_length, fields[9]);
        if (!reply.has_ecdh) {
            fail("sessionreply", line_number, "has_ecdh clear on a reply carrying both key and signature");
        } else {
            g_passed++;
        }
    }

    /* Truncation must be rejected, not half-parsed. Every prefix is tried because the interesting ones
     * are wherever a length prefix or a varint happens to land, and the runner does not know where those
     * are without re-implementing the parser it is testing. */
    {
        int i;
        int accepted_truncation = 0;
        for (i = 1; i < encoded_len; i++) {
            takion_session_reply partial;
            if (takion_control_parse_session_reply(encoded, (size_t)i, &partial)) {
                accepted_truncation = 1;
                printf("  (accepted a %d-byte prefix of a %d-byte message)\n", i, encoded_len);
                break;
            }
        }
        if (accepted_truncation) {
            fail("sessionreply", line_number, "a truncated message parsed as complete");
        } else {
            g_passed++;
        }
    }
}

/* launchspec <w> <h> <fps> <bitrateKbps> <mtu> <rttMs> <hevc 0|1> <hdr 0|1> <handshakeKey16> <jsonHex> */
static void run_launchspec(char **fields, int count, int line_number)
{
    static char json[HALYARD_LAUNCH_SPEC_MAX];
    uint8_t handshake_key[16];
    halyard_launch_spec_params params;
    size_t written;

    if (count < 11) { fail("launchspec", line_number, "too few fields"); return; }
    if (parse_hex(fields[9], handshake_key, sizeof(handshake_key)) != 16) {
        fail("launchspec", line_number, "handshakeKey must be 16 bytes");
        return;
    }

    memset(&params, 0, sizeof(params));
    params.width        = (int)strtol(fields[1], NULL, 10);
    params.height       = (int)strtol(fields[2], NULL, 10);
    params.fps          = (int)strtol(fields[3], NULL, 10);
    params.bitrate_kbps = (int)strtol(fields[4], NULL, 10);
    params.mtu          = (int)strtol(fields[5], NULL, 10);
    params.rtt_ms       = (int)strtol(fields[6], NULL, 10);
    params.is_hevc      = (int)strtol(fields[7], NULL, 10);
    params.is_hdr       = (int)strtol(fields[8], NULL, 10);

    written = halyard_launch_spec_build(&params, handshake_key, json, sizeof(json));
    if (written == 0) { fail("launchspec", line_number, "build returned 0"); return; }
    check_hex_equal("launchspec", line_number, "json", (const uint8_t *)json, written, fields[10]);
}

/*
 * The hand-written literals in source/connect/main.c. These are the only protobuf this port still
 * encodes by eye, and the failure they invite is specific: field 31 length-delimited is
 * (31 << 3) | 2 = 250, a TWO-byte varint tag. Writing the single 0xFA the arithmetic suggests leaves a
 * message whose type field still reads 31 correctly and whose remainder is garbage - so this walks the
 * whole message, and also proves the wrong encoding would actually be caught.
 */
static void run_connect_literals(void)
{
    static const uint8_t kVersionRequest[] = {
        0x08, 0x1F, 0xFA, 0x01, 0x02, 0x08, 0x09,
    };
    /* The same bytes with the truncated one-byte tag - what a careless hand-encode produces. */
    static const uint8_t kBadVersionRequest[] = {
        0x08, 0x1F, 0xFA, 0x02, 0x08, 0x09,
    };
    static const uint8_t kDisconnect[] = {
        0x08, 0x08, 0x52, 0x0D, 0x0A, 0x0B, 'r','i','p','c','o','r','d','-','3','d','s',
    };
    uint32_t type = 0xffffffffu;

    if (takion_control_validate(kVersionRequest, sizeof(kVersionRequest))
        && takion_control_peek_type(kVersionRequest, sizeof(kVersionRequest), &type)
        && type == 31u) {
        g_passed++;
    } else {
        g_failed++;
        printf("FAIL literals: PROTOCOL_VERSION_REQUEST did not validate as type 31\n");
    }

    if (takion_control_validate(kBadVersionRequest, sizeof(kBadVersionRequest))) {
        g_failed++;
        printf("FAIL literals: the one-byte-tag encoding validated - this test proves nothing\n");
    } else {
        g_passed++;
    }

    type = 0xffffffffu;
    if (takion_control_validate(kDisconnect, sizeof(kDisconnect))
        && takion_control_peek_type(kDisconnect, sizeof(kDisconnect), &type)
        && type == TAKION_CONTROL_DISCONNECT) {
        g_passed++;
    } else {
        g_failed++;
        printf("FAIL literals: DISCONNECT did not validate as type 8\n");
    }
}

/* Minimal protobuf writers, for hand-building the nested message below. Deliberately independent of
 * takion_control_proto.c's own writers: building the test input with the code under test would make the
 * nesting agree with itself and prove nothing about the field numbers. */
static size_t put_varint(uint8_t *buf, uint64_t value)
{
    size_t n = 0;
    while (value >= 0x80u) {
        buf[n++] = (uint8_t)((value & 0x7Fu) | 0x80u);
        value >>= 7;
    }
    buf[n++] = (uint8_t)value;
    return n;
}

static size_t put_varint_field(uint8_t *buf, unsigned field, uint64_t value)
{
    size_t n = put_varint(buf, (uint64_t)field << 3);
    return n + put_varint(buf + n, value);
}

static size_t put_len_field(uint8_t *buf, unsigned field, const uint8_t *data, size_t length)
{
    size_t n = put_varint(buf, ((uint64_t)field << 3) | 2u);
    n += put_varint(buf + n, length);
    memcpy(buf + n, data, length);
    return n + length;
}

/*
 * STREAM_INFO, hand-built. This is a three-level nested message (ControlMessage -> StreamInfoPayload ->
 * ResolutionPayload) and the field it exists to reach - the SPS/PPS parameter sets - is the one thing a
 * decoder cannot start without, because those bytes appear nowhere in the video stream itself. A real
 * one from a console is 280 bytes; this is the same shape, small enough to read.
 */
static void run_stream_info(void)
{
    static const uint8_t kVideoHeader[] = { 0x00, 0x00, 0x00, 0x01, 0x67, 0x42, 0xC0, 0x1E };
    static const uint8_t kAudioHeader[] = { 0x4F, 0x70, 0x75, 0x73 };
    uint8_t resolution[64];
    uint8_t payload[128];
    uint8_t message[160];
    size_t r = 0, p = 0, m = 0;
    takion_stream_info info;

    /* ResolutionPayload{width=640, height=360, videoHeader=...} */
    r += put_varint_field(resolution + r, 1, 640);
    r += put_varint_field(resolution + r, 2, 360);
    r += put_len_field(resolution + r, 3, kVideoHeader, sizeof(kVideoHeader));

    /* StreamInfoPayload{resolution=<above>, audioHeader=..., congestionControlInterval=200} */
    p += put_len_field(payload + p, 1, resolution, r);
    p += put_len_field(payload + p, 2, kAudioHeader, sizeof(kAudioHeader));
    p += put_varint_field(payload + p, 6, 200); /* an unmodelled field, to prove it is skipped */

    /* ControlMessage{type=13, streamInfoPayload=<above>} - field 15 needs a two-byte tag (0x7A 0x01). */
    m += put_varint_field(message + m, 1, TAKION_CONTROL_STREAM_INFO);
    m += put_len_field(message + m, 15, payload, p);

    if (!takion_control_parse_stream_info(message, m, &info)) {
        g_failed++;
        printf("FAIL streaminfo: parse failed\n");
        return;
    }
    if (info.has_resolution && info.width == 640u && info.height == 360u
        && info.video_header_length == sizeof(kVideoHeader)
        && memcmp(info.video_header, kVideoHeader, sizeof(kVideoHeader)) == 0
        && info.audio_header_length == sizeof(kAudioHeader)
        && memcmp(info.audio_header, kAudioHeader, sizeof(kAudioHeader)) == 0) {
        g_passed++;
    } else {
        g_failed++;
        printf("FAIL streaminfo: fields did not survive the nested parse\n");
    }

    /* A SESSION_REPLY must not parse as a STREAM_INFO - the type check is not decoration, since both
     * carry a length-delimited payload and confusing them would hand the demuxer garbage. */
    {
        uint8_t other[8];
        size_t n = takion_control_build_bare(TAKION_CONTROL_SESSION_REPLY, other, sizeof(other));
        takion_stream_info wrong;
        if (n > 0 && takion_control_parse_stream_info(other, n, &wrong)) {
            g_failed++;
            printf("FAIL streaminfo: a SESSION_REPLY parsed as STREAM_INFO\n");
        } else {
            g_passed++;
        }
    }
}

/* The bare-envelope shape, which has no vector because it is two bytes and no payload. */
/*
 * The BANDWIDTH_PROBE envelope that arms and disarms senkusha's echo mode.
 *
 * Expected bytes are hand-encoded from docs/protocol/bandwidth_probe.proto rather than captured, because
 * what is under test is our encoder against the schema:
 *
 *   ControlMessage.type = 1 varint    -> 08 0C            (12 = BANDWIDTH_PROBE)
 *   ControlMessage.bandwidthProbePayload = 14 length      -> 72 06   ((14<<3)|2 = 0x72)
 *     BandwidthProbePayload.command = 1 varint            -> 08 00   (ECHO_COMMAND, emitted although
 *                                                                     zero: it is `required`)
 *     BandwidthProbePayload.echoCommand = 2 length        -> 12 02
 *       EchoCommand.state = 1 varint                      -> 08 01 / 08 00
 */
static void run_echo_command(void)
{
    static const uint8_t expect_on[]  = { 0x08, 0x0C, 0x72, 0x06, 0x08, 0x00, 0x12, 0x02, 0x08, 0x01 };
    static const uint8_t expect_off[] = { 0x08, 0x0C, 0x72, 0x06, 0x08, 0x00, 0x12, 0x02, 0x08, 0x00 };
    uint8_t buf[32];
    size_t n;

    n = takion_control_build_echo_command(1, buf, sizeof(buf));
    if (n != sizeof(expect_on) || memcmp(buf, expect_on, n) != 0) {
        g_failed++;
        printf("FAIL echo: EchoCommand{state=true} encoding\n");
    } else {
        g_passed++;
    }

    n = takion_control_build_echo_command(0, buf, sizeof(buf));
    if (n != sizeof(expect_off) || memcmp(buf, expect_off, n) != 0) {
        g_failed++;
        printf("FAIL echo: EchoCommand{state=false} encoding\n");
    } else {
        g_passed++;
    }

    /* A buffer one byte short must refuse rather than truncate a message the console would misparse. */
    if (takion_control_build_echo_command(1, buf, sizeof(expect_on) - 1) != 0) {
        g_failed++;
        printf("FAIL echo: undersized buffer was not refused\n");
    } else {
        g_passed++;
    }
}

/*
 * The two MTU legs' envelopes, hand-encoded from docs/protocol/bandwidth_probe.proto.
 *
 *   MTU_COMMAND{id=1, mtuReq=1454, num=1}  (1454 = 0x5AE -> varint AE 0B)
 *     inner   08 01  10 AE 0B  20 01                          (id=1, mtuReq=2, num=4)
 *     payload 08 01  1A 07 <inner>                            (command=1, mtuCommand=3)
 *     message 08 0C  72 0B <payload>
 *
 *   CLIENT_MTU_COMMAND{id=1, mtuReq=1454, state=true, mtuDown=1454}
 *     inner   08 01  10 AE 0B  18 01  20 AE 0B                (id, mtuReq, state, mtuDown; 10 bytes)
 *     payload 08 04  2A 0A <inner>                            (command=4, clientMtuCommand=5; 14 bytes)
 *     message 08 0C  72 0E <payload>                          (18 bytes)
 */
static void run_mtu_commands(void)
{
    static const uint8_t expect_mtu[] = {
        0x08, 0x0C, 0x72, 0x0B,
        0x08, 0x01, 0x1A, 0x07,
        0x08, 0x01, 0x10, 0xAE, 0x0B, 0x20, 0x01,
    };
    static const uint8_t expect_client[] = {
        0x08, 0x0C, 0x72, 0x0E,
        0x08, 0x04, 0x2A, 0x0A,
        0x08, 0x01, 0x10, 0xAE, 0x0B, 0x18, 0x01, 0x20, 0xAE, 0x0B,
    };
    uint8_t buf[64];
    size_t n;

    n = takion_control_build_mtu_command(1u, 1454u, 1u, buf, sizeof(buf));
    if (n != sizeof(expect_mtu) || memcmp(buf, expect_mtu, n) != 0) {
        g_failed++;
        printf("FAIL mtu: MTU_COMMAND encoding (%u bytes)\n", (unsigned)n);
    } else {
        g_passed++;
    }

    n = takion_control_build_client_mtu_command(1u, 1454u, 1, buf, sizeof(buf));
    if (n != sizeof(expect_client) || memcmp(buf, expect_client, n) != 0) {
        g_failed++;
        printf("FAIL mtu: CLIENT_MTU_COMMAND{state=true} encoding (%u bytes)\n", (unsigned)n);
    } else {
        g_passed++;
    }

    /* The close differs from the open only in the state byte - and getting that wrong leaves the console
     * stuck in client-MTU mode, so it is worth an assertion of its own. */
    n = takion_control_build_client_mtu_command(2u, 1454u, 0, buf, sizeof(buf));
    if (n != sizeof(expect_client) || buf[14] != 0x00u || buf[9] != 0x02u) {
        g_failed++;
        printf("FAIL mtu: CLIENT_MTU_COMMAND{state=false} did not clear the state byte\n");
    } else {
        g_passed++;
    }

    /* All three must still peek as BANDWIDTH_PROBE, or the console will route them nowhere. */
    {
        uint32_t type = 0xffffffffu;
        if (!takion_control_peek_type(buf, n, &type) || type != TAKION_CONTROL_BANDWIDTH_PROBE) {
            g_failed++;
            printf("FAIL mtu: envelope did not peek as BANDWIDTH_PROBE\n");
        } else {
            g_passed++;
        }
    }

    if (takion_control_build_mtu_command(1u, 1454u, 1u, buf, sizeof(expect_mtu) - 1) != 0) {
        g_failed++;
        printf("FAIL mtu: undersized buffer was not refused\n");
    } else {
        g_passed++;
    }
}

/*
 * The RTT ping itself. Field positions are [W] from session8-wireshark frames 155-175 via
 * SenkushaEchoProbe.cs; this checks that the C port puts them where that file says they go.
 */
static void run_senkusha_echo(void)
{
    /* Sized for the MTU variant below, not just the 548-byte RTT ping. */
    uint8_t buf[1500];
    uint8_t seq = 0xAAu;
    size_t n;

    n = senkusha_echo_build(7u, 0x1122334455ull, SENKUSHA_ECHO_PAYLOAD, 0x00u, buf, sizeof(buf));
    if (n != SENKUSHA_ECHO_PAYLOAD) {
        g_failed++;
        printf("FAIL senkusha: ping length %u, expected %u\n",
            (unsigned)n, (unsigned)SENKUSHA_ECHO_PAYLOAD);
        return;
    }

    if (buf[0] != 0x03u || buf[5] != 7u || buf[6] != 0xFFu || buf[9] != 0xFFu) {
        g_failed++;
        printf("FAIL senkusha: header bytes (type %02x seq %02x markers %02x %02x)\n",
            buf[0], buf[5], buf[6], buf[9]);
    } else {
        g_passed++;
    }

    /* Five bytes, big-endian, at offset 22. */
    if (buf[22] != 0x11u || buf[23] != 0x22u || buf[24] != 0x33u
        || buf[25] != 0x44u || buf[26] != 0x55u) {
        g_failed++;
        printf("FAIL senkusha: timestamp not big-endian at offset 22\n");
    } else {
        g_passed++;
    }

    /* The RTT ping's tail is zero; only the MTU test pads with 0x47. */
    if (buf[27] != 0x00u || buf[SENKUSHA_ECHO_PAYLOAD - 1] != 0x00u) {
        g_failed++;
        printf("FAIL senkusha: RTT ping tail is not zero-filled\n");
    } else {
        g_passed++;
    }

    /* The console echoes verbatim, so our own ping must be recognised as an echo of itself. */
    if (!senkusha_echo_is_echo(buf, n, &seq) || seq != 7u) {
        g_failed++;
        printf("FAIL senkusha: a ping was not recognised as its own echo\n");
    } else {
        g_passed++;
    }

    /* An A/V packet (base type 2) must not be mistaken for an echo. */
    buf[0] = 0x02u;
    if (senkusha_echo_is_echo(buf, n, &seq)) {
        g_failed++;
        printf("FAIL senkusha: a non-echo datagram was accepted\n");
    } else {
        g_passed++;
    }

    /* The MTU variant pads from offset 27 with 0x47 - incompressible, so the datagram is genuinely the
     * size it claims even across a link doing compression. */
    n = senkusha_echo_build(0u, 1ull, 1226u, 0x47u, buf, sizeof(buf));
    if (n != 1226u || buf[27] != 0x47u || buf[1225] != 0x47u || buf[0] != 0x03u) {
        g_failed++;
        printf("FAIL senkusha: MTU-sized ping padding\n");
    } else {
        g_passed++;
    }
}

static void run_bare_envelope(void)
{
    uint8_t buf[8];
    size_t written;
    uint32_t type = 0xffffffffu;

    written = takion_control_build_bare(TAKION_CONTROL_HEARTBEAT, buf, sizeof(buf));
    if (written == 0 || !takion_control_peek_type(buf, written, &type)
        || type != TAKION_CONTROL_HEARTBEAT) {
        g_failed++;
        printf("FAIL bare: heartbeat envelope did not round-trip\n");
    } else {
        g_passed++;
    }

    /* A SESSION_REPLY parse must refuse an envelope of a different type rather than returning a
     * zero-filled struct the caller might read as a valid reply. */
    {
        takion_session_reply reply;
        if (takion_control_parse_session_reply(buf, written, &reply)) {
            g_failed++;
            printf("FAIL bare: a HEARTBEAT envelope parsed as a SESSION_REPLY\n");
        } else {
            g_passed++;
        }
    }

    if (takion_control_build_bare(TAKION_CONTROL_HEARTBEAT, buf, 1) != 0) {
        g_failed++;
        printf("FAIL bare: built into a 1-byte buffer\n");
    } else {
        g_passed++;
    }
}

/* This file asserts by hand rather than through a macro; one local helper keeps the new runner from
 * repeating the same six lines eleven times. */
static void pv_check(int condition, const char *what)
{
    if (condition) {
        g_passed++;
    } else {
        g_failed++;
        printf("FAIL protocol-version: %s\n", what);
    }
}

/*
 * Version negotiation, which decides the ECDH curve and therefore decides whether a session can be
 * established at all. A client that assumes its own top version builds a key on a curve the console may
 * not have chosen, and nothing says so until the peer's point is rejected - after a signature over that
 * same point has already verified, which makes it read like a crypto fault rather than a negotiation one.
 */
static void run_protocol_version_negotiation(void)
{
    static const uint32_t kVersions[] = { 9u, 10u, 11u, 13u, 14u, 15u, 16u, 17u };
    uint8_t buf[128];
    size_t n;
    uint32_t type = 0u;
    uint32_t agreed = 0xffffffffu;

    n = takion_control_build_protocol_version_request(kVersions,
                                                      sizeof(kVersions) / sizeof(kVersions[0]),
                                                      buf, sizeof(buf));
    pv_check(n > 0, "the version request builds");
    pv_check(takion_control_peek_type(buf, n, &type) && type == TAKION_CONTROL_PROTOCOL_VERSION_REQUEST,
          "...and announces itself as a PROTOCOL_VERSION_REQUEST");

    /*
     * The single-version form must be byte-identical to the literal the senkusha bring-up has always
     * sent. That literal came from a capture, and it is the only independent check available that this
     * encoding is right - two nested fields written by hand are exactly where a two-byte varint tag
     * gets written as one.
     */
    {
        static const uint8_t kKnown[] = { 0x08, 0x1F, 0xFA, 0x01, 0x02, 0x08, 0x09 };
        static const uint32_t kNine[] = { 9u };
        uint8_t one[32];
        size_t m = takion_control_build_protocol_version_request(kNine, 1u, one, sizeof(one));

        pv_check(m == sizeof(kKnown), "the one-version request is the length the capture shows");
        pv_check(m == sizeof(kKnown) && memcmp(one, kKnown, m) == 0,
              "...and byte-identical to it - field 31 is a TWO-byte tag, 0xFA 0x01");
    }

    /* An ack naming version 11: ControlMessage{type=32, protocolVersionAck={protocolVersion=11}} */
    {
        static const uint8_t kAck[] = { 0x08, 0x20, 0x82, 0x02, 0x02, 0x08, 0x0B };

        pv_check(takion_control_parse_protocol_version_ack(kAck, sizeof(kAck), &agreed), "the ack parses");
        pv_check(agreed == 11u, "...and yields the version the console chose, not the one we asked for");
    }

    /* An ack with no version field: not an error, the caller falls back to what it requested. */
    {
        static const uint8_t kBare[] = { 0x08, 0x20 };
        uint32_t none = 7u;

        pv_check(!takion_control_parse_protocol_version_ack(kBare, sizeof(kBare), &none),
              "an ack carrying no version reports that it carried none");
        pv_check(none == 0u, "...and does not leave a stale value behind");
    }

    /* Truncation is refused rather than half-read. */
    {
        static const uint8_t kShort[] = { 0x08, 0x20, 0x82, 0x02, 0x09, 0x08 };
        uint32_t bad = 3u;

        pv_check(!takion_control_parse_protocol_version_ack(kShort, sizeof(kShort), &bad),
              "a truncated ack is refused");
    }
}

int main(int argc, char **argv)
{
    const char *path = (argc > 1) ? argv[1] : "vectors/control-proto.kat";
    FILE *file;
    static char line[MAX_LINE];
    int line_number = 0;

    file = fopen(path, "r");
    if (file == NULL) {
        printf("could not open %s\n", path);
        printf("generate it from the repository root with:\n");
        printf("    dotnet run --project tools/Ripcord.ProtocolLab -- vectors\n");
        return 1;
    }

    while (fgets(line, sizeof(line), file) != NULL) {
        char *fields[16];
        int count = 0;
        char *token;

        line_number++;
        if (line[0] == '#' || line[0] == '\n' || line[0] == '\r' || line[0] == '\0')
            continue;

        token = strtok(line, " \t\r\n");
        while (token != NULL && count < 16) {
            fields[count++] = token;
            token = strtok(NULL, " \t\r\n");
        }
        if (count == 0)
            continue;

        if (strcmp(fields[0], "version") == 0)
            continue;

        if (strcmp(fields[0], "sessionreq") == 0)
            run_sessionreq(fields, count, line_number);
        else if (strcmp(fields[0], "sessionreply") == 0)
            run_sessionreply(fields, count, line_number);
        else if (strcmp(fields[0], "launchspec") == 0)
            run_launchspec(fields, count, line_number);
    }

    fclose(file);

    run_bare_envelope();
    run_echo_command();
    run_mtu_commands();
    run_senkusha_echo();
    run_connect_literals();
    run_stream_info();
    run_protocol_version_negotiation();

    printf("\n%d passed, %d failed\n", g_passed, g_failed);
    if (g_passed == 0) {
        printf("no vectors were actually checked - treating that as a failure.\n");
        return 1;
    }
    return g_failed == 0 ? 0 : 1;
}
