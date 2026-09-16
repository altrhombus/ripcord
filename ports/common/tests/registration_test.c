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

    printf("\n%d passed, %d failed (%d vector(s))\n", g_passed, g_failed, vectors);
    return g_failed == 0 ? 0 : 1;
}
