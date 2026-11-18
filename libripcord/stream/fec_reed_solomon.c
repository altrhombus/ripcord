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

/* C[i][j] = 1 / (i XOR (m + j)). i < m <= m + j, so the argument is never zero and the inverse exists. */
static uint8_t coefficient(int parity_row, int source_column, int m)
{
    uint8_t inv = 0;
    (void)fec_galois_inverse((uint8_t)(parity_row ^ (m + source_column)), &inv);
    return inv;
}

/* destination ^= coeff * source, byte by byte. */
static void multiply_add(uint8_t *destination, uint8_t coeff, const uint8_t *source, size_t length)
{
    size_t t;

    if (coeff == 0)
        return;
    for (t = 0; t < length; t++)
        destination[t] = (uint8_t)(destination[t] ^ fec_galois_multiply(coeff, source[t]));
}

int fec_reed_solomon_encode(uint8_t *units, size_t coded_length, int k, int m)
{
    int i, j;

    if (k <= 0 || m < 0 || k + m > FEC_MAX_TOTAL_UNITS)
        return 0;

    fec_galois_init();
    for (i = 0; i < m; i++) {
        uint8_t *parity = units + (size_t)(k + i) * coded_length;

        memset(parity, 0, coded_length);
        for (j = 0; j < k; j++)
            multiply_add(parity, coefficient(i, j, m), units + (size_t)j * coded_length, coded_length);
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

int fec_reed_solomon_recover_source(uint8_t *units, size_t coded_length, int k, int m,
    const uint8_t *arrived)
{
    int missing[FEC_MAX_TOTAL_UNITS];
    int rows[FEC_MAX_TOTAL_UNITS];
    int e = 0, row_count = 0;
    /*
     * static, NOT locals. Two 64x64 matrices are 8 KB, and a .3dsx main thread has a 32 KB stack in total,
     * several frames deep inside the demux flush path. The Takion probe's 49.6 KB local data-aborted on
     * real hardware for the same reason (see takion_reliable_channel.h), and the 3DS build passes
     * -Wframe-larger-than=8192.
     *
     * THE TRADE: this makes recovery non-reentrant. Only one call may run at a time in the whole
     * process, whichever demux it serves; two threads recovering at once race on these matrices. A
     * caller that needs that must serialise its calls.
     */
    static uint8_t cauchy[MAX_MATRIX];
    static uint8_t inverse[MAX_MATRIX];
    uint8_t owed[FEC_MAX_TOTAL_UNITS];
    int i, j, r, c;
    size_t t;

    if (k <= 0 || m < 0 || k + m > FEC_MAX_TOTAL_UNITS)
        return 0;

    for (j = 0; j < k; j++) {
        if (!arrived[j])
            missing[e++] = j;
    }
    if (e == 0)
        return 1; /* every source unit arrived; nothing to rebuild */

    for (i = 0; i < m && row_count < e; i++) {
        if (arrived[k + i])
            rows[row_count++] = i;
    }
    if (row_count < e)
        return 0; /* fewer parity units than missing source units */

    fec_galois_init();

    /* What each chosen parity unit owes to the missing source units alone: the parity unit minus the share
     * of every source unit that arrived. Written into the missing units' own places, which are free. */
    for (r = 0; r < e; r++) {
        uint8_t *dest = units + (size_t)missing[r] * coded_length;

        memcpy(dest, units + (size_t)(k + rows[r]) * coded_length, coded_length);
        for (j = 0; j < k; j++) {
            if (arrived[j])
                multiply_add(dest, coefficient(rows[r], j, m), units + (size_t)j * coded_length, coded_length);
        }
    }

    /* remainder = S . missing_units, S[r][c] = C[rows[r]][missing[c]]. Invert S. */
    memset(inverse, 0, (size_t)e * (size_t)e);
    for (r = 0; r < e; r++) {
        inverse[r * e + r] = 1;
        for (c = 0; c < e; c++)
            cauchy[r * e + c] = coefficient(rows[r], missing[c], m);
    }
    if (!invert(cauchy, inverse, e))
        return 0; /* unreachable for a Cauchy submatrix; kept so a bad table fails closed */

    /* Apply the inverse one byte position at a time, so the remainders can be overwritten in place. */
    for (t = 0; t < coded_length; t++) {
        for (r = 0; r < e; r++)
            owed[r] = units[(size_t)missing[r] * coded_length + t];
        for (c = 0; c < e; c++) {
            uint8_t value = 0;

            for (r = 0; r < e; r++)
                value = (uint8_t)(value ^ fec_galois_multiply(inverse[c * e + r], owed[r]));
            units[(size_t)missing[c] * coded_length + t] = value;
        }
    }

    return 1;
}