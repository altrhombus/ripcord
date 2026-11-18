/*
 * libripcord - the elliptic-curve Diffie-Hellman seam (spec sec5.2).
 *
 * This is the one primitive in the core that is NOT implemented here. Everything else in crypto/ is
 * transcribed from a public specification and fits in a few hundred lines; ECDH needs constant-time
 * modular arithmetic over a 521-bit prime field. The .NET side delegates too (HalyardStreamKeySchedule
 * uses System.Security.Cryptography.ECDiffieHellman), so rc_ecdh.c delegates to mbedtls
 * (-DRC_CRYPTO_MBEDTLS), or to a backend the build supplies (RC_ECDH_EXTERNAL_BACKEND, below).
 *
 * BUILDS WITHOUT A BACKEND, DELIBERATELY, so the vector runner still builds with only a C compiler (see
 * rc_crypto.h). With neither switch defined every backend entry point fails cleanly and
 * rc_ecdh_available() returns 0, so the host test suite skips its ECDH cases. It must never substitute
 * a fake key agreement: one returning predictable "shared secrets" would pass tests and then negotiate
 * a session with no confidentiality.
 *
 * THE RNG IS THE CALLER'S. mbedtls's own entropy sources assume a hosted OS the consoles do not
 * provide, so a port passes its platform's generator (rc_random.h). In tests, known-answer vectors
 * require a *fixed* private key. The callback signature matches mbedtls's own so it can be passed
 * straight through.
 *
 * WHICH CURVE. Version-dependent (HalyardStreamKeySchedule, spec sec5.2): protocol versions 0x0d-0x11
 * use P-521, everything else uses P-256. Client version 17 (0x11) therefore negotiates P-521. The
 * selection is made a layer up, by whoever knows the negotiated version.
 */
#ifndef RC_ECDH_H
#define RC_ECDH_H

#include <stddef.h>
#include <stdint.h>

/* Curve selectors. The values are this core's own; nothing on the wire carries a curve id. */
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
 * `private_key` is secret material. Nothing in this core persists one - a pair lives for one session -
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
 * WHY THE LAST derive_shared FAILED, for diagnosis only. The derivation is a chain of backend calls, and
 * a well-formed peer point on the agreed curve can still fail for platform reasons (an allocation the
 * backend could not make, for instance).
 *
 * `step` is RC_ECDH_STEP_*, `code` is the backend's own return value, verbatim and unmapped.
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
 * It lets a caller put a known-good point through the identical path, at the identical call depth, to
 * tell a bad point from a bad group or environment.
 */
int rc_ecdh_check_peer_point(unsigned curve, const uint8_t *point, size_t length);

/*
 * What the last derivation was actually handed: a fingerprint over the peer bytes as the derivation
 * read them, to compare with a point that passed rc_ecdh_check_peer_point in the same run.
 *
 * FNV-1a: an identity check between two places in one program, not a security property.
 */
unsigned long rc_ecdh_last_peer_fingerprint(void);
unsigned long rc_ecdh_fingerprint(const unsigned char *data, size_t length);
size_t rc_ecdh_last_private_length(void);
unsigned rc_ecdh_last_curve(void);

int rc_ecdh_last_error_step(void);
int rc_ecdh_last_error_code(void);

/*
 * WHAT A CALL WAS HANDED AND WHERE IT STOPPED, IN THE CALLER'S OWN STRUCT.
 *
 * The rc_ecdh_last_* getters above read one process-wide record that every call without _diag overwrites,
 * so two threads deriving at once race on it. The _diag variants do the same work and write the record
 * into `diag` instead (NULL to discard it), so a caller that can derive concurrently reads its own answer.
 * The session negotiator uses them. The getters are for single-threaded callers, and are fed by the calls
 * without _diag only.
 *
 * With RC_ECDH_EXTERNAL_BACKEND the backend reports through the process-wide hooks below, so there the
 * _diag variants can only copy that record, and are as single-threaded as the hooks.
 */
typedef struct rc_ecdh_diagnostics {
    int error_step;                  /* RC_ECDH_STEP_*, RC_ECDH_STEP_NONE on success */
    int error_code;                  /* the backend's own error value, verbatim */
    unsigned long peer_fingerprint;  /* rc_ecdh_fingerprint over the peer key as the derivation read it */
    size_t private_length;
    unsigned curve;
} rc_ecdh_diagnostics;

int rc_ecdh_check_peer_point_diag(unsigned curve, const uint8_t *point, size_t length, rc_ecdh_diagnostics *diag);

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
int rc_ecdh_derive_shared(const rc_ecdh_keypair *pair,
                          const uint8_t *peer_public_key, size_t peer_public_key_length,
                          rc_rng_fn rng, void *rng_ctx,
                          uint8_t *out_secret, size_t out_secret_size, size_t *out_secret_length);

int rc_ecdh_derive_shared_diag(const rc_ecdh_keypair *pair,
                               const uint8_t *peer_public_key, size_t peer_public_key_length,
                               rc_rng_fn rng, void *rng_ctx,
                               uint8_t *out_secret, size_t out_secret_size, size_t *out_secret_length,
                               rc_ecdh_diagnostics *diag);

/*
 * AN EXTERNAL BACKEND. A build that defines RC_ECDH_EXTERNAL_BACKEND compiles neither the Mbed TLS branch
 * of rc_ecdh.c nor its fail-closed branch, and must itself define the five backend entry points:
 * rc_ecdh_available, rc_ecdh_generate, rc_ecdh_keypair_from_private, rc_ecdh_derive_shared and
 * rc_ecdh_check_peer_point, with exactly the contracts stated above. That includes validating the peer
 * point before it is used, and failing rather than returning anything key-shaped.
 *
 * It is for a port whose platform has its own audited curve implementation, one that needs no
 * build-time fetch.
 *
 * The backend reports into the same diagnostics as the Mbed TLS branch through these two hooks. `code`
 * is whatever the backend's own error value is, verbatim, as above.
 */
#if defined(RC_ECDH_EXTERNAL_BACKEND)
void rc_ecdh_backend_record_failure(int step, int code);
void rc_ecdh_backend_record_derivation(unsigned curve, size_t private_length,
                                       const uint8_t *peer_public_key, size_t peer_public_key_length);
#endif

#endif /* RC_ECDH_H */
