/*
 * libripcord - video FEC self-test: GF(2^8) field laws + systematic Cauchy Reed-Solomon round-trips.
 *
 * Ground truth: GaloisField256_MatchesConsoleInverseTable below is the vendor client's own GF(2^8)
 * division table, dumped from its live field object 2026-08-02 (breakpoint at 0x101036db in the
 * coding-matrix builder FUN_101035e0). Row a=1 of that table is 1/b, the inverse table transcribed here
 * as head/tail bytes - see FecTests.cs (the .NET reference's own copy of this same vector) and spec
 * sec6.2 for the RVAs and the "unique across all 16 primitive degree-8 polynomials" argument for why this
 * is a meaningful check rather than a tautology.
 *
 * Everything else here is self-consistency, exactly as the .NET reference's own FecTests.cs documents for
 * its equivalent cases: it proves the core's decoder inverts the core's encoder, over the field and
 * matrix form that are independently pinned to the console (field by the vector above, matrix form [C] by
 * decompilation - see fec_reed_solomon.h). No capture in the captures folder pairs A/V traffic with usable
 * stream keys, so there is no console-produced parity unit available to compare against directly.
 *
 * THIS BUILDS FOR THE HOST, NOT A CONSOLE - see tests/Makefile.
 */
#include "../stream/fec_galois.h"
#include "../stream/fec_reed_solomon.h"

#include <stdio.h>
#include <stdlib.h>
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

static void test_galois_inverse_and_divide_consistency(void)
{
    int a, b;

    for (a = 1; a < 256; a++) {
        uint8_t inv, prod, quot;

        CHECK(fec_galois_inverse((uint8_t)a, &inv), "inverse(%d) failed", a);
        CHECK(fec_galois_multiply((uint8_t)a, inv) == 1, "a=%d: a*inverse(a) != 1", a);
        CHECK(fec_galois_divide(1, (uint8_t)a, &quot) && quot == inv, "a=%d: 1/a != inverse(a)", a);

        for (b = 1; b < 256; b += 37) {
            prod = fec_galois_multiply((uint8_t)a, (uint8_t)b);
            CHECK(fec_galois_divide(prod, (uint8_t)b, &quot) && quot == (uint8_t)a,
                "a=%d b=%d: (a*b)/b != a", a, b);
        }
    }
}

static void test_galois_multiply_zero(void)
{
    CHECK(fec_galois_multiply(0, 123) == 0, "0*123 != 0");
    CHECK(fec_galois_multiply(123, 0) == 0, "123*0 != 0");
    CHECK(fec_galois_multiply(1, 123) == 123, "1*123 != 123");
}

static void test_galois_matches_console_inverse_table(void)
{
    static const uint8_t head[] = {
        0x01, 0x8e, 0xf4, 0x47, 0xa7, 0x7a, 0xba, 0xad, 0x9d, 0xdd, 0x98, 0x3d, 0xaa, 0x5d, 0x96
    };
    static const uint8_t tail[] = { 0x42, 0xd4, 0xe8, 0x75, 0x7f, 0xff, 0x7e, 0xfd };
    size_t i;
    uint8_t inv;

    for (i = 0; i < sizeof(head); i++) {
        CHECK(fec_galois_inverse((uint8_t)(i + 1), &inv) && inv == head[i],
            "inverse(%d) != console head byte 0x%02x", (int)(i + 1), head[i]);
    }
    for (i = 0; i < sizeof(tail); i++) {
        CHECK(fec_galois_inverse((uint8_t)(248 + i), &inv) && inv == tail[i],
            "inverse(%d) != console tail byte 0x%02x", (int)(248 + i), tail[i]);
    }

    /* Division by zero is undefined in this field - the console stores a 0xff sentinel where this core
     * reports failure instead; nothing in fec_reed_solomon.c ever indexes it (see that header). */
    CHECK(!fec_galois_inverse(0, &inv), "inverse(0) should fail, not silently succeed");
}

static void test_cauchy_matrix_matches_reference_construction(void)
{
    const int k = 4, m = 2;
    uint8_t matrix[FEC_MAX_TOTAL_UNITS * FEC_MAX_TOTAL_UNITS];
    int i, j;

    CHECK(fec_reed_solomon_build_matrix(k, m, matrix), "build_matrix failed");
    for (i = 0; i < m; i++) {
        for (j = 0; j < k; j++) {
            uint8_t expected;
            CHECK(fec_galois_inverse((uint8_t)(i ^ (m + j)), &expected), "reference inverse failed");
            CHECK(matrix[i * k + j] == expected, "matrix[%d][%d] mismatch", i, j);
        }
    }
}

/* Fills `count` bytes at `out` with a deterministic pseudo-random sequence - reproducible across runs
 * without depending on libc's rand() implementation details, matching the spirit (not the exact PRNG) of
 * the .NET reference's RandomNumberGenerator.Fill / `new Random(seed)` fixtures. */
static void fill_pseudo_random(uint8_t *out, size_t count, uint32_t seed)
{
    size_t i;
    uint32_t state = seed | 1u;

    for (i = 0; i < count; i++) {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        out[i] = (uint8_t)state;
    }
}


/* Lose the listed units (zero them, clear their flags); the rest are marked arrived. */
static void lose(uint8_t *frame, uint8_t *arrived, int units, size_t coded_length, const int *lost, int lost_count)
{
    int u;

    for (u = 0; u < units; u++)
        arrived[u] = 1;
    for (u = 0; u < lost_count; u++) {
        memset(frame + (size_t)lost[u] * coded_length, 0, coded_length);
        arrived[lost[u]] = 0;
    }
}

static void run_loss_case(const char *label, int k, int m, size_t coded_length, const int *lost, int lost_count,
    uint32_t seed)
{
    int total = k + m;
    static uint8_t frame[FEC_MAX_TOTAL_UNITS * 1024];
    static uint8_t pristine[FEC_MAX_TOTAL_UNITS * 1024];
    uint8_t arrived[FEC_MAX_TOTAL_UNITS];
    int u;

    if ((size_t)total * coded_length > sizeof(frame)) {
        g_failed++;
        printf("FAIL %s: fixture too large for test buffer\n", label);
        return;
    }

    memset(frame, 0, (size_t)total * coded_length);
    for (u = 0; u < k; u++)
        fill_pseudo_random(frame + (size_t)u * coded_length, coded_length, seed + (uint32_t)u * 2654435761u);

    CHECK(fec_reed_solomon_encode(frame, coded_length, k, m), "%s: encode failed", label);
    memcpy(pristine, frame, (size_t)total * coded_length);

    lose(frame, arrived, total, coded_length, lost, lost_count);

    CHECK(fec_reed_solomon_recover_source(frame, coded_length, k, m, arrived), "%s: recovery failed", label);
    for (u = 0; u < k; u++) {
        CHECK(memcmp(frame + (size_t)u * coded_length, pristine + (size_t)u * coded_length, coded_length) == 0,
            "%s: source unit %d mismatch after recovery", label, u);
    }
}

static void test_reed_solomon_recovers_missing_source_units(void)
{
    static const int lost_a[] = { 0 };
    static const int lost_b[] = { 0, 3 };
    static const int lost_c[] = { 1, 4 };
    static const int lost_d[] = { 0, 2, 5 };
    static const int lost_e[] = { 0, 1, 2, 3 };
    static const int lost_f[] = { 2 };

    run_loss_case("k4m2 one lost", 4, 2, 200, lost_a, 1, 1001);
    run_loss_case("k4m2 two lost", 4, 2, 200, lost_b, 2, 1002);
    run_loss_case("k6m3 mixed", 6, 3, 200, lost_c, 2, 1003);
    run_loss_case("k6m3 three lost", 6, 3, 200, lost_d, 3, 1004);
    run_loss_case("k8m4 first four lost", 8, 4, 200, lost_e, 4, 1005);
    run_loss_case("k3m1 one lost", 3, 1, 200, lost_f, 1, 1006);
}

static void test_reed_solomon_losing_a_parity_unit_still_recovers_source(void)
{
    /* Source unit 1 and parity unit 0 lost: parity unit 1 is enough for one missing source unit. */
    static const int lost[] = { 1, 4 };
    run_loss_case("k4m2 source and parity lost", 4, 2, 64, lost, 2, 2001);
}

/* A real cap47 coded length: a multiple of 4, not of 16 (spec sec6.2). */
static void test_reed_solomon_recovers_at_a_real_coded_length(void)
{
    static const int lost[] = { 0, 5 };
    run_loss_case("k6m3 coded length 1004", 6, 3, 1004, lost, 2, 3001);
    run_loss_case("k6m3 coded length 1012", 6, 3, 1012, lost, 2, 3002);
}

/* Every loss pattern of a small frame: recovered exactly when enough parity arrived. Any square submatrix
 * of a Cauchy matrix is invertible, so no pattern with enough parity may fail. */
static void test_reed_solomon_recovers_every_loss_pattern_that_leaves_enough_parity(void)
{
    enum { K = 5, M = 3, TOTAL = K + M, LEN = 12 };
    uint8_t original[TOTAL * LEN];
    uint8_t frame[TOTAL * LEN];
    uint8_t arrived[TOTAL];
    int mask, u, recovered = 0;

    memset(original, 0, sizeof(original));
    fill_pseudo_random(original, (size_t)K * LEN, 99u);
    CHECK(fec_reed_solomon_encode(original, LEN, K, M), "encode failed");

    for (mask = 0; mask < (1 << TOTAL); mask++) {
        int lost[TOTAL];
        int lost_count = 0, lost_source = 0, lost_parity = 0, enough, ok;

        for (u = 0; u < TOTAL; u++) {
            if (mask & (1 << u)) {
                lost[lost_count++] = u;
                if (u < K) lost_source++; else lost_parity++;
            }
        }

        memcpy(frame, original, sizeof(frame));
        lose(frame, arrived, TOTAL, LEN, lost, lost_count);

        enough = lost_source <= M - lost_parity;
        ok = fec_reed_solomon_recover_source(frame, LEN, K, M, arrived);
        CHECK(ok == enough, "mask 0x%02x: recovery returned %d, expected %d", mask, ok, enough);
        if (ok && enough) {
            CHECK(memcmp(frame, original, (size_t)K * LEN) == 0, "mask 0x%02x: source mismatch", mask);
            recovered++;
        }
    }

    CHECK(recovered > 0, "no pattern recovered");
}

static void test_reed_solomon_too_many_losses_fails(void)
{
    enum { K = 4, M = 2, LEN = 32 };
    static const int lost[] = { 0, 1, 2 };
    uint8_t frame[(K + M) * LEN];
    uint8_t arrived[K + M];

    memset(frame, 0, sizeof(frame));
    fill_pseudo_random(frame, (size_t)K * LEN, 4001u);
    CHECK(fec_reed_solomon_encode(frame, LEN, K, M), "encode failed");

    lose(frame, arrived, K + M, LEN, lost, 3); /* three lost, only two parity units */
    CHECK(!fec_reed_solomon_recover_source(frame, LEN, K, M, arrived),
        "recovery should fail with more losses than parity units");
}

static void test_reed_solomon_no_parity(void)
{
    enum { K = 3, LEN = 16 };
    uint8_t frame[K * LEN];
    uint8_t all[K] = { 1, 1, 1 };
    uint8_t one_lost[K] = { 1, 0, 1 };

    fill_pseudo_random(frame, sizeof(frame), 5u);
    CHECK(fec_reed_solomon_encode(frame, LEN, K, 0), "m = 0 encode should succeed");
    CHECK(fec_reed_solomon_recover_source(frame, LEN, K, 0, all), "m = 0 complete frame should succeed");
    CHECK(!fec_reed_solomon_recover_source(frame, LEN, K, 0, one_lost), "m = 0 lost unit cannot be recovered");
}

int main(void)
{
    fec_galois_init();

    test_galois_inverse_and_divide_consistency();
    test_galois_multiply_zero();
    test_galois_matches_console_inverse_table();
    test_cauchy_matrix_matches_reference_construction();
    test_reed_solomon_recovers_missing_source_units();
    test_reed_solomon_losing_a_parity_unit_still_recovers_source();
    test_reed_solomon_recovers_at_a_real_coded_length();
    test_reed_solomon_recovers_every_loss_pattern_that_leaves_enough_parity();
    test_reed_solomon_too_many_losses_fails();
    test_reed_solomon_no_parity();

    printf("\n%d passed, %d failed\n", g_passed, g_failed);
    return g_failed == 0 ? 0 : 1;
}