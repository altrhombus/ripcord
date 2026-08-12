/*
 * ripcord-3ds - the v1 Halyard control-plane crypto.
 *
 * This is the C counterpart of Ripcord.Protocol.Halyard.Common/Crypto/V1: the control-session KDF, the
 * per-field IV derivation, and the field/streaminfo ciphers built on them - ported from that
 * implementation (docs/protocol/ps5-session-crypto.md is the spec both read) and checked against it with
 * known-answer vectors (tests/vector_runner.c), so a transcription slip shows up as a mismatch instead of
 * shipping quietly.
 *
 * NOTHING HERE IS A SECRET. The lookup tables live in a generated file (halyard_v1_constants.g.c, built
 * from the committed src/Ripcord.Protocol.Halyard/Data/halyard-v1-constants.json) and are the generic
 * interoperability constants described in the repository NOTICE. Per-console and per-account material -
 * registration keys, pairing records, session keys - never appears in this tree; on this port it arrives
 * as a pairing record copied from a desktop Ripcord install.
 */
#ifndef HALYARD_V1_H
#define HALYARD_V1_H

#include <stddef.h>
#include <stdint.h>

#define HALYARD_KEY_LENGTH 16
#define HALYARD_MATERIAL_LENGTH 16
#define HALYARD_IV_LENGTH 16
#define HALYARD_CONTEXT_KEY_LENGTH 16

#define HALYARD_KDF_TABLE_ENTRY_COUNT 32
#define HALYARD_KDF_TABLE_ENTRY_SIZE 16
#define HALYARD_KDF_TABLE_LENGTH (HALYARD_KDF_TABLE_ENTRY_COUNT * HALYARD_KDF_TABLE_ENTRY_SIZE)

/* The console-family discriminator. It selects the KDF variant AND participates in context-key selection,
 * so it is one value threaded through both rather than two independent flags. */
#define HALYARD_VERSION_SELECTOR_PS4 0
#define HALYARD_VERSION_SELECTOR_PS5 1

/* ---- Bundled interoperability constants (generated; see tools/gen_constants.py) ---- */

extern const int halyard_v1_constants_bundled;
extern const int halyard_v1_has_ps4_tables;

extern const uint8_t halyard_v1_kdf_table1[HALYARD_KDF_TABLE_LENGTH];
extern const uint8_t halyard_v1_kdf_table2[HALYARD_KDF_TABLE_LENGTH];
extern const uint8_t halyard_v1_ps4_kdf_table1[HALYARD_KDF_TABLE_LENGTH];
extern const uint8_t halyard_v1_ps4_kdf_table2[HALYARD_KDF_TABLE_LENGTH];

extern const uint8_t halyard_v1_ctx_codec_in_high[HALYARD_CONTEXT_KEY_LENGTH];
extern const uint8_t halyard_v1_ctx_selector_one[HALYARD_CONTEXT_KEY_LENGTH];
extern const uint8_t halyard_v1_ctx_selector_zero[HALYARD_CONTEXT_KEY_LENGTH];
extern const uint8_t halyard_v1_ctx_fallback_zero[HALYARD_CONTEXT_KEY_LENGTH];

/* ---- Control-session key derivation ---- */

/*
 * Derive the control AES key and the IV material from the console's per-connect nonce (RP-Nonce) and the
 * stored per-pairing companion (RP-Key).
 *
 * ROLES, AND THEY ARE NOT INTERCHANGEABLE: `key` is derived from the COMPANION and is the AES-128 key;
 * `material` is derived from the NONCE and is fed into halyard_field_iv_derive - it is not itself an IV.
 * Ripcord shipped these swapped once. It passed every primitive-level test, because those feed key and
 * material in directly, and then produced garbage against a real console. If a test here starts failing,
 * check this before anything else.
 *
 * Returns 0 on success, non-zero if the requested family's tables are not bundled.
 */
int halyard_control_kdf_derive(const uint8_t nonce[HALYARD_KEY_LENGTH],
                               const uint8_t companion[HALYARD_KEY_LENGTH],
                               int version_selector,
                               uint8_t key[HALYARD_KEY_LENGTH],
                               uint8_t material[HALYARD_MATERIAL_LENGTH]);

/* ---- Per-field IV ---- */

/*
 * Select the field context key from the two negotiated selectors. The high-band codec cases (8, 9) are
 * checked first and win regardless of the version selector. Returns a pointer to one of the 16-byte
 * bundled constants; never NULL.
 */
const uint8_t *halyard_field_context_key(int codec_selector, int version_selector);

/*
 * IV = HMAC-SHA256(context_key, material || big-endian uint64 counter)[0:16].
 *
 * The counter is the field's position in the per-connection sequence - one counter that increments once
 * per field-encrypt call, fixing the field order (RP-Auth = 0, RP-Did = 1, RP-OSType = 2,
 * RP-StartBitrate = 3, RP-StreamingType = 4). BIG-endian: a little-endian or 32-bit write passes every
 * small-counter test and then fails on a long session, which is why the vectors include 2^32 and above.
 */
void halyard_field_iv_derive(const uint8_t context_key[HALYARD_CONTEXT_KEY_LENGTH],
                             const uint8_t material[HALYARD_MATERIAL_LENGTH],
                             uint64_t counter,
                             uint8_t iv[HALYARD_IV_LENGTH]);

/* ---- The field and streaminfo ciphers ---- */

typedef struct {
    uint8_t key[HALYARD_KEY_LENGTH];
    uint8_t material[HALYARD_MATERIAL_LENGTH];
    uint8_t context_key[HALYARD_CONTEXT_KEY_LENGTH];
} halyard_control_field;

/* Build the field crypto for a session: run the KDF, then select the context key. Returns 0 on success. */
int halyard_control_field_init(halyard_control_field *ctx,
                               const uint8_t nonce[HALYARD_KEY_LENGTH],
                               const uint8_t companion[HALYARD_KEY_LENGTH],
                               int codec_selector,
                               int version_selector);

/* Encrypt/decrypt one control header field (AES-128-CFB128). `in` and `out` may alias. */
void halyard_control_field_encrypt(const halyard_control_field *ctx, uint64_t counter,
                                   const uint8_t *in, uint8_t *out, size_t length);
void halyard_control_field_decrypt(const halyard_control_field *ctx, uint64_t counter,
                                   const uint8_t *in, uint8_t *out, size_t length);

/* Encrypt/decrypt the streaminfo / launchSpec config (AES-128-OFB, so one call does both directions).
 * Shares the IV derivation with the fields above but not the cipher mode. */
void halyard_control_streaminfo_crypt(const halyard_control_field *ctx, uint64_t counter,
                                      const uint8_t *in, uint8_t *out, size_t length);

#endif /* HALYARD_V1_H */
