/*
 * ripcord-3ds - video FEC self-test: GF(2^8) field laws + systematic Cauchy Reed-Solomon round-trips.
 *
 * Ground truth: GaloisField256_MatchesConsoleInverseTable below is the vendor client's own GF(2^8)
 * division table, dumped from its live field object 2026-08-02 (breakpoint at 0x101036db in the
 * coding-matrix builder FUN_101035e0). Row a=1 of that table is 1/b, the inverse table transcribed here
 * as head/tail bytes - see FecTests.cs (the .NET reference's own copy of this same vector) and spec
 * sec6.2 for the RVAs and the "unique across all 16 primitive degree-8 polynomials" argument for why this
 * is a meaningful check rather than a tautology.
 *
 * Everything else here is self-consistency, exactly as the .NET reference's own FecTests.cs documents for
 * its equivalent cases: it proves this port's decoder inverts this port's encoder, over the field and
 * matrix form that are independently pinned to the console (field by the vector above, matrix form [C] by
 * decompilation - see fec_reed_solomon.h). No capture in the dirty room pairs A/V traffic with usable
 * stream keys, so there is no console-produced parity unit available to compare against directly.
 *
 * THIS BUILDS FOR THE HOST, NOT THE 3DS - see tests/Makefile.
 */
#include "../source/stream/fec_galois.h"
#include "../source/stream/fec_reed_solomon.h"

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

    /* Division by zero is undefined in this field - the console stores a 0xff sentinel where this port
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

static void run_erasure_case(const char *label, int k, int m, size_t unit_size, size_t stride,
    const int *erasures, int erasure_count, uint32_t seed)
{
    int total = k + m;
    uint8_t frame[FEC_MAX_TOTAL_UNITS * 256];
    uint8_t pristine[FEC_MAX_TOTAL_UNITS * 256];
    uint8_t present[FEC_MAX_TOTAL_UNITS];
    int u, e;

    if ((size_t)total * stride > sizeof(frame)) {
        g_failed++;
        printf("FAIL %s: fixture too large for test buffer\n", label);
        return;
    }

    memset(frame, 0, (size_t)total * stride);
    for (u = 0; u < k; u++)
        fill_pseudo_random(frame + (size_t)u * stride, unit_size, seed + (uint32_t)u * 2654435761u);

    CHECK(fec_reed_solomon_encode(frame, unit_size, stride, k, m), "%s: encode failed", label);
    memcpy(pristine, frame, (size_t)total * stride);

    for (u = 0; u < total; u++)
        present[u] = 1;
    for (e = 0; e < erasure_count; e++) {
        memset(frame + (size_t)erasures[e] * stride, 0, stride);
        present[erasures[e]] = 0;
    }

    CHECK(fec_reed_solomon_decode(frame, unit_size, stride, k, m, present), "%s: decode failed", label);
    for (u = 0; u < k; u++) {
        CHECK(memcmp(frame + (size_t)u * stride, pristine + (size_t)u * stride, unit_size) == 0,
            "%s: source unit %d mismatch after recovery", label, u);
    }
}

static void test_reed_solomon_reconstructs_erased_source_units(void)
{
    static const int erasures_a[] = { 0 };
    static const int erasures_b[] = { 0, 3 };
    static const int erasures_c[] = { 1, 4 };
    static const int erasures_d[] = { 0, 2, 5 };
    static const int erasures_e[] = { 0, 1, 2, 3 };
    static const int erasures_f[] = { 2 };

    run_erasure_case("k4m2 one lost", 4, 2, 200, 208, erasures_a, 1, 1001);
    run_erasure_case("k4m2 two lost", 4, 2, 200, 208, erasures_b, 2, 1002);
    run_erasure_case("k6m3 mixed", 6, 3, 200, 208, erasures_c, 2, 1003);
    run_erasure_case("k6m3 three lost", 6, 3, 200, 208, erasures_d, 3, 1004);
    run_erasure_case("k8m4 first four lost", 8, 4, 200, 208, erasures_e, 4, 1005);
    run_erasure_case("k3m1 one lost", 3, 1, 200, 208, erasures_f, 1, 1006);
}

static void test_reed_solomon_losing_a_parity_unit_still_recovers_source(void)
{
    const int k = 4, m = 2, total = k + m;
    const size_t unit_size = 64, stride = 64;
    uint8_t frame[6 * 64];
    uint8_t pristine[6 * 64];
    uint8_t present[6];
    int u;

    for (u = 0; u < k; u++)
        fill_pseudo_random(frame + (size_t)u * stride, unit_size, 2001u + (uint32_t)u);
    CHECK(fec_reed_solomon_encode(frame, unit_size, stride, k, m), "encode failed");
    memcpy(pristine, frame, sizeof(frame));

    for (u = 0; u < total; u++)
        present[u] = 1;
    memset(frame + (size_t)1 * stride, 0, stride); present[1] = 0;         /* source unit 1 */
    memset(frame + (size_t)k * stride, 0, stride); present[k + 0] = 0; /* parity unit 0 */

    CHECK(fec_reed_solomon_decode(frame, unit_size, stride, k, m, present), "decode failed");
    CHECK(memcmp(frame + 1 * stride, pristine + 1 * stride, unit_size) == 0, "source unit 1 mismatch");
}

static void test_reed_solomon_recovery_is_independent_of_slot_stride(void)
{
    const int k = 4, m = 2, total = k + m;
    const size_t unit_size = 999; /* deliberately not even 4-aligned, to prove the decoder does not care */
    const size_t tight = unit_size;
    const size_t aligned16 = (unit_size + 0xf) & ~(size_t)0xf;
    uint8_t source[4][999];
    uint8_t a[6 * 999];
    uint8_t b[6 * 1008]; /* aligned16 for unit_size=999 is 1008 */
    uint8_t present[6];
    int u;

    for (u = 0; u < k; u++)
        fill_pseudo_random(source[u], unit_size, 3001u + (uint32_t)u);

    memset(a, 0, sizeof(a));
    memset(b, 0, sizeof(b));
    for (u = 0; u < k; u++) {
        memcpy(a + (size_t)u * tight, source[u], unit_size);
        memcpy(b + (size_t)u * aligned16, source[u], unit_size);
    }
    CHECK(fec_reed_solomon_encode(a, unit_size, tight, k, m), "tight-stride encode failed");
    CHECK(fec_reed_solomon_encode(b, unit_size, aligned16, k, m), "aligned-stride encode failed");

    for (u = 0; u < total; u++)
        present[u] = 1;
    present[0] = 0;
    present[k - 1] = 0;
    memset(a, 0, tight);
    memset(a + (size_t)(k - 1) * tight, 0, tight);
    memset(b, 0, aligned16);
    memset(b + (size_t)(k - 1) * aligned16, 0, aligned16);

    CHECK(fec_reed_solomon_decode(a, unit_size, tight, k, m, present), "tight-stride decode failed");
    CHECK(fec_reed_solomon_decode(b, unit_size, aligned16, k, m, present), "aligned-stride decode failed");

    for (u = 0; u < k; u++) {
        CHECK(memcmp(a + (size_t)u * tight, source[u], unit_size) == 0, "tight-stride unit %d mismatch", u);
        CHECK(memcmp(b + (size_t)u * aligned16, source[u], unit_size) == 0, "aligned-stride unit %d mismatch", u);
    }
}

static void test_reed_solomon_too_many_erasures_fails(void)
{
    const int k = 4, m = 2, total = k + m;
    const size_t unit_size = 32, stride = 32;
    uint8_t frame[6 * 32];
    uint8_t present[6];
    int u;

    for (u = 0; u < k; u++)
        fill_pseudo_random(frame + (size_t)u * stride, unit_size, 4001u + (uint32_t)u);
    CHECK(fec_reed_solomon_encode(frame, unit_size, stride, k, m), "encode failed");

    for (u = 0; u < total; u++)
        present[u] = 1;
    present[0] = present[1] = present[2] = 0; /* 3 erasures, only m=2 parity - unrecoverable */

    CHECK(!fec_reed_solomon_decode(frame, unit_size, stride, k, m, present),
        "decode should fail with more erasures than parity units");
}

int main(void)
{
    fec_galois_init();

    test_galois_inverse_and_divide_consistency();
    test_galois_multiply_zero();
    test_galois_matches_console_inverse_table();
    test_cauchy_matrix_matches_reference_construction();
    test_reed_solomon_reconstructs_erased_source_units();
    test_reed_solomon_losing_a_parity_unit_still_recovers_source();
    test_reed_solomon_recovery_is_independent_of_slot_stride();
    test_reed_solomon_too_many_erasures_fails();

    printf("\n%d passed, %d failed\n", g_passed, g_failed);
    return g_failed == 0 ? 0 : 1;
}
