/*
 * ripcord-3ds - Phase 4 self-test: the /sess/init and /sess/ctrl exchange, minus the crypto itself.
 *
 * The control-field cipher this layer builds on is already checked byte-for-byte against the .NET side
 * in vector_runner.c/control-crypto.kat - this file does not re-litigate that. What it checks is
 * everything new in Phase 4: the binary ctrl-frame codec (against real bytes lifted from
 * HalyardCtrlMessageTests.cs), the /sess/ctrl field plaintext constructions (against the shapes in
 * docs/protocol/ps5-remoteplay-v1-spec.md sec2.1 and the wire-confirmed HalyardSessCtrlFields.cs), the
 * HTTP-like request/response builder and parser (against hand-transcribed text), the base64 codec
 * (round-trip plus a couple of RFC 4648 test-vector-shaped cases), and the control-arm probe. One
 * end-to-end case chains field-plaintext -> control-field encrypt -> base64, exercising the whole
 * pipeline this port would actually run, even though each stage is separately verified elsewhere.
 *
 * THIS BUILDS FOR THE HOST, NOT THE 3DS - see tests/Makefile.
 */
#include "../halyard/halyard_v1.h"
#include "../session/halyard_control_arm.h"
#include "../session/halyard_ctrl_message.h"
#include "../session/halyard_sess_fields.h"
#include "../session/halyard_sess_request.h"
#include "../util/rc_base64.h"

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

/* ---- base64 ---- */

static void test_base64(void)
{
    uint8_t decoded[64];
    char encoded[128];
    size_t n;

    /* RFC 4648 sec10 test vectors. */
    n = rc_base64_encode((const uint8_t *)"f", 1, encoded, sizeof(encoded));
    CHECK(n == 4 && strcmp(encoded, "Zg==") == 0, "encode('f') = '%s', want 'Zg=='", encoded);

    n = rc_base64_encode((const uint8_t *)"fo", 2, encoded, sizeof(encoded));
    CHECK(n == 4 && strcmp(encoded, "Zm8=") == 0, "encode('fo') = '%s', want 'Zm8='", encoded);

    n = rc_base64_encode((const uint8_t *)"foobar", 6, encoded, sizeof(encoded));
    CHECK(n == 8 && strcmp(encoded, "Zm9vYmFy") == 0, "encode('foobar') = '%s', want 'Zm9vYmFy'", encoded);

    n = rc_base64_decode("Zm9vYmFy", 8, decoded, sizeof(decoded));
    CHECK(n == 6 && memcmp(decoded, "foobar", 6) == 0, "decode('Zm9vYmFy') did not round-trip");

    n = rc_base64_decode("not-base64!!", 12, decoded, sizeof(decoded));
    CHECK(n == (size_t)-1, "decode should reject characters outside the alphabet");

    n = rc_base64_decode("Zg=", 3, decoded, sizeof(decoded));
    CHECK(n == (size_t)-1, "decode should reject a length that is not a multiple of 4");

    /* A 16-byte nonce, the actual size /sess/init's RP-Nonce decodes to. */
    {
        uint8_t nonce[16];
        size_t i;
        for (i = 0; i < sizeof(nonce); i++)
            nonce[i] = (uint8_t)(i * 17 + 3);

        n = rc_base64_encode(nonce, sizeof(nonce), encoded, sizeof(encoded));
        CHECK(n == 24, "a 16-byte encode should produce 24 base64 characters, got %u", (unsigned)n);

        n = rc_base64_decode(encoded, n, decoded, sizeof(decoded));
        CHECK(n == sizeof(nonce) && memcmp(decoded, nonce, sizeof(nonce)) == 0,
            "16-byte nonce did not round-trip through base64");
    }
}

/* ---- the binary ctrl-channel frame, against HalyardCtrlMessageTests.cs's own vectors ---- */

static void test_ctrl_message(void)
{
    uint8_t buf[64];
    size_t n;
    unsigned type;
    const uint8_t *payload;
    size_t payload_length;

    /* Parse_RealHeartbeatRequest: size=0, type=0x00fe, reserved=0. */
    {
        uint8_t wire[8];
        from_hex("0000000000fe0000", wire, sizeof(wire));
        n = halyard_ctrl_message_parse(wire, sizeof(wire), &type, &payload, &payload_length);
        CHECK(n == 8, "heartbeat request should parse as 8 bytes, got %u", (unsigned)n);
        CHECK(type == HALYARD_CTRL_TYPE_HEARTBEAT_REQ, "wrong type for heartbeat request: 0x%04x", type);
        CHECK(payload_length == 0, "heartbeat request should have no payload");
    }

    /* HeartbeatReply_SerializesToExactWireBytes. */
    n = halyard_ctrl_message_build(HALYARD_CTRL_TYPE_HEARTBEAT_REP, NULL, 0, buf, sizeof(buf));
    {
        uint8_t expect[8];
        from_hex("0000000001fe0000", expect, sizeof(expect));
        CHECK(n == 8 && memcmp(buf, expect, 8) == 0, "heartbeat reply did not serialize to the exact wire bytes");
    }

    /* Parse_MessageWithPayload_AndAdvancesForNextFrame: a session-id frame immediately followed by a
     * heartbeat request - the parser must consume exactly the first frame and leave the second intact. */
    {
        uint8_t session_payload[17];
        uint8_t wire[8 + 17 + 8];
        /* Synthetic: 0x10 then ASCII "SyntheticSessId!" - the real frame's shape, invented content.
         * The value here until 2026-09-11 made the same claim and was a real captured (encrypted)
         * payload; see HalyardCtrlMessageTests.cs and CaptureProvenanceTests. */
        size_t payload_len = from_hex("1053796e74686574696353657373496421", session_payload, sizeof(session_payload));
        size_t offset = 0;

        CHECK(payload_len == 17, "test setup: session payload should be 17 bytes");

        offset += from_hex("0000001100330000", wire, sizeof(wire));
        memcpy(wire + offset, session_payload, payload_len);
        offset += payload_len;
        offset += from_hex("0000000000fe0000", wire + offset, sizeof(wire) - offset);

        n = halyard_ctrl_message_parse(wire, offset, &type, &payload, &payload_length);
        CHECK(n == 8 + 17, "session-id frame should consume 25 bytes, got %u", (unsigned)n);
        CHECK(type == HALYARD_CTRL_TYPE_SESSION_ID, "wrong type for session-id frame: 0x%04x", type);
        CHECK(payload_length == 17 && memcmp(payload, session_payload, 17) == 0, "session-id payload mismatch");

        n = halyard_ctrl_message_parse(wire + n, offset - n, &type, &payload, &payload_length);
        CHECK(n == 8 && type == HALYARD_CTRL_TYPE_HEARTBEAT_REQ,
            "the second frame should still parse as a heartbeat request after the first is consumed");
    }

    /* TryParse_IncompleteFrame_ReturnsFalse: header claims a 17-byte payload, only 5 are present. */
    {
        uint8_t partial[13];
        from_hex("00000011003300001122334455", partial, sizeof(partial));
        n = halyard_ctrl_message_parse(partial, sizeof(partial), &type, &payload, &payload_length);
        CHECK(n == 0, "an incomplete frame must report 0 (wait for more), not parse short");
    }

    /* build() then parse() should round-trip a non-empty payload. */
    {
        static const uint8_t login = 0x2a;
        n = halyard_ctrl_message_build(HALYARD_CTRL_TYPE_LOGIN, &login, 1, buf, sizeof(buf));
        CHECK(n == 9, "a 1-byte-payload frame should serialize to 9 bytes, got %u", (unsigned)n);

        n = halyard_ctrl_message_parse(buf, n, &type, &payload, &payload_length);
        CHECK(n == 9 && type == HALYARD_CTRL_TYPE_LOGIN && payload_length == 1 && payload[0] == login,
            "build-then-parse round-trip failed for a 1-byte payload");
    }

    /* A buffer too small to hold the frame must fail closed. */
    n = halyard_ctrl_message_build(HALYARD_CTRL_TYPE_HEARTBEAT_REQ, NULL, 0, buf, 4);
    CHECK(n == 0, "undersized buffer should report 0, not a truncated frame");
}

/* ---- /sess/ctrl field plaintexts (spec sec2.1 / HalyardSessCtrlFields.cs) ---- */

static void test_sess_fields(void)
{
    uint8_t out16[16];
    uint8_t out32[32];
    char os[32];
    size_t n;

    /* RP-Auth: registration key (8 bytes here), zero-padded to 16. */
    {
        uint8_t registkey[8];
        from_hex("1a2b3c4d5e6f0011", registkey, sizeof(registkey));
        halyard_sess_field_auth_plaintext(registkey, sizeof(registkey), out16);
        CHECK(memcmp(out16, registkey, 8) == 0, "RP-Auth plaintext should start with the raw registration key");
        {
            static const uint8_t zeros[8] = { 0 };
            CHECK(memcmp(out16 + 8, zeros, 8) == 0, "RP-Auth plaintext should zero-pad the remaining 8 bytes");
        }
    }

    /* RP-Did: 10-byte prefix + up to 16 device-id bytes + 6 zero bytes, NOT length-prefixed. */
    {
        uint8_t device_id[16];
        static const uint8_t expect_prefix[10] = { 0x00, 0x18, 0x00, 0x00, 0x00, 0x07, 0x00, 0x40, 0x00, 0x80 };
        size_t i;
        for (i = 0; i < sizeof(device_id); i++)
            device_id[i] = (uint8_t)(0xa0 + i);

        halyard_sess_field_did_plaintext(device_id, sizeof(device_id), out32);
        CHECK(memcmp(out32, expect_prefix, sizeof(expect_prefix)) == 0, "RP-Did prefix mismatch");
        CHECK(memcmp(out32 + 10, device_id, 16) == 0, "RP-Did middle 16 bytes should be the device id verbatim");
        {
            static const uint8_t zeros[6] = { 0 };
            CHECK(memcmp(out32 + 26, zeros, 6) == 0, "RP-Did should end in 6 zero bytes");
        }
    }

    /* A short device id should leave the rest of the 16-byte middle (and the 6-byte suffix) zero, not
     * garbage or a length prefix. */
    {
        uint8_t short_id[3] = { 0x01, 0x02, 0x03 };
        uint8_t zeros19[19] = { 0 };

        halyard_sess_field_did_plaintext(short_id, sizeof(short_id), out32);
        CHECK(memcmp(out32, "\x00\x18\x00\x00\x00\x07\x00\x40\x00\x80", 10) == 0, "RP-Did prefix mismatch (short id)");
        CHECK(memcmp(out32 + 10, short_id, 3) == 0, "RP-Did should still carry the short device id verbatim");
        CHECK(memcmp(out32 + 13, zeros19, sizeof(zeros19)) == 0,
            "RP-Did's unused middle bytes and 6-byte suffix should all be zero for a short device id");
    }

    /* RP-OSType: ASCII "Win{major}.{minor}\0". */
    n = halyard_sess_field_os_type_plaintext(10, 0, os, sizeof(os));
    CHECK(n == 8 && memcmp(os, "Win10.0", 7) == 0 && os[7] == '\0',
        "RP-OSType plaintext should be 'Win10.0\\0' (8 bytes), got %u bytes", (unsigned)n);

    /* RP-StartBitrate / RP-StreamingType: 4-byte little-endian int - [X], see the header's confidence note. */
    {
        uint8_t out4[4];
        halyard_sess_field_int32le_plaintext(10000, out4);
        CHECK(out4[0] == 0x10 && out4[1] == 0x27 && out4[2] == 0x00 && out4[3] == 0x00,
            "10000 should encode little-endian as 10 27 00 00, got %02x %02x %02x %02x",
            out4[0], out4[1], out4[2], out4[3]);
    }

    /* Login PIN: ASCII digits only, rejects anything else. */
    {
        uint8_t pin_out[8];
        n = halyard_sess_field_login_pin_plaintext("1234", 4, pin_out, sizeof(pin_out));
        CHECK(n == 4 && memcmp(pin_out, "1234", 4) == 0, "login PIN plaintext should be the ASCII digits verbatim");

        n = halyard_sess_field_login_pin_plaintext("12a4", 4, pin_out, sizeof(pin_out));
        CHECK(n == 0, "a non-digit login PIN must be rejected, not silently encrypted");
    }
}

/* ---- the /sess/init and /sess/ctrl HTTP-like request/response, against hand-transcribed text ---- */

static void test_sess_request_paths(void)
{
    CHECK(strcmp(halyard_sess_path(1, "init"), "/sie/ps5/rp/sess/init") == 0, "PS5 init path mismatch");
    CHECK(strcmp(halyard_sess_path(0, "ctrl"), "/sie/ps4/rp/sess/ctrl") == 0, "PS4 ctrl path mismatch");
    CHECK(strcmp(halyard_sess_version(1), "1.0") == 0, "PS5 RP-Version should be 1.0");
    CHECK(strcmp(halyard_sess_version(0), "10.0") == 0, "PS4 RP-Version should be 10.0");
}

static void test_sess_init_request_serialize(void)
{
    halyard_sess_request req;
    char buf[512];
    size_t n;
    static const char expect[] =
        "GET /sie/ps5/rp/sess/init HTTP/1.1\r\n"
        "Host: 192.168.1.42\r\n"
        "User-Agent: remoteplay Windows\r\n"
        "Connection: close\r\n"
        "RP-Registkey: 1a2b3c4d5e6f0011\r\n"
        "RP-Version: 1.0\r\n"
        "Content-Length: 0\r\n"
        "\r\n";

    halyard_sess_request_init(&req, "GET", halyard_sess_path(1, "init"));
    halyard_sess_request_add_header(&req, "Host", "192.168.1.42");
    halyard_sess_request_add_header(&req, "User-Agent", "remoteplay Windows");
    halyard_sess_request_add_header(&req, "Connection", "close");
    halyard_sess_request_add_header(&req, "RP-Registkey", "1a2b3c4d5e6f0011");
    halyard_sess_request_add_header(&req, "RP-Version", halyard_sess_version(1));

    n = halyard_sess_request_serialize(&req, buf, sizeof(buf));
    CHECK(n == strlen(expect) && memcmp(buf, expect, n) == 0, "/sess/init request bytes did not match");

    /* An undersized buffer must fail closed rather than silently truncate a request the caller would
     * otherwise send as-is. */
    n = halyard_sess_request_serialize(&req, buf, 8);
    CHECK(n == 0, "undersized buffer should report 0, not a truncated request");
}

static void test_sess_response_parse(void)
{
    static const char response[] =
        "HTTP/1.1 200 OK\r\n"
        "RP-Nonce: cUJ2UY6/o88oJvSuTr2SGw==\r\n"
        "Content-Length: 0\r\n"
        "\r\n";
    halyard_sess_response parsed;
    char nonce_b64[64];
    size_t n = halyard_sess_response_parse(response, strlen(response), &parsed);

    CHECK(n == strlen(response), "a well-formed response should consume every byte, got %u", (unsigned)n);
    CHECK(parsed.status_code == 200, "status code mismatch: got %d", parsed.status_code);
    CHECK(halyard_sess_response_header(response, &parsed, "rp-nonce", nonce_b64, sizeof(nonce_b64))
        && strcmp(nonce_b64, "cUJ2UY6/o88oJvSuTr2SGw==") == 0,
        "RP-Nonce header lookup failed or mismatched (case-insensitive name match)");
    CHECK(!halyard_sess_response_header(response, &parsed, "RP-Missing", nonce_b64, sizeof(nonce_b64)),
        "looking up an absent header should fail, not return stale/garbage data");

    /* An incomplete response (header block not yet terminated) must report 0, not parse what's there. */
    {
        static const char partial[] = "HTTP/1.1 200 OK\r\nRP-Nonce: cUJ2";
        n = halyard_sess_response_parse(partial, strlen(partial), &parsed);
        CHECK(n == 0, "a response missing its trailing blank line should report 0 (wait for more)");
    }

    /* A declared body that has not fully arrived yet must also report 0. */
    {
        static const char short_body[] = "HTTP/1.1 200 OK\r\nContent-Length: 10\r\n\r\n12345";
        n = halyard_sess_response_parse(short_body, strlen(short_body), &parsed);
        CHECK(n == 0, "a response whose declared body hasn't fully arrived should report 0");
    }
}

/* ---- the control-listener arming probe ---- */

static void test_control_arm(void)
{
    char probe[HALYARD_CONTROL_ARM_PROBE_SIZE];

    halyard_control_arm_build_probe(1, probe);
    CHECK(memcmp(probe, "SRC3", 4) == 0, "PS5 arm probe should be 'SRC3'");
    halyard_control_arm_build_probe(0, probe);
    CHECK(memcmp(probe, "SRC2", 4) == 0, "PS4 arm probe should be 'SRC2'");

    CHECK(halyard_control_arm_is_reply(1, (const uint8_t *)"RES3\x01\x05\x00", 7),
        "PS5 reply with trailing status bytes should still match");
    CHECK(!halyard_control_arm_is_reply(1, (const uint8_t *)"RES2\x01\x05\x00", 7),
        "a PS4 reply should not match a PS5 probe");
    CHECK(!halyard_control_arm_is_reply(1, (const uint8_t *)"RE", 2),
        "a reply shorter than the magic must not match");
}

/* ---- end-to-end: field plaintext -> the already-verified control-field cipher -> base64 ---- */

static void test_end_to_end_field_pipeline(void)
{
    static const uint8_t nonce[16] = {
        0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff
    };
    static const uint8_t companion[16] = {
        0x0f, 0x1e, 0x2d, 0x3c, 0x4b, 0x5a, 0x69, 0x78, 0x87, 0x96, 0xa5, 0xb4, 0xc3, 0xd2, 0xe1, 0xf0
    };
    uint8_t registkey[8] = { 0x1a, 0x2b, 0x3c, 0x4d, 0x5e, 0x6f, 0x00, 0x11 };
    halyard_control_field ctx;
    uint8_t plaintext[16], ciphertext[16], recovered[16];
    char encoded[32];
    size_t n;

    if (halyard_control_field_init(&ctx, nonce, companion, 0, HALYARD_VERSION_SELECTOR_PS5) != 0) {
        CHECK(0, "control field init refused - constants not bundled in this build?");
        return;
    }

    halyard_sess_field_auth_plaintext(registkey, sizeof(registkey), plaintext);
    halyard_control_field_encrypt(&ctx, HALYARD_SESS_COUNTER_AUTH, plaintext, ciphertext, sizeof(ciphertext));

    n = rc_base64_encode(ciphertext, sizeof(ciphertext), encoded, sizeof(encoded));
    CHECK(n == 24, "a 16-byte ciphertext should base64-encode to 24 characters");

    /* Decrypting what was just encrypted, at the same counter, must recover the plaintext - this is the
     * exact shape the console performs on RP-Auth. Decoding the base64 back first exercises the whole
     * chain a real /sess/ctrl request would go through end to end. */
    {
        uint8_t decoded[16];
        n = rc_base64_decode(encoded, n, decoded, sizeof(decoded));
        CHECK(n == 16, "base64 round-trip should recover 16 bytes");

        halyard_control_field_decrypt(&ctx, HALYARD_SESS_COUNTER_AUTH, decoded, recovered, sizeof(recovered));
        CHECK(memcmp(recovered, plaintext, sizeof(plaintext)) == 0,
            "end-to-end RP-Auth pipeline did not round-trip");
    }
}

int main(void)
{
    test_base64();
    test_ctrl_message();
    test_sess_fields();
    test_sess_request_paths();
    test_sess_init_request_serialize();
    test_sess_response_parse();
    test_control_arm();
    test_end_to_end_field_pipeline();

    printf("\n%d passed, %d failed\n", g_passed, g_failed);
    return g_failed == 0 ? 0 : 1;
}
