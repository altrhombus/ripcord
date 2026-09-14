/* See takion_session_negotiator.h for where this sits in the connect sequence and what handshakeKey
 * actually is. */

#include "takion_session_negotiator.h"

#include "../crypto/rc_crypto.h"
#include "../stream/stream_key_schedule.h"

#include <string.h>

/* The vendor sends four zero bytes for the unused required `encryptedKey` field. */
static const uint8_t kUnusedEncryptedKey[4] = { 0, 0, 0, 0 };

unsigned takion_session_curve_for_version(uint32_t protocol_version)
{
    return (protocol_version >= 0x0du && protocol_version <= 0x11u)
               ? RC_ECDH_CURVE_P521
               : RC_ECDH_CURVE_P256;
}

/*
 * Constant-time compare. A signature check that bails on the first differing byte leaks, through timing,
 * how much of a forged tag was right - which is enough to build a valid one a byte at a time. memcmp is
 * exactly that check, which is why it is not used here.
 */
static int fixed_time_equal(const uint8_t *a, const uint8_t *b, size_t length)
{
    uint8_t difference = 0;
    size_t i;

    for (i = 0; i < length; i++) {
        difference = (uint8_t)(difference | (uint8_t)(a[i] ^ b[i]));
    }
    return difference == 0;
}

size_t takion_session_negotiator_begin(takion_session_negotiator *ctx,
                                       uint32_t protocol_version,
                                       const uint8_t handshake_key[16],
                                       const char *launch_spec, size_t launch_spec_length,
                                       rc_rng_fn rng, void *rng_ctx,
                                       uint8_t *out_request, size_t out_request_size)
{
    takion_session_request request;
    uint8_t signature[RC_SHA256_DIGEST_SIZE];

    if (ctx == NULL || handshake_key == NULL || launch_spec == NULL || rng == NULL
        || out_request == NULL) {
        return 0;
    }

    memset(ctx, 0, sizeof(*ctx));
    ctx->curve = takion_session_curve_for_version(protocol_version);
    memcpy(ctx->handshake_key, handshake_key, sizeof(ctx->handshake_key));

    if (!rc_ecdh_generate(ctx->curve, rng, rng_ctx, &ctx->local_pair)) {
        memset(ctx, 0, sizeof(*ctx));
        return 0;
    }

    rc_hmac_sha256(ctx->handshake_key, sizeof(ctx->handshake_key),
                   ctx->local_pair.public_key, ctx->local_pair.public_key_length,
                   signature);

    memset(&request, 0, sizeof(request));
    request.client_version = protocol_version;
    request.session_key = TAKION_SESSION_KEY;
    request.session_key_length = strlen(TAKION_SESSION_KEY);
    request.launch_spec_json = launch_spec;
    request.launch_spec_json_length = launch_spec_length;
    request.encrypted_key = kUnusedEncryptedKey;
    request.encrypted_key_length = sizeof(kUnusedEncryptedKey);
    request.ecdh_public_key = ctx->local_pair.public_key;
    request.ecdh_public_key_length = ctx->local_pair.public_key_length;
    request.ecdh_signature = signature;
    request.ecdh_signature_length = sizeof(signature);

    return takion_control_build_session_request(&request, out_request, out_request_size);
}

int takion_session_negotiator_accept_reply(takion_session_negotiator *ctx,
                                           const uint8_t *reply, size_t reply_length,
                                           rc_rng_fn rng, void *rng_ctx)
{
    takion_session_reply parsed;
    uint8_t expected_signature[RC_SHA256_DIGEST_SIZE];
    uint8_t shared[RC_ECDH_SECRET_MAX];
    size_t shared_length = 0;
    int ok = 0;

    if (ctx == NULL || reply == NULL || rng == NULL) {
        if (ctx != NULL)
            ctx->last_reject_reason = TAKION_SESSION_REJECT_ARGUMENTS;
        return 0;
    }
    ctx->established = 0;
    ctx->last_reject_reason = TAKION_SESSION_REJECT_NONE;

    if (!takion_control_parse_session_reply(reply, reply_length, &parsed)) {
        ctx->last_reject_reason = TAKION_SESSION_REJECT_PARSE;
        return 0;
    }
    /* The console telling us our version is unacceptable is a clean, diagnosable failure - and it will
     * not have sent usable key material alongside that refusal. */
    if (!parsed.version_accepted) {
        ctx->last_reject_reason = TAKION_SESSION_REJECT_VERSION;
        return 0;
    }
    if (!parsed.has_ecdh) {
        ctx->last_reject_reason = TAKION_SESSION_REJECT_NO_ECDH;
        return 0;
    }
    if (parsed.ecdh_signature_length != sizeof(expected_signature)) {
        ctx->last_reject_reason = TAKION_SESSION_REJECT_SIG_LENGTH;
        return 0;
    }

    /* Authenticate the peer's public key BEFORE doing anything with it. */
    rc_hmac_sha256(ctx->handshake_key, sizeof(ctx->handshake_key),
                   parsed.ecdh_public_key, parsed.ecdh_public_key_length,
                   expected_signature);
    if (!fixed_time_equal(expected_signature, parsed.ecdh_signature, sizeof(expected_signature))) {
        ctx->last_reject_reason = TAKION_SESSION_REJECT_SIGNATURE;
        return 0;
    }

    /*
     * Record what arrived before trying to use it, and separate a curve disagreement from a derivation
     * that failed for any other reason. Both refusals look identical from outside, and they want
     * completely different things looked at: the first is a negotiation fault several steps earlier,
     * the second is the crypto itself.
     */
    ctx->last_peer_key_length = parsed.ecdh_public_key_length;
    ctx->last_peer_key_prefix = (parsed.ecdh_public_key_length > 0u) ? parsed.ecdh_public_key[0] : 0u;
    if (rc_ecdh_curve_for_public_key_length(parsed.ecdh_public_key_length) != ctx->curve) {
        ctx->last_reject_reason = TAKION_SESSION_REJECT_CURVE;
        return 0;
    }

    /* rc_ecdh_derive_shared re-checks that the peer key is on our curve and on the curve at all; the
     * signature above only proves whoever sent it knew handshakeKey, not that the point is well-formed. */
    if (rc_ecdh_derive_shared(&ctx->local_pair,
                              parsed.ecdh_public_key, parsed.ecdh_public_key_length,
                              rng, rng_ctx, shared, sizeof(shared), &shared_length)) {
        stream_key_schedule_derive_direction(shared, shared_length, ctx->handshake_key,
                                             STREAM_KEY_SCHEDULE_DIRECTION_CLIENT_TO_SERVER,
                                             ctx->send_aes_key, ctx->send_base_iv);
        stream_key_schedule_derive_direction(shared, shared_length, ctx->handshake_key,
                                             STREAM_KEY_SCHEDULE_DIRECTION_SERVER_TO_CLIENT,
                                             ctx->receive_aes_key, ctx->receive_base_iv);
        ctx->established = 1;
        ok = 1;
    } else {
        ctx->last_reject_reason = TAKION_SESSION_REJECT_DERIVE;
    }

    memset(shared, 0, sizeof(shared));
    return ok;
}

void takion_session_negotiator_reset(takion_session_negotiator *ctx)
{
    if (ctx != NULL) {
        memset(ctx, 0, sizeof(*ctx));
    }
}
