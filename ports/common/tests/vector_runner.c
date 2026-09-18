/*
 * ripcord-3ds - known-answer vector runner.
 *
 * Reads the vector file produced by
 *
 *     dotnet run --project tools/Ripcord.ProtocolLab -- vectors
 *
 * and checks this port's control-plane crypto against it, line by line. Every answer in that file was
 * computed by the .NET implementation; every answer here is computed by the C one. Where they disagree,
 * one of the two has misread docs/protocol/ps5-session-crypto.md - and that is the finding worth having,
 * because a spec two independent implementations read differently is a spec defect, not a porting detail.
 *
 * THIS BUILDS FOR THE HOST, NOT THE 3DS. It is plain C99 with no devkitPro, no libctru and no console
 * involved, so the crypto can be verified on any machine with a compiler:
 *
 *     make -C ports/ripcord-3ds/tests
 *
 * The on-device build compiles the same source/ files; if they are wrong, they are wrong here first and
 * far more cheaply.
 *
 * The file format is deliberately flat text rather than JSON: a line per vector, hex fields, split on
 * spaces. A JSON parser would be a dependency this port does not otherwise need, on a target where every
 * kilobyte of binary is real, to read a file whose entire grammar is "words on a line".
 */
#include "../crypto/rc_crypto.h"
#include "../halyard/halyard_v1.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define MAX_LINE 8192
#define MAX_BLOB 2048

static int g_passed;
static int g_failed;
static int g_skipped;

/* Per-kind tallies, so "161 passed" cannot hide the fact that an entire kind was silently never exercised
 * because of a typo in its keyword. */
typedef struct {
    const char *name;
    int count;
} kind_tally;

static kind_tally g_kinds[] = {
    { "kdf", 0 }, { "ctxkey", 0 }, { "iv", 0 },
    { "cfbenc", 0 }, { "cfbdec", 0 }, { "ofb", 0 },
    { "field", 0 }, { "streaminfo", 0 },
};

static void tally(const char *kind)
{
    size_t i;
    for (i = 0; i < sizeof(g_kinds) / sizeof(g_kinds[0]); i++) {
        if (strcmp(g_kinds[i].name, kind) == 0) {
            g_kinds[i].count++;
            return;
        }
    }
}

/* ---- small helpers ---- */

static int hex_nibble(char c)
{
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

/* Parse a hex string into `out`. Returns the byte count, or -1 on malformed input / overflow. */
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

static void encode_base64(const uint8_t *data, size_t length, char *out)
{
    static const char kAlphabet[] =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    size_t i = 0;
    size_t o = 0;

    while (i + 2 < length) {
        uint32_t triple = ((uint32_t)data[i] << 16) | ((uint32_t)data[i + 1] << 8) | data[i + 2];
        out[o++] = kAlphabet[(triple >> 18) & 0x3f];
        out[o++] = kAlphabet[(triple >> 12) & 0x3f];
        out[o++] = kAlphabet[(triple >> 6) & 0x3f];
        out[o++] = kAlphabet[triple & 0x3f];
        i += 3;
    }

    if (i + 1 == length) {
        uint32_t triple = (uint32_t)data[i] << 16;
        out[o++] = kAlphabet[(triple >> 18) & 0x3f];
        out[o++] = kAlphabet[(triple >> 12) & 0x3f];
        out[o++] = '=';
        out[o++] = '=';
    } else if (i + 2 == length) {
        uint32_t triple = ((uint32_t)data[i] << 16) | ((uint32_t)data[i + 1] << 8);
        out[o++] = kAlphabet[(triple >> 18) & 0x3f];
        out[o++] = kAlphabet[(triple >> 12) & 0x3f];
        out[o++] = kAlphabet[(triple >> 6) & 0x3f];
        out[o++] = '=';
    }

    out[o] = '\0';
}

static void report(const char *kind, int line_number, int ok,
                   const char *expected, const char *actual)
{
    if (ok) {
        g_passed++;
        tally(kind);
        return;
    }

    g_failed++;
    printf("FAIL  line %d  [%s]\n", line_number, kind);
    printf("        expected %s\n", expected);
    printf("        actual   %s\n", actual);
}

static void check_bytes(const char *kind, int line_number,
                        const uint8_t *actual, size_t actual_length,
                        const char *expected_hex)
{
    char actual_hex[MAX_BLOB * 2 + 1];
    to_hex(actual, actual_length, actual_hex);
    report(kind, line_number, strcmp(actual_hex, expected_hex) == 0, expected_hex, actual_hex);
}

/* ---- per-kind handlers ---- */

/* kdf <family> <nonce> <companion> <key> <material> */
static void run_kdf(char **fields, int count, int line_number)
{
    uint8_t nonce[16], companion[16], key[16], material[16];
    uint8_t expected[32];
    int version_selector;
    char actual_hex[65];
    char expected_hex[65];

    if (count != 6) { g_skipped++; return; }

    version_selector = (strcmp(fields[1], "ps4") == 0)
        ? HALYARD_VERSION_SELECTOR_PS4 : HALYARD_VERSION_SELECTOR_PS5;

    if (parse_hex(fields[2], nonce, sizeof(nonce)) != 16 ||
        parse_hex(fields[3], companion, sizeof(companion)) != 16) {
        g_skipped++;
        return;
    }

    if (halyard_control_kdf_derive(nonce, companion, version_selector, key, material) != 0) {
        printf("FAIL  line %d  [kdf] derive refused (constants missing for %s?)\n",
               line_number, fields[1]);
        g_failed++;
        return;
    }

    /* Key and material are checked together as one 32-byte answer. Checking them separately would let a
     * port that has them swapped fail twice and read as two unrelated bugs; this way the diff shows the
     * swap. That specific mistake has been made on this protocol before. */
    memcpy(expected, key, 16);
    memcpy(expected + 16, material, 16);
    to_hex(expected, 32, actual_hex);
    snprintf(expected_hex, sizeof(expected_hex), "%s%s", fields[4], fields[5]);

    report("kdf", line_number, strcmp(actual_hex, expected_hex) == 0, expected_hex, actual_hex);
}

/* ctxkey <codecSelector> <versionSelector> <key> */
static void run_ctxkey(char **fields, int count, int line_number)
{
    const uint8_t *key;

    if (count != 4) { g_skipped++; return; }

    key = halyard_field_context_key(atoi(fields[1]), atoi(fields[2]));
    check_bytes("ctxkey", line_number, key, HALYARD_CONTEXT_KEY_LENGTH, fields[3]);
}

/* iv <contextKey> <material> <counter> <iv> */
static void run_iv(char **fields, int count, int line_number)
{
    uint8_t context_key[16], material[16], iv[16];
    uint64_t counter;

    if (count != 5) { g_skipped++; return; }

    if (parse_hex(fields[1], context_key, sizeof(context_key)) != 16 ||
        parse_hex(fields[2], material, sizeof(material)) != 16) {
        g_skipped++;
        return;
    }

    counter = strtoull(fields[3], NULL, 10);
    halyard_field_iv_derive(context_key, material, counter, iv);
    check_bytes("iv", line_number, iv, sizeof(iv), fields[4]);
}

/* cfbenc|cfbdec|ofb <key> <iv> <input> <output> */
static void run_mode(char **fields, int count, int line_number)
{
    uint8_t key[16], iv[16];
    uint8_t input[MAX_BLOB];
    uint8_t output[MAX_BLOB];
    int input_length;

    if (count != 5) { g_skipped++; return; }

    if (parse_hex(fields[1], key, sizeof(key)) != 16 ||
        parse_hex(fields[2], iv, sizeof(iv)) != 16) {
        g_skipped++;
        return;
    }

    input_length = parse_hex(fields[3], input, sizeof(input));
    if (input_length < 0) { g_skipped++; return; }

    if (strcmp(fields[0], "cfbenc") == 0)
        rc_aes128_cfb128(key, iv, input, output, (size_t)input_length, 1);
    else if (strcmp(fields[0], "cfbdec") == 0)
        rc_aes128_cfb128(key, iv, input, output, (size_t)input_length, 0);
    else
        rc_aes128_ofb(key, iv, input, output, (size_t)input_length);

    check_bytes(fields[0], line_number, output, (size_t)input_length, fields[4]);
}

/* field <nonce> <companion> <codecSel> <verSel> <counter> <plaintext> <ciphertextBase64> */
static void run_field(char **fields, int count, int line_number)
{
    halyard_control_field ctx;
    uint8_t nonce[16], companion[16];
    uint8_t plaintext[MAX_BLOB];
    uint8_t ciphertext[MAX_BLOB];
    char actual_b64[MAX_BLOB * 2];
    int plaintext_length;
    uint64_t counter;

    if (count != 8) { g_skipped++; return; }

    if (parse_hex(fields[1], nonce, sizeof(nonce)) != 16 ||
        parse_hex(fields[2], companion, sizeof(companion)) != 16) {
        g_skipped++;
        return;
    }

    plaintext_length = parse_hex(fields[6], plaintext, sizeof(plaintext));
    if (plaintext_length < 0) { g_skipped++; return; }

    counter = strtoull(fields[5], NULL, 10);

    if (halyard_control_field_init(&ctx, nonce, companion, atoi(fields[3]), atoi(fields[4])) != 0) {
        printf("FAIL  line %d  [field] init refused (constants missing?)\n", line_number);
        g_failed++;
        return;
    }

    halyard_control_field_encrypt(&ctx, counter, plaintext, ciphertext, (size_t)plaintext_length);
    encode_base64(ciphertext, (size_t)plaintext_length, actual_b64);
    report("field", line_number, strcmp(actual_b64, fields[7]) == 0, fields[7], actual_b64);

    /* Round-trip too: the decrypt path uses a different feedback rule, so an encrypt-only check would let
     * a broken decrypt through. This is the whole reason the .NET side emits cfbdec vectors as well. */
    {
        uint8_t recovered[MAX_BLOB];
        char recovered_hex[MAX_BLOB * 2 + 1];
        halyard_control_field_decrypt(&ctx, counter, ciphertext, recovered, (size_t)plaintext_length);
        to_hex(recovered, (size_t)plaintext_length, recovered_hex);
        report("field", line_number, strcmp(recovered_hex, fields[6]) == 0, fields[6], recovered_hex);
    }
}

/* streaminfo <nonce> <companion> <codecSel> <verSel> <counter> <input> <output> */
static void run_streaminfo(char **fields, int count, int line_number)
{
    halyard_control_field ctx;
    uint8_t nonce[16], companion[16];
    uint8_t input[MAX_BLOB];
    uint8_t output[MAX_BLOB];
    int input_length;
    uint64_t counter;

    if (count != 8) { g_skipped++; return; }

    if (parse_hex(fields[1], nonce, sizeof(nonce)) != 16 ||
        parse_hex(fields[2], companion, sizeof(companion)) != 16) {
        g_skipped++;
        return;
    }

    input_length = parse_hex(fields[6], input, sizeof(input));
    if (input_length < 0) { g_skipped++; return; }

    counter = strtoull(fields[5], NULL, 10);

    if (halyard_control_field_init(&ctx, nonce, companion, atoi(fields[3]), atoi(fields[4])) != 0) {
        printf("FAIL  line %d  [streaminfo] init refused (constants missing?)\n", line_number);
        g_failed++;
        return;
    }

    halyard_control_streaminfo_crypt(&ctx, counter, input, output, (size_t)input_length);
    check_bytes("streaminfo", line_number, output, (size_t)input_length, fields[7]);
}

/* ---- public-reference self-test ---- */

/*
 * Anchor the AES block cipher to FIPS-197 C.1 before trusting anything else.
 *
 * Everything below this point compares against vectors generated by Ripcord's .NET implementation, which
 * makes those checks a test of AGREEMENT, not of correctness - two implementations can agree and both be
 * wrong. This one vector is from the published standard, so it fails independently of anything in this
 * repository. It also localises the most likely defect: a single mistyped S-box byte passes most inputs
 * and fails a scattered few, which reads as a mode bug for a surprisingly long time. (Ask how I know.)
 */
static int self_test(void)
{
    static const uint8_t key[16] = {
        0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
        0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f
    };
    static const uint8_t plaintext[16] = {
        0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77,
        0x88, 0x99, 0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff
    };
    static const char kExpected[] = "69c4e0d86a7b0430d8cdb78070b4c55a";

    rc_aes128 ctx;
    uint8_t out[16];
    char actual[33];

    rc_aes128_init(&ctx, key);
    rc_aes128_encrypt_block(&ctx, plaintext, out);
    to_hex(out, sizeof(out), actual);

    if (strcmp(actual, kExpected) != 0) {
        printf("FAIL  AES-128 does not match FIPS-197 C.1\n");
        printf("        expected %s\n", kExpected);
        printf("        actual   %s\n", actual);
        return 1;
    }

    printf("self-test: AES-128 matches FIPS-197 C.1\n");
    return 0;
}

/* ---- driver ---- */

int main(int argc, char **argv)
{
    const char *path = (argc > 1) ? argv[1] : "vectors/control-crypto.kat";
    FILE *file;
    char line[MAX_LINE];
    int line_number = 0;
    size_t i;

    if (self_test() != 0)
        return 1;

    if (!halyard_v1_constants_bundled) {
        printf("ripcord-3ds vectors: interop constants are not compiled in; nothing to check.\n");
        printf("  run `make constants` (or a plain `make`) first.\n");
        return 1;
    }

    file = fopen(path, "r");
    if (file == NULL) {
        printf("ripcord-3ds vectors: cannot open %s\n", path);
        printf("  generate it with: dotnet run --project tools/Ripcord.ProtocolLab -- vectors\n");
        return 1;
    }

    printf("ripcord-3ds vectors: %s\n", path);

    while (fgets(line, sizeof(line), file) != NULL) {
        char *fields[16];
        int count = 0;
        char *token;

        line_number++;

        /* Comments and blanks. */
        if (line[0] == '#' || line[0] == '\n' || line[0] == '\r' || line[0] == '\0')
            continue;

        token = strtok(line, " \t\r\n");
        while (token != NULL && count < 16) {
            fields[count++] = token;
            token = strtok(NULL, " \t\r\n");
        }
        if (count == 0)
            continue;

        if (strcmp(fields[0], "version") == 0) {
            if (atoi(fields[1]) != 1) {
                printf("unsupported vector file version %s (this runner speaks version 1)\n", fields[1]);
                fclose(file);
                return 1;
            }
            continue;
        }
        if (strcmp(fields[0], "ps4tables") == 0)
            continue;

        if (strcmp(fields[0], "kdf") == 0)
            run_kdf(fields, count, line_number);
        else if (strcmp(fields[0], "ctxkey") == 0)
            run_ctxkey(fields, count, line_number);
        else if (strcmp(fields[0], "iv") == 0)
            run_iv(fields, count, line_number);
        else if (strcmp(fields[0], "cfbenc") == 0 ||
                 strcmp(fields[0], "cfbdec") == 0 ||
                 strcmp(fields[0], "ofb") == 0)
            run_mode(fields, count, line_number);
        else if (strcmp(fields[0], "field") == 0)
            run_field(fields, count, line_number);
        else if (strcmp(fields[0], "streaminfo") == 0)
            run_streaminfo(fields, count, line_number);
        else
            g_skipped++;
    }

    fclose(file);

    printf("\n  by kind: ");
    for (i = 0; i < sizeof(g_kinds) / sizeof(g_kinds[0]); i++)
        printf("%s=%d ", g_kinds[i].name, g_kinds[i].count);
    printf("\n");

    printf("\n%d passed, %d failed, %d skipped\n", g_passed, g_failed, g_skipped);

    /* A run that checked nothing is a failure, not a pass. The most likely cause is a vector file from a
     * newer emitter whose keywords this runner does not know - which would otherwise report a cheerful
     * "0 failed". */
    if (g_passed == 0) {
        printf("no vectors were actually checked - treating that as a failure.\n");
        return 1;
    }

    return g_failed == 0 ? 0 : 1;
}
