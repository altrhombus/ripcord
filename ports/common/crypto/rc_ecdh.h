/*
 * ripcord-3ds - the elliptic-curve Diffie-Hellman seam (spec sec5.2).
 *
 * This is the one primitive in the whole port that is NOT implemented here. Everything else in
 * source/crypto is transcribed from a public specification and fits in a few hundred lines; ECDH needs
 * modular arithmetic over a 521-bit prime field, and a hand-written constant-time bigint is exactly the
 * kind of security-critical code that should not be written twice by someone who does not have to.
 *
 * The .NET side made the same call for the same reason - HalyardStreamKeySchedule delegates entirely to
 * System.Security.Cryptography.ECDiffieHellman and the project has no custom EC point arithmetic
 * anywhere - so this file delegates to mbedtls, which devkitPro packages for the 3DS as `3ds-mbedtls`.
 *
 * BUILDS WITHOUT MBEDTLS, DELIBERATELY. rc_crypto.h's whole argument is that the vector runner must
 * build on any machine with a C compiler and nothing else installed, and that argument does not stop
 * being true here. Without -DRC_CRYPTO_MBEDTLS every function below fails cleanly and
 * rc_ecdh_available() returns 0, so the host test suite skips its ECDH cases the same way the managed
 * suites skip vectors that need the dirty room. What it must never do is silently substitute a fake
 * key agreement; a stub that returns predictable "shared secrets" would let a broken build pass its
 * tests and then negotiate a session with no confidentiality at all.
 *
 * THE RNG IS THE CALLER'S. Two reasons, and neither is style. On device, mbedtls's own entropy sources
 * assume a hosted OS that the 3DS does not provide, so the entropy has to come from libctru anyway. In
 * tests, known-answer vectors require a *fixed* private key, and a curve implementation that insists on
 * generating its own randomness cannot be checked against another implementation's numbers at all -
 * which is the entire verification method this port is built on. The callback signature deliberately
 * matches mbedtls's own so it can be passed straight through.
 *
 * WHICH CURVE. Version-dependent, and not a free choice (HalyardStreamKeySchedule, spec sec5.2):
 * protocol versions 0x0d-0x11 use P-521, everything else uses P-256. The shipped client version is 17
 * (0x11), so P-521 is what a real session negotiates today. Both are implemented because the selection
 * is made a layer up, by whoever knows the negotiated version.
 */
#ifndef RC_ECDH_H
#define RC_ECDH_H

#include <stddef.h>
#include <stdint.h>

/* Curve selectors. The values are this port's own; nothing on the wire carries a curve id. */
#define RC_ECDH_CURVE_P256 1u
#define RC_ECDH_CURVE_P521 2u

/* Uncompressed SEC1 point: 0x04 || X || Y. 65 bytes for P-256, 133 for P-521. */
#define RC_ECDH_P256_PUBKEY_LENGTH 65u
#define RC_ECDH_P521_PUBKEY_LENGTH 133u
#define RC_ECDH_PUBKEY_MAX 133u

/* The shared secret is the X coordinate alone, at the curve's full width - 32 or 66 bytes. */
#define RC_ECDH_P256_SECRET_LENGTH 32u
#define RC_ECDH_P521_SECRET_LENGTH 66u
#define RC_ECDH_SECRET_MAX 66u

#define RC_ECDH_PRIVATE_MAX 66u

/*
 * Random-byte source. Returns 0 on success, non-zero on failure - mbedtls's own f_rng convention, so an
 * implementation can be handed to either side unchanged.
 */
typedef int (*rc_rng_fn)(void *ctx, uint8_t *out, size_t length);

/*
 * An ephemeral key pair. Plain bytes rather than an mbedtls handle on purpose: this header has to
 * compile on a machine with no mbedtls installed, and the struct outlives the mbedtls contexts, which
 * are set up and torn down inside each call rather than held open.
 *
 * `private_key` is secret material. Nothing in this port persists one - a pair lives for one session -
 * but a caller that copies it elsewhere owns that decision.
 */
typedef struct {
    unsigned curve;
    uint8_t private_key[RC_ECDH_PRIVATE_MAX];
    size_t private_key_length;
    uint8_t public_key[RC_ECDH_PUBKEY_MAX];
    size_t public_key_length;
} rc_ecdh_keypair;

/* 1 if this build has a real ECDH backend, 0 if every call below will fail. */
int rc_ecdh_available(void);

/* Byte length of an uncompressed public point on `curve`, or 0 if the curve is unknown. */
size_t rc_ecdh_public_key_length(unsigned curve);

/* Byte length of a shared secret on `curve`, or 0 if the curve is unknown. */
size_t rc_ecdh_secret_length(unsigned curve);

/*
 * Maps a public-key length back to the curve that produced it, or 0 if the length matches neither. This
 * is how a received peer key is classified - the wire carries no curve id, and the .NET side detects it
 * the same way, from the length alone.
 */
unsigned rc_ecdh_curve_for_public_key_length(size_t length);

/*
 * Generates a fresh ephemeral pair on `curve`. Returns 1 on success, 0 on failure (unknown curve, no
 * backend, or the RNG refused).
 */
int rc_ecdh_generate(unsigned curve, rc_rng_fn rng, void *rng_ctx, rc_ecdh_keypair *out_pair);

/*
 * Rebuilds a pair from a known private scalar, recomputing the public point. This exists for
 * known-answer vectors, which is why it is in the header rather than hidden in the test: a fixed
 * private key is the only way to compare this implementation's ECDH against the .NET side's, and a
 * test-only back door bolted on from outside would be worse than an honest documented entry point.
 * Returns 1 on success, 0 on failure (including a scalar that is zero or >= the group order).
 *
 * `rng` is used for coordinate blinding only and never for the key itself; it may not be NULL.
 */
int rc_ecdh_keypair_from_private(unsigned curve, const uint8_t *private_key, size_t private_key_length,
                                 rc_rng_fn rng, void *rng_ctx, rc_ecdh_keypair *out_pair);

/*
 * Computes the ECDH shared secret: the X coordinate of (local private) * (peer public point), written
 * big-endian at the curve's full width with leading zeros preserved.
 *
 * The peer key is validated as a point actually on the curve before it is used - an unvalidated peer
 * point is the classic invalid-curve attack, and the console is not a trusted input just because it is
 * on the LAN. Returns 1 on success, 0 on failure (curve mismatch between the pair and the peer key, a
 * malformed or off-curve peer point, no backend, or a degenerate result).
 *
 * The secret feeds stream_key_schedule_derive_direction() as the HMAC key, unreduced and untruncated.
 */
/*
 * WHY THE LAST derive_shared FAILED, for diagnosis only.
 *
 * The derivation is a chain of half a dozen backend calls and every one of them returns a specific error
 * code, all of which used to be collapsed into a single 0. On a console that is one round trip per
 * guess, and the guesses are not cheap: a peer point that is well-formed and on the agreed curve can
 * still fail to derive for reasons that have nothing to do with the protocol - an allocation the backend
 * could not make, for instance, which is a platform fact and not a crypto one.
 *
 * `step` is RC_ECDH_STEP_*, `code` is the backend's own return value, verbatim and unmapped, because a
 * translated error code is one more layer between a reader and what actually happened.
 *
 * Single-threaded and diagnostic: nothing in the protocol depends on these, they are overwritten by
 * every call, and a caller that ignores them is unaffected.
 */
#define RC_ECDH_STEP_NONE         0
#define RC_ECDH_STEP_ARGUMENTS    1
#define RC_ECDH_STEP_CURVE        2
#define RC_ECDH_STEP_SIZES        3
#define RC_ECDH_STEP_GROUP_LOAD   4
#define RC_ECDH_STEP_PRIVATE_KEY  5
#define RC_ECDH_STEP_PEER_READ    6
#define RC_ECDH_STEP_PEER_CHECK   7
#define RC_ECDH_STEP_MULTIPLY     8  /* the expensive one - an arbitrary-point multiply */
#define RC_ECDH_STEP_IDENTITY     9
#define RC_ECDH_STEP_WRITE       10

/*
 * Validate a peer's public point without deriving anything: load the curve, read the point, and ask the
 * backend whether it is on the curve. Returns 1 if it is. On failure the rc_ecdh_last_error_* pair says
 * which call objected and what it returned.
 *
 * This exists so a caller can ask the question in isolation. A derivation that fails tells you very
 * little on its own - the same refusal covers the point, the group, and the environment the call was
 * made in - and being able to put a KNOWN-GOOD point through the identical path, at the identical call
 * depth, is what separates those.
 */
int rc_ecdh_check_peer_point(unsigned curve, const uint8_t *point, size_t length);

/*
 * What the last derivation was actually handed, as opposed to what the caller believes it passed.
 *
 * A point that passes rc_ecdh_check_peer_point and fails the identical check inside
 * rc_ecdh_derive_shared, in the same run, is either not the same point or not the same environment.
 * A fingerprint over the bytes AS THE DERIVATION READ THEM settles which, and no amount of reading the
 * call site does - the call site is what is in question.
 *
 * FNV-1a, because this is an identity check between two places in one program and not a security
 * property; it wants to be cheap and identical on both sides, not strong.
 */
unsigned long rc_ecdh_last_peer_fingerprint(void);
unsigned long rc_ecdh_fingerprint(const unsigned char *data, size_t length);
size_t rc_ecdh_last_private_length(void);
unsigned rc_ecdh_last_curve(void);

int rc_ecdh_last_error_step(void);
int rc_ecdh_last_error_code(void);

int rc_ecdh_derive_shared(const rc_ecdh_keypair *pair,
                          const uint8_t *peer_public_key, size_t peer_public_key_length,
                          rc_rng_fn rng, void *rng_ctx,
                          uint8_t *out_secret, size_t out_secret_size, size_t *out_secret_length);

#endif /* RC_ECDH_H */
