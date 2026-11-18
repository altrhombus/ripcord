/*
 * libripcord - systematic Cauchy Reed-Solomon erasure coding over GF(2^8) (spec sec6.2 /
 * CauchyReedSolomon.cs): k source units followed by m parity units, parity unit i being
 * sum_j C[i][j] * source_j with C[i][j] = 1 / (i XOR (m+j)) - [C] confirmed from the console's own
 * decompiled coding-matrix builder, over the field fec_galois.h implements ([V] confirmed separately).
 *
 * Units are laid out the way the console lays them out: one contiguous buffer, unit u at
 * u * coded_length, every unit exactly coded_length bytes. All coding is per byte position, so there is
 * no dependency on what the bytes mean (compressed video, Opus audio, anything).
 *
 * Recovery works on the missing source units only: with e of them missing, take e parity units that
 * arrived, remove the arrived source units' share from each, and solve the e*e system those parity rows
 * and the missing columns of C give. Every square submatrix of a Cauchy matrix is invertible, so the
 * system has a solution whenever e parity units arrived.
 *
 * FEC_MAX_TOTAL_UNITS bounds k+m for this module's own fixed-size working buffers (no heap allocation
 * anywhere in this core). The wire header's unit_index field is 11 bits wide (spec sec6.1), so the wire
 * format itself permits more; a frame that exceeds the cap is refused rather than overflowing a buffer.
 */
#ifndef FEC_REED_SOLOMON_H
#define FEC_REED_SOLOMON_H

#include <stdint.h>
#include <stddef.h>

#define FEC_MAX_TOTAL_UNITS 64

/* Builds the m*k coding matrix into out (row-major, out[i*k+j] = 1/(i XOR (m+j))). Returns 0 if k+m
 * exceeds FEC_MAX_TOTAL_UNITS or either is 0; out must hold at least (size_t)m * (size_t)k bytes. */
int fec_reed_solomon_build_matrix(int k, int m, uint8_t *out);

/* Compute the m parity units (indices k..k+m-1) from the k source units already in units, in place.
 * m == 0 is a frame with no parity and succeeds without writing. Used to build test fixtures; the client
 * only ever recovers real console traffic. */
int fec_reed_solomon_encode(uint8_t *units, size_t coded_length, int k, int m);

/* Rebuild the source units that did not arrive (index < k with arrived[index] == 0) in place. Returns 0
 * when fewer parity units arrived than source units are missing, or k+m exceeds FEC_MAX_TOTAL_UNITS;
 * arrived units are never written. arrived is an array of k+m flags (nonzero == arrived). A frame with
 * nothing missing succeeds, whatever m is.
 *
 * Not reentrant: its e*e working matrices are static (see the .c), so only one call may run at a time
 * in the process. */
int fec_reed_solomon_recover_source(uint8_t *units, size_t coded_length, int k, int m,
    const uint8_t *arrived);

#endif /* FEC_REED_SOLOMON_H */
