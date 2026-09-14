/*
 * ripcord-3ds - Phase 6a: the stream key agreement (spec sec4.2/sec5.1-5.3).
 *
 * Ported from Ripcord.Protocol.Halyard.Takion.TakionSessionNegotiator. This is the exchange that turns a
 * connected Takion transport into an encrypted one: each side sends an ephemeral ECDH public key
 * authenticated by an HMAC over it, and the agreed secret becomes the per-direction AES-128 keys and
 * base IVs that stream_packet_crypto has been waiting for since Phase 5.5.
 *
 * NO SOCKET, DELIBERATELY - the same split halyard_discovery.h and the Takion codecs already use. This
 * module produces the bytes to send and consumes the bytes received; whoever owns the reliable channel
 * moves them. That keeps the whole key agreement host-testable, which matters more here than anywhere
 * else in the port: this is the one exchange where a bug yields a session that connects and then decodes
 * to garbage, with nothing in between to say which side was wrong.
 *
 * WHERE THIS SITS in the connect sequence (HalyardTakionStream.StartAsync):
 *   1. /sess/ctrl control plane is up (Phase 4) - it is what carries handshakeKey to the console, see
 *      below.
 *   2. Takion SCTP handshake completes (Phase 5).
 *   3. The reliable channel is running, NOT yet sealed.
 *   4. >>> this module: SESSION_REQUEST out, SESSION_REPLY in, per-direction keys out <<<
 *   5. GMAC sealing is switched on with those keys; everything after this point is authenticated.
 * Both messages ride TAKION_CHANNEL_SESSION (0x0001) as reliable DATA.
 *
 * ON handshakeKey, WHICH IS EASY TO GET WRONG. It is 16 fresh random bytes generated per session by the
 * CLIENT, and it is NOT derived from the pairing record and NOT related to the control-plane KDF. It
 * reaches the console inside the launch spec - AES-128-OFB-encrypted under the control plane's "out1"
 * key and base64'd (halyard_control_streaminfo_crypt), as the JSON member "handshakeKey" - so by the
 * time this exchange runs, both sides already know it and can each authenticate the other's public key
 * with it. Its only job is to bind the two ECDH public keys to a session the console already agreed to;
 * it contributes no long-term secrecy, and a caller that passes a fixed value instead of random bytes
 * has removed the exchange's only protection against a man in the middle.
 *
 * WHAT THIS DOES NOT DO. It does not build the launch spec (that needs session configuration - codec,
 * resolution, bitrate - and belongs to whoever owns the connect flow), and it does not generate the
 * handshakeKey. Both arrive as caller-supplied inputs, the same way stream_packet_crypto takes an
 * already-derived key rather than reaching for the layer above it.
 */
#ifndef TAKION_SESSION_NEGOTIATOR_H
#define TAKION_SESSION_NEGOTIATOR_H

#include "takion_control_proto.h"

#include "../crypto/rc_ecdh.h"

#include <stddef.h>
#include <stdint.h>

/*
 * The negotiated client version. 17 (0x11) is what the shipped client sends and what this port claims;
 * it also selects P-521 over P-256, since the curve is chosen by version (0x0d-0x11 -> P-521).
 */
#define TAKION_CLIENT_VERSION 17u

/*
 * The largest peer point this code handles: an uncompressed P-521 point, 0x04 || X(66) || Y(66).
 *
 * Stated here rather than pulled in from rc_ecdh.h, because this header is included by callers that do
 * not otherwise need the crypto layer on their include path - and making a diagnostic field drag a new
 * dependency through every port's Makefile is the wrong trade. rc_ecdh.h's RC_ECDH_PUBKEY_MAX is the
 * same number for the same reason; if a larger curve ever appears, both move together.
 */
#define TAKION_SESSION_PEER_KEY_MAX 133u

/*
 * The observed session-key string. It is a literal, not an identifier this port is failing to fill in:
 * the vendor client sends exactly this on a direct LAN session, and the console accepts it. Treated as a
 * protocol constant for the same reason the "PS5" platform tag is.
 */
#define TAKION_SESSION_KEY "InvalidSessionId"

typedef struct {
    /* Populated by takion_session_negotiator_begin(). */
    rc_ecdh_keypair local_pair;
    uint8_t handshake_key[16];
    unsigned curve;

    /* Populated by takion_session_negotiator_accept_reply() on success. */
    uint8_t send_aes_key[16];
    uint8_t send_base_iv[16];
    uint8_t receive_aes_key[16];
    uint8_t receive_base_iv[16];
    int established;
    /*
     * Set by accept_reply on every call - TAKION_SESSION_REJECT_* above. Diagnostic only: nothing in the
     * protocol depends on it, and a caller that ignores it behaves exactly as before.
     */
    int last_reject_reason;

    /*
     * The peer's public key as it arrived, recorded for diagnosis only.
     *
     * A curve disagreement and a derivation that failed for some other reason are the same refusal from
     * outside, and the length is what tells them apart: 65 is P-256, 133 is P-521, anything else is
     * neither. The first byte is kept too - an uncompressed point starts 0x04, and a key that is the
     * right length with the wrong prefix is a different problem again.
     */
    size_t last_peer_key_length;
    uint8_t last_peer_key_prefix;

    /*
     * The peer's point itself, copied here rather than left as a pointer into a buffer the caller may
     * have moved on from. A caller that tried to re-read it later got zeros - the pointer was into the
     * channel's own storage - which produced a dump that contradicted the prefix recorded beside it.
     *
     * A public key for one session. Sized for the largest curve this code knows.
     */
    uint8_t last_peer_key[TAKION_SESSION_PEER_KEY_MAX];

    /* When the refusal was DERIVE: which backend call failed and what it returned. RC_ECDH_STEP_* and
     * the backend's own code, unmapped - see rc_ecdh.h. */
    int last_ecdh_step;
    int last_ecdh_code;

    /* What the derivation was handed, recorded inside it. Compare last_derive_fingerprint against a
     * fingerprint of last_peer_key: equal means the same bytes reached both, and the difference is the
     * environment rather than the data. */
    unsigned long last_derive_fingerprint;
    size_t last_derive_private_length;
    unsigned last_derive_curve;
} takion_session_negotiator;

/* Maps a negotiated protocol version to its curve: 0x0d-0x11 -> P-521, anything else -> P-256. */
unsigned takion_session_curve_for_version(uint32_t protocol_version);

/*
 * Starts a negotiation: generates the ephemeral pair and builds the SESSION_REQUEST to send.
 *
 * `launch_spec` is the already-encrypted, already-base64 launch spec (see the header note on
 * handshakeKey - the plaintext of that same spec is where handshakeKey travels). `handshake_key` must be
 * the same 16 bytes embedded in it.
 *
 * Returns the number of bytes written to `out_request`, or 0 on failure (no ECDH backend, bad curve, RNG
 * refused, buffer too small).
 */
size_t takion_session_negotiator_begin(takion_session_negotiator *ctx,
                                       uint32_t protocol_version,
                                       const uint8_t handshake_key[16],
                                       const char *launch_spec, size_t launch_spec_length,
                                       rc_rng_fn rng, void *rng_ctx,
                                       uint8_t *out_request, size_t out_request_size);

/*
 * Consumes a SESSION_REPLY and, if it checks out, derives all four key/IV values into `ctx`.
 *
 * Returns 1 on success. Returns 0 - leaving ctx->established clear - if the message is not a well-formed
 * SESSION_REPLY, if the console rejected our version, if it carried no ECDH key, if the peer key is not
 * on our curve or not on the curve at all, or if its ecdhSignature does not verify under handshakeKey.
 * That last check is the one that matters: without it, anything able to inject a DATA chunk on this
 * channel could substitute its own public key and read the whole session.
 */
/*
 * WHY accept_reply SAID NO, recorded in the context rather than returned.
 *
 * The function has five distinct ways to refuse a reply and they all used to collapse into a single 0,
 * which on hardware reads as "the console answered and we did not like it" - true, unactionable, and one
 * console round trip per guess. This is a field rather than a new parameter so that existing callers,
 * which are ports this change has no business breaking, keep compiling unchanged.
 *
 * The distinction that matters most is SIGNATURE vs the rest: a signature that does not verify means the
 * console computed it under a different handshakeKey than the one we embedded, which points at the launch
 * spec's encryption rather than at anything in this file.
 */
#define TAKION_SESSION_REJECT_NONE        0  /* no refusal recorded - either accepted, or never called */
#define TAKION_SESSION_REJECT_ARGUMENTS   1
#define TAKION_SESSION_REJECT_PARSE       2  /* not a well-formed SESSION_REPLY at all                 */
#define TAKION_SESSION_REJECT_VERSION     3  /* the console refused our protocol version               */
#define TAKION_SESSION_REJECT_NO_ECDH     4  /* accepted the version, carried no ECDH key              */
#define TAKION_SESSION_REJECT_SIG_LENGTH  5  /* the signature was not 32 bytes                         */
#define TAKION_SESSION_REJECT_SIGNATURE   6  /* it did not verify under handshakeKey                   */
#define TAKION_SESSION_REJECT_CURVE       7  /* the point's LENGTH names a different curve than ours   */
#define TAKION_SESSION_REJECT_DERIVE      8  /* the length agreed and the derivation still failed      */

int takion_session_negotiator_accept_reply(takion_session_negotiator *ctx,
                                           const uint8_t *reply, size_t reply_length,
                                           rc_rng_fn rng, void *rng_ctx);

/* Wipes the key material. Call when the session ends. */
void takion_session_negotiator_reset(takion_session_negotiator *ctx);

#endif /* TAKION_SESSION_NEGOTIATOR_H */
