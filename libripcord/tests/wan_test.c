/*
 * libripcord - the candidates of an internet connect, and the STUN NAT classification.
 *
 * The candidate cases are AccountCandidateTests.cs, value for value: the captured client's STUN, STATIC,
 * LOCAL order, the port-preserving NAT that must not produce a duplicate, and offering nothing rather than
 * something wrong. The selection cases follow PreferredCandidate's four steps in order. Every address is
 * private or from the documentation ranges.
 *
 * The classification runs StunReflexiveAddress.DiscoverAsync's comparison against fake servers on
 * loopback, in threads, as stun_test.c does: two agreeing servers, two disagreeing, and one silent.
 */
#include "../session/halyard_wan_candidates.h"
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

/* Not random: a counter, safe only because the sole peers are fake servers in this process. */
static uint8_t g_counter;

int rc_random_bytes(uint8_t *out, size_t length)
{
    size_t i;

    for (i = 0; i < length; i++)
        out[i] = (uint8_t)(g_counter++ * 13u + 5u);
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

static int is(const halyard_wan_candidate *c, const char *type, const char *address, unsigned port)
{
    return strcmp(c->type, type) == 0 && strcmp(c->address, address) == 0 && c->port == port;
}

static void test_our_candidates(void)
{
    halyard_wan_candidate c[HALYARD_WAN_OUR_CANDIDATES_MAX];
    char long_address[HALYARD_WAN_ADDRESS_MAX + 4];
    int n;

    /* OffersStunStaticAndLocal_InThatOrder */
    n = halyard_wan_our_candidates("10.0.0.7", 63711, "198.51.100.202", 1298, c, 3);
    CHECK(n == 3 && is(&c[0], "STUN", "198.51.100.202", 1298) && is(&c[1], "STATIC", "198.51.100.202", 63711)
              && is(&c[2], "LOCAL", "10.0.0.7", 63711),
          "STUN, STATIC, LOCAL in that order");

    /* APortPreservingNat_DoesNotProduceTwoIdenticalCandidates */
    n = halyard_wan_our_candidates("10.0.0.7", 63711, "198.51.100.202", 63711, c, 3);
    CHECK(n == 2 && is(&c[0], "STUN", "198.51.100.202", 63711) && is(&c[1], "LOCAL", "10.0.0.7", 63711),
          "a port-preserving NAT yields no duplicate");

    /* WithNoReflexiveAddress_TheLocalOneStillStands */
    n = halyard_wan_our_candidates("10.0.0.7", 63711, NULL, 0, c, 3);
    CHECK(n == 1 && is(&c[0], "LOCAL", "10.0.0.7", 63711), "the local candidate stands alone");

    /* WithNothingKnown_OffersNothingRatherThanSomethingWrong */
    CHECK(halyard_wan_our_candidates(NULL, 0, NULL, 0, c, 3) == 0, "nothing known offers nothing");

    /* A reflexive address with no local one: STUN only, since the STATIC guess needs our port. */
    n = halyard_wan_our_candidates(NULL, 0, "198.51.100.202", 1298, c, 3);
    CHECK(n == 1 && is(&c[0], "STUN", "198.51.100.202", 1298), "reflexive only");

    /* C only: capacity and length are refused rather than truncated. */
    memset(long_address, '1', sizeof(long_address) - 1);
    long_address[sizeof(long_address) - 1] = '\0';
    CHECK(halyard_wan_our_candidates("10.0.0.7", 1, NULL, 0, c, 2) == -1, "a short output array is refused");
    CHECK(halyard_wan_our_candidates(long_address, 1, NULL, 0, c, 3) == -1, "an over-long address is refused");
}

static void set(halyard_wan_candidate *c, const char *type, const char *address, uint16_t port)
{
    memset(c, 0, sizeof(*c));
    snprintf(c->type, sizeof(c->type), "%s", type);
    snprintf(c->address, sizeof(c->address), "%s", address);
    c->port = port;
}

static void test_choose(void)
{
    halyard_wan_candidate offer[3];
    halyard_wan_interface lan = { { 192, 168, 1, 20 }, { 255, 255, 255, 0 } };
    halyard_wan_interface elsewhere = { { 172, 31, 0, 5 }, { 255, 255, 0, 0 } };
    halyard_wan_interface zero_mask = { { 203, 0, 113, 9 }, { 0, 0, 0, 0 } };
    uint8_t a[4];

    /* The console's typical offer: its reflexive STATIC, then its LOCAL. */
    set(&offer[0], "STATIC", "203.0.113.50", 9303);
    set(&offer[1], "LOCAL", "192.168.1.40", 9303);

    CHECK(halyard_wan_choose_candidate(offer, 2, &lan, 1, NULL) == 1, "on the same network the local candidate wins");
    CHECK(halyard_wan_choose_candidate(offer, 2, &elsewhere, 1, NULL) == 0, "off it, the first parseable one");
    CHECK(halyard_wan_choose_candidate(offer, 2, NULL, 0, NULL) == 0, "with no interfaces, the first parseable one");
    CHECK(halyard_wan_choose_candidate(offer, 2, &zero_mask, 1, NULL) == 0, "a zero mask never matches");

    /* Steps 3 and 4: nothing parses. */
    set(&offer[0], "STUN", "not-an-address", 9303);
    set(&offer[1], "LOCAL", "console.local", 9303);
    CHECK(halyard_wan_choose_candidate(offer, 2, &lan, 1, "console.local") == 1, "the named host, verbatim");
    CHECK(halyard_wan_choose_candidate(offer, 2, &lan, 1, "other") == 0, "else the first at all");
    CHECK(halyard_wan_choose_candidate(offer, 0, &lan, 1, NULL) == -1, "an empty offer has no choice");

    /* A field filled with no terminator is read as unparseable, never overrun. */
    memset(offer[2].address, '7', sizeof(offer[2].address));
    offer[2].port = 1;
    CHECK(halyard_wan_choose_candidate(&offer[2], 1, &lan, 1, NULL) == 0, "an unterminated address is not read past");

    /* Strict dotted quads only. */
    CHECK(halyard_wan_parse_ipv4("192.0.2.7", a) && a[0] == 192 && a[1] == 0 && a[2] == 2 && a[3] == 7, "parses");
    CHECK(!halyard_wan_parse_ipv4("10", a) && !halyard_wan_parse_ipv4("1.2.3", a) && !halyard_wan_parse_ipv4("1.2.3.4.5", a)
              && !halyard_wan_parse_ipv4("256.1.1.1", a) && !halyard_wan_parse_ipv4("1.2.3.4 ", a)
              && !halyard_wan_parse_ipv4("1..3.4", a) && !halyard_wan_parse_ipv4("0001.2.3.4", a)
              && !halyard_wan_parse_ipv4("::1", a) && !halyard_wan_parse_ipv4("", a),
          "everything but a strict dotted quad is refused");
    {
        char text[16];
        const uint8_t addr[4] = { 198, 51, 100, 0 };

        CHECK(halyard_wan_format_ipv4(addr, text, sizeof(text)) == 12 && strcmp(text, "198.51.100.0") == 0, "formats");
        CHECK(halyard_wan_format_ipv4(addr, text, 12) == 0, "and refuses a buffer one short");
    }
}

/* ---- the NAT classification, against fake servers ---- */

typedef struct {
    int sock;
    struct sockaddr_in addr;
    uint8_t mapped_ip[4];
    uint16_t mapped_port;
    int silent;
    volatile int stop;
} fake_server;

static void *server_main(void *arg)
{
    fake_server *s = (fake_server *)arg;

    while (!s->stop) {
        uint8_t buf[256];
        uint8_t out[32];
        struct sockaddr_in from;
        socklen_t from_len = (socklen_t)sizeof(from);
        ssize_t n = recvfrom(s->sock, buf, sizeof(buf), 0, (struct sockaddr *)&from, &from_len);
        rc_stun_message request;
        uint16_t xport;

        if (n <= 0) {
            rc_sleep_ms(2);
            continue;
        }
        if (s->silent || !rc_stun_parse(buf, (size_t)n, &request) || request.type != RC_STUN_BINDING_REQUEST)
            continue;

        /* A Binding Success carrying one XOR-MAPPED-ADDRESS (RFC 5389 section 15.2). */
        out[0] = 0x01;
        out[1] = 0x01;
        out[2] = 0x00;
        out[3] = 12;
        out[4] = 0x21;
        out[5] = 0x12;
        out[6] = 0xA4;
        out[7] = 0x42;
        memcpy(out + 8, request.transaction_id, 12);
        out[20] = 0x00;
        out[21] = 0x20;
        out[22] = 0x00;
        out[23] = 8;
        out[24] = 0;
        out[25] = RC_STUN_FAMILY_IPV4;
        xport = (uint16_t)(s->mapped_port ^ 0x2112u);
        out[26] = (uint8_t)(xport >> 8);
        out[27] = (uint8_t)xport;
        out[28] = (uint8_t)(s->mapped_ip[0] ^ 0x21);
        out[29] = (uint8_t)(s->mapped_ip[1] ^ 0x12);
        out[30] = (uint8_t)(s->mapped_ip[2] ^ 0xA4);
        out[31] = (uint8_t)(s->mapped_ip[3] ^ 0x42);
        (void)sendto(s->sock, out, sizeof(out), 0, (struct sockaddr *)&from, from_len);
    }
    return NULL;
}

static void start(fake_server *s, pthread_t *thread, unsigned o3, uint16_t port, int silent)
{
    socklen_t len = (socklen_t)sizeof(s->addr);

    memset(s, 0, sizeof(*s));
    s->sock = socket(AF_INET, SOCK_DGRAM, 0);
    s->addr.sin_family = AF_INET;
    s->addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    bind(s->sock, (struct sockaddr *)&s->addr, (socklen_t)sizeof(s->addr));
    getsockname(s->sock, (struct sockaddr *)&s->addr, &len);
    s->mapped_ip[0] = 198;
    s->mapped_ip[1] = 51;
    s->mapped_ip[2] = 100;
    s->mapped_ip[3] = (uint8_t)o3;
    s->mapped_port = port;
    s->silent = silent;
    {
        struct timeval tv = { 0, 20000 };

        setsockopt(s->sock, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof(tv));
    }
    pthread_create(thread, NULL, server_main, s);
}

static void stop(fake_server *s, pthread_t *thread)
{
    s->stop = 1;
    pthread_join(*thread, NULL);
    close(s->sock);
}

static int classify(unsigned o3a, uint16_t pa, int silent_a, unsigned o3b, uint16_t pb, int silent_b,
                    rc_stun_mapping *out)
{
    fake_server a, b;
    pthread_t ta, tb;
    struct sockaddr_in servers[2];
    struct sockaddr_in any;
    int client = socket(AF_INET, SOCK_DGRAM, 0);
    rc_stun_gather_status status;

    start(&a, &ta, o3a, pa, silent_a);
    start(&b, &tb, o3b, pb, silent_b);
    servers[0] = a.addr;
    servers[1] = b.addr;
    memset(&any, 0, sizeof(any));
    any.sin_family = AF_INET;
    any.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    bind(client, (struct sockaddr *)&any, (socklen_t)sizeof(any));

    status = rc_stun_discover_mapping(client, servers, 2, 1, 200, out);

    close(client);
    stop(&a, &ta);
    stop(&b, &tb);
    return (int)status;
}

static void test_mapping(void)
{
    rc_stun_mapping m;
    rc_stun_address x, y;

    CHECK(classify(9, 40000, 0, 9, 40000, 0, &m) == RC_STUN_GATHER_OK && m.endpoint_independent == 1
              && m.reflexive.port == 40000 && m.reflexive.address[3] == 9,
          "two agreeing servers: endpoint-independent");
    CHECK(classify(9, 40000, 0, 9, 40001, 0, &m) == RC_STUN_GATHER_OK && m.endpoint_independent == 0
              && m.reflexive.port == 40000,
          "two disagreeing servers: symmetric, and the first answer is kept");
    CHECK(classify(9, 40000, 1, 10, 40002, 0, &m) == RC_STUN_GATHER_OK && m.endpoint_independent == -1
              && m.reflexive.port == 40002,
          "one silent server: undetermined");
    CHECK(classify(9, 1, 1, 9, 1, 1, &m) == RC_STUN_GATHER_NO_ANSWER, "no answer at all is a normal outcome");
    CHECK(rc_stun_discover_mapping(-1, NULL, 0, 1, 1, &m) == RC_STUN_GATHER_BAD_ARGUMENT, "bad arguments");

    memset(&x, 0, sizeof(x));
    x.family = RC_STUN_FAMILY_IPV4;
    x.port = 5;
    y = x;
    y.address[8] = 1; /* beyond an IPv4 address: ignored */
    CHECK(rc_stun_address_equal(&x, &y), "IPv4 compares four bytes");
    y.family = RC_STUN_FAMILY_IPV6;
    x.family = RC_STUN_FAMILY_IPV6;
    CHECK(!rc_stun_address_equal(&x, &y), "IPv6 compares sixteen");
}

int main(void)
{
    test_our_candidates();
    test_choose();
    test_mapping();

    printf("wan_test: %d passed, %d failed\n", g_passed, g_failed);
    return g_failed == 0 ? 0 : 1;
}
