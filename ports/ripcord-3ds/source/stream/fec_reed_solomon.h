/*
 * ripcord-3ds - systematic Cauchy Reed-Solomon erasure coding over GF(2^8) (spec sec6.2 /
 * CauchyReedSolomon.cs): k source units followed by m parity units, recoverable as long as any k of the
 * k+m units survive. Matrix entry [i][j] = 1 / (i XOR (m+j)) - [C] confirmed from the console's own
 * decompiled coding-matrix builder, over the field fec_galois.h implements ([V] confirmed separately).
 *
 * Units live in one contiguous buffer, unit u at u*stride, each logically unit_size bytes (any tail up to
 * stride is ignored - recovery is proven independent of stride in the .NET reference's
 * ReedSolomon_RecoveryIsIndependentOfSlotStride). All coding is per-byte and independent across byte
 * positions, so there is no dependency on what the bytes mean (compressed video, Opus audio, anything).
 *
 * FEC_MAX_TOTAL_UNITS bounds k+m for this module's own fixed-size working buffers (no heap allocation
 * anywhere in this port). The wire header's unit_index field is 11 bits wide (spec sec6.1), so the wire
 * format itself permits more, but every captured frame in the dirty room codes far fewer units than this
 * cap - a real frame is a handful of MTU-sized fragments, not thousands. A frame that (somehow) exceeds
 * the cap is rejected by fec_reed_solomon_decode rather than overflowing a buffer.
 */
#ifndef FEC_REED_SOLOMON_H
#define FEC_REED_SOLOMON_H

#include <stdint.h>
#include <stddef.h>

#define FEC_MAX_TOTAL_UNITS 64

/* Builds the m*k coding matrix into out (row-major, out[i*k+j] = 1/(i XOR (m+j))). Returns 0 if k+m
 * exceeds FEC_MAX_TOTAL_UNITS or either is 0; out must hold at least (size_t)m * (size_t)k bytes. */
int fec_reed_solomon_build_matrix(int k, int m, uint8_t *out);

/* Encode: compute the m parity units (indices k..k+m-1) from the k source units already in frame_buf, in
 * place. Used to build test fixtures; the client only ever decodes real console traffic. */
int fec_reed_solomon_encode(uint8_t *frame_buf, size_t unit_size, size_t stride, int k, int m);

/* Reconstruct the erased SOURCE units (index < k with present[index] == 0) in place, from the surviving
 * source + parity units. Returns 0 if fewer than k units survive (unrecoverable), the selected k*k system
 * is singular, or k+m exceeds FEC_MAX_TOTAL_UNITS. Present source units are left untouched; present is an
 * array of k+m flags (nonzero == present). */
int fec_reed_solomon_decode(uint8_t *frame_buf, size_t unit_size, size_t stride, int k, int m,
    const uint8_t *present);

#endif /* FEC_REED_SOLOMON_H */
