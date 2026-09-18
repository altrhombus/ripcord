/*
 * ripcord-ps3 - self-test for the CSPRNG seam's platform-independent half.
 *
 * THIS BUILDS FOR THE HOST, NOT THE PS3. `sysGetRandomNumber` is defined right here, in this file, and
 * tests/stub-sdk/lv2/system.h stands in for the SDK header - so rc_random_ps3.c compiles unmodified and
 * every path through it can be driven on demand.
 *
 * WHAT THIS CAN AND CANNOT ESTABLISH, because the distinction is the entire reason the file is worth
 * having and is easy to overstate.
 *
 * It CANNOT tell you that the PS3's generator works. The two things rc_random_ps3.c marks [X] - whether
 * zero means success, and whether an unaligned destination is accepted - are facts about lv2, and a fake
 * that answers them however this file says so proves nothing at all about a console. Those are settled by
 * the bring-up program on hardware, and nowhere else.
 *
 * It CAN establish everything around them, and that half is the part with the bugs in it: the chunking
 * loop the 4096-byte cap forces, the refusal to hand back a partially-filled buffer, the gate that stops
 * anything being drawn before the probe has passed, and the inversion in rc_random_rng_callback. Every
 * one of those is ordinary C with an invisible failure mode - a short fill or a stale tail looks exactly
 * like a good key - so they are precisely what should not first be exercised on a console.
 *
 * The probe's unaligned draw is checked in the one way a host can check it: by recording the address the
 * fake was handed and asserting it was odd. That tests that rc_random_init still ASKS the awkward
 * question, which is the property that would quietly rot if someone tidied the buffer arithmetic.
 */
#include "../../common/util/rc_random.h"

#include <lv2/system.h>

#include <stdio.h>
#include <string.h>

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

/* ---------------------------------------------------------------------------------------------------
 * The fake generator. Every knob here exists to reproduce one failure the console could produce and
 * that a host otherwise could not ask for.
 * ------------------------------------------------------------------------------------------------ */

static u32 g_result;             /* what the call reports; 0 is success */
static int g_write_bytes;        /* whether it writes anything at all */
static int g_fail_after;         /* succeed this many calls, then fail; -1 to never fail */
static unsigned g_call_count;
static u64 g_last_size;
static u64 g_largest_size;
static const void *g_last_addr;
static int g_saw_odd_addr;
/*
 * A running byte that NEVER TAKES THE VALUE ZERO, which is load-bearing rather than decorative: the
 * chunking test proves the whole buffer was filled by looking for a remaining zero, and a plain
 * uint8_t counter wraps through 0 every 256 bytes. The first version of this file did exactly that and
 * the oversized-draw test failed against a correct implementation - a fake that is wrong in a way that
 * accuses the code is the worst kind, so this is written out rather than left as an increment.
 */
static uint8_t g_counter;

static void fake_reset(void)
{
    g_result = 0u;
    g_write_bytes = 1;
    g_fail_after = -1;
    g_call_count = 0u;
    g_last_size = 0u;
    g_largest_size = 0u;
    g_last_addr = NULL;
    g_saw_odd_addr = 0;
    g_counter = 0u;
}

u32 sysGetRandomNumber(void *addr, u64 size)
{
    uint8_t *out = (uint8_t *)addr;
    u64 i;

    g_call_count++;
    g_last_size = size;
    g_last_addr = addr;
    if (size > g_largest_size)
        g_largest_size = size;
    if (((uintptr_t)addr & 1u) != 0u)
        g_saw_odd_addr = 1;

    if (g_fail_after >= 0 && g_call_count > (unsigned)g_fail_after)
        return 0x80010002u;

    if (g_write_bytes) {
        for (i = 0u; i < size; i++) {
            g_counter = (uint8_t)(g_counter + 1u);
            if (g_counter == 0u)
                g_counter = 1u;
            out[i] = g_counter;
        }
    }
    return g_result;
}

/* ---------------------------------------------------------------------------------------------------
 * The probe
 * ------------------------------------------------------------------------------------------------ */

static void test_init_probe(void)
{
    fake_reset();
    rc_random_exit();

    CHECK(rc_random_init() == 1, "init should succeed when the generator works");
    CHECK(g_call_count == 1u, "init should draw exactly once, drew %u times", g_call_count);
    CHECK(g_saw_odd_addr == 1,
          "init must ask for bytes at an odd address - that is how alignment gets tested on hardware");

    /* Idempotent: a second init must not re-probe. Two calls in a connect flow is a plausible mistake
     * and re-probing would be harmless but wasteful; the contract says "call once at startup". */
    g_call_count = 0u;
    CHECK(rc_random_init() == 1, "second init should succeed");
    CHECK(g_call_count == 0u, "second init should not draw again, drew %u times", g_call_count);

    rc_random_exit();
}

static void test_init_rejects_a_call_that_fails(void)
{
    fake_reset();
    rc_random_exit();
    g_fail_after = 0;

    CHECK(rc_random_init() == 0, "init must fail when the generator reports an error");
    rc_random_exit();
}

static void test_init_rejects_a_call_that_writes_nothing(void)
{
    uint8_t key[16];

    fake_reset();
    rc_random_exit();

    /* The failure the probe exists for: success reported, buffer untouched. That is what an
     * unimplemented syscall looks like, and the resulting key is all zeros. */
    g_write_bytes = 0;

    CHECK(rc_random_init() == 0, "init must fail when a 'successful' call writes nothing");

    /* And the gate must hold afterwards, because this is the case where everything still looks fine. */
    memset(key, 0xAA, sizeof(key));
    CHECK(rc_random_bytes(key, sizeof(key)) == 0, "bytes must refuse after a failed init");
    CHECK(key[0] == 0u && key[15] == 0u, "a refused draw must leave the buffer zeroed, not stale");

    rc_random_exit();
}

static void test_gate_before_init(void)
{
    uint8_t key[16];

    fake_reset();
    rc_random_exit();

    memset(key, 0xAA, sizeof(key));
    CHECK(rc_random_bytes(key, sizeof(key)) == 0, "bytes must refuse before init");
    CHECK(g_call_count == 0u, "a refused draw must not reach the generator");
    CHECK(key[0] == 0u, "a refused draw must zero the buffer");
}

/* ---------------------------------------------------------------------------------------------------
 * Drawing
 * ------------------------------------------------------------------------------------------------ */

static void test_small_draw(void)
{
    uint8_t key[16];

    fake_reset();
    rc_random_exit();
    CHECK(rc_random_init() == 1, "init");

    g_call_count = 0u;
    CHECK(rc_random_bytes(key, sizeof(key)) == 1, "a 16-byte draw should succeed");
    CHECK(g_call_count == 1u, "16 bytes is one call, was %u", g_call_count);
    CHECK(g_last_size == 16u, "the whole request should be passed through, saw %llu",
          (unsigned long long)g_last_size);

    rc_random_exit();
}

static void test_zero_length_is_success(void)
{
    uint8_t key[1];

    fake_reset();
    rc_random_exit();
    CHECK(rc_random_init() == 1, "init");

    g_call_count = 0u;
    /* Nothing was asked for, so nothing failed. The generator must not be troubled for it either - the
     * SDK caps the size from above and says nothing about zero. */
    CHECK(rc_random_bytes(key, 0u) == 1, "a zero-length draw is vacuously successful");
    CHECK(g_call_count == 0u, "a zero-length draw should not call the generator");

    rc_random_exit();
}

static void test_null_is_refused(void)
{
    fake_reset();
    rc_random_exit();
    CHECK(rc_random_init() == 1, "init");

    CHECK(rc_random_bytes(NULL, 16u) == 0, "a NULL destination must be refused, not dereferenced");

    rc_random_exit();
}

static void test_chunking_past_the_cap(void)
{
    static uint8_t big[RANDOM_NUMBER_MAX_SIZE * 2 + 7];
    size_t i;
    int all_written = 1;

    fake_reset();
    rc_random_exit();
    CHECK(rc_random_init() == 1, "init");

    memset(big, 0, sizeof(big));
    g_call_count = 0u;
    g_largest_size = 0u;

    CHECK(rc_random_bytes(big, sizeof(big)) == 1, "an oversized draw should succeed by chunking");

    /* 4096 + 4096 + 7. The exact count matters less than the two properties around it: no single call
     * exceeded the cap, and the whole buffer was filled. */
    CHECK(g_call_count == 3u, "expected 3 chunks for %zu bytes, saw %u calls", sizeof(big), g_call_count);
    CHECK(g_largest_size <= (u64)RANDOM_NUMBER_MAX_SIZE,
          "no chunk may exceed the cap, largest was %llu", (unsigned long long)g_largest_size);
    CHECK(g_last_size == 7u, "the final chunk should be the 7-byte remainder, was %llu",
          (unsigned long long)g_last_size);

    /* The fake writes a running counter that never emits 0 (see g_counter), so a zero anywhere is a byte
     * the loop skipped - which is exactly the short-fill bug this test exists for. */
    for (i = 0u; i < sizeof(big); i++) {
        if (big[i] == 0u) {
            all_written = 0;
            break;
        }
    }
    CHECK(all_written == 1, "every byte of an oversized draw must be written");

    rc_random_exit();
}

static void test_exactly_the_cap(void)
{
    static uint8_t big[RANDOM_NUMBER_MAX_SIZE];

    fake_reset();
    rc_random_exit();
    CHECK(rc_random_init() == 1, "init");

    g_call_count = 0u;
    CHECK(rc_random_bytes(big, sizeof(big)) == 1, "a draw of exactly the cap should succeed");
    CHECK(g_call_count == 1u, "the cap is inclusive - expected 1 call, saw %u", g_call_count);

    rc_random_exit();
}

static void test_partial_failure_discards_everything(void)
{
    static uint8_t big[RANDOM_NUMBER_MAX_SIZE * 2];
    size_t i;
    int any_nonzero = 0;

    fake_reset();
    rc_random_exit();
    CHECK(rc_random_init() == 1, "init");

    /* Succeed once, then fail: the first 4096 bytes are good entropy and the rest is not. This is the
     * dangerous shape - a buffer that looks convincing in a hexdump and is half stale. */
    g_fail_after = 1;
    g_call_count = 0u;
    memset(big, 0xAA, sizeof(big));

    CHECK(rc_random_bytes(big, sizeof(big)) == 0, "a partial fill must be reported as failure");

    for (i = 0u; i < sizeof(big); i++) {
        if (big[i] != 0u) {
            any_nonzero = 1;
            break;
        }
    }
    CHECK(any_nonzero == 0, "a partial fill must be discarded entirely, not left half-good");

    rc_random_exit();
}

/* ---------------------------------------------------------------------------------------------------
 * The mbedtls adaptor, where the two conventions meet
 * ------------------------------------------------------------------------------------------------ */

static void test_rng_callback_inverts(void)
{
    uint8_t key[16];

    fake_reset();
    rc_random_exit();
    CHECK(rc_random_init() == 1, "init");

    /* 0 on success - mbedtls's f_rng convention, the inverse of everything above. An inversion here is
     * the specific mistake that makes every entropy failure look like a success. */
    CHECK(rc_random_rng_callback(NULL, key, sizeof(key)) == 0,
          "the callback must return 0 for success");

    g_fail_after = 0;
    g_call_count = 0u;
    CHECK(rc_random_rng_callback(NULL, key, sizeof(key)) != 0,
          "the callback must return non-zero for failure");

    rc_random_exit();
}

int main(void)
{
    test_init_probe();
    test_init_rejects_a_call_that_fails();
    test_init_rejects_a_call_that_writes_nothing();
    test_gate_before_init();
    test_small_draw();
    test_zero_length_is_success();
    test_null_is_refused();
    test_chunking_past_the_cap();
    test_exactly_the_cap();
    test_partial_failure_discards_everything();
    test_rng_callback_inverts();

    printf("\n%d passed, %d failed\n", g_passed, g_failed);
    return g_failed == 0 ? 0 : 1;
}
