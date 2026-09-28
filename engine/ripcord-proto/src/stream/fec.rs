//! Video FEC: GF(2^8) arithmetic and systematic Cauchy Reed-Solomon erasure coding (spec sec 6.2).
//! Ported from `libripcord/stream/fec_galois.c` and `fec_reed_solomon.c`.
//!
//! The field's primitive polynomial is 0x11d, confirmed [V] against the vendor binary two independent
//! ways, and pinned here by the console-dumped inverse table in the tests. k source units are followed
//! by m parity units, and any k surviving units recover the rest. Matrix entry `[i][j] = 1 / (i XOR
//! (m+j))`, confirmed [C] from the console's own coding-matrix builder.
//!
//! Units live in one buffer, unit `u` at `u * stride`, each logically `unit_size` bytes; the tail up to
//! `stride` is ignored. Coding is per byte, so what the bytes mean does not matter.

const PRIMITIVE_POLYNOMIAL: u16 = 0x11d;

/// Bounds k+m for recovery, as in the C core. A frame may occupy more slots than this (see the
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
/// of a thousand bytes or more. This used to read the row out of a 64 KB table of every product, built at
/// compile time, which was a tenth of the whole engine's size (2026-09-26) for work done only when a frame
/// has lost units.
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

/// Whether `frame` can hold `units` units of `unit_size` at `stride`.
fn geometry_fits(frame: &[u8], unit_size: usize, stride: usize, units: usize) -> bool {
    unit_size <= stride
        && units
            .checked_sub(1)
            .and_then(|last| last.checked_mul(stride))
            .and_then(|off| off.checked_add(unit_size))
            .is_some_and(|end| end <= frame.len())
}

/// Computes the m parity units (indices k..k+m) from the k source units in place. For fixtures; the
/// client only ever decodes.
pub fn encode(frame: &mut [u8], unit_size: usize, stride: usize, k: usize, m: usize) -> bool {
    let mut matrix = [0u8; MAX_MATRIX];
    if build_matrix(k, m, &mut matrix).is_none() || !geometry_fits(frame, unit_size, stride, k + m) {
        return false;
    }
    for i in 0..m {
        let (sources, parity) = frame.split_at_mut((k + i) * stride);
        let dest = &mut parity[..unit_size];
        dest.fill(0);
        for j in 0..k {
            let coeff = matrix[i * k + j];
            if coeff != 0 {
                mul_accumulate(dest, &sources[j * stride..j * stride + unit_size], coeff);
            }
        }
    }
    true
}

/// Working space for [`decode`]: three 64x64 matrices, 12 KB. The caller owns it, so decoding is
/// reentrant (the C core keeps these `static` for a 32 KB console stack).
pub struct DecodeScratch {
    matrix: [u8; MAX_MATRIX],
    a: [u8; MAX_MATRIX],
    inv: [u8; MAX_MATRIX],
}

impl Default for DecodeScratch {
    fn default() -> Self {
        Self { matrix: [0; MAX_MATRIX], a: [0; MAX_MATRIX], inv: [0; MAX_MATRIX] }
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

/// Reconstructs the erased source units (index < k with `present[index]` false) in place from the
/// survivors. `false` if fewer than k units survive, the system is singular, k+m exceeds
/// [`MAX_TOTAL_UNITS`], or the geometry does not fit `frame` or `present`. Present units are left as
/// they are.
pub fn decode(
    scratch: &mut DecodeScratch,
    frame: &mut [u8],
    unit_size: usize,
    stride: usize,
    k: usize,
    m: usize,
    present: &[bool],
) -> bool {
    let total = k + m;
    if k == 0
        || m == 0
        || total > MAX_TOTAL_UNITS
        || present.len() < total
        || !geometry_fits(frame, unit_size, stride, total)
    {
        return false;
    }

    let mut chosen = [0usize; MAX_TOTAL_UNITS];
    let mut chosen_count = 0;
    for u in (0..total).filter(|&u| present[u]).take(k) {
        chosen[chosen_count] = u;
        chosen_count += 1;
    }
    if chosen_count < k {
        return false;
    }
    if present[..k].iter().all(|&p| p) {
        return true;
    }

    let DecodeScratch { matrix, a, inv } = scratch;
    if build_matrix(k, m, matrix).is_none() {
        return false;
    }

    // Row r of A is the generator row of chosen unit r: an identity row for a source unit, the coding
    // row for a parity unit. A . data = chosen values.
    a[..k * k].fill(0);
    inv[..k * k].fill(0);
    for (r, &u) in chosen[..k].iter().enumerate() {
        inv[r * k + r] = 1;
        if u < k {
            a[r * k + u] = 1;
        } else {
            a[r * k..r * k + k].copy_from_slice(&matrix[(u - k) * k..(u - k) * k + k]);
        }
    }
    if !invert(&mut a[..k * k], &mut inv[..k * k], k) {
        return false;
    }

    // data[e] = sum over r of inv[e][r] . chosen[r], per byte. Every chosen unit that is a source unit
    // is present, so it is never the unit being written, and every erased unit is rebuilt only from
    // chosen (present) units.
    let mut unit = [0u8; 4096];
    for e in (0..k).filter(|&e| !present[e]) {
        let mut acc_heap;
        let acc: &mut [u8] = if unit_size <= unit.len() {
            &mut unit[..unit_size]
        } else {
            acc_heap = vec![0u8; unit_size];
            &mut acc_heap
        };
        acc.fill(0);
        for (r, &src) in chosen[..k].iter().enumerate() {
            let coeff = inv[e * k + r];
            if coeff != 0 {
                mul_accumulate(acc, &frame[src * stride..src * stride + unit_size], coeff);
            }
        }
        frame[e * stride..e * stride + unit_size].copy_from_slice(acc);
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

    fn erasure_case(
        label: &str,
        k: usize,
        m: usize,
        unit_size: usize,
        stride: usize,
        erasures: &[usize],
        seed: u32,
    ) {
        let total = k + m;
        let mut frame = vec![0u8; total * stride];
        for u in 0..k {
            fill_pseudo_random(
                &mut frame[u * stride..u * stride + unit_size],
                seed.wrapping_add((u as u32).wrapping_mul(2_654_435_761)),
            );
        }
        assert!(encode(&mut frame, unit_size, stride, k, m), "{label}: encode");
        let pristine = frame.clone();
        let mut present = vec![true; total];
        for &e in erasures {
            frame[e * stride..(e + 1) * stride].fill(0);
            present[e] = false;
        }
        assert!(
            decode(&mut DecodeScratch::default(), &mut frame, unit_size, stride, k, m, &present),
            "{label}: decode"
        );
        for u in 0..k {
            assert_eq!(
                frame[u * stride..u * stride + unit_size],
                pristine[u * stride..u * stride + unit_size],
                "{label}: unit {u}"
            );
        }
    }

    #[test]
    fn reconstructs_erased_source_units() {
        erasure_case("k4m2 one lost", 4, 2, 200, 208, &[0], 1001);
        erasure_case("k4m2 two lost", 4, 2, 200, 208, &[0, 3], 1002);
        erasure_case("k6m3 mixed", 6, 3, 200, 208, &[1, 4], 1003);
        erasure_case("k6m3 three lost", 6, 3, 200, 208, &[0, 2, 5], 1004);
        erasure_case("k8m4 first four lost", 8, 4, 200, 208, &[0, 1, 2, 3], 1005);
        erasure_case("k3m1 one lost", 3, 1, 200, 208, &[2], 1006);
        erasure_case("a lost parity unit", 4, 2, 64, 64, &[1, 4], 2001);
        erasure_case("unit larger than the stack accumulator", 3, 2, 5000, 5008, &[0, 2], 2002);
    }

    #[test]
    fn recovery_is_independent_of_slot_stride() {
        erasure_case("tight stride", 4, 2, 999, 999, &[0, 3], 3001);
        erasure_case("16-aligned stride", 4, 2, 999, 1008, &[0, 3], 3001);
    }

    #[test]
    fn refuses_what_it_cannot_do() {
        let mut frame = vec![0u8; 6 * 32];
        assert!(encode(&mut frame, 32, 32, 4, 2));
        let mut s = DecodeScratch::default();
        let mut present = [false, false, false, true, true, true];
        assert!(!decode(&mut s, &mut frame, 32, 32, 4, 2, &present), "more erasures than parity");
        present = [false, true, true, true, true, true];
        assert!(
            !decode(&mut s, &mut frame[..5 * 32], 32, 32, 4, 2, &present),
            "buffer shorter than the geometry"
        );
        assert!(!decode(&mut s, &mut frame, 33, 32, 4, 2, &present), "unit larger than its stride");
        assert!(!decode(&mut s, &mut frame, 32, 32, 60, 5, &present), "k+m past the cap");
        assert!(!encode(&mut frame, 32, 32, 0, 2));
    }
}
