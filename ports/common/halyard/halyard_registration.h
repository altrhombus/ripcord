/*
 * ripcord - v1 registration (PIN pairing) key derivation.
 *
 * Ported from src/Ripcord.Protocol.Halyard.Common/Crypto/V1/HalyardRegistrationKdf.cs, which carries the
 * provenance in full. This file is the portable half: the arithmetic, and nothing about transports.
 *
 * WHAT REGISTRATION IS. Not a static key derivation but a PIN-authenticated key exchange. The client puts
 * a freshly random context in the request body, and ONE 16-byte transport key K protects both the request
 * field and the response that carries the pairing record:
 *
 *     K = registration_table[ context[selector_offset] & 0x1f ]     one of 32 static 16-byte entries
 *     K[12..16] ^= big-endian u32(passcode)                         the PIN folds into the last four
 *
 * The fold is the whole authentication. The console recomputes K from the context it received and the
 * passcode the user read off its own screen, so a client with the wrong PIN derives the wrong key and
 * cannot decrypt the pairing record. The table lookup is obfuscation, not secrecy.
 *
 * AND THE MATERIAL TRAVELS WRAPPED. The 16-byte per-pairing material is client-random and has to reach
 * the console, so it goes inside the request context in wrapped form - a per-byte transform through a
 * second table, scattered to two fixed offsets. The console unwraps it to derive the same field IV.
 *
 * THE TWO FAMILIES DIFFER ONLY IN THEIR TABLES AND ONE BIAS. Both wrap as
 * ((material[i] ^ table[i]) + bias + i); PS5's bias is -0x2d and PS4's is +0x29. Mirroring the control
 * KDF, which also differs between families only in its tables.
 *
 * THE ACCOUNT ("web") ROUTE IS NOT HERE. It uses the same tables with a different transform and supplies
 * its key rather than deriving one from a passcode. A port that has no PSN layer cannot reach it, and
 * half of it would be worse than none.
 */
#ifndef HALYARD_REGISTRATION_H
#define HALYARD_REGISTRATION_H

#include <stddef.h>
#include <stdint.h>

#define HALYARD_REGISTRATION_KEY_LENGTH   16
#define HALYARD_REGISTRATION_ENTRY_COUNT  32

/*
 * Where the wrapped material sits inside the request context. Two ranges, not one: the console reads
 * bytes 0..8 from one offset and 8..16 from another, and the split is the vendor's, not a convenience.
 */
#define HALYARD_REGISTRATION_WRAPPED_LOW   0x191
#define HALYARD_REGISTRATION_WRAPPED_HIGH  0x0c7

/* Whether this build carries the tables at all - gen_constants.py emits them only with --registration. */
int halyard_registration_available(void);

/*
 * The 16-byte transport key, from the transmitted context and the 8-digit PIN. `is_ps5` picks the family.
 * Returns 0 if the tables are absent, the context is too short, or the family's tables were not built in.
 */
int halyard_registration_derive_key(int is_ps5, const uint8_t *context, size_t context_length,
                                    uint32_t passcode, uint8_t out_key[16]);

/*
 * Wrap the per-pairing material for transmission, and its inverse. `context` need only be long enough to
 * hold the selector byte; the caller scatters the result with halyard_registration_scatter.
 */
int halyard_registration_wrap_material(int is_ps5, const uint8_t material[16],
                                       const uint8_t *context, size_t context_length,
                                       uint8_t out_wrapped[16]);
int halyard_registration_unwrap_material(int is_ps5, const uint8_t wrapped[16],
                                         const uint8_t *context, size_t context_length,
                                         uint8_t out_material[16]);

/* Scatter the 16 wrapped bytes into the context at the two offsets above, and gather them back. */
int halyard_registration_scatter(const uint8_t wrapped[16], uint8_t *context, size_t context_length);
int halyard_registration_gather(const uint8_t *context, size_t context_length, uint8_t out_wrapped[16]);

/*
 * THE REQUEST BODY: a 0x1e0-byte random context carrying the wrapped material, followed by the encrypted
 * Client-Type / Np-AccountId field. One transport key protects both directions, so the caller keeps the
 * context and the material to decrypt the reply with.
 *
 * The field cipher is the CONTROL plane's, unchanged - same AES-128-CFB128 over the same IV derivation.
 * Only its three inputs differ: the key comes from the PIN rather than the session KDF, the material is
 * the one wrapped into the context, and the context key is selector_one (PS5) or selector_zero (PS4).
 * That is why there is no registration cipher here, only a way to fill in halyard_control_field.
 */
#define HALYARD_REGISTRATION_CONTEXT_LENGTH 0x1e0
#define HALYARD_REGISTRATION_FIELD_COUNTER  0u

struct halyard_control_field_tag;

/*
 * Fills `field` for a registration exchange. `material` is the 16 random bytes the caller also wrapped
 * into the context. Returns 0 if the tables are absent or the context is too short.
 */
int halyard_registration_field_init(struct halyard_control_field_tag *field, int is_ps5,
                                    const uint8_t *context, size_t context_length,
                                    uint32_t passcode, const uint8_t material[16]);

#endif /* HALYARD_REGISTRATION_H */
