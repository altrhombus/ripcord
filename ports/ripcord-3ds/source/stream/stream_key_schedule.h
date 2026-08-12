/*
 * ripcord-3ds - the stream-plane key schedule (spec sec5 / HalyardStreamKeySchedule.DeriveDirection).
 *
 * Turns an ECDH shared secret into a per-direction AES-128 key + 16-byte base IV. This file does NOT do
 * the ECDH itself - the shared secret is a caller-supplied input, computed however the caller obtained
 * it (this port has not implemented ECDH point arithmetic yet; see SETUP.md for why that is a distinct,
 * deliberate gap rather than an oversight). Everything downstream of the shared secret - this KDF, the
 * packet crypto in stream_packet_crypto.h - is complete and tested now, so only the ECDH step itself
 * remains before the stream plane is usable end to end.
 *
 * KDF (SP 800-108 counter mode, a single HMAC-SHA256 block):
 *   info  = 0x01 || direction || 0x00 || handshakeKey(16) || 0x01 0x00   (21 bytes; 0x0100 = 256 output bits)
 *   block = HMAC-SHA256(key = sharedSecret, msg = info)                  (32 bytes)
 *   aesKey = block[0:16] ; baseIv = block[16:32]
 *
 * The shared secret is used directly as the HMAC key with NO reduction, even though it can be 66 bytes
 * (P-521) - HMAC-SHA256 hashes an over-length key down to 32 bytes internally per its own definition, so
 * rc_hmac_sha256 (already handling this generically) needs no special-casing here.
 *
 * Checked against cross-language vectors (tests/vectors/stream-crypto.kat's `streamkdf` lines, generated
 * via HalyardStreamKeySchedule.DeriveDirection directly) covering both the 32-byte (P-256) and 66-byte
 * (P-521) shared-secret lengths this protocol uses.
 */
#ifndef STREAM_KEY_SCHEDULE_H
#define STREAM_KEY_SCHEDULE_H

#include <stddef.h>
#include <stdint.h>

#define STREAM_KEY_SCHEDULE_DIRECTION_CLIENT_TO_SERVER 2u
#define STREAM_KEY_SCHEDULE_DIRECTION_SERVER_TO_CLIENT 3u

/*
 * Derives the 16-byte AES key and 16-byte base IV for one direction. `shared_secret` may be any length
 * (32 bytes for P-256, 66 for P-521 - see the file header on curve selection, which lives one level up
 * since this function doesn't need to know which curve produced its input).
 */
void stream_key_schedule_derive_direction(const uint8_t *shared_secret, size_t shared_secret_length,
                                          const uint8_t handshake_key[16], unsigned direction,
                                          uint8_t out_aes_key[16], uint8_t out_base_iv[16]);

#endif /* STREAM_KEY_SCHEDULE_H */
