//! Video FEC: GF(2^8) arithmetic and systematic Cauchy Reed-Solomon erasure coding (spec sec 6.2).
//! Ported from `libripcord/stream/fec_galois.c` and `fec_reed_solomon.c`.
//!
//! The field's primitive polynomial is 0x11d, confirmed [V] against the vendor binary two independent
//! ways, and pinned here by the console-dumped inverse table in the tests. k source units are followed
//! by m parity units, parity unit i being `sum_j C[i][j] * source_j` with `C[i][j] = 1 / (i XOR (m+j))`,
//! confirmed [C] from the console's own coding-matrix builder.
//!
//! Units are laid out the way the console lays them out: one buffer, unit `u` at `u * coded_length`,
//! every unit exactly `coded_length` bytes. Coding is per byte, so what the bytes mean does not matter.
//!
//! Recovery works on the missing source units only: with e of them missing, take e parity units that
//! arrived, remove the arrived source units' share from each, and solve the e-by-e system those parity
//! rows and the missing columns of C give. Every square submatrix of a Cauchy matrix is invertible, so
//! the system has a solution whenever e parity units arrived.

const PRIMITIVE_POLYNOMIAL: u16 = 0x11d;

/// Bounds k+m for recovery, as in the C core. A frame may occupy more units than this (see the
/// demuxer); it is then assembled from whatever arrived, without recovery.
pub const MAX_TOTAL_UNITS: usize = 64;
const MAX_MATRIX: usize = MAX_TOTAL_UNITS * MAX_TOTAL_UNITS;

struct Tables {
    exp: [u8; 512],
    log: [u8; 256],
}

const fn build_tables() -> Tables {
    let mut t = Tables { exp: [0; 512], log: [0; 256] };
    let mut x: u16 = 1;
    let mut i = 0;
    while i < 255 {
        t.exp[i] = x as u8;
        t.log[x as usize] = i as u8;
        x <<= 1;
        if x & 0x100 != 0 {
            x ^= PRIMITIVE_POLYNOMIAL;
        }
        i += 1;
    }
    // Doubled, so multiply's log sum (< 510) needs no modulo.
    while i < 512 {
        t.exp[i] = t.exp[i - 255];
        i += 1;
    }
    t
}

static TABLES: Tables = build_tables();

pub fn multiply(a: u8, b: u8) -> u8 {
    if a == 0 || b == 0 {
        return 0;
    }
    TABLES.exp[usize::from(TABLES.log[usize::from(a)]) + usize::from(TABLES.log[usize::from(b)])]
}

/// `None` when `b` is zero, which is undefined in the field.
pub fn divide(a: u8, b: u8) -> Option<u8> {
    match (a, b) {
        (_, 0) => None,
        (0, _) => Some(0),
        _ => Some(
            TABLES.exp
                [usize::from(TABLES.log[usize::from(a)]) + 255 - usize::from(TABLES.log[usize::from(b)])],
        ),
    }
}

pub fn inverse(a: u8) -> Option<u8> {
    divide(1, a)
}

/// `dest[t] ^= coeff * src[t]` for every byte.
///
/// One lookup per byte, from a row of products built for this coefficient: 256 multiplies, against a unit
/// of a thousand bytes or more. A 64 KB compile-time table of every product would be a tenth of the whole
/// engine's size, for work done only when a frame has lost units.
fn mul_accumulate(dest: &mut [u8], src: &[u8], coeff: u8) {
    if coeff == 0 {
        return;
    }
    let mut row = [0u8; 256];
    for (b, product) in row.iter_mut().enumerate().skip(1) {
        *product = multiply(coeff, b as u8);
    }
    for (d, s) in dest.iter_mut().zip(src) {
        *d ^= row[usize::from(*s)];
    }
}

/// The m-by-k coding matrix, row-major. `None` if k or m is zero or k+m exceeds [`MAX_TOTAL_UNITS`].
pub fn build_matrix(k: usize, m: usize, out: &mut [u8]) -> Option<()> {
    if k == 0 || m == 0 || k + m > MAX_TOTAL_UNITS || out.len() < k * m {
        return None;
    }
    for i in 0..m {
        for j in 0..k {
            // i < m <= m+j, so i ^ (m+j) is never zero and the inverse always exists. Both are below 64.
            out[i * k + j] = inverse((i ^ (m + j)) as u8)?;
        }
    }
    Some(())
}

/// `C[i][j] = 1 / (i XOR (m + j))`. i < m <= m + j, so the argument is never zero and the inverse exists;
/// k+m is at most [`MAX_TOTAL_UNITS`] wherever this is called, so it fits a byte.
fn coefficient(parity_row: usize, source_column: usize, m: usize) -> u8 {
    inverse((parity_row ^ (m + source_column)) as u8).unwrap_or(0)
}

/// Whether `frame` holds `units` units of `coded_length`.
fn geometry_fits(frame: &[u8], coded_length: usize, units: usize) -> bool {
    units.checked_mul(coded_length).is_some_and(|end| end <= frame.len())
}

/// Computes the m parity units (indices k..k+m) from the k source units in place. m = 0 is a frame with
/// no parity and succeeds without writing. For fixtures; the client only ever recovers.
pub fn encode(frame: &mut [u8], coded_length: usize, k: usize, m: usize) -> bool {
    if k == 0 || k + m > MAX_TOTAL_UNITS || !geometry_fits(frame, coded_length, k + m) {
        return false;
    }
    for i in 0..m {
        let (sources, parity) = frame.split_at_mut((k + i) * coded_length);
        let dest = &mut parity[..coded_length];
        dest.fill(0);
        for j in 0..k {
            mul_accumulate(dest, &sources[j * coded_length..(j + 1) * coded_length], coefficient(i, j, m));
        }
    }
    true
}

/// Working space for [`recover_source`]: two 64x64 matrices, 8 KB. The caller owns it, so recovery is
/// reentrant and allocation-free (the C core keeps these `static` for a 32 KB console stack).
pub struct RecoverScratch {
    cauchy: [u8; MAX_MATRIX],
    inverse: [u8; MAX_MATRIX],
}

impl Default for RecoverScratch {
    fn default() -> Self {
        Self { cauchy: [0; MAX_MATRIX], inverse: [0; MAX_MATRIX] }
    }
}

/// Gauss-Jordan inverse of the n-by-n matrix in `work` (destroyed) into `inv`, which must start as the
/// identity. `false` if singular.
fn invert(work: &mut [u8], inv: &mut [u8], n: usize) -> bool {
    for col in 0..n {
        let Some(pivot) = (col..n).find(|&r| work[r * n + col] != 0) else {
            return false;
        };
        if pivot != col {
            for j in 0..n {
                work.swap(pivot * n + j, col * n + j);
                inv.swap(pivot * n + j, col * n + j);
            }
        }
        let Some(inv_pivot) = inverse(work[col * n + col]) else {
            return false;
        };
        for j in 0..n {
            work[col * n + j] = multiply(work[col * n + j], inv_pivot);
            inv[col * n + j] = multiply(inv[col * n + j], inv_pivot);
        }
        for r in 0..n {
            let factor = work[r * n + col];
            if r == col || factor == 0 {
                continue;
            }
            for j in 0..n {
                work[r * n + j] ^= multiply(factor, work[col * n + j]);
                inv[r * n + j] ^= multiply(factor, inv[col * n + j]);
            }
        }
    }
    true
}

/// Rebuilds the source units that did not arrive (index < k with `arrived[index]` false) in place.
/// `false` when fewer parity units arrived than source units are missing, k+m exceeds
/// [`MAX_TOTAL_UNITS`], or the geometry does not fit `frame` or `arrived`. Arrived units are never
/// written. A frame with nothing missing succeeds, whatever m is.
pub fn recover_source(
    scratch: &mut RecoverScratch,
    frame: &mut [u8],
    coded_length: usize,
    k: usize,
    m: usize,
    arrived: &[bool],
) -> bool {
    let total = k + m;
    if k == 0
        || total > MAX_TOTAL_UNITS
        || arrived.len() < total
        || !geometry_fits(frame, coded_length, total)
    {
        return false;
    }

    let mut missing = [0usize; MAX_TOTAL_UNITS];
    let mut e = 0;
    for j in (0..k).filter(|&j| !arrived[j]) {
        missing[e] = j;
        e += 1;
    }
    if e == 0 {
        return true;
    }
    let mut rows = [0usize; MAX_TOTAL_UNITS];
    let mut row_count = 0;
    for i in (0..m).filter(|&i| arrived[k + i]).take(e) {
        rows[row_count] = i;
        row_count += 1;
    }
    if row_count < e {
        return false;
    }
    let (missing, rows) = (&missing[..e], &rows[..e]);
    let len = coded_length;

    // What each chosen parity unit owes to the missing source units alone: the parity unit minus the share
    // of every source unit that arrived. Written into the missing units' own places, which are free.
    for (&dest_unit, &row) in missing.iter().zip(rows) {
        frame.copy_within((k + row) * len..(k + row + 1) * len, dest_unit * len);
        for j in (0..k).filter(|&j| arrived[j]) {
            // The arrived source unit j is never a missing one, so the two ranges never overlap.
            let (lo, hi) = frame.split_at_mut(dest_unit.max(j) * len);
            let (dest, src) = if dest_unit > j {
                (&mut hi[..len], &lo[j * len..(j + 1) * len])
            } else {
                (&mut lo[dest_unit * len..(dest_unit + 1) * len], &hi[..len])
            };
            mul_accumulate(dest, src, coefficient(row, j, m));
        }
    }

    // remainder = S . missing_units, S[r][c] = C[rows[r]][missing[c]]. Invert S.
    let RecoverScratch { cauchy, inverse: inv } = scratch;
    let (cauchy, inv) = (&mut cauchy[..e * e], &mut inv[..e * e]);
    inv.fill(0);
    for r in 0..e {
        inv[r * e + r] = 1;
        for c in 0..e {
            cauchy[r * e + c] = coefficient(rows[r], missing[c], m);
        }
    }
    if !invert(cauchy, inv, e) {
        return false; // unreachable for a Cauchy submatrix; kept so a bad table fails closed
    }

    // Apply the inverse one byte position at a time, so the remainders are overwritten in place.
    let mut owed = [0u8; MAX_TOTAL_UNITS];
    for t in 0..len {
        for (r, &unit) in missing.iter().enumerate() {
            owed[r] = frame[unit * len + t];
        }
        for (c, &unit) in missing.iter().enumerate() {
            let mut value = 0u8;
            for (r, &o) in owed[..e].iter().enumerate() {
                value ^= multiply(inv[c * e + r], o);
            }
            frame[unit * len + t] = value;
        }
    }
    true
}
#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn inverse_and_divide_are_consistent() {
        for a in 1..=255u8 {
            let inv = inverse(a).unwrap();
            assert_eq!(multiply(a, inv), 1, "a={a}");
            for b in (1..=255u8).step_by(37) {
                assert_eq!(divide(multiply(a, b), b), Some(a), "a={a} b={b}");
            }
        }
        assert_eq!(multiply(0, 123), 0);
        assert_eq!(multiply(123, 0), 0);
        assert_eq!(multiply(1, 123), 123);
    }

    /// The vendor client's own GF(2^8) inverse table, dumped from its live field object 2026-08-02:
    /// genuine ground truth, the same bytes `libripcord/tests/fec_test.c` and `FecTests.cs` check.
    #[test]
    fn matches_console_inverse_table() {
        let head = [0x01, 0x8e, 0xf4, 0x47, 0xa7, 0x7a, 0xba, 0xad, 0x9d, 0xdd, 0x98, 0x3d, 0xaa, 0x5d, 0x96];
        let tail = [0x42, 0xd4, 0xe8, 0x75, 0x7f, 0xff, 0x7e, 0xfd];
        for (i, &want) in head.iter().enumerate() {
            assert_eq!(inverse(i as u8 + 1), Some(want));
        }
        for (i, &want) in tail.iter().enumerate() {
            assert_eq!(inverse(248 + i as u8), Some(want));
        }
        assert_eq!(inverse(0), None);
    }

    #[test]
    fn cauchy_matrix_matches_its_definition() {
        let mut matrix = [0u8; 8];
        build_matrix(4, 2, &mut matrix).unwrap();
        for i in 0..2 {
            for j in 0..4 {
                assert_eq!(Some(matrix[i * 4 + j]), inverse((i ^ (2 + j)) as u8));
            }
        }
    }

    /// The same xorshift as `fec_test.c`, so a failing case here can be replayed there.
    fn fill_pseudo_random(out: &mut [u8], seed: u32) {
        let mut state = seed | 1;
        for b in out {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            *b = state as u8;
        }
    }

    fn encoded_frame(k: usize, m: usize, coded_length: usize, seed: u32) -> Vec<u8> {
        let mut frame = vec![0u8; (k + m) * coded_length];
        for u in 0..k {
            fill_pseudo_random(
                &mut frame[u * coded_length..(u + 1) * coded_length],
                seed.wrapping_add((u as u32).wrapping_mul(2_654_435_761)),
            );
        }
        assert!(encode(&mut frame, coded_length, k, m), "encode");
        frame
    }

    /// Lose the listed units (zeroed, flags cleared); everything else arrived.
    fn lose(frame: &mut [u8], units: usize, coded_length: usize, lost: &[usize]) -> Vec<bool> {
        let mut arrived = vec![true; units];
        for &u in lost {
            frame[u * coded_length..(u + 1) * coded_length].fill(0);
            arrived[u] = false;
        }
        arrived
    }

    fn loss_case(label: &str, k: usize, m: usize, coded_length: usize, lost: &[usize], seed: u32) {
        let mut frame = encoded_frame(k, m, coded_length, seed);
        let pristine = frame.clone();
        let arrived = lose(&mut frame, k + m, coded_length, lost);
        assert!(
            recover_source(&mut RecoverScratch::default(), &mut frame, coded_length, k, m, &arrived),
            "{label}: recover"
        );
        assert_eq!(frame[..k * coded_length], pristine[..k * coded_length], "{label}: source units");
    }

    #[test]
    fn recovers_missing_source_units() {
        loss_case("k4m2 one lost", 4, 2, 200, &[0], 1001);
        loss_case("k4m2 two lost", 4, 2, 200, &[0, 3], 1002);
        loss_case("k6m3 mixed", 6, 3, 200, &[1, 4], 1003);
        loss_case("k6m3 three lost", 6, 3, 200, &[0, 2, 5], 1004);
        loss_case("k8m4 first four lost", 8, 4, 200, &[0, 1, 2, 3], 1005);
        loss_case("k3m1 one lost", 3, 1, 200, &[2], 1006);
        loss_case("a lost parity unit", 4, 2, 64, &[1, 4], 2001);
        loss_case("a long unit", 3, 2, 5000, &[0, 2], 2002);
    }

    /// A real cap47 coded length: a multiple of 4, not of 16 (spec sec 6.2).
    #[test]
    fn recovers_at_a_real_coded_length() {
        loss_case("k6m3 coded length 1004", 6, 3, 1004, &[0, 5], 3001);
        loss_case("k6m3 coded length 1012", 6, 3, 1012, &[0, 5], 3002);
    }

    /// Every loss pattern of a small frame: recovered exactly when enough parity arrived. Any square
    /// submatrix of a Cauchy matrix is invertible, so no pattern with enough parity may fail.
    #[test]
    fn recovers_every_loss_pattern_that_leaves_enough_parity() {
        const K: usize = 5;
        const M: usize = 3;
        const LEN: usize = 12;
        let original = encoded_frame(K, M, LEN, 99);
        let mut scratch = RecoverScratch::default();
        let mut recovered = 0;
        for mask in 0u32..1 << (K + M) {
            let lost: Vec<usize> = (0..K + M).filter(|&u| mask & (1 << u) != 0).collect();
            let lost_source = lost.iter().filter(|&&u| u < K).count();
            let lost_parity = lost.len() - lost_source;
            let mut frame = original.clone();
            let arrived = lose(&mut frame, K + M, LEN, &lost);
            let enough = lost_source + lost_parity <= M;
            assert_eq!(
                recover_source(&mut scratch, &mut frame, LEN, K, M, &arrived),
                enough,
                "mask {mask:#x}"
            );
            if enough {
                assert_eq!(frame[..K * LEN], original[..K * LEN], "mask {mask:#x}");
                recovered += 1;
            }
        }
        assert!(recovered > 0);
    }

    #[test]
    fn no_parity_recovers_nothing_but_accepts_a_complete_frame() {
        let mut frame = encoded_frame(3, 0, 16, 5);
        let mut s = RecoverScratch::default();
        assert!(recover_source(&mut s, &mut frame, 16, 3, 0, &[true, true, true]));
        assert!(!recover_source(&mut s, &mut frame, 16, 3, 0, &[true, false, true]));
    }

    #[test]
    fn refuses_what_it_cannot_do() {
        let mut frame = encoded_frame(4, 2, 32, 4001);
        let mut s = RecoverScratch::default();
        let mut arrived = [false, false, false, true, true, true];
        assert!(!recover_source(&mut s, &mut frame, 32, 4, 2, &arrived), "more losses than parity");
        arrived = [false, true, true, true, true, true];
        assert!(
            !recover_source(&mut s, &mut frame[..5 * 32], 32, 4, 2, &arrived),
            "buffer shorter than the geometry"
        );
        assert!(!recover_source(&mut s, &mut frame, 32, 60, 5, &arrived), "k+m past the cap");
        assert!(!encode(&mut frame, 32, 0, 2));
    }
}
