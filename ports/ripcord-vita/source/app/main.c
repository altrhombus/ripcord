/*
 * ripcord-vita - on-device smoke test.
 *
 * Ported from ports/ripcord-3ds/source/app/main.c, and kept deliberately close to it: the point of this
 * program is a NUMBER that can be compared against the 3DS's, so the work it measures has to be the same
 * work. The 3DS measured ~25 us per control-field encryption on an ARM11 at 804 MHz with no crypto
 * extensions, which settled that the control plane is free and the A/V path is where the budget goes.
 * A Cortex-A9 should do considerably better; how much better is the question, and it is the first real
 * input to whether software AES on the A/V path is affordable here.
 *
 * Like its 3DS counterpart it checks SELF-CONSISTENCY (round trips, determinism) rather than known
 * answers. The known-answer check against the .NET implementation lives in ports/common/tests and runs
 * on a host, where a failure is cheap to diagnose.
 *
 * OUTPUT GOES TO A FILE, not the screen. vitasdk ships no debug-screen printf (psvDebugScreen is a
 * samples/common file, not SDK), and the 3DS port's logging model turned out to be the better one
 * anyway: results land in a file you copy off the card rather than a photograph of a handheld you then
 * retype. See ux0:data/ripcord/smoke-test.log.
 */

#include "crypto/rc_crypto.h"
#include "halyard/halyard_v1.h"
#include "platform/rc_platform.h"
#include "util/rc_log.h"

#include <psp2/kernel/processmgr.h>

#include <stdio.h>
#include <string.h>

#define ITERATIONS 1000

static int run_checks(void)
{
    /* Fixed, obviously-synthetic inputs. Nothing here is a key, a nonce or a pairing value from any
     * real console - this is a smoke test, not a session. */
    static const uint8_t nonce[16] = {
        0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77,
        0x88, 0x99, 0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff
    };
    static const uint8_t companion[16] = {
        0x0f, 0x1e, 0x2d, 0x3c, 0x4b, 0x5a, 0x69, 0x78,
        0x87, 0x96, 0xa5, 0xb4, 0xc3, 0xd2, 0xe1, 0xf0
    };

    halyard_control_field ctx, ctx2;
    uint8_t plaintext[64], ciphertext[64], roundtrip[64];
    uint64_t start, elapsed_ms;
    size_t i;
    int failures = 0;

    for (i = 0; i < sizeof(plaintext); i++)
        plaintext[i] = (uint8_t)(i * 7u + 3u);

    /* 1. The KDF is deterministic: same inputs, same context, twice. */
    if (!halyard_v1_constants_bundled) {
        rc_log("FAIL  interop constants not compiled in\n");
        return 1;
    }
    rc_log("constants: bundled%s\n\n", halyard_v1_has_ps4_tables ? ", with PS4 tables" : ", PS5 only");

    /* Codec selector 2 and the PS5 version selector are what halyard_control_session.c passes for a
     * real session; using anything else here would measure a code path no console ever drives. */
    if (halyard_control_field_init(&ctx, nonce, companion, 2, HALYARD_VERSION_SELECTOR_PS5) != 0) {
        rc_log("FAIL  field init refused\n");
        return 1;
    }
    halyard_control_field_init(&ctx2, nonce, companion, 2, HALYARD_VERSION_SELECTOR_PS5);
    if (memcmp(&ctx, &ctx2, sizeof(ctx)) != 0) {
        rc_log("FAIL  KDF is not deterministic\n");
        failures++;
    } else {
        rc_log("ok    KDF deterministic\n");
    }

    /* 2. Encrypt/decrypt round-trips at lengths either side of the AES block boundary, which is where
     *    CFB's partial-final-block rule bites. */
    {
        static const size_t lengths[] = { 1, 15, 16, 17, 31, 32, 33, 64 };
        size_t n;
        int all_ok = 1;
        for (n = 0; n < sizeof(lengths) / sizeof(lengths[0]); n++) {
            size_t length = lengths[n];
            halyard_control_field_encrypt(&ctx, (uint64_t)n, plaintext, ciphertext, length);
            halyard_control_field_decrypt(&ctx, (uint64_t)n, ciphertext, roundtrip, length);
            if (memcmp(plaintext, roundtrip, length) != 0) {
                rc_log("FAIL  round trip at length %u\n", (unsigned)length);
                all_ok = 0;
                failures++;
            }
            if (length >= 16 && memcmp(plaintext, ciphertext, length) == 0) {
                rc_log("FAIL  ciphertext equals plaintext at length %u\n", (unsigned)length);
                all_ok = 0;
                failures++;
            }
        }
        if (all_ok)
            rc_log("ok    round trip at 8 lengths, 1..64\n");
    }

    /* 3. The counter actually participates: the same plaintext under two counters must differ. */
    halyard_control_field_encrypt(&ctx, 0, plaintext, ciphertext, sizeof(plaintext));
    halyard_control_field_encrypt(&ctx, 1, plaintext, roundtrip, sizeof(plaintext));
    if (memcmp(ciphertext, roundtrip, sizeof(ciphertext)) == 0) {
        rc_log("FAIL  counter does not affect the keystream\n");
        failures++;
    } else {
        rc_log("ok    counter affects the keystream\n");
    }

    /* 4. The measurement this program exists for. */
    start = rc_time_ms();
    for (i = 0; i < ITERATIONS; i++)
        halyard_control_field_encrypt(&ctx, (uint64_t)i, plaintext, ciphertext, sizeof(plaintext));
    elapsed_ms = rc_time_ms() - start;

    rc_log("\n%d field encryptions in %llu ms\n", ITERATIONS, (unsigned long long)elapsed_ms);
    if (elapsed_ms > 0)
        rc_log("  ~%llu us each\n", (unsigned long long)(elapsed_ms * 1000u / ITERATIONS));
    else
        rc_log("  under 1 ms total - too fast for this clock to resolve\n");
    rc_log("  (3DS/ARM11 @ 804 MHz measured ~25 us each)\n");

    return failures;
}

int main(void)
{
    int failures;

    /* No argv[0] on this platform, so rc_program_dir falls back to RC_PROGRAM_DIR_FALLBACK, which the
     * Makefile defines as "ux0:data/ripcord/". That directory must exist - the Vita will not create it. */
    rc_log_open(NULL, "smoke-test.log");

    rc_log("ripcord-vita smoke test\n");
    rc_log("=======================\n\n");

    failures = run_checks();

    rc_log("\n%s\n", failures == 0 ? "all checks passed" : "CHECKS FAILED");
    rc_log_close();

    sceKernelExitProcess(0);
    return 0;
}
