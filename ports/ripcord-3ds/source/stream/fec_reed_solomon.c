#include "fec_reed_solomon.h"
#include "fec_galois.h"

#include <string.h>

#define MAX_MATRIX (FEC_MAX_TOTAL_UNITS * FEC_MAX_TOTAL_UNITS)

int fec_reed_solomon_build_matrix(int k, int m, uint8_t *out)
{
    int i, j;

    if (k <= 0 || m <= 0 || k + m > FEC_MAX_TOTAL_UNITS)
        return 0;

    fec_galois_init();
    for (i = 0; i < m; i++) {
        for (j = 0; j < k; j++) {
            uint8_t inv;
            /* i < m <= m+j always, so i ^ (m+j) is never zero - the inverse always exists. See the
             * header's provenance comment for why this is the well-definedness argument, not a guess. */
            if (!fec_galois_inverse((uint8_t)(i ^ (m + j)), &inv))
                return 0;
            out[i * k + j] = inv;
        }
    }
    return 1;
}

int fec_reed_solomon_encode(uint8_t *frame_buf, size_t unit_size, size_t stride, int k, int m)
{
    uint8_t matrix[MAX_MATRIX];
    int i, j;

    if (!fec_reed_solomon_build_matrix(k, m, matrix))
        return 0;

    for (i = 0; i < m; i++) {
        size_t parity_offset = (size_t)(k + i) * stride;
        size_t t;

        memset(frame_buf + parity_offset, 0, unit_size);
        for (j = 0; j < k; j++) {
            uint8_t coeff = matrix[i * k + j];
            size_t src_offset;

            if (coeff == 0)
                continue;
            src_offset = (size_t)j * stride;
            for (t = 0; t < unit_size; t++) {
                frame_buf[parity_offset + t] = (uint8_t)(frame_buf[parity_offset + t] ^
                    fec_galois_multiply(coeff, frame_buf[src_offset + t]));
            }
        }
    }
    return 1;
}

static void swap_rows(uint8_t *matrix, int n, int r0, int r1)
{
    int j;
    for (j = 0; j < n; j++) {
        uint8_t tmp = matrix[r0 * n + j];
        matrix[r0 * n + j] = matrix[r1 * n + j];
        matrix[r1 * n + j] = tmp;
    }
}

/* Gauss-Jordan inverse of an n*n GF(2^8) matrix (row-major, in `work`, destroyed) into `inv` (must start
 * as the identity - callers below rely on this). Returns 0 if singular. */
static int invert(uint8_t *work, uint8_t *inv, int n)
{
    int col;

    for (col = 0; col < n; col++) {
        int pivot = -1;
        int r, j;
        uint8_t inv_pivot;

        for (r = col; r < n; r++) {
            if (work[r * n + col] != 0) {
                pivot = r;
                break;
            }
        }
        if (pivot < 0)
            return 0;

        if (pivot != col) {
            swap_rows(work, n, pivot, col);
            swap_rows(inv, n, pivot, col);
        }

        if (!fec_galois_inverse(work[col * n + col], &inv_pivot))
            return 0;
        for (j = 0; j < n; j++) {
            work[col * n + j] = fec_galois_multiply(work[col * n + j], inv_pivot);
            inv[col * n + j] = fec_galois_multiply(inv[col * n + j], inv_pivot);
        }

        for (r = 0; r < n; r++) {
            uint8_t factor;

            if (r == col)
                continue;
            factor = work[r * n + col];
            if (factor == 0)
                continue;
            for (j = 0; j < n; j++) {
                work[r * n + j] = (uint8_t)(work[r * n + j] ^ fec_galois_multiply(factor, work[col * n + j]));
                inv[r * n + j] = (uint8_t)(inv[r * n + j] ^ fec_galois_multiply(factor, inv[col * n + j]));
            }
        }
    }
    return 1;
}

int fec_reed_solomon_decode(uint8_t *frame_buf, size_t unit_size, size_t stride, int k, int m,
    const uint8_t *present)
{
    int total = k + m;
    int chosen[FEC_MAX_TOTAL_UNITS];
    int chosen_count = 0;
    int any_erased_source = 0;
    /*
     * static, NOT locals. Three 64x64 matrices are 12.6 KB, and a .3dsx main thread has a 32 KB stack in
     * total - so as locals this one function claimed 40% of it, several frames deep inside the demux
     * flush path. That is not a theoretical concern on this target: the Takion probe's 49.6 KB local
     * data-aborted on real hardware for the same reason (see takion_reliable_channel.h), and the 3DS
     * build now enforces -Wframe-larger-than=8192, which is what flagged this.
     *
     * THE TRADE: this makes fec_reed_solomon_decode non-reentrant and single-threaded-only. That is true
     * of nothing else in source/stream, and it is safe today only because this port has no threads at
     * all (no threadCreate anywhere). If the media pipeline ever decodes on its own thread, these must
     * become a caller-supplied scratch struct rather than quietly racing.
     */
    static uint8_t matrix[MAX_MATRIX];
    static uint8_t a[MAX_MATRIX];
    static uint8_t inv[MAX_MATRIX];
    int u, e, r, j;

    if (k <= 0 || m <= 0 || total > FEC_MAX_TOTAL_UNITS)
        return 0;

    for (u = 0; u < total && chosen_count < k; u++) {
        if (present[u])
            chosen[chosen_count++] = u;
    }
    if (chosen_count < k)
        return 0; /* too many erasures */

    for (e = 0; e < k; e++) {
        if (!present[e]) {
            any_erased_source = 1;
            break;
        }
    }
    if (!any_erased_source)
        return 1; /* every source unit survived; nothing to reconstruct */

    if (!fec_reed_solomon_build_matrix(k, m, matrix))
        return 0;

    /* Build A (k*k): row r is the generator row of chosen unit r - identity row for a surviving source
     * unit, the coding-matrix row for a surviving parity unit. A . data = chosenValues. */
    memset(a, 0, (size_t)k * (size_t)k);
    memset(inv, 0, (size_t)k * (size_t)k);
    for (r = 0; r < k; r++) {
        u = chosen[r];
        inv[r * k + r] = 1;
        if (u < k) {
            a[r * k + u] = 1;
        } else {
            int coding_row = u - k;
            for (j = 0; j < k; j++)
                a[r * k + j] = matrix[coding_row * k + j];
        }
    }

    if (!invert(a, inv, k))
        return 0;

    /* For each erased source unit e: data[e] = sum_r inv[e][r] . chosenUnit[r] (per byte). */
    for (e = 0; e < k; e++) {
        size_t dest_offset;
        size_t t;

        if (present[e])
            continue;

        dest_offset = (size_t)e * stride;
        memset(frame_buf + dest_offset, 0, unit_size);
        for (r = 0; r < k; r++) {
            uint8_t coeff = inv[e * k + r];
            size_t src_offset;

            if (coeff == 0)
                continue;
            src_offset = (size_t)chosen[r] * stride;
            for (t = 0; t < unit_size; t++) {
                frame_buf[dest_offset + t] = (uint8_t)(frame_buf[dest_offset + t] ^
                    fec_galois_multiply(coeff, frame_buf[src_offset + t]));
            }
        }
    }

    return 1;
}
