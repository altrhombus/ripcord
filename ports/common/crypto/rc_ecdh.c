/* See rc_ecdh.h for why this delegates instead of implementing, and why it still builds when the
 * backend is absent. */

#include "rc_ecdh.h"

#include <string.h>

size_t rc_ecdh_public_key_length(unsigned curve)
{
    switch (curve) {
    case RC_ECDH_CURVE_P256:
        return RC_ECDH_P256_PUBKEY_LENGTH;
    case RC_ECDH_CURVE_P521:
        return RC_ECDH_P521_PUBKEY_LENGTH;
    default:
        return 0;
    }
}

size_t rc_ecdh_secret_length(unsigned curve)
{
    switch (curve) {
    case RC_ECDH_CURVE_P256:
        return RC_ECDH_P256_SECRET_LENGTH;
    case RC_ECDH_CURVE_P521:
        return RC_ECDH_P521_SECRET_LENGTH;
    default:
        return 0;
    }
}

/* See the header: diagnostic only, single-threaded, overwritten by every derive. */
static int s_last_error_step;
static int s_last_error_code;

int rc_ecdh_last_error_step(void) { return s_last_error_step; }
int rc_ecdh_last_error_code(void) { return s_last_error_code; }

static int ecdh_fail(int step, int code)
{
    s_last_error_step = step;
    s_last_error_code = code;
    return 0;
}

unsigned rc_ecdh_curve_for_public_key_length(size_t length)
{
    if (length == RC_ECDH_P256_PUBKEY_LENGTH) {
        return RC_ECDH_CURVE_P256;
    }
    if (length == RC_ECDH_P521_PUBKEY_LENGTH) {
        return RC_ECDH_CURVE_P521;
    }
    return 0;
}

#if defined(RC_CRYPTO_MBEDTLS)

#include <mbedtls/bignum.h>
#include <mbedtls/ecp.h>
#include <mbedtls/platform_util.h>

/*
 * ONE API FOR TWO INCOMPATIBLE MBEDTLS MAJORS. devkitPro ships 2.28 for the 3DS while a Linux host box
 * gets 3.x, and 3.x moved mbedtls_ecp_point's X/Y/Z coordinates behind MBEDTLS_PRIVATE. So the shared
 * secret's X coordinate is NOT read off the point struct - it is recovered from
 * mbedtls_ecp_point_write_binary's uncompressed output, which is a documented, stable, public API in
 * both. mbedtls_ecp_group's own G/N/nbits are still public fields in both majors, so those are used
 * directly. Everything here sticks to function-level API for the same reason; if this file ever needs a
 * struct field that 3.x has hidden, the fix is another write_binary-shaped detour, not a
 * MBEDTLS_PRIVATE_ACCESS define.
 */

static mbedtls_ecp_group_id group_id_for(unsigned curve)
{
    switch (curve) {
    case RC_ECDH_CURVE_P256:
        return MBEDTLS_ECP_DP_SECP256R1;
    case RC_ECDH_CURVE_P521:
        return MBEDTLS_ECP_DP_SECP521R1;
    default:
        return MBEDTLS_ECP_DP_NONE;
    }
}

int rc_ecdh_available(void)
{
    return 1;
}

/* Writes `point` as 0x04 || X || Y and checks it came out at the expected width. */
static int write_uncompressed(mbedtls_ecp_group *grp, const mbedtls_ecp_point *point,
                              uint8_t *out, size_t out_size, size_t expected_length)
{
    size_t written = 0;

    if (mbedtls_ecp_point_write_binary(grp, point, MBEDTLS_ECP_PF_UNCOMPRESSED,
                                       &written, out, out_size) != 0) {
        return 0;
    }
    return (written == expected_length) ? 1 : 0;
}

static int finish_keypair(mbedtls_ecp_group *grp, const mbedtls_mpi *d, const mbedtls_ecp_point *q,
                          unsigned curve, rc_ecdh_keypair *out_pair)
{
    size_t private_length = rc_ecdh_secret_length(curve);
    size_t public_length = rc_ecdh_public_key_length(curve);

    memset(out_pair, 0, sizeof(*out_pair));
    out_pair->curve = curve;

    if (mbedtls_mpi_write_binary(d, out_pair->private_key, private_length) != 0) {
        return 0;
    }
    out_pair->private_key_length = private_length;

    if (!write_uncompressed(grp, q, out_pair->public_key, sizeof(out_pair->public_key),
                            public_length)) {
        return 0;
    }
    out_pair->public_key_length = public_length;
    return 1;
}

int rc_ecdh_generate(unsigned curve, rc_rng_fn rng, void *rng_ctx, rc_ecdh_keypair *out_pair)
{
    mbedtls_ecp_group grp;
    mbedtls_mpi d;
    mbedtls_ecp_point q;
    mbedtls_ecp_group_id id = group_id_for(curve);
    int ok = 0;

    if (out_pair == NULL || rng == NULL || id == MBEDTLS_ECP_DP_NONE) {
        return 0;
    }

    mbedtls_ecp_group_init(&grp);
    mbedtls_mpi_init(&d);
    mbedtls_ecp_point_init(&q);

    if (mbedtls_ecp_group_load(&grp, id) == 0
        && mbedtls_ecp_gen_keypair(&grp, &d, &q, rng, rng_ctx) == 0) {
        ok = finish_keypair(&grp, &d, &q, curve, out_pair);
    }

    mbedtls_ecp_point_free(&q);
    mbedtls_mpi_free(&d);
    mbedtls_ecp_group_free(&grp);

    if (!ok && out_pair != NULL) {
        memset(out_pair, 0, sizeof(*out_pair));
    }
    return ok;
}

int rc_ecdh_keypair_from_private(unsigned curve, const uint8_t *private_key, size_t private_key_length,
                                 rc_rng_fn rng, void *rng_ctx, rc_ecdh_keypair *out_pair)
{
    mbedtls_ecp_group grp;
    mbedtls_mpi d;
    mbedtls_ecp_point q;
    mbedtls_ecp_group_id id = group_id_for(curve);
    int ok = 0;

    if (out_pair == NULL || private_key == NULL || rng == NULL || id == MBEDTLS_ECP_DP_NONE) {
        return 0;
    }
    if (private_key_length == 0u || private_key_length > RC_ECDH_PRIVATE_MAX) {
        return 0;
    }

    mbedtls_ecp_group_init(&grp);
    mbedtls_mpi_init(&d);
    mbedtls_ecp_point_init(&q);

    if (mbedtls_ecp_group_load(&grp, id) == 0
        && mbedtls_mpi_read_binary(&d, private_key, private_key_length) == 0
        /* 0 < d < N. mbedtls_ecp_mul would reject an out-of-range scalar anyway, but checking here says
         * which input was wrong rather than failing inside the multiply. */
        && mbedtls_mpi_cmp_int(&d, 0) > 0
        && mbedtls_mpi_cmp_mpi(&d, &grp.N) < 0
        && mbedtls_ecp_mul(&grp, &q, &d, &grp.G, rng, rng_ctx) == 0) {
        ok = finish_keypair(&grp, &d, &q, curve, out_pair);
    }

    mbedtls_ecp_point_free(&q);
    mbedtls_mpi_free(&d);
    mbedtls_ecp_group_free(&grp);

    if (!ok) {
        memset(out_pair, 0, sizeof(*out_pair));
    }
    return ok;
}

int rc_ecdh_check_peer_point(unsigned curve, const uint8_t *point, size_t length)
{
    mbedtls_ecp_group grp;
    mbedtls_ecp_point pt;
    mbedtls_ecp_group_id id;
    int rc;
    int ok = 0;

    s_last_error_step = RC_ECDH_STEP_NONE;
    s_last_error_code = 0;

    if (point == NULL)
        return ecdh_fail(RC_ECDH_STEP_ARGUMENTS, 0);
    if (rc_ecdh_curve_for_public_key_length(length) != curve)
        return ecdh_fail(RC_ECDH_STEP_CURVE, 0);
    id = group_id_for(curve);
    if (id == MBEDTLS_ECP_DP_NONE)
        return ecdh_fail(RC_ECDH_STEP_CURVE, 0);

    mbedtls_ecp_group_init(&grp);
    mbedtls_ecp_point_init(&pt);

    do {
        rc = mbedtls_ecp_group_load(&grp, id);
        if (rc != 0) { (void)ecdh_fail(RC_ECDH_STEP_GROUP_LOAD, rc); break; }

        rc = mbedtls_ecp_point_read_binary(&grp, &pt, point, length);
        if (rc != 0) { (void)ecdh_fail(RC_ECDH_STEP_PEER_READ, rc); break; }

        rc = mbedtls_ecp_check_pubkey(&grp, &pt);
        if (rc != 0) { (void)ecdh_fail(RC_ECDH_STEP_PEER_CHECK, rc); break; }

        ok = 1;
    } while (0);

    mbedtls_ecp_point_free(&pt);
    mbedtls_ecp_group_free(&grp);
    return ok;
}

int rc_ecdh_derive_shared(const rc_ecdh_keypair *pair,
                          const uint8_t *peer_public_key, size_t peer_public_key_length,
                          rc_rng_fn rng, void *rng_ctx,
                          uint8_t *out_secret, size_t out_secret_size, size_t *out_secret_length)
{
    mbedtls_ecp_group grp;
    mbedtls_mpi d;
    mbedtls_ecp_point peer;
    mbedtls_ecp_point shared;
    mbedtls_ecp_group_id id;
    uint8_t point_buf[RC_ECDH_PUBKEY_MAX];
    size_t secret_length;
    size_t public_length;
    int ok = 0;

    int rc;

    s_last_error_step = RC_ECDH_STEP_NONE;
    s_last_error_code = 0;

    if (pair == NULL || peer_public_key == NULL || out_secret == NULL || rng == NULL) {
        return ecdh_fail(RC_ECDH_STEP_ARGUMENTS, 0);
    }
    /* The wire carries no curve id, so the peer key's length is what identifies its curve - and it has
     * to be the same curve we hold a private scalar on. A mismatch here is a protocol-level
     * disagreement about the negotiated version, not something to coerce. */
    if (rc_ecdh_curve_for_public_key_length(peer_public_key_length) != pair->curve) {
        return ecdh_fail(RC_ECDH_STEP_CURVE, 0);
    }
    id = group_id_for(pair->curve);
    if (id == MBEDTLS_ECP_DP_NONE) {
        return ecdh_fail(RC_ECDH_STEP_CURVE, 0);
    }
    secret_length = rc_ecdh_secret_length(pair->curve);
    public_length = rc_ecdh_public_key_length(pair->curve);
    if (out_secret_size < secret_length || pair->private_key_length == 0u) {
        return ecdh_fail(RC_ECDH_STEP_SIZES, 0);
    }

    mbedtls_ecp_group_init(&grp);
    mbedtls_mpi_init(&d);
    mbedtls_ecp_point_init(&peer);
    mbedtls_ecp_point_init(&shared);

    /*
     * Written as a sequence rather than a single && chain so that the step and the backend's own error
     * code survive. The chain was shorter to read and told a console-side failure nothing it could use.
     */
    do {
        rc = mbedtls_ecp_group_load(&grp, id);
        if (rc != 0) { (void)ecdh_fail(RC_ECDH_STEP_GROUP_LOAD, rc); break; }

        rc = mbedtls_mpi_read_binary(&d, pair->private_key, pair->private_key_length);
        if (rc != 0) { (void)ecdh_fail(RC_ECDH_STEP_PRIVATE_KEY, rc); break; }

        rc = mbedtls_ecp_point_read_binary(&grp, &peer, peer_public_key, peer_public_key_length);
        if (rc != 0) { (void)ecdh_fail(RC_ECDH_STEP_PEER_READ, rc); break; }

        /* Reject a peer point that is not actually on the curve before multiplying by our secret
         * scalar - the invalid-curve attack this defends against leaks the private key one small
         * subgroup at a time, and "it came from the console" is not authentication. */
        rc = mbedtls_ecp_check_pubkey(&grp, &peer);
        if (rc != 0) { (void)ecdh_fail(RC_ECDH_STEP_PEER_CHECK, rc); break; }

        rc = mbedtls_ecp_mul(&grp, &shared, &d, &peer, rng, rng_ctx);
        if (rc != 0) { (void)ecdh_fail(RC_ECDH_STEP_MULTIPLY, rc); break; }

        /* The identity has no affine X to take; write_binary would emit a single 0x00 byte and the
         * width check below would catch it, but saying so explicitly documents the case. */
        if (mbedtls_ecp_is_zero(&shared) != 0) { (void)ecdh_fail(RC_ECDH_STEP_IDENTITY, 0); break; }

        if (!write_uncompressed(&grp, &shared, point_buf, sizeof(point_buf), public_length)) {
            (void)ecdh_fail(RC_ECDH_STEP_WRITE, 0);
            break;
        }

        /* point_buf is 0x04 || X || Y at the curve's full width, leading zeros preserved - so X is
         * simply the secret_length bytes after the prefix. */
        memcpy(out_secret, point_buf + 1, secret_length);
        if (out_secret_length != NULL) {
            *out_secret_length = secret_length;
        }
        ok = 1;
    } while (0);

    mbedtls_platform_zeroize(point_buf, sizeof(point_buf));
    mbedtls_ecp_point_free(&shared);
    mbedtls_ecp_point_free(&peer);
    mbedtls_mpi_free(&d);
    mbedtls_ecp_group_free(&grp);
    return ok;
}

#else /* !RC_CRYPTO_MBEDTLS */

/*
 * No backend. Every entry point fails; see the header for why there is deliberately no fallback
 * implementation rather than a stub that returns something key-shaped.
 */

int rc_ecdh_available(void)
{
    return 0;
}

int rc_ecdh_generate(unsigned curve, rc_rng_fn rng, void *rng_ctx, rc_ecdh_keypair *out_pair)
{
    (void)curve;
    (void)rng;
    (void)rng_ctx;
    if (out_pair != NULL) {
        memset(out_pair, 0, sizeof(*out_pair));
    }
    return 0;
}

int rc_ecdh_keypair_from_private(unsigned curve, const uint8_t *private_key, size_t private_key_length,
                                 rc_rng_fn rng, void *rng_ctx, rc_ecdh_keypair *out_pair)
{
    (void)curve;
    (void)private_key;
    (void)private_key_length;
    (void)rng;
    (void)rng_ctx;
    if (out_pair != NULL) {
        memset(out_pair, 0, sizeof(*out_pair));
    }
    return 0;
}

int rc_ecdh_derive_shared(const rc_ecdh_keypair *pair,
                          const uint8_t *peer_public_key, size_t peer_public_key_length,
                          rc_rng_fn rng, void *rng_ctx,
                          uint8_t *out_secret, size_t out_secret_size, size_t *out_secret_length)
{
    (void)pair;
    (void)peer_public_key;
    (void)peer_public_key_length;
    (void)rng;
    (void)rng_ctx;
    (void)out_secret;
    (void)out_secret_size;
    (void)out_secret_length;
    return 0;
}

#endif /* RC_CRYPTO_MBEDTLS */
