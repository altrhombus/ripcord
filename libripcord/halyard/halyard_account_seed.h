/*
 * ripcord - the account ("web"/no-PIN) registration seed: recovering it from what the console publishes.
 *
 * Ported from src/Ripcord.Protocol.Halyard.Common/Crypto/V1/HalyardAccountSeedDelivery.cs, which carries
 * the provenance; the mechanism is written up in docs/protocol/ps5-cloud-session-api.md ("The no-PIN
 * registration seed") and ps5-session-establishment.md.
 *
 * WHAT THE SEED IS. The PIN route folds a passcode the user reads off the console into a table entry.
 * The account route has no passcode; instead the CONSOLE generates a 16-byte seed and delivers it,
 * encrypted, over the account service:
 *
 *   1. The client makes two ephemeral 16-byte values and sends them in the cloud `commands` call as
 *      data1 (the field-cipher KEY) and data2 (the field-cipher MATERIAL).
 *   2. The console field-encrypts the seed under them - the ordinary control-plane field cipher, counter
 *      0 - and publishes the ciphertext as customData1 on the push channel, DOUBLE-base64: the base64 of
 *      an ASCII base64 string.
 *   3. The client decodes twice and decrypts. The seed then keys /sess/rgst - see
 *      halyard_registration_derive_account_key.
 *
 * WHICH HALF IS WHOSE. Steps 1 and 2 are cloud traffic - HTTPS, JSON and a WebSocket - and belong to the
 * client's cloud tier, not to this core (see docs/macos-plan.md, "The split"). What crosses the boundary
 * is plain data: the 32 bytes of data1/data2 the cloud tier sent, and the customData1 string it received.
 * Everything that turns that string into a seed is here, because it is crypto over console-supplied
 * bytes, and the same for every client.
 *
 * NO SOCKETS, NO CLOCK, NO RANDOMNESS. Generating data1/data2 is halyard_account_regist_flow.h's job, so
 * this file can be reached by a fuzzer and a known-answer runner with nothing else linked.
 *
 * Status: [C] against the vendor client's own seed on several captured pairings, [V] live on a PS5
 * (2026-09-03). The PS4 family is [X]: it uses the PS4 registration context key, as the .NET side does,
 * and no PS4 account pairing has been run.
 */
#ifndef HALYARD_ACCOUNT_SEED_H
#define HALYARD_ACCOUNT_SEED_H

#include <stddef.h>
#include <stdint.h>

#define HALYARD_ACCOUNT_SEED_LENGTH 16

/*
 * The largest customData1 ciphertext this accepts. Every captured and live value is 17 bytes for the
 * 16-byte seed (only the first 16 matter), so this is headroom against a console that pads differently,
 * not a size anyone has seen. A bound, because the input is console-supplied and this core does not
 * allocate.
 */
#define HALYARD_ACCOUNT_SEED_MAX_CIPHERTEXT 64

/*
 * Decode customData1's double base64 to the raw ciphertext. `text` need not be NUL-terminated.
 * Returns the ciphertext length, or 0 if either layer is not valid base64 or the result does not fit.
 *
 * Deliberately strict where .NET's Convert.FromBase64String is lenient: that one skips embedded
 * whitespace, this does not. No observed value carries any, and a strict decoder here costs a retry on
 * a malformed frame rather than a wrong seed.
 */
size_t halyard_account_seed_decode_custom_data1(const char *text, size_t text_length,
                                                uint8_t *out, size_t out_size);

/*
 * Recover the seed from the decoded ciphertext. `is_ps5` picks the context key the console encrypted
 * under - the family's registration context key, the same one /sess/rgst uses.
 *
 * Returns 1 on success, 0 if the ciphertext is shorter than the seed. A WRONG data1/data2 is not
 * detectable here - the field cipher has no tag, so it yields sixteen bytes of noise - which is why the
 * caller should count frames that decrypt and frames that do not separately (HalyardAccountPairing does,
 * and the two failures send you to opposite ends of the stack).
 */
int halyard_account_seed_recover(int is_ps5, const uint8_t data1[16], const uint8_t data2[16],
                                 const uint8_t *ciphertext, size_t ciphertext_length,
                                 uint8_t out_seed[16]);

/* Both steps at once, from the customData1 string as the push channel delivered it. Returns 1 or 0. */
int halyard_account_seed_recover_custom_data1(int is_ps5, const uint8_t data1[16], const uint8_t data2[16],
                                              const char *custom_data1, size_t custom_data1_length,
                                              uint8_t out_seed[16]);

/*
 * The console's side, for tests and a console emulation: field-encrypt `seed` under data1/data2, and
 * encode a ciphertext as the double-base64 wire value (NUL-terminated; returns its length, or 0 if
 * `out_size` is too small).
 */
void halyard_account_seed_seal(int is_ps5, const uint8_t data1[16], const uint8_t data2[16],
                               const uint8_t seed[16], uint8_t out_ciphertext[16]);
size_t halyard_account_seed_encode_custom_data1(const uint8_t *ciphertext, size_t ciphertext_length,
                                                char *out, size_t out_size);

#endif /* HALYARD_ACCOUNT_SEED_H */
