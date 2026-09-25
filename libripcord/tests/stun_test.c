/*
 * libripcord - the STUN client, both layers.
 *
 * THE VECTORS ARE THE .NET SUITE'S, byte for byte, so both implementations answer to one set:
 * StunMessageTests.cs (RFC 5769's sample IPv4 response, reduced to its XOR-MAPPED-ADDRESS, and its
 * plain, legacy and padded variants) and StunReflexiveAddressTests.cs (a builder-made response, reproduced
 * here by build_response below with the same inputs). RFC 5769 exists so a STUN implementation can be
 * checked without a server: decoding its sample to exactly the published address proves the XOR arithmetic
 * rather than making it look plausible. Every address is from the documentation ranges; nothing here
 * comes from a capture.
 *
 * The cases .NET does not have are the ones C needs and C# does not: the bounds of every length a
 * datagram declares, since here a trusted length is a memory-safety bug and not an exception.
 *
 * THE SOCKET LAYER is exercised on loopback against a fake server in a second thread, mirroring
 * StunClientTests.cs: an answer, a silent first server, a stale transaction id, and nothing at all.
 */
#include "../net/rc_stun.h"
#include "../platform/rc_platform.h"

#include <pthread.h>
#include <stdio.h>
#include <string.h>

#include <arpa/inet.h>
#include <netinet/in.h>
#include <sys/socket.h>
#include <sys/types.h>
#include <unistd.h>

/*
 * THE TEST'S OWN RANDOMNESS, AND IT IS NOT RANDOM - the same injection regist_flow_test.c makes, for the
 * same reason: rc_platform_host.c deliberately has no CSPRNG. A counter is safe only because the sole
 * peer is a fake server in this process. It does make the ids distinct per attempt, which is the one
 * property the tests below depend on.
 */
static uint8_t g_counter;

int rc_random_bytes(uint8_t *out, size_t length)
{
    size_t i;

    for (i = 0; i < length; i++)
        out[i] = (uint8_t)(g_counter++ * 31u + 17u);
    return 1;
}

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
    if (c >= '0' && c <= '9')
        return c - '0';
    if (c >= 'a' && c <= 'f')
        return c - 'a' + 10;
    if (c >= 'A' && c <= 'F')
        return c - 'A' + 10;
    return -1;
}

static size_t from_hex(const char *text, uint8_t *out, size_t capacity)
{
    size_t n = 0;
    size_t i;

    for (i = 0; text[i] != '\0' && text[i + 1] != '\0' && n < capacity; i += 2) {
        int hi = hex_nibble(text[i]);
        int lo = hex_nibble(text[i + 1]);

        if (hi < 0 || lo < 0)
            return 0;
        out[n++] = (uint8_t)((hi << 4) | lo);
    }
    return n;
}

/* RFC 5769's transaction id, shared by every hand-written vector below. */
static const char k_rfc5769_id_hex[] = "b7e7a701bc34d686fa87dfae";

/* StunReflexiveAddressTests.TransactionId: the bytes 1..12. */
static void reflexive_test_id(uint8_t out[RC_STUN_TRANSACTION_ID_SIZE])
{
    size_t i;

    for (i = 0; i < RC_STUN_TRANSACTION_ID_SIZE; i++)
        out[i] = (uint8_t)(i + 1);
}

static int is_ipv4(const rc_stun_address *a, unsigned o0, unsigned o1, unsigned o2, unsigned o3,
                   unsigned port)
{
    return a->family == RC_STUN_FAMILY_IPV4 && a->address[0] == o0 && a->address[1] == o1
        && a->address[2] == o2 && a->address[3] == o3 && a->port == port;
}

/* ---- the message layer ---- */

static void test_build_request(void)
{
    uint8_t id[RC_STUN_CLASSIC_TRANSACTION_ID_SIZE];
    uint8_t out[32];
    uint8_t expect[RC_STUN_HEADER_SIZE];
    rc_stun_message parsed;
    size_t n;

    from_hex(k_rfc5769_id_hex, id, sizeof(id));

    n = rc_stun_build_binding_request(id, RC_STUN_TRANSACTION_ID_SIZE, out, sizeof(out));
    from_hex("00010000" "2112a442" "b7e7a701bc34d686fa87dfae", expect, sizeof(expect));
    CHECK(n == RC_STUN_HEADER_SIZE && memcmp(out, expect, n) == 0,
        "a 12-byte id should give type, zero length, the cookie, then the id");

    /* BindingRequest_RoundTrips: header only, and it reads back as a request with our id and no address. */
    CHECK(rc_stun_parse(out, n, &parsed) == 1, "our own request should parse as STUN");
    CHECK(parsed.type == RC_STUN_BINDING_REQUEST, "round trip type: got 0x%04x", parsed.type);
    CHECK(memcmp(parsed.transaction_id, id, RC_STUN_TRANSACTION_ID_SIZE) == 0, "round trip id");
    CHECK(parsed.has_mapped_address == 0, "a request carries no address");

    /* Sixteen bytes: RFC 3489 framing, written verbatim into bytes 4..20 with no cookie inserted. */
    from_hex("0102030405060708090a0b0c0d0e0f10", id, sizeof(id));
    n = rc_stun_build_binding_request(id, RC_STUN_CLASSIC_TRANSACTION_ID_SIZE, out, sizeof(out));
    CHECK(n == RC_STUN_HEADER_SIZE && out[0] == 0x00 && out[1] == 0x01 && out[2] == 0 && out[3] == 0
        && memcmp(out + 4, id, 16) == 0, "a 16-byte id should be written verbatim after the length");

    /* Fails closed rather than handing back a datagram the caller would send truncated. */
    CHECK(rc_stun_build_binding_request(id, 12, out, 19) == 0, "a 19-byte buffer should be refused");
    CHECK(rc_stun_build_binding_request(id, 12, out, 20) == 20, "exactly 20 bytes is enough");
    CHECK(rc_stun_build_binding_request(id, 0, out, sizeof(out)) == 0, "a 0-byte id should be refused");
    CHECK(rc_stun_build_binding_request(id, 13, out, sizeof(out)) == 0, "a 13-byte id should be refused");
    CHECK(rc_stun_build_binding_request(NULL, 12, out, sizeof(out)) == 0, "a NULL id should be refused");
    CHECK(rc_stun_build_binding_request(id, 12, NULL, sizeof(out)) == 0, "a NULL buffer should be refused");
}

/* Parses `hex` both ways and checks it decodes to 192.0.2.1:32853, the RFC 5769 address. */
static void check_rfc5769_address(const char *label, const char *hex)
{
    uint8_t msg[128];
    uint8_t id[RC_STUN_TRANSACTION_ID_SIZE];
    size_t n = from_hex(hex, msg, sizeof(msg));
    rc_stun_message parsed;
    rc_stun_address addr;

    from_hex(k_rfc5769_id_hex, id, sizeof(id));
    CHECK(rc_stun_parse(msg, n, &parsed) == 1, "%s: should parse", label);
    CHECK(parsed.type == RC_STUN_BINDING_SUCCESS, "%s: should be a Binding Success", label);
    CHECK(parsed.has_mapped_address && is_ipv4(&parsed.mapped_address, 192, 0, 2, 1, 32853),
        "%s: should decode to the documented address", label);

    memset(&addr, 0, sizeof(addr));
    CHECK(rc_stun_parse_binding_response(msg, n, id, sizeof(id), &addr) == RC_STUN_RESPONSE_OK
        && is_ipv4(&addr, 192, 0, 2, 1, 32853), "%s: the matching-response path should agree", label);
}

static void test_dotnet_message_vectors(void)
{
    /* StunMessageTests.DecodesTheRfc5769XorMappedAddressVector */
    check_rfc5769_address("RFC 5769 XOR-MAPPED-ADDRESS",
        "0101000c" "2112a442" "b7e7a701bc34d686fa87dfae" "00200008" "0001a147e112a643");

    /* DecodesPlainMappedAddress: the RFC 3489 form some servers still return, not XORed. */
    check_rfc5769_address("plain MAPPED-ADDRESS",
        "0101000c" "2112a442" "b7e7a701bc34d686fa87dfae" "00010008" "00018055c0000201");

    /* DecodesTheLegacyXorMappedAddressAttribute: 0x8020 must decode identically to 0x0020. */
    check_rfc5769_address("legacy 0x8020 XOR form",
        "0101000c" "2112a442" "b7e7a701bc34d686fa87dfae" "80200008" "0001a147e112a643");

    /* SkipsUnknownAttributesAndStillFindsTheAddress: SOFTWARE of 3 bytes, padded to 4, then the XOR form. */
    check_rfc5769_address("unknown attribute with padding first",
        "01010014" "2112a442" "b7e7a701bc34d686fa87dfae" "80220003" "41424300" "00200008" "0001a147e112a643");
}

static void test_dotnet_non_stun(void)
{
    uint8_t msg[64];
    uint8_t id[RC_STUN_TRANSACTION_ID_SIZE];
    rc_stun_message parsed;
    rc_stun_address addr;
    size_t n;

    from_hex(k_rfc5769_id_hex, id, sizeof(id));

    /* NonStunDatagramsReturnNull: empty, shorter than a header, and a full header with the wrong cookie. */
    CHECK(rc_stun_parse(msg, 0, &parsed) == 0, "an empty datagram is not STUN");
    n = from_hex("00010000", msg, sizeof(msg));
    CHECK(rc_stun_parse(msg, n, &parsed) == 0, "a 4-byte datagram is not STUN");
    n = from_hex("00010000" "00000000" "b7e7a701bc34d686fa87dfae", msg, sizeof(msg));
    CHECK(n == 20 && rc_stun_parse(msg, n, &parsed) == 0, "the wrong cookie is not STUN");
    CHECK(rc_stun_parse_binding_response(msg, n, id, sizeof(id), &addr) == RC_STUN_RESPONSE_NOT_STUN,
        "the wrong cookie is NOT_STUN on the response path too");

    /* AttributeRunningPastTheEndDoesNotThrow: an 8-byte region whose one attribute claims 8 of value with
     * only 4 to hold it. The message is still STUN; the address is simply unreadable. */
    n = from_hex("01010008" "2112a442" "b7e7a701bc34d686fa87dfae" "00200008" "00010001", msg, sizeof(msg));
    CHECK(rc_stun_parse(msg, n, &parsed) == 1 && parsed.has_mapped_address == 0,
        "an attribute past the region should leave no address, not fail the message");
    CHECK(rc_stun_parse_binding_response(msg, n, id, sizeof(id), &addr) == RC_STUN_RESPONSE_NO_ADDRESS,
        "and the response path should report it as ours with no address");
}

/*
 * StunReflexiveAddressTests.Response: a Binding Response carrying XOR-MAPPED-ADDRESS for the given IPv4
 * endpoint, optionally preceded by other attributes, each padded to four bytes.
 */
typedef struct {
    uint16_t type;
    const uint8_t *value;
    size_t length;
} extra_attribute;

static size_t append_attribute(uint8_t *out, size_t at, uint16_t type, const uint8_t *value, size_t length)
{
    size_t padded = (length + 3u) & ~(size_t)3u;

    out[at] = (uint8_t)(type >> 8);
    out[at + 1] = (uint8_t)type;
    out[at + 2] = (uint8_t)(length >> 8);
    out[at + 3] = (uint8_t)length;
    memcpy(out + at + 4, value, length);
    memset(out + at + 4 + length, 0, padded - length);
    return at + 4 + padded;
}

static size_t build_response(uint8_t *out, const uint8_t ip[4], uint16_t port, const uint8_t *id,
                             uint16_t message_type, uint32_t cookie, const extra_attribute *before,
                             size_t before_count)
{
    uint8_t xor_value[8];
    size_t at = RC_STUN_HEADER_SIZE;
    size_t attributes;
    size_t i;

    for (i = 0; i < before_count; i++)
        at = append_attribute(out, at, before[i].type, before[i].value, before[i].length);

    xor_value[0] = 0;
    xor_value[1] = RC_STUN_FAMILY_IPV4;
    xor_value[2] = (uint8_t)((port >> 8) ^ (RC_STUN_MAGIC_COOKIE >> 24));
    xor_value[3] = (uint8_t)((port & 0xFFu) ^ ((RC_STUN_MAGIC_COOKIE >> 16) & 0xFFu));
    xor_value[4] = (uint8_t)(ip[0] ^ (RC_STUN_MAGIC_COOKIE >> 24));
    xor_value[5] = (uint8_t)(ip[1] ^ ((RC_STUN_MAGIC_COOKIE >> 16) & 0xFFu));
    xor_value[6] = (uint8_t)(ip[2] ^ ((RC_STUN_MAGIC_COOKIE >> 8) & 0xFFu));
    xor_value[7] = (uint8_t)(ip[3] ^ (RC_STUN_MAGIC_COOKIE & 0xFFu));
    at = append_attribute(out, at, 0x0020, xor_value, sizeof(xor_value));

    attributes = at - RC_STUN_HEADER_SIZE;
    out[0] = (uint8_t)(message_type >> 8);
    out[1] = (uint8_t)message_type;
    out[2] = (uint8_t)(attributes >> 8);
    out[3] = (uint8_t)attributes;
    out[4] = (uint8_t)(cookie >> 24);
    out[5] = (uint8_t)(cookie >> 16);
    out[6] = (uint8_t)(cookie >> 8);
    out[7] = (uint8_t)cookie;
    memcpy(out + 8, id, RC_STUN_TRANSACTION_ID_SIZE);
    return at;
}

static void test_dotnet_reflexive_vectors(void)
{
    static const uint8_t ip_a[4] = { 198, 51, 100, 55 };
    static const uint8_t ip_b[4] = { 203, 0, 113, 9 };
    static const uint8_t ip_lo[4] = { 127, 0, 0, 1 };
    static const uint8_t source_address[8] = { 0, 1, 0x0d, 0x96, 52, 40, 62, 100 };
    static const uint8_t software[3] = { 0x61, 0x62, 0x63 };
    extra_attribute before[2];
    uint8_t id[RC_STUN_TRANSACTION_ID_SIZE];
    uint8_t other[RC_STUN_TRANSACTION_ID_SIZE];
    uint8_t msg[128];
    rc_stun_address addr;
    size_t n;
    size_t i;

    reflexive_test_id(id);

    /* RecoversTheMappedEndpoint */
    n = build_response(msg, ip_a, 3492, id, RC_STUN_BINDING_SUCCESS, RC_STUN_MAGIC_COOKIE, NULL, 0);
    CHECK(rc_stun_parse_binding_response(msg, n, id, sizeof(id), &addr) == RC_STUN_RESPONSE_OK
        && is_ipv4(&addr, 198, 51, 100, 55, 3492), "should recover the mapped endpoint");

    /* SkipsPrecedingAttributes_IncludingOnesNeedingPadding: walk a 3-byte one wrongly and every attribute
     * after it is misread. */
    before[0].type = 0x0004;
    before[0].value = source_address;
    before[0].length = sizeof(source_address);
    before[1].type = 0x8022;
    before[1].value = software;
    before[1].length = sizeof(software);
    n = build_response(msg, ip_b, 61000, id, RC_STUN_BINDING_SUCCESS, RC_STUN_MAGIC_COOKIE, before, 2);
    CHECK(rc_stun_parse_binding_response(msg, n, id, sizeof(id), &addr) == RC_STUN_RESPONSE_OK
        && is_ipv4(&addr, 203, 0, 113, 9, 61000), "should skip preceding attributes, padding included");

    /* RejectsAResponseToSomebodyElsesRequest: the bytes 50..61 as somebody else's id. */
    for (i = 0; i < sizeof(other); i++)
        other[i] = (uint8_t)(50 + i);
    n = build_response(msg, ip_lo, 1234, other, RC_STUN_BINDING_SUCCESS, RC_STUN_MAGIC_COOKIE, NULL, 0);
    CHECK(rc_stun_parse_binding_response(msg, n, id, sizeof(id), &addr) == RC_STUN_RESPONSE_MISMATCH,
        "somebody else's transaction id should be a mismatch");

    /* RejectsWhatIsNotABindingResponse: an error response, and a datagram with the wrong cookie. */
    n = build_response(msg, ip_lo, 1234, id, RC_STUN_BINDING_ERROR, RC_STUN_MAGIC_COOKIE, NULL, 0);
    CHECK(rc_stun_parse_binding_response(msg, n, id, sizeof(id), &addr) == RC_STUN_RESPONSE_NOT_SUCCESS,
        "an error response is not a binding response");
    n = build_response(msg, ip_lo, 1234, id, RC_STUN_BINDING_SUCCESS, 0xDEADBEEFu, NULL, 0);
    CHECK(rc_stun_parse_binding_response(msg, n, id, sizeof(id), &addr) == RC_STUN_RESPONSE_NOT_STUN,
        "the wrong cookie is not STUN");

    /* RejectsATruncatedMessage */
    msg[0] = 0x01;
    msg[1] = 0x01;
    msg[2] = 0x00;
    CHECK(rc_stun_parse_binding_response(msg, 3, id, sizeof(id), &addr) == RC_STUN_RESPONSE_NOT_STUN,
        "three bytes are not a message");
}

/* ---- what C needs and C# does not: bounds, precedence, and the edges of the walk ---- */

static void test_bounds(void)
{
    uint8_t msg[128];
    uint8_t id[RC_STUN_TRANSACTION_ID_SIZE];
    rc_stun_message parsed;
    rc_stun_address addr;
    size_t n;
    size_t cut;

    from_hex(k_rfc5769_id_hex, id, sizeof(id));

    /* Every truncation of a valid response: never a read past the end (ASan says so in the sanitizer
     * build), and never an address out of a message whose declared length no longer fits. */
    n = from_hex("0101000c" "2112a442" "b7e7a701bc34d686fa87dfae" "00200008" "0001a147e112a643", msg, sizeof(msg));
    for (cut = 0; cut < n; cut++) {
        rc_stun_response_status status = rc_stun_parse_binding_response(msg, cut, id, sizeof(id), &addr);

        CHECK(status == RC_STUN_RESPONSE_NOT_STUN, "a response cut to %u bytes should not be STUN",
            (unsigned)cut);
    }

    /* Trailing bytes after the declared region are ignored, as .NET ignores them. */
    msg[n] = 0xEE;
    msg[n + 1] = 0xEE;
    CHECK(rc_stun_parse_binding_response(msg, n + 2, id, sizeof(id), &addr) == RC_STUN_RESPONSE_OK
        && is_ipv4(&addr, 192, 0, 2, 1, 32853), "trailing bytes past the declared length are ignored");

    /* The largest length the field can declare, on a datagram that holds none of it. */
    msg[2] = 0xFF;
    msg[3] = 0xFF;
    CHECK(rc_stun_parse(msg, n, &parsed) == 0, "a 65535-byte declared length on a short datagram");

    /* Either top bit of the type set: a valid-looking header that is not STUN. */
    n = from_hex("4101000c" "2112a442" "b7e7a701bc34d686fa87dfae" "00200008" "0001a147e112a643", msg, sizeof(msg));
    CHECK(rc_stun_parse(msg, n, &parsed) == 0, "type with bit 14 set is not STUN");
    msg[0] = 0x81;
    CHECK(rc_stun_parse(msg, n, &parsed) == 0, "type with bit 15 set is not STUN");

    /* An attribute region shorter than an attribute header: nothing to walk, no address, still STUN. */
    n = from_hex("01010003" "2112a442" "b7e7a701bc34d686fa87dfae" "002000", msg, sizeof(msg));
    CHECK(rc_stun_parse(msg, n, &parsed) == 1 && parsed.has_mapped_address == 0,
        "a 3-byte attribute region holds no attribute");

    /* A value too short for its family, and a family that is neither: unreadable, never over-read. */
    n = from_hex("01010008" "2112a442" "b7e7a701bc34d686fa87dfae" "00200004" "0001a147", msg, sizeof(msg));
    CHECK(rc_stun_parse(msg, n, &parsed) == 1 && parsed.has_mapped_address == 0,
        "an IPv4 value of 4 bytes has no room for the address");
    n = from_hex("0101000c" "2112a442" "b7e7a701bc34d686fa87dfae" "00200008" "0003a147e112a643", msg, sizeof(msg));
    CHECK(rc_stun_parse(msg, n, &parsed) == 1 && parsed.has_mapped_address == 0, "family 3 is no address");
    n = from_hex("0101000c" "2112a442" "b7e7a701bc34d686fa87dfae" "00200008" "0002a147e112a643", msg, sizeof(msg));
    CHECK(rc_stun_parse(msg, n, &parsed) == 1 && parsed.has_mapped_address == 0,
        "an IPv6 family with 4 address bytes is no address");

    /* Bad arguments are reported as such, not as a datagram that failed to parse. */
    CHECK(rc_stun_parse_binding_response(NULL, 0, id, sizeof(id), &addr) == RC_STUN_RESPONSE_BAD_ARGUMENT,
        "NULL data");
    CHECK(rc_stun_parse_binding_response(msg, n, id, 13, &addr) == RC_STUN_RESPONSE_BAD_ARGUMENT,
        "a 13-byte id");
    CHECK(rc_stun_parse_binding_response(msg, n, id, sizeof(id), NULL) == RC_STUN_RESPONSE_BAD_ARGUMENT,
        "NULL out");
    CHECK(rc_stun_parse(NULL, 20, &parsed) == 0 && rc_stun_parse(msg, n, NULL) == 0, "NULL to rc_stun_parse");
}

static void test_precedence(void)
{
    uint8_t msg[128];
    rc_stun_message parsed;
    size_t n;

    /* A MAPPED-ADDRESS first and an XOR form after it: the XOR form wins even though it came second. The
     * plain one here says 198.51.100.1:1, so which was taken is unambiguous. */
    n = from_hex("01010018" "2112a442" "b7e7a701bc34d686fa87dfae"
                 "00010008" "00010001c6336401" "00200008" "0001a147e112a643", msg, sizeof(msg));
    CHECK(rc_stun_parse(msg, n, &parsed) == 1 && is_ipv4(&parsed.mapped_address, 192, 0, 2, 1, 32853),
        "an XOR form later in the message should win over an earlier plain one");

    /* Two plain forms: the first is kept. */
    n = from_hex("01010018" "2112a442" "b7e7a701bc34d686fa87dfae"
                 "00010008" "00018055c0000201" "00010008" "00010001c6336401", msg, sizeof(msg));
    CHECK(rc_stun_parse(msg, n, &parsed) == 1 && is_ipv4(&parsed.mapped_address, 192, 0, 2, 1, 32853),
        "of two plain forms the first should be kept");

    /* An unreadable XOR form (family 3) is stepped over and a plain one after it still counts. */
    n = from_hex("01010018" "2112a442" "b7e7a701bc34d686fa87dfae"
                 "00200008" "0003a147e112a643" "00010008" "00018055c0000201", msg, sizeof(msg));
    CHECK(rc_stun_parse(msg, n, &parsed) == 1 && is_ipv4(&parsed.mapped_address, 192, 0, 2, 1, 32853),
        "an unreadable XOR form should not hide a readable plain one");

    /* A plain form, then a final attribute whose padding is missing: the walk stops at the end rather
     * than stepping past it, and the plain address found before it still counts. */
    n = from_hex("01010013" "2112a442" "b7e7a701bc34d686fa87dfae"
                 "00010008" "00018055c0000201" "80220003" "414243", msg, sizeof(msg));
    CHECK(rc_stun_parse(msg, n, &parsed) == 1 && is_ipv4(&parsed.mapped_address, 192, 0, 2, 1, 32853),
        "an unpadded final attribute should end the walk, keeping what was found");
}

static void test_ipv6(void)
{
    uint8_t msg[128];
    rc_stun_message parsed;
    size_t n;
    /* A documentation-prefix address (RFC 3849), masked by hand against cookie || the RFC 5769 id - the
     * one case the XOR mask's transaction-id half is reached at all. */
    static const uint8_t expect[16] = {
        0x20, 0x01, 0x0d, 0xb8, 0x12, 0x34, 0x56, 0x78, 0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77
    };

    n = from_hex("01010018" "2112a442" "b7e7a701bc34d686fa87dfae" "00200014"
                 "0002a147" "0113a9faa5d3f179bc25f4b5bed2b9d9", msg, sizeof(msg));
    CHECK(rc_stun_parse(msg, n, &parsed) == 1 && parsed.has_mapped_address
        && parsed.mapped_address.family == RC_STUN_FAMILY_IPV6 && parsed.mapped_address.port == 32853
        && memcmp(parsed.mapped_address.address, expect, 16) == 0,
        "an XORed IPv6 address should unmask against the cookie and the transaction id");
}

static void test_classic_framing(void)
{
    uint8_t id[RC_STUN_CLASSIC_TRANSACTION_ID_SIZE];
    uint8_t msg[64];
    rc_stun_address addr;
    size_t n;

    /* An RFC 3489 server echoes all 16 bytes and has no cookie; its answer is a plain MAPPED-ADDRESS. */
    from_hex("0102030405060708090a0b0c0d0e0f10", id, sizeof(id));
    n = from_hex("0101000c" "0102030405060708090a0b0c0d0e0f10" "00010008" "00018055c0000201", msg, sizeof(msg));
    CHECK(rc_stun_parse_binding_response(msg, n, id, sizeof(id), &addr) == RC_STUN_RESPONSE_OK
        && is_ipv4(&addr, 192, 0, 2, 1, 32853), "a classic response echoing the 16-byte id should match");

    /* The first four bytes are the part a 12-byte comparison would never look at. */
    msg[4] ^= 0xFF;
    CHECK(rc_stun_parse_binding_response(msg, n, id, sizeof(id), &addr) == RC_STUN_RESPONSE_MISMATCH,
        "a classic response differing in the first four id bytes should not match");

    /* And the same bytes are not a modern message: no cookie. */
    msg[4] ^= 0xFF;
    CHECK(rc_stun_parse_binding_response(msg, n, id, 12, &addr) == RC_STUN_RESPONSE_NOT_STUN,
        "without a cookie a classic response is not RFC 5389 STUN");
}

/* ---- the socket layer, on loopback ---- */

typedef struct {
    int sock;
    struct sockaddr_in addr;
    int corrupt_id;
    int answers;               /* how many requests were answered */
    volatile int stop;
    uint8_t mapped_ip[4];
    uint16_t mapped_port;
} fake_server;

static void *fake_server_main(void *arg)
{
    fake_server *server = (fake_server *)arg;

    while (!server->stop) {
        uint8_t buf[256];
        struct sockaddr_in from;
        socklen_t from_len = (socklen_t)sizeof(from);
        ssize_t n = recvfrom(server->sock, buf, sizeof(buf), 0, (struct sockaddr *)&from, &from_len);
        rc_stun_message request;

        if (n <= 0) {
            rc_sleep_ms(2);
            continue;
        }
        if (!rc_stun_parse(buf, (size_t)n, &request) || request.type != RC_STUN_BINDING_REQUEST)
            continue;
        if (server->corrupt_id)
            request.transaction_id[0] ^= 0xFF;
        {
            uint8_t response[64];
            size_t len = build_response(response, server->mapped_ip, server->mapped_port,
                                        request.transaction_id, RC_STUN_BINDING_SUCCESS,
                                        RC_STUN_MAGIC_COOKIE, NULL, 0);

            (void)sendto(server->sock, response, len, 0, (struct sockaddr *)&from, from_len);
            server->answers++;
        }
    }
    return NULL;
}

/* A non-blocking UDP socket on 127.0.0.1 with a kernel-chosen port. */
static int open_loopback(struct sockaddr_in *out)
{
    int sock = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    socklen_t len = (socklen_t)sizeof(*out);

    if (sock < 0)
        return -1;
    memset(out, 0, sizeof(*out));
    out->sin_family = AF_INET;
    out->sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    out->sin_port = 0;
    if (bind(sock, (struct sockaddr *)out, sizeof(*out)) != 0
        || getsockname(sock, (struct sockaddr *)out, &len) != 0) {
        close(sock);
        return -1;
    }
    return sock;
}

/* `run` 0 is a silent server: a bound socket nobody reads, so a request vanishes with no ICMP either. */
static int start_server(fake_server *server, int corrupt_id, int run, pthread_t *thread)
{
    static const uint8_t mapped[4] = { 203, 0, 113, 44 };

    memset(server, 0, sizeof(*server));
    server->sock = open_loopback(&server->addr);
    server->corrupt_id = corrupt_id;
    memcpy(server->mapped_ip, mapped, 4);
    server->mapped_port = 51234;
    if (server->sock < 0)
        return 0;
    if (!run)
        return 1;
    {
        struct timeval tv;

        /* The server blocks with a short timeout rather than spinning, so `stop` is seen promptly. */
        tv.tv_sec = 0;
        tv.tv_usec = 20000;
        (void)setsockopt(server->sock, SOL_SOCKET, SO_RCVTIMEO, &tv, (socklen_t)sizeof(tv));
    }
    return pthread_create(thread, NULL, fake_server_main, server) == 0;
}

/* `thread` is NULL for a silent server, which has none. */
static void stop_server(fake_server *server, const pthread_t *thread)
{
    server->stop = 1;
    if (thread != NULL)
        pthread_join(*thread, NULL);
    close(server->sock);
}

static void test_gather(void)
{
    fake_server answering, silent, liar;
    pthread_t answering_thread, liar_thread;
    struct sockaddr_in local, servers[2];
    rc_stun_address addr;
    size_t index;
    int sock;
    uint64_t start;
    rc_stun_gather_status status;

    sock = open_loopback(&local);
    CHECK(sock >= 0, "could not open the client socket");
    if (sock < 0)
        return;

    /* Gather_ReturnsTheReflexiveEndpointTheServerReports */
    if (start_server(&answering, 0, 1, &answering_thread)) {
        servers[0] = answering.addr;
        index = 99;
        status = rc_stun_gather(sock, servers, 1, RC_STUN_DEFAULT_ATTEMPTS, 1000, &addr, &index);
        CHECK(status == RC_STUN_GATHER_OK && is_ipv4(&addr, 203, 0, 113, 44, 51234) && index == 0,
            "should return what the server reports (status %d)", (int)status);

        /* Gather_FallsThroughToASecondServerWhenTheFirstIsSilent */
        if (start_server(&silent, 0, 0, NULL)) {
            servers[0] = silent.addr;
            servers[1] = answering.addr;
            index = 99;
            status = rc_stun_gather(sock, servers, 2, 1, 150, &addr, &index);
            CHECK(status == RC_STUN_GATHER_OK && index == 1, "should fall through to the second server");
            stop_server(&silent, NULL);
        } else {
            CHECK(0, "could not start the silent server");
        }
        stop_server(&answering, &answering_thread);
    } else {
        CHECK(0, "could not start the answering server");
    }

    /* Gather_IgnoresAResponseWithAMismatchedTransactionId: every answer is for somebody else, so each
     * attempt must run its window out rather than take one - and both attempts must have been made. */
    if (start_server(&liar, 1, 1, &liar_thread)) {
        servers[0] = liar.addr;
        status = rc_stun_gather(sock, servers, 1, 2, 150, &addr, &index);
        CHECK(status == RC_STUN_GATHER_NO_ANSWER, "a mismatched id must not be accepted (status %d)",
            (int)status);
        stop_server(&liar, &liar_thread);
        CHECK(liar.answers == 2, "both attempts should have been sent and answered: %d", liar.answers);
    } else {
        CHECK(0, "could not start the lying server");
    }

    /* Gather_WhenNothingAnswers_ReportsFailureRatherThanHanging: a port somebody had and closed. Also
     * checks attempts 0 is taken as 1, by the time it takes: one window, not zero and not three. */
    {
        struct sockaddr_in dead;
        int dead_sock = open_loopback(&dead);

        if (dead_sock >= 0) {
            close(dead_sock);
            servers[0] = dead;
            start = rc_time_ms();
            status = rc_stun_gather(sock, servers, 1, 0, 200, &addr, &index);
            CHECK(status == RC_STUN_GATHER_NO_ANSWER, "nothing answering is NO_ANSWER (status %d)", (int)status);
            CHECK(rc_time_ms() - start >= 190 && rc_time_ms() - start < 500,
                "attempts 0 should mean one 200 ms window, took %u ms", (unsigned)(rc_time_ms() - start));
        } else {
            CHECK(0, "could not open the dead socket");
        }
    }

    CHECK(rc_stun_gather(-1, servers, 1, 1, 10, &addr, &index) == RC_STUN_GATHER_BAD_ARGUMENT, "bad socket");
    CHECK(rc_stun_gather(sock, NULL, 1, 1, 10, &addr, &index) == RC_STUN_GATHER_BAD_ARGUMENT, "NULL servers");
    CHECK(rc_stun_gather(sock, servers, 0, 1, 10, &addr, &index) == RC_STUN_GATHER_NO_ANSWER,
        "no servers is simply no answer");

    close(sock);
}

int main(void)
{
    test_build_request();
    test_dotnet_message_vectors();
    test_dotnet_non_stun();
    test_dotnet_reflexive_vectors();
    test_bounds();
    test_precedence();
    test_ipv6();
    test_classic_framing();
    test_gather();

    printf("\n%d passed, %d failed\n", g_passed, g_failed);
    return g_failed == 0 ? 0 : 1;
}
