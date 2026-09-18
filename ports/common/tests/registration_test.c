/*
 * ripcord - PIN-registration known-answer runner.
 *
 * Reads vectors/registration-crypto.kat, produced by
 *
 *     dotnet run --project tools/Ripcord.ProtocolLab -- vectors
 *
 * and checks halyard_registration.c against it line by line. Every answer in that file was computed by
 * the .NET implementation; every answer here is computed by the C one.
 *
 * WHY THIS MATTERS MORE THAN MOST OF THE SUITE. The registration key exchange is the one part of pairing
 * that fails SILENTLY. A wrong transport key does not produce a wrong answer - it produces a console
 * that responds 403 with a generic application reason, which reads exactly like a mistyped PIN, a stale
 * search probe, or the wrong transport. Every one of those is a plausible thing to chase, and none of
 * them is checkable from the console's side of the wire. Checking the arithmetic here, on a host,
 * against numbers the reference computed, is what stops that from being a hardware mystery in front of a
 * television with a PIN on it.
 *
 * Runs on the host. No console, no toolchain, no devkit.
 */
#include "../halyard/halyard_registration.h"
#include "../halyard/halyard_v1.h"
#include "../session/halyard_regist_message.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int g_passed;
static int g_failed;

static void check(int condition, const char *what, int line)
{
    if (condition) {
        g_passed++;
    } else {
        g_failed++;
        printf("FAIL (line %d): %s\n", line, what);
    }
}

static int unhex(const char *text, uint8_t *out, size_t out_size, size_t *out_length)
{
    size_t n = strlen(text);
    size_t i;

    if ((n & 1u) != 0u || n / 2u > out_size)
        return 0;
    for (i = 0; i < n; i += 2) {
        unsigned value;

        if (sscanf(text + i, "%2x", &value) != 1)
            return 0;
        out[i / 2] = (uint8_t)value;
    }
    *out_length = n / 2u;
    return 1;
}

/*
 * THE MESSAGE LAYER, which carries no secret and needs no tables - that is the whole reason it is a
 * separate file from the key exchange. Every fixture below is synthetic.
 */
static void test_message(void)
{
    char field[256];
    uint8_t request[512];
    size_t n;

    /* The request field, whose Np-AccountId is a base64'd LITTLE-endian u64 for a numeric id. */
    n = halyard_regist_field_plaintext("1234567890123456", field, sizeof(field));
    check(n > 0, "field plaintext built", __LINE__);
    check(strstr(field, "Client-Type: " HALYARD_REGIST_CLIENT_TYPE_HEX "\r\n") == field,
          "Client-Type leads the field", __LINE__);
    check(strstr(field, "\r\nNp-AccountId: ") != NULL, "Np-AccountId follows it", __LINE__);
    /* 1234567890123456 little-endian is c0 ba 8a 3c d5 62 04 00, which base64s to wLqKPNViBAA=. */
    check(strstr(field, "Np-AccountId: wLqKPNViBAA=\r\n") != NULL,
          "numeric id is a base64 LITTLE-endian u64", __LINE__);

    /* The request head. Uppercase HOST with no port, no Content-Type, no Np-AccountId header. */
    {
        const uint8_t body[4] = { 0xde, 0xad, 0xbe, 0xef };

        n = halyard_regist_build_request(1, "192.0.2.5", body, sizeof(body), request, sizeof(request));
        check(n > sizeof(body), "request built", __LINE__);
        request[n - sizeof(body)] = '\0';   /* terminate the head for strstr; the body follows it */
        check(strstr((char *)request, "POST /sie/ps5/rp/sess/rgst HTTP/1.1\r\n") == (char *)request,
              "PS5 path on the request line", __LINE__);
        check(strstr((char *)request, "\r\nHOST: 192.0.2.5\r\n") != NULL,
              "uppercase HOST, no port", __LINE__);
        check(strstr((char *)request, "\r\nContent-Length: 4\r\n") != NULL, "content length",
              __LINE__);
        check(strstr((char *)request, "\r\nRP-Version: 1.0\r\n") != NULL, "PS5 RP-Version", __LINE__);
        check(strstr((char *)request, "Content-Type") == NULL, "no Content-Type", __LINE__);
        check(strstr((char *)request, "Np-AccountId") == NULL,
              "the account id is NOT a header - it is inside the encrypted body", __LINE__);

        n = halyard_regist_build_request(0, "192.0.2.5", body, sizeof(body), request, sizeof(request));
        request[n - sizeof(body)] = '\0';
        check(strstr((char *)request, "POST /sie/ps4/rp/sess/rgst") == (char *)request,
              "PS4 path", __LINE__);
        check(strstr((char *)request, "\r\nRP-Version: 10.0\r\n") != NULL, "PS4 RP-Version", __LINE__);
    }

    /* Splitting a refusal, and keeping the console's own explanation of it. */
    {
        static const char refusal[] =
            "HTTP/1.1 403 Forbidden\r\n"
            "RP-Application-Reason: 80108bff\r\n"
            "Content-Length: 0\r\n"
            "\r\n";
        int status = 0;
        const uint8_t *body = NULL;
        size_t body_length = 0;
        char reason[32];

        check(halyard_regist_split_response((const uint8_t *)refusal, sizeof(refusal) - 1,
                                            &status, &body, &body_length, reason, sizeof(reason)),
              "refusal parsed", __LINE__);
        check(status == 403, "status recovered", __LINE__);
        check(strcmp(reason, "80108bff") == 0,
              "the console's own reason is kept - without it every refusal reads alike", __LINE__);
        check(body_length == 0, "empty body", __LINE__);
    }

    /* And a success, whose body is the (here already decrypted) pairing record. */
    {
        static const char ok[] =
            "HTTP/1.1 200 OK\r\n"
            "Content-Length: 64\r\n"
            "\r\n"
            "PS5-RegistKey: 3161326233633464\r\n"
            "RP-Key: 000102030405060708090a0b0c0d0e0f\r\n"
            "RP-KeyType: 2\r\n";
        int status = 0;
        const uint8_t *body = NULL;
        size_t body_length = 0;
        halyard_regist_record rec;

        check(halyard_regist_split_response((const uint8_t *)ok, sizeof(ok) - 1,
                                            &status, &body, &body_length, NULL, 0),
              "success parsed", __LINE__);
        check(status == 200, "200", __LINE__);
        check(halyard_regist_parse_record(body, body_length, &rec), "record parsed", __LINE__);
        check(rec.is_ps5 == 1, "family comes from which RegistKey field the console used", __LINE__);
        check(rec.key_type == 2, "key type", __LINE__);
        check(rec.registration_key_length == 8, "registkey is hex-DECODED to 8 bytes", __LINE__);
        /*
         * The decoded bytes are themselves the ASCII "1a2b3c4d". Storing the 16-character hex string
         * instead double-encodes it and /sess/init is answered with a 403 - the exact failure this
         * check exists to prevent.
         */
        check(memcmp(rec.registration_key, "1a2b3c4d", 8) == 0,
              "and the 8 bytes are the ASCII the console meant", __LINE__);
        check(rec.companion[0] == 0x00 && rec.companion[15] == 0x0f, "companion decoded", __LINE__);
    }

    /* A reply missing either required field is not a pairing record. */
    {
        static const char partial[] = "PS5-RegistKey: 3161326233633464\r\n";
        halyard_regist_record rec;

        check(!halyard_regist_parse_record((const uint8_t *)partial, sizeof(partial) - 1, &rec),
              "a record with no RP-Key is refused", __LINE__);
    }
}

int main(int argc, char **argv)
{
    const char *path = (argc > 1) ? argv[1] : "vectors/registration-crypto.kat";
    FILE *f = fopen(path, "r");
    char line[4096];
    int vectors = 0;

    if (f == NULL) {
        printf("registration_test: cannot open %s\n", path);
        return 1;
    }

    /*
     * A build without the tables is a legitimate build, not a failure - see gen_constants.py. Saying so
     * and passing is right; silently checking nothing is not, which is why this prints either way.
     */
    if (!halyard_registration_available()) {
        printf("registration_test: this build carries no registration tables - nothing to check\n");
        fclose(f);
        return 0;
    }

    while (fgets(line, sizeof(line), f) != NULL) {
        char ctx_hex[2048], mat_hex[64], key_hex[64], wrap_hex[64];
        unsigned long passcode;
        int is_ps5;

        if (sscanf(line, "registration %d %2047s %lu %63s %63s %63s",
                   &is_ps5, ctx_hex, &passcode, mat_hex, key_hex, wrap_hex) != 6)
            continue;

        {
            uint8_t context[1024], material[16], want_key[16], want_wrap[16];
            uint8_t got_key[16], got_wrap[16], got_material[16];
            uint8_t scattered[1024], gathered[16];
            size_t ctx_len = 0, n;

            if (!unhex(ctx_hex, context, sizeof(context), &ctx_len)
                || !unhex(mat_hex, material, sizeof(material), &n)
                || !unhex(key_hex, want_key, sizeof(want_key), &n)
                || !unhex(wrap_hex, want_wrap, sizeof(want_wrap), &n)) {
                printf("FAIL: unparsable vector\n");
                g_failed++;
                continue;
            }

            /* PS4 vectors are only meaningful where the PS4 tables were built in. */
            if (!is_ps5 && !halyard_v1_has_ps4_registration)
                continue;

            check(halyard_registration_derive_key(is_ps5, context, ctx_len,
                                                  (uint32_t)passcode, got_key),
                  "derive_key succeeds", __LINE__);
            check(memcmp(got_key, want_key, 16) == 0, "transport key matches the reference", __LINE__);

            check(halyard_registration_wrap_material(is_ps5, material, context, ctx_len, got_wrap),
                  "wrap_material succeeds", __LINE__);
            check(memcmp(got_wrap, want_wrap, 16) == 0, "wrapped material matches", __LINE__);

            /*
             * The round trip is checked here as well as on the .NET side, because the two transforms
             * are written separately in both implementations and matching the reference's WRAPPED bytes
             * says nothing about whether this side's unwrap agrees with this side's wrap.
             */
            check(halyard_registration_unwrap_material(is_ps5, got_wrap, context, ctx_len, got_material),
                  "unwrap_material succeeds", __LINE__);
            check(memcmp(got_material, material, 16) == 0, "unwrap returns the material", __LINE__);

            /* Scatter and gather, which the wire needs and the reference's vector does not carry. */
            memcpy(scattered, context, ctx_len);
            check(halyard_registration_scatter(got_wrap, scattered, ctx_len), "scatter succeeds",
                  __LINE__);
            check(halyard_registration_gather(scattered, ctx_len, gathered), "gather succeeds",
                  __LINE__);
            check(memcmp(gathered, got_wrap, 16) == 0, "gather returns what scatter wrote", __LINE__);

            vectors++;
        }
    }
    fclose(f);

    /* An empty file would otherwise pass silently, which is the failure this whole runner exists against. */
    if (vectors == 0) {
        printf("FAIL: no vectors were read from %s\n", path);
        g_failed++;
    }

    test_message();

    printf("\n%d passed, %d failed (%d vector(s))\n", g_passed, g_failed, vectors);
    return g_failed == 0 ? 0 : 1;
}
