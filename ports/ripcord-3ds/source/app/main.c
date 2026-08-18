/*
 * ripcord-3ds - on-device smoke test.
 *
 * The first milestone for this port is not a video stream, it is proving the control-plane crypto runs
 * correctly and fast enough on an ARM11. This is that: boot the .3dsx, watch it exercise the KDF, the
 * per-field IV and both cipher modes on real hardware, and read how long a field encryption actually
 * takes on a 268/804 MHz core with no crypto extensions.
 *
 * It deliberately checks SELF-CONSISTENCY (round trips, determinism) rather than known answers. The
 * known-answer check against the .NET implementation lives in tests/vector_runner.c and runs on a host,
 * where a failure is cheap to diagnose; baking expected values in here would mean committing derived
 * vectors to the tree for no gain, since anything wrong here is wrong on the host first.
 *
 * The timing number is the interesting output. If a field encryption costs microseconds, the control
 * plane is free and the open question is the A/V path; if it costs milliseconds, that is a finding.
 */
#include "halyard/halyard_v1.h"
#include "crypto/rc_crypto.h"
#include "util/rc_log.h"

#include <3ds.h>

#include <stdio.h>
#include <string.h>

#define ITERATIONS 1000

static void print_hex(const char *label, const uint8_t *data, size_t length)
{
    size_t i;
    rc_log("%s", label);
    for (i = 0; i < length; i++)
        rc_log("%02x", data[i]);
    rc_log("\n");
}

static int run_checks(void)
{
    /* Fixed, obviously-synthetic inputs. Nothing here is a key, a nonce or a pairing value from any real
     * console - this is a smoke test, not a session. */
    static const uint8_t nonce[16] = {
        0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77,
        0x88, 0x99, 0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff
    };
    static const uint8_t companion[16] = {
        0x0f, 0x1e, 0x2d, 0x3c, 0x4b, 0x5a, 0x69, 0x78,
        0x87, 0x96, 0xa5, 0xb4, 0xc3, 0xd2, 0xe1, 0xf0
    };

    halyard_control_field ctx;
    uint8_t key[16], material[16];
    uint8_t key_again[16], material_again[16];
    uint8_t plaintext[64];
    uint8_t ciphertext[64];
    uint8_t recovered[64];
    uint8_t iv[16];
    int failures = 0;
    size_t i;

    if (!halyard_v1_constants_bundled) {
        rc_log("\x1b[31mFAIL\x1b[0m interop constants not compiled in\n");
        return 1;
    }
    rc_log("constants: bundled%s\n", halyard_v1_has_ps4_tables ? ", with PS4 tables" : ", PS5 only");

    /* 1. The KDF runs and is deterministic. */
    if (halyard_control_kdf_derive(nonce, companion, HALYARD_VERSION_SELECTOR_PS5, key, material) != 0) {
        rc_log("\x1b[31mFAIL\x1b[0m kdf refused\n");
        return 1;
    }
    halyard_control_kdf_derive(nonce, companion, HALYARD_VERSION_SELECTOR_PS5, key_again, material_again);
    if (memcmp(key, key_again, 16) != 0 || memcmp(material, material_again, 16) != 0) {
        rc_log("\x1b[31mFAIL\x1b[0m kdf is not deterministic\n");
        failures++;
    }
    print_hex("  key      ", key, 16);
    print_hex("  material ", material, 16);

    /* Cross-check these two lines against the host runner's output for the same inputs if anything here
     * ever looks suspicious - a mismatch would mean an endianness or alignment fault specific to ARM11. */

    /* 2. The IV derivation runs and depends on the counter. */
    halyard_field_iv_derive(halyard_field_context_key(0, 1), material, 0, iv);
    print_hex("  iv[0]    ", iv, 16);
    {
        uint8_t iv_one[16];
        halyard_field_iv_derive(halyard_field_context_key(0, 1), material, 1, iv_one);
        if (memcmp(iv, iv_one, 16) == 0) {
            rc_log("\x1b[31mFAIL\x1b[0m iv does not vary with the counter\n");
            failures++;
        }
    }

    /* 3. CFB round-trips at a length that is not a multiple of the block size, which is where the
     *    partial-final-block rule actually bites. */
    for (i = 0; i < sizeof(plaintext); i++)
        plaintext[i] = (uint8_t)(i * 7 + 1);

    if (halyard_control_field_init(&ctx, nonce, companion, 0, HALYARD_VERSION_SELECTOR_PS5) != 0) {
        rc_log("\x1b[31mFAIL\x1b[0m field init refused\n");
        return failures + 1;
    }

    {
        const size_t lengths[] = { 1, 15, 16, 17, 33, 64 };
        size_t n;
        for (n = 0; n < sizeof(lengths) / sizeof(lengths[0]); n++) {
            size_t length = lengths[n];
            halyard_control_field_encrypt(&ctx, 0, plaintext, ciphertext, length);
            halyard_control_field_decrypt(&ctx, 0, ciphertext, recovered, length);
            if (memcmp(plaintext, recovered, length) != 0) {
                rc_log("\x1b[31mFAIL\x1b[0m cfb round-trip at %u bytes\n", (unsigned)length);
                failures++;
            }
        }
    }

    /* 4. OFB is its own inverse. */
    halyard_control_streaminfo_crypt(&ctx, 0, plaintext, ciphertext, sizeof(plaintext));
    halyard_control_streaminfo_crypt(&ctx, 0, ciphertext, recovered, sizeof(plaintext));
    if (memcmp(plaintext, recovered, sizeof(plaintext)) != 0) {
        rc_log("\x1b[31mFAIL\x1b[0m ofb round-trip\n");
        failures++;
    }

    return failures;
}

static void run_timing(void)
{
    static const uint8_t nonce[16] = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
    static const uint8_t companion[16] = { 16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1 };

    halyard_control_field ctx;
    uint8_t plaintext[16] = { 0 };
    uint8_t ciphertext[16];
    u64 start, elapsed_ms;
    int i;

    if (halyard_control_field_init(&ctx, nonce, companion, 0, HALYARD_VERSION_SELECTOR_PS5) != 0)
        return;

    /* Each iteration is one HMAC-SHA256 plus one AES block - the real per-field cost, since the IV is
     * re-derived for every field rather than cached. */
    start = osGetTime();
    for (i = 0; i < ITERATIONS; i++)
        halyard_control_field_encrypt(&ctx, (uint64_t)i, plaintext, ciphertext, sizeof(plaintext));
    elapsed_ms = osGetTime() - start;

    rc_log("\n%d field encryptions in %llu ms\n", ITERATIONS, (unsigned long long)elapsed_ms);
    if (elapsed_ms > 0)
        rc_log("  ~%llu us each\n", (unsigned long long)(elapsed_ms * 1000 / ITERATIONS));
}

int main(int argc, char **argv)
{
    int failures;

    /* Without this a New 3DS runs at the Old 3DS clock speed - the timing number below would describe a
     * machine we are not targeting. Named directly in SETUP.md's gotcha list. */
    osSetSpeedupEnable(true);

    gfxInitDefault();
    consoleInit(GFX_TOP, NULL);

    rc_log_open(argc > 0 ? argv[0] : NULL, "smoke-test.log");

    rc_log("ripcord-3ds control-crypto smoke test\n");
    rc_log("-------------------------------------\n");

    failures = run_checks();
    run_timing();

    rc_log("\n%s\n", failures == 0
        ? "\x1b[32mall checks passed\x1b[0m"
        : "\x1b[31mSOME CHECKS FAILED\x1b[0m");
    rc_log("\nPress START to exit.\n");
    rc_log_close();

    while (aptMainLoop()) {
        hidScanInput();
        if (hidKeysDown() & KEY_START)
            break;
        gfxFlushBuffers();
        gfxSwapBuffers();
        gspWaitForVBlank();
    }

    gfxExit();
    return 0;
}
