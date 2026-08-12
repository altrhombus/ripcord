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
#include "../source/crypto/rc_gcm.h"
#include "../source/stream/stream_key_schedule.h"
#include "../source/stream/stream_packet_crypto.h"

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

    rc_gmac(key, iv, sizeof(iv), aad, (size_t)aad_len, tag);
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

    printf("\n%d passed, %d failed\n", g_passed, g_failed);
    if (g_passed == 0) {
        printf("no vectors were actually checked - treating that as a failure.\n");
        return 1;
    }
    return g_failed == 0 ? 0 : 1;
}
