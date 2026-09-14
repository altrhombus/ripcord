/*
 * ripcord-3ds - stream-plane crypto known-answer runner.
 *
 * Reads tests/vectors/stream-crypto.kat, produced by
 *
 *     dotnet run --project tools/Ripcord.ProtocolLab -- vectors
 *
 * and checks this port's GMAC (rc_gmac), stream key schedule (stream_key_schedule_derive_direction) and
 * per-packet nonce/tag derivation (stream_packet_crypto) against it. Every answer in the file was
 * computed by the .NET implementation (AesGcmCore.Mac / HalyardStreamKeySchedule / HalyardPacketCrypto);
 * every answer here is computed by the C one - the same cross-language check tests/vector_runner.c
 * already established for the control-plane crypto, extended to the stream plane because a memorized
 * NIST GCM test vector risked being subtly wrong in a way this cross-check does not.
 *
 * THIS BUILDS FOR THE HOST, NOT THE 3DS - see tests/Makefile. The file format matches
 * vector_runner.c's: flat text, one vector per line, hex fields split on spaces.
 */
#include "../crypto/rc_gcm.h"
#include "../stream/stream_key_schedule.h"
#include "../stream/stream_packet_crypto.h"
#include "../takion/takion_control_sealer.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define MAX_LINE 8192
#define MAX_BLOB 2048

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

static void run_gmac(char **fields, int count, int line_number)
{
    uint8_t key[16], iv[16], aad[MAX_BLOB], tag[16];
    int aad_len;

    if (count < 5) { g_failed++; return; }
    parse_hex(fields[1], key, sizeof(key));
    parse_hex(fields[2], iv, sizeof(iv));
    /* "-" is the emitter's sentinel for a zero-length AAD - see LabVectors.EmitGmac for why a literal
     * empty field can't be used (whitespace-splitting readers silently collapse it). */
    aad_len = (strcmp(fields[3], "-") == 0) ? 0 : parse_hex(fields[3], aad, sizeof(aad));
    if (aad_len < 0) { g_failed++; return; }

    /* Static: rc_gmac_key carries ~4.6 KB of GHASH tables and has no business on a stack frame. */
    {
        static rc_gmac_key gk;
        rc_gmac_key_init(&gk, key);
        rc_gmac_with_key(&gk, iv, sizeof(iv), aad, (size_t)aad_len, tag);
    }
    check_hex_equal("gmac", line_number, "tag", tag, sizeof(tag), fields[4]);
}

static void run_streamkdf(char **fields, int count, int line_number)
{
    uint8_t shared_secret[MAX_BLOB], handshake_key[16], aes_key[16], base_iv[16];
    int secret_len;
    unsigned direction;

    if (count < 6) { g_failed++; return; }
    secret_len = parse_hex(fields[1], shared_secret, sizeof(shared_secret));
    if (secret_len < 0) { g_failed++; return; }
    parse_hex(fields[2], handshake_key, sizeof(handshake_key));
    direction = (unsigned)atoi(fields[3]);

    stream_key_schedule_derive_direction(shared_secret, (size_t)secret_len, handshake_key, direction,
        aes_key, base_iv);
    check_hex_equal("streamkdf", line_number, "aesKey", aes_key, sizeof(aes_key), fields[4]);
    check_hex_equal("streamkdf", line_number, "baseIv", base_iv, sizeof(base_iv), fields[5]);
}

static void run_packetnonce(char **fields, int count, int line_number)
{
    uint8_t aes_key[16], base_iv[16];
    uint64_t key_pos;
    stream_packet_crypto ctx;
    uint8_t gmac_nonce[16], ctr_nonce[16], gmac_key[16];

    if (count < 7) { g_failed++; return; }
    parse_hex(fields[1], aes_key, sizeof(aes_key));
    parse_hex(fields[2], base_iv, sizeof(base_iv));
    key_pos = strtoull(fields[3], NULL, 10);

    stream_packet_crypto_init(&ctx, aes_key, base_iv);
    stream_packet_crypto_gmac_nonce(&ctx, key_pos, gmac_nonce);
    stream_packet_crypto_ctr_nonce(&ctx, key_pos, ctr_nonce);
    stream_packet_crypto_gmac_key(&ctx, key_pos, gmac_key);

    check_hex_equal("packetnonce", line_number, "gmacNonce", gmac_nonce, sizeof(gmac_nonce), fields[4]);
    check_hex_equal("packetnonce", line_number, "ctrNonce", ctr_nonce, sizeof(ctr_nonce), fields[5]);
    check_hex_equal("packetnonce", line_number, "gmacKey", gmac_key, sizeof(gmac_key), fields[6]);
}

static void run_packettag(char **fields, int count, int line_number)
{
    uint8_t aes_key[16], base_iv[16], packet[MAX_BLOB], tag[4];
    uint64_t key_pos;
    int zero_key_pos, tag_offset, packet_len;
    stream_packet_crypto ctx;

    if (count < 7) { g_failed++; return; }
    parse_hex(fields[1], aes_key, sizeof(aes_key));
    parse_hex(fields[2], base_iv, sizeof(base_iv));
    key_pos = strtoull(fields[3], NULL, 10);
    zero_key_pos = atoi(fields[4]);
    tag_offset = atoi(fields[5]);
    packet_len = parse_hex(fields[6], packet, sizeof(packet));
    if (packet_len < 0) { g_failed++; return; }

    stream_packet_crypto_init(&ctx, aes_key, base_iv);
    if (!stream_packet_crypto_compute_tag(&ctx, key_pos, packet, (size_t)packet_len,
            tag_offset, zero_key_pos, tag)) {
        g_failed++;
        printf("FAIL packettag line %d: compute_tag reported failure\n", line_number);
        return;
    }
    check_hex_equal("packettag", line_number, "tag", tag, sizeof(tag), fields[7]);
}

/* This suite is vector-driven and asserts by hand; one local helper keeps the sealer checks readable. */
static void seal_check(int condition, const char *what)
{
    if (condition) {
        g_passed++;
    } else {
        g_failed++;
        printf("FAIL control-sealer: %s\n", what);
    }
}

/*
 * The control sealer. Three things matter and only the first is obvious.
 *
 * That the tag lands at offset 5 and the key position at 9 is the part anyone would test. That the key
 * position ADVANCES BY THE PACKET'S BLOCK-ALIGNED LENGTH is the part that bites: it is a byte count, not
 * a packet count, and a repeated position is a repeated GMAC nonce under one key - as quiet a failure as
 * a repeated IV. And that the AAD zeroes the key-position field as well as the tag is what distinguishes
 * the control rule from the A/V one; getting it wrong produces a tag the console silently rejects.
 */
static void run_control_sealer(void)
{
    static const uint8_t key[16] = {
        0x00,0x11,0x22,0x33,0x44,0x55,0x66,0x77,0x88,0x99,0xaa,0xbb,0xcc,0xdd,0xee,0xff
    };
    static const uint8_t iv[16] = {
        0x0f,0x1e,0x2d,0x3c,0x4b,0x5a,0x69,0x78,0x87,0x96,0xa5,0xb4,0xc3,0xd2,0xe1,0xf0
    };
    takion_control_sealer sealer;
    uint8_t packet[40];
    uint8_t first_tag[4];
    uint32_t pos;

    takion_control_sealer_init(&sealer, key, iv);
    seal_check(sealer.key_pos == 0u, "the first packet seals at key position 0");

    memset(packet, 0xa5, sizeof(packet));
    takion_control_sealer_seal(&sealer, packet, sizeof(packet));

    pos = ((uint32_t)packet[TAKION_CONTROL_KEYPOS_OFFSET + 0] << 24)
        | ((uint32_t)packet[TAKION_CONTROL_KEYPOS_OFFSET + 1] << 16)
        | ((uint32_t)packet[TAKION_CONTROL_KEYPOS_OFFSET + 2] << 8)
        | (uint32_t)packet[TAKION_CONTROL_KEYPOS_OFFSET + 3];
    seal_check(pos == 0u, "the key position is written big-endian at offset 9");
    memcpy(first_tag, packet + TAKION_CONTROL_TAG_OFFSET, sizeof(first_tag));
    seal_check(memcmp(first_tag, "\xa5\xa5\xa5\xa5", 4) != 0, "a tag was written at offset 5");

    /* 40 bytes rounds up to 48, so the next position is 48 and not 1. */
    seal_check(sealer.key_pos == 48u, "the position advances by the BLOCK-ALIGNED length - 40 rounds to 48, got %u");

    /* An exact multiple of the block is not padded. */
    memset(packet, 0xa5, sizeof(packet));
    takion_control_sealer_seal(&sealer, packet, 32u);
    seal_check(sealer.key_pos == 48u + 32u, "an exact block multiple advances by its own length");

    /* The same bytes at a different position must not produce the same tag - that is the whole point of
     * the position being in the nonce. */
    {
        uint8_t a[32], b[32];
        takion_control_sealer s2;

        takion_control_sealer_init(&s2, key, iv);
        memset(a, 0x5a, sizeof(a));
        memset(b, 0x5a, sizeof(b));
        takion_control_sealer_seal(&s2, a, sizeof(a));
        takion_control_sealer_seal(&s2, b, sizeof(b));
        seal_check(memcmp(a + TAKION_CONTROL_TAG_OFFSET, b + TAKION_CONTROL_TAG_OFFSET, 4) != 0, "identical payloads at different key positions must not share a tag");
    }

    /* Too short to hold the fields: left alone rather than written past the end. */
    {
        uint8_t tiny[8];
        uint64_t before;

        memset(tiny, 0x3c, sizeof(tiny));
        before = sealer.key_pos;
        takion_control_sealer_seal(&sealer, tiny, sizeof(tiny));
        seal_check(tiny[7] == 0x3c && sealer.key_pos == before, "a packet too short to seal is left untouched and spends no position");
    }

    takion_control_sealer_reset(&sealer);
    seal_check(sealer.enabled == 0 && sealer.key_pos == 0u, "reset wipes the sealer");
}

int main(int argc, char **argv)
{
    const char *path = (argc > 1) ? argv[1] : "vectors/stream-crypto.kat";
    FILE *file = fopen(path, "r");
    char line[MAX_LINE];
    int line_number = 0;

    if (file == NULL) {
        printf("could not open %s\n", path);
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

        if (strcmp(fields[0], "gmac") == 0)
            run_gmac(fields, count, line_number);
        else if (strcmp(fields[0], "streamkdf") == 0)
            run_streamkdf(fields, count, line_number);
        else if (strcmp(fields[0], "packetnonce") == 0)
            run_packetnonce(fields, count, line_number);
        else if (strcmp(fields[0], "packettag") == 0)
            run_packettag(fields, count, line_number);
    }

    fclose(file);

    run_control_sealer();

    printf("\n%d passed, %d failed\n", g_passed, g_failed);
    if (g_passed == 0) {
        printf("no vectors were actually checked - treating that as a failure.\n");
        return 1;
    }
    return g_failed == 0 ? 0 : 1;
}
