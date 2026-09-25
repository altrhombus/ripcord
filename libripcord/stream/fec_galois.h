/*
 * ripcord-3ds - GF(2^8) arithmetic for the video FEC (spec sec6.2 / GaloisField256.cs).
 *
 * Primitive polynomial 0x11d (x^8+x^4+x^3+x^2+1) - confirmed [V] two independent ways against the
 * vendor binary (a literal immediate in its table-builder, and a live dump of its inverse table), and a
 * fixed compile-time constant, never negotiated or per-session. Multiplication/division go through
 * exp/log tables built once at startup, the textbook GF(256) approach.
 *
 * The inverse table is checked in tests/fec_test.c against real console-dumped bytes
 * (docs/protocol/ps5-remoteplay-v1-spec.md / FecTests.cs's GaloisField256_MatchesConsoleInverseTable) -
 * genuine ground truth, not a self-consistency check.
 */
#ifndef FEC_GALOIS_H
#define FEC_GALOIS_H

#include <stdint.h>

void fec_galois_init(void); /* builds the exp/log tables; idempotent, call before any of the below */

uint8_t fec_galois_multiply(uint8_t a, uint8_t b);

/* Returns 0 and leaves *out unset if b == 0 (division by zero is undefined in this field, matching the
 * .NET reference's own exception - callers must never hit this in practice; see fec_reed_solomon.h for
 * why the Cauchy matrix construction never can). */
int fec_galois_divide(uint8_t a, uint8_t b, uint8_t *out);

/* Multiplicative inverse: 1/a. Returns 0 and leaves *out unset if a == 0. */
int fec_galois_inverse(uint8_t a, uint8_t *out);

#endif /* FEC_GALOIS_H */
