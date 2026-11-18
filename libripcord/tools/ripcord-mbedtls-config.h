/*
 * ripcord ports - a minimal Mbed TLS configuration: elliptic curves and bignums, nothing else.
 *
 * libripcord/crypto/rc_ecdh.c delegates exactly one primitive to this library - ECDH over P-256 and
 * P-521 - and uses only its mbedtls_ecp_* and mbedtls_mpi_* entry points. There is no TLS here, no
 * X.509, no cipher suites, no entropy source, no filesystem and no clock: this port supplies its own
 * randomness through the platform seam's rc_random_bytes (platform/rc_platform.h) and speaks a protocol
 * that is not TLS.
 *
 * Everything below is therefore an ALLOW-list rather than the usual mbedtls config, which is a
 * deny-list over a large default. It keeps the built archive small enough to link into a handheld, and
 * what this file enables is the complete list of what Ripcord asks of Mbed TLS.
 */

#ifndef RIPCORD_MBEDTLS_CONFIG_H
#define RIPCORD_MBEDTLS_CONFIG_H

/* Bignum arithmetic - mbedtls_mpi_*, the layer ECP is built on. */
#define MBEDTLS_BIGNUM_C

/* Elliptic curves over a prime field - mbedtls_ecp_*. */
#define MBEDTLS_ECP_C

/*
 * The two curves the protocol actually selects, and no others.
 *
 * HalyardStreamKeySchedule picks the curve from the negotiated protocol version (0x0d-0x11 -> P-521,
 * otherwise P-256), and client version 17 is what a real session negotiates today. No code path can
 * reach any other curve.
 */
#define MBEDTLS_ECP_DP_SECP256R1_ENABLED
#define MBEDTLS_ECP_DP_SECP521R1_ENABLED

/* The fast reduction routines for the NIST primes. Pure win for the two curves above. */
#define MBEDTLS_ECP_NIST_OPTIM

/*
 * No internal DRBG - and this does NOT disable the side-channel countermeasure it looks like it does.
 *
 * Mbed TLS blinds its scalar multiplications against timing/power analysis, and needs randomness to do
 * it. It sources that from its own CTR_DRBG *only when the caller passes f_rng == NULL*; a caller that
 * supplies an RNG gets blinding from that instead. Without this define, check_config.h refuses to
 * build ECP unless MBEDTLS_CTR_DRBG_C and MBEDTLS_ENTROPY_C come with it - an entropy source that, per
 * rc_ecdh.h, cannot work on a console.
 *
 * All three call sites in libripcord/crypto/rc_ecdh.c pass a real (rng, rng_ctx) pair -
 * mbedtls_ecp_gen_keypair, and both mbedtls_ecp_mul calls (public-key derivation and the shared-secret
 * computation) - so blinding is active on every operation, sourced from the platform CSPRNG
 * (sceKernelGetRandomNumber on Vita).
 *
 * If a future call site ever passes NULL for f_rng, THIS DEFINE BECOMES A REAL VULNERABILITY.
 * rc_ecdh.c is the file to check.
 */
#define MBEDTLS_ECP_NO_INTERNAL_RNG

/*
 * NOT ENABLED, and each absence is load-bearing:
 *
 *   MBEDTLS_ENTROPY_C / MBEDTLS_CTR_DRBG_C - the RNG is the caller's (rc_ecdh.h explains why): on
 *       device mbedtls's own entropy sources assume a hosted OS, and in tests a known-answer vector
 *       needs a FIXED private key.
 *   MBEDTLS_SELF_TEST     - the known-answer checking lives in libripcord/tests, against the .NET
 *                           implementation.
 *   MBEDTLS_PLATFORM_C    - mbedtls_calloc/mbedtls_free then resolve straight to calloc/free.
 *   MBEDTLS_FS_IO, MBEDTLS_HAVE_TIME, MBEDTLS_NET_C - no filesystem, no clock, no sockets wanted here.
 *   MBEDTLS_SSL_*, MBEDTLS_X509_* - this is not TLS.
 */

#include "mbedtls/check_config.h"

#endif /* RIPCORD_MBEDTLS_CONFIG_H */
