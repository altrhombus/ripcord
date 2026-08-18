/*
 * ripcord-3ds - stream key-agreement known-answer runner (ECDH -> per-direction stream keys).
 *
 * Reads tests/vectors/session-crypto.kat, produced by
 *
 *     dotnet run --project tools/Ripcord.ProtocolLab -- vectors
 *
 * and checks this port's ECDH seam (rc_ecdh) and the chain hanging off it against the .NET
 * implementation's answers (HalyardStreamKeySchedule). Same cross-language method as vector_runner.c and
 * stream_crypto_test.c; what is new here is that the thing being checked is a *delegated* primitive.
 *
 * WHY CHECK A LIBRARY WE DID NOT WRITE. rc_ecdh.c hands the point arithmetic to mbedtls, so this is not
 * testing mbedtls's ECDH - it is testing the ~150 lines of ours around it, which is where every plausible
 * bug actually lives: curve selection, uncompressed-point layout, whether the shared X is written at the
 * curve's full width or trimmed, and whether a peer key is validated before use. Those got the whole port
 * wrong the last time a coordinate width was assumed rather than checked, and mbedtls will not catch any
 * of them because from its side each is a legal request.
 *
 * SKIPS CLEANLY WITH NO BACKEND. Built without -DRC_CRYPTO_MBEDTLS there is no EC implementation to
 * check, and this runner says so and exits 0 rather than failing - the same contract the managed suites
 * use for vectors that need the dirty room, and the reason rc_crypto.h's "builds anywhere" property
 * survives this phase. The `streamkeys` chain is the one that matters most when it does run: it goes
 * private scalar -> shared secret -> KDF -> keys with no intermediate handed over, so a port that gets
 * ECDH wrong cannot pass the half it can still do.
 *
 * THIS BUILDS FOR THE HOST, NOT THE 3DS - see tests/Makefile.
 */
#include "../crypto/rc_crypto.h"
#include "../crypto/rc_ecdh.h"
#include "../stream/stream_key_schedule.h"
#include "../takion/takion_session_negotiator.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define MAX_LINE 4096
#define MAX_BLOB 512

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
    to_hex(actual, length, actual_hex);
    if (strcmp(actual_hex, expect_hex) == 0) {
        g_passed++;
    } else {
        g_failed++;
        printf("FAIL %s line %d: %s mismatch\n  got  %s\n  want %s\n",
            kind, line_number, label, actual_hex, expect_hex);
    }
}

static void fail(const char *kind, int line_number, const char *why)
{
    g_failed++;
    printf("FAIL %s line %d: %s\n", kind, line_number, why);
}

/*
 * A counter RNG. rc_ecdh uses randomness only to blind intermediate point coordinates, which changes
 * nothing about the result, so a fixed sequence here keeps the run reproducible without weakening
 * anything being tested. Do not copy this into device code.
 */
static int test_rng(void *ctx, uint8_t *out, size_t length)
{
    unsigned char *state = (unsigned char *)ctx;
    size_t i;
    for (i = 0; i < length; i++) {
        out[i] = (uint8_t)(*state);
        (*state)++;
    }
    return 0;
}

static unsigned curve_from_name(const char *name)
{
    if (strcmp(name, "p256") == 0) return RC_ECDH_CURVE_P256;
    if (strcmp(name, "p521") == 0) return RC_ECDH_CURVE_P521;
    return 0;
}

/* Rebuilds the local pair from the vector's fixed scalar. Returns 1 on success. */
static int load_pair(const char *curve_name, const char *priv_hex, rc_ecdh_keypair *out_pair)
{
    unsigned char rng_state = 0x5a;
    uint8_t priv[RC_ECDH_PRIVATE_MAX];
    int priv_len;
    unsigned curve = curve_from_name(curve_name);

    if (curve == 0)
        return 0;
    priv_len = parse_hex(priv_hex, priv, sizeof(priv));
    if (priv_len <= 0)
        return 0;
    return rc_ecdh_keypair_from_private(curve, priv, (size_t)priv_len, test_rng, &rng_state, out_pair);
}

/* ecdhpub <curve> <privHex> <pubHex> */
static void run_ecdhpub(char **fields, int count, int line_number)
{
    rc_ecdh_keypair pair;

    if (count < 4) { fail("ecdhpub", line_number, "too few fields"); return; }
    if (!load_pair(fields[1], fields[2], &pair)) {
        fail("ecdhpub", line_number, "keypair_from_private rejected a scalar .NET accepted");
        return;
    }
    check_hex_equal("ecdhpub", line_number, "publicKey",
        pair.public_key, pair.public_key_length, fields[3]);
}

/* ecdhshared <curve> <localPrivHex> <peerPubHex> <sharedHex> */
static void run_ecdhshared(char **fields, int count, int line_number)
{
    rc_ecdh_keypair pair;
    uint8_t peer_pub[RC_ECDH_PUBKEY_MAX];
    uint8_t shared[RC_ECDH_SECRET_MAX];
    size_t shared_len = 0;
    unsigned char rng_state = 0xa5;
    int peer_len;

    if (count < 5) { fail("ecdhshared", line_number, "too few fields"); return; }
    if (!load_pair(fields[1], fields[2], &pair)) {
        fail("ecdhshared", line_number, "keypair_from_private failed");
        return;
    }
    peer_len = parse_hex(fields[3], peer_pub, sizeof(peer_pub));
    if (peer_len <= 0) { fail("ecdhshared", line_number, "bad peer key hex"); return; }

    if (!rc_ecdh_derive_shared(&pair, peer_pub, (size_t)peer_len, test_rng, &rng_state,
                               shared, sizeof(shared), &shared_len)) {
        fail("ecdhshared", line_number, "derive_shared rejected a valid peer point");
        return;
    }
    check_hex_equal("ecdhshared", line_number, "sharedSecret", shared, shared_len, fields[4]);
}

/* ecdhsig <handshakeKey16> <pubHex> <sigHex> - no EC involved, just the HMAC that authenticates the key */
static void run_ecdhsig(char **fields, int count, int line_number)
{
    uint8_t handshake_key[16];
    uint8_t pub[RC_ECDH_PUBKEY_MAX];
    uint8_t signature[RC_SHA256_DIGEST_SIZE];
    int pub_len;

    if (count < 4) { fail("ecdhsig", line_number, "too few fields"); return; }
    if (parse_hex(fields[1], handshake_key, sizeof(handshake_key)) != 16) {
        fail("ecdhsig", line_number, "handshakeKey must be 16 bytes");
        return;
    }
    pub_len = parse_hex(fields[2], pub, sizeof(pub));
    if (pub_len <= 0) { fail("ecdhsig", line_number, "bad pubkey hex"); return; }

    rc_hmac_sha256(handshake_key, sizeof(handshake_key), pub, (size_t)pub_len, signature);
    check_hex_equal("ecdhsig", line_number, "signature", signature, sizeof(signature), fields[3]);
}

/* streamkeys <curve> <localPrivHex> <peerPubHex> <handshakeKey16> <direction> <aesKey16> <baseIv16> */
static void run_streamkeys(char **fields, int count, int line_number)
{
    rc_ecdh_keypair pair;
    uint8_t peer_pub[RC_ECDH_PUBKEY_MAX];
    uint8_t shared[RC_ECDH_SECRET_MAX];
    uint8_t handshake_key[16];
    uint8_t aes_key[16];
    uint8_t base_iv[16];
    size_t shared_len = 0;
    unsigned char rng_state = 0x3c;
    unsigned direction;
    int peer_len;

    if (count < 8) { fail("streamkeys", line_number, "too few fields"); return; }
    if (!load_pair(fields[1], fields[2], &pair)) {
        fail("streamkeys", line_number, "keypair_from_private failed");
        return;
    }
    peer_len = parse_hex(fields[3], peer_pub, sizeof(peer_pub));
    if (peer_len <= 0) { fail("streamkeys", line_number, "bad peer key hex"); return; }
    if (parse_hex(fields[4], handshake_key, sizeof(handshake_key)) != 16) {
        fail("streamkeys", line_number, "handshakeKey must be 16 bytes");
        return;
    }
    direction = (unsigned)atoi(fields[5]);

    if (!rc_ecdh_derive_shared(&pair, peer_pub, (size_t)peer_len, test_rng, &rng_state,
                               shared, sizeof(shared), &shared_len)) {
        fail("streamkeys", line_number, "derive_shared failed");
        return;
    }
    stream_key_schedule_derive_direction(shared, shared_len, handshake_key, direction,
                                         aes_key, base_iv);
    check_hex_equal("streamkeys", line_number, "aesKey", aes_key, sizeof(aes_key), fields[6]);
    check_hex_equal("streamkeys", line_number, "baseIv", base_iv, sizeof(base_iv), fields[7]);
}

/*
 * Checks that a tampered peer point is refused. This one has no vector because there is nothing for the
 * .NET side to compute - the expected answer is "rejected" - but it is the property most worth holding
 * onto: without the on-curve check, multiplying our private scalar by an attacker's chosen invalid point
 * leaks the scalar a subgroup at a time, and every legitimate vector above still passes.
 */
static void run_off_curve_guard(void)
{
    unsigned curves[2] = { RC_ECDH_CURVE_P256, RC_ECDH_CURVE_P521 };
    unsigned char rng_state = 0x11;
    size_t i;

    for (i = 0; i < 2; i++) {
        rc_ecdh_keypair a, b;
        uint8_t tampered[RC_ECDH_PUBKEY_MAX];
        uint8_t shared[RC_ECDH_SECRET_MAX];
        size_t shared_len = 0;

        if (!rc_ecdh_generate(curves[i], test_rng, &rng_state, &a)
            || !rc_ecdh_generate(curves[i], test_rng, &rng_state, &b)) {
            g_failed++;
            printf("FAIL offcurve: key generation failed for curve %u\n", curves[i]);
            continue;
        }

        memcpy(tampered, b.public_key, b.public_key_length);
        tampered[5] = (uint8_t)(tampered[5] ^ 0xffu);

        if (rc_ecdh_derive_shared(&a, tampered, b.public_key_length, test_rng, &rng_state,
                                  shared, sizeof(shared), &shared_len)) {
            g_failed++;
            printf("FAIL offcurve: an off-curve peer point was accepted for curve %u\n", curves[i]);
        } else {
            g_passed++;
        }

        /* A peer key on the other curve must be refused too - the wire carries no curve id, so length is
         * the only thing distinguishing them, and silently agreeing on the wrong curve would be worse
         * than failing to connect. */
        if (rc_ecdh_derive_shared(&a, b.public_key,
                                  b.public_key_length == RC_ECDH_P256_PUBKEY_LENGTH
                                      ? RC_ECDH_P521_PUBKEY_LENGTH : RC_ECDH_P256_PUBKEY_LENGTH,
                                  test_rng, &rng_state, shared, sizeof(shared), &shared_len)) {
            g_failed++;
            printf("FAIL offcurve: a mismatched-curve peer key was accepted for curve %u\n", curves[i]);
        } else {
            g_passed++;
        }
    }
}

/* ---- a whole negotiation, both sides ---- */

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

static size_t put_len_field(uint8_t *buf, unsigned field, const uint8_t *data, size_t length)
{
    size_t n = put_varint(buf, ((uint64_t)field << 3) | 2u);
    n += put_varint(buf + n, length);
    memcpy(buf + n, data, length);
    return n + length;
}

static size_t put_varint_field(uint8_t *buf, unsigned field, uint64_t value)
{
    size_t n = put_varint(buf, (uint64_t)field << 3);
    return n + put_varint(buf + n, value);
}

/*
 * Hand-encodes a SESSION_REPLY, standing in for the console.
 *
 * Deliberately NOT built with takion_control_build_*: this port has no reply builder (it never sends
 * one), and using its request builder here would mean the parser was only ever fed bytes its own
 * encoder produced - which proves the two agree with each other and nothing about the schema. These
 * bytes are laid out from docs/protocol/stream_control.proto directly. The .kat vectors cover the same
 * ground against Google.Protobuf's output; this exists so the negotiation can be driven end to end.
 */
static size_t build_session_reply(uint8_t *buf, size_t buf_size,
                                  const uint8_t *pub, size_t pub_length,
                                  const uint8_t *signature, size_t signature_length,
                                  int version_accepted, int include_ecdh)
{
    uint8_t payload[512];
    size_t p = 0;
    size_t n;
    static const char kSessionKey[] = "InvalidSessionId";

    p += put_varint_field(payload + p, 1, 17);                      /* serverVersion */
    p += put_varint_field(payload + p, 2, 0);                       /* token */
    p += put_varint_field(payload + p, 3, 1);                       /* encryptedKeyAccepted */
    p += put_varint_field(payload + p, 4, version_accepted ? 1u : 0u); /* versionAccepted */
    p += put_len_field(payload + p, 5, (const uint8_t *)kSessionKey, strlen(kSessionKey));
    if (include_ecdh) {
        p += put_len_field(payload + p, 8, pub, pub_length);
        p += put_len_field(payload + p, 9, signature, signature_length);
    }

    n = put_varint_field(buf, 1, 1); /* ControlMessage.type = SESSION_REPLY */
    if (n + 8 + p > buf_size) {
        return 0;
    }
    n += put_len_field(buf + n, 3, payload, p);
    return n;
}

/*
 * Drives a complete key agreement against a simulated console and checks that both sides land on the
 * same four values - with the directions crossed, which is the detail worth testing: our send key is the
 * console's receive key, and a port that derives both ends with the same direction byte agrees with
 * itself perfectly and decodes nothing.
 */
static void run_negotiation(void)
{
    unsigned char rng_state = 0x77;
    uint8_t handshake_key[16];
    takion_session_negotiator client;
    rc_ecdh_keypair console_pair;
    uint8_t request[2048];
    uint8_t reply[1024];
    uint8_t console_signature[RC_SHA256_DIGEST_SIZE];
    uint8_t console_shared[RC_ECDH_SECRET_MAX];
    uint8_t console_send_key[16], console_send_iv[16];
    uint8_t console_recv_key[16], console_recv_iv[16];
    size_t console_shared_length = 0;
    size_t request_length, reply_length;
    static const char kLaunchSpec[] = "dGhpcyBpcyBub3QgYSByZWFsIGxhdW5jaCBzcGVj";
    size_t i;

    for (i = 0; i < sizeof(handshake_key); i++) {
        handshake_key[i] = (uint8_t)(0xA0u + i);
    }

    request_length = takion_session_negotiator_begin(&client, TAKION_CLIENT_VERSION, handshake_key,
        kLaunchSpec, strlen(kLaunchSpec), test_rng, &rng_state, request, sizeof(request));
    if (request_length == 0) {
        g_failed++;
        printf("FAIL negotiate: begin() produced no request\n");
        return;
    }

    /* Version 17 must select P-521 - the curve is chosen by version, not configured. */
    if (client.curve != RC_ECDH_CURVE_P521) {
        g_failed++;
        printf("FAIL negotiate: client version 17 did not select P-521\n");
        return;
    }
    g_passed++;

    /* The console: its own ephemeral pair, signed with the same handshakeKey. */
    if (!rc_ecdh_generate(RC_ECDH_CURVE_P521, test_rng, &rng_state, &console_pair)) {
        g_failed++;
        printf("FAIL negotiate: console keygen failed\n");
        return;
    }
    rc_hmac_sha256(handshake_key, sizeof(handshake_key),
                   console_pair.public_key, console_pair.public_key_length, console_signature);

    reply_length = build_session_reply(reply, sizeof(reply),
        console_pair.public_key, console_pair.public_key_length,
        console_signature, sizeof(console_signature), 1, 1);
    if (reply_length == 0) {
        g_failed++;
        printf("FAIL negotiate: could not build the simulated reply\n");
        return;
    }

    if (!takion_session_negotiator_accept_reply(&client, reply, reply_length, test_rng, &rng_state)
        || !client.established) {
        g_failed++;
        printf("FAIL negotiate: client rejected a valid reply\n");
        return;
    }
    g_passed++;

    /* The console derives from its own private key and the client's public key. */
    if (!rc_ecdh_derive_shared(&console_pair, client.local_pair.public_key,
                               client.local_pair.public_key_length, test_rng, &rng_state,
                               console_shared, sizeof(console_shared), &console_shared_length)) {
        g_failed++;
        printf("FAIL negotiate: console-side derive failed\n");
        return;
    }
    stream_key_schedule_derive_direction(console_shared, console_shared_length, handshake_key,
        STREAM_KEY_SCHEDULE_DIRECTION_CLIENT_TO_SERVER, console_recv_key, console_recv_iv);
    stream_key_schedule_derive_direction(console_shared, console_shared_length, handshake_key,
        STREAM_KEY_SCHEDULE_DIRECTION_SERVER_TO_CLIENT, console_send_key, console_send_iv);

    if (memcmp(client.send_aes_key, console_recv_key, 16) == 0
        && memcmp(client.send_base_iv, console_recv_iv, 16) == 0
        && memcmp(client.receive_aes_key, console_send_key, 16) == 0
        && memcmp(client.receive_base_iv, console_send_iv, 16) == 0) {
        g_passed++;
    } else {
        g_failed++;
        printf("FAIL negotiate: the two sides derived different keys\n");
    }

    /* A forged signature must be refused. Flipping one bit of the console's tag is the cheapest
     * possible man-in-the-middle, and it is the whole reason handshakeKey is in this exchange. */
    {
        takion_session_negotiator victim;
        uint8_t forged_signature[RC_SHA256_DIGEST_SIZE];
        uint8_t forged_reply[1024];
        size_t forged_length;
        unsigned char forge_rng = 0x22;

        memcpy(forged_signature, console_signature, sizeof(forged_signature));
        forged_signature[0] = (uint8_t)(forged_signature[0] ^ 0x01u);

        forged_length = build_session_reply(forged_reply, sizeof(forged_reply),
            console_pair.public_key, console_pair.public_key_length,
            forged_signature, sizeof(forged_signature), 1, 1);

        if (takion_session_negotiator_begin(&victim, TAKION_CLIENT_VERSION, handshake_key,
                kLaunchSpec, strlen(kLaunchSpec), test_rng, &forge_rng,
                request, sizeof(request)) == 0) {
            g_failed++;
            printf("FAIL negotiate: begin() failed on the tamper case\n");
        } else if (takion_session_negotiator_accept_reply(&victim, forged_reply, forged_length,
                                                          test_rng, &forge_rng)
                   || victim.established) {
            g_failed++;
            printf("FAIL negotiate: a forged ecdhSignature was accepted\n");
        } else {
            g_passed++;
        }

        /* versionAccepted=false, and a reply with no ECDH material at all, must both be refused. */
        forged_length = build_session_reply(forged_reply, sizeof(forged_reply),
            console_pair.public_key, console_pair.public_key_length,
            console_signature, sizeof(console_signature), 0, 1);
        if (takion_session_negotiator_accept_reply(&victim, forged_reply, forged_length,
                                                   test_rng, &forge_rng)) {
            g_failed++;
            printf("FAIL negotiate: a version-rejected reply was accepted\n");
        } else {
            g_passed++;
        }

        forged_length = build_session_reply(forged_reply, sizeof(forged_reply), NULL, 0, NULL, 0, 1, 0);
        if (takion_session_negotiator_accept_reply(&victim, forged_reply, forged_length,
                                                   test_rng, &forge_rng)) {
            g_failed++;
            printf("FAIL negotiate: a reply carrying no ECDH key was accepted\n");
        } else {
            g_passed++;
        }

        takion_session_negotiator_reset(&victim);
    }

    takion_session_negotiator_reset(&client);
}

int main(int argc, char **argv)
{
    const char *path = (argc > 1) ? argv[1] : "vectors/session-crypto.kat";
    FILE *file;
    char line[MAX_LINE];
    int line_number = 0;

    if (!rc_ecdh_available()) {
        printf("skipped: built without an ECDH backend (-DRC_CRYPTO_MBEDTLS); nothing to check.\n");
        return 0;
    }

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

        if (strcmp(fields[0], "ecdhpub") == 0)
            run_ecdhpub(fields, count, line_number);
        else if (strcmp(fields[0], "ecdhshared") == 0)
            run_ecdhshared(fields, count, line_number);
        else if (strcmp(fields[0], "ecdhsig") == 0)
            run_ecdhsig(fields, count, line_number);
        else if (strcmp(fields[0], "streamkeys") == 0)
            run_streamkeys(fields, count, line_number);
    }

    fclose(file);

    run_off_curve_guard();
    run_negotiation();

    printf("\n%d passed, %d failed\n", g_passed, g_failed);
    if (g_passed == 0) {
        printf("no vectors were actually checked - treating that as a failure.\n");
        return 1;
    }
    return g_failed == 0 ? 0 : 1;
}
