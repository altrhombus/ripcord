/*
 * ripcord-3ds - the v1 per-packet stream crypto (HalyardPacketCrypto), one channel direction.
 *
 * AES-128-CTR payload encryption plus a 4-byte GMAC, both keyed off the packet's 64-bit key position.
 * Encrypt-then-MAC: the tag is computed over the packet with its tag field (and, for some packet
 * classes, the following key-position field) zeroed, so a receiver verifies the GMAC first and only then
 * AES-CTR-decrypts the payload.
 *
 * Nonces (16 bytes, little-endian addition into the base IV, modulo 2^128):
 *   GMAC nonce = baseIv + keyPos/16
 *   CTR nonce  = baseIv + keyPos/16 + 1        (one block above the GMAC's)
 *
 * The GMAC key rotates; the payload AES key does not:
 *   window 0:  gmacKey = fold(aesKey, baseIv)
 *   window n>=1: gmacKey = fold(gmacKey_window0, ivAdd(baseIv, n * 0xAF6E))     (n = keyPos / 45000)
 *   fold(a, b) = SHA-256(a || b)[0:16] XOR SHA-256(a || b)[16:32]
 *
 * AAD zeroing convention (which bytes of the packet are treated as zero when computing the tag) differs
 * by packet class - this is the one place this layer is genuinely protocol-specific rather than "just
 * GCM": A/V (tag offset 10) and feedback (tag offset 8) zero only the 4-byte tag; control (tag offset 5)
 * and congestion (tag offset 7) zero the tag AND the following 4-byte key-position field. Callers pass
 * `zero_key_pos` explicitly rather than this module guessing from the offset.
 *
 * Checked against cross-language vectors (tests/vectors/stream-crypto.kat's `packetnonce` and
 * `packettag` lines, generated via HalyardPacketCrypto directly) covering the rotation-window boundary,
 * the 32-/64-bit key-position edges, and both AAD-zeroing conventions.
 */
#ifndef STREAM_PACKET_CRYPTO_H
#define STREAM_PACKET_CRYPTO_H

#include <stddef.h>
#include <stdint.h>

#define STREAM_PACKET_CRYPTO_TAG_LENGTH 4
#define STREAM_PACKET_CRYPTO_AV_TAG_OFFSET 10
#define STREAM_PACKET_CRYPTO_FEEDBACK_TAG_OFFSET 8

/* The largest packet this module will compute a tag over. Generous for control/congestion/A-V packets
 * at this protocol's MTU; compute_tag/seal/verify fail closed (return 0) rather than overrun past it. */
#define STREAM_PACKET_CRYPTO_MAX_PACKET 2048

typedef struct {
    uint8_t aes_key[16];
    uint8_t base_iv[16];
    uint8_t gmac_base_key[16]; /* fold(aes_key, base_iv) - the window-0 GMAC key */
} stream_packet_crypto;

void stream_packet_crypto_init(stream_packet_crypto *ctx, const uint8_t aes_key[16], const uint8_t base_iv[16]);

void stream_packet_crypto_gmac_nonce(const stream_packet_crypto *ctx, uint64_t key_pos, uint8_t out[16]);
void stream_packet_crypto_ctr_nonce(const stream_packet_crypto *ctx, uint64_t key_pos, uint8_t out[16]);
void stream_packet_crypto_gmac_key(const stream_packet_crypto *ctx, uint64_t key_pos, uint8_t out[16]);

/* AES-128-CTR encrypt/decrypt `payload` in place (symmetric - the same call both ways). */
void stream_packet_crypto_crypt_payload(const stream_packet_crypto *ctx, uint64_t key_pos,
                                       uint8_t *payload, size_t payload_length);

/*
 * Computes the 4-byte tag over `packet` as it stands on the wire (encrypted payload), with the AAD
 * zeroing described above applied to a scratch copy - `packet` itself is never modified. Returns 1 on
 * success, 0 if packet_length exceeds STREAM_PACKET_CRYPTO_MAX_PACKET.
 */
int stream_packet_crypto_compute_tag(const stream_packet_crypto *ctx, uint64_t key_pos,
                                     const uint8_t *packet, size_t packet_length,
                                     int tag_offset, int zero_key_pos, uint8_t out_tag[4]);

/* Writes the computed tag into packet[tag_offset .. tag_offset+4) in place (sender side). Returns 1 on
 * success, 0 on the same failure condition as compute_tag. */
int stream_packet_crypto_seal(const stream_packet_crypto *ctx, uint64_t key_pos,
                             uint8_t *packet, size_t packet_length, int tag_offset, int zero_key_pos);

/* Verifies packet's on-wire tag (receiver side). Returns 1 if it matches, 0 if it does not or
 * packet_length exceeds STREAM_PACKET_CRYPTO_MAX_PACKET. */
int stream_packet_crypto_verify(const stream_packet_crypto *ctx, uint64_t key_pos,
                                const uint8_t *packet, size_t packet_length, int tag_offset, int zero_key_pos);

#endif /* STREAM_PACKET_CRYPTO_H */
