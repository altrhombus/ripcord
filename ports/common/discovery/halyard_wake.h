/*
 * ripcord ports - the LAN wake datagram (rest mode -> awake).
 *
 * The companion to halyard_discovery.h, and the thing it could previously only watch happen: the SRCH
 * parser already reports `is_awake == 0` on a console answering "HTTP/1.1 620 Server Standby", and until
 * now no port could do anything about it. A console in rest mode is the ordinary state, not an edge case.
 *
 * Derived from docs/protocol/ps5-local-discovery.md, "LAN wake (rest mode -> awake)", marked [V] from
 * cap49 - a capture taken with the console's internet blocked at the router, which is what establishes
 * that the wake is purely local and needs no cloud, no OAuth and no TLS. That is also why this belongs in
 * ports/common at all, while registration does not (see ports/ripcord-3ds/README.md, "Pairing happens on
 * a PC, not here").
 *
 * THE SEQUENCE, all on the discovery port - 9302 for PS5, 987 for PS4, never the `host-request-port:997`
 * the standby reply advertises, which the spec calls a red herring for wake:
 *
 *     client -> SRCH        broadcast
 *     console <- 620 Server Standby
 *     client -> WAKEUP      no reply, ever
 *     client -> SRCH        poll
 *     console <- 200 Ok     awake
 *
 * NOTHING ACKNOWLEDGES THE WAKEUP. Readiness is observed by going back to halyard_discovery and watching
 * `is_awake` flip, which is the caller's loop rather than this module's job. A port that waits for a
 * reply to the WAKEUP itself waits forever.
 *
 * LINE ENDINGS ARE BARE LINE FEEDS, NOT CRLF - and this is the detail most likely to be "corrected" by
 * someone tidying up. The vendor's SRCH uses CRLF and its WAKEUP does not, in the same exchange, on the
 * same port. The spec records the datagram verbatim for this reason. Do not make them consistent.
 *
 * The single-letter field values (client-type:vr, auth-type:R, model:w, app-type:r) are copied verbatim
 * and their meanings are not separately derived, so they are not editorialised here either.
 */
#ifndef HALYARD_WAKE_H
#define HALYARD_WAKE_H

#include "halyard_discovery.h"

#include <stddef.h>
#include <stdint.h>

/* Decimal int32 is at most "-2147483648" - 11 characters - plus the NUL. */
#define HALYARD_WAKE_CREDENTIAL_MAX 12

/*
 * Derives `user-credential` from the registration key, as text ready to drop into the datagram.
 *
 * `registkey` is the raw key as halyard_pairing_file.c stores it (`record.registkey` /
 * `registkey_length`), which for a real key is 8 bytes of ASCII hex digits. The spec's derivation is
 *
 *     user-credential = int(ascii(hex_decode(RP-Registkey)), 16)
 *
 * so the bytes are read as hex TEXT, not as a number that has already been decoded. A key whose bytes
 * are not all hex digits is a corrupt record and is refused rather than guessed at.
 *
 * Returns 1 on success, 0 on a bad key, a bad length, or a buffer under HALYARD_WAKE_CREDENTIAL_MAX.
 *
 * *** THE FIELD IS A SIGNED 32-BIT DECIMAL, AND THIS DISAGREES WITH THE .NET SIDE. ***
 *
 * ps5-local-discovery.md pins it in the PS4 section, [W] from cap53-cap57: "the PS4's credential is
 * negative, which pins the field as a signed 32-bit decimal (a detail the positive PS5 sample left
 * ambiguous)". Every PS5 sample we hold is below 2^31, where signed and unsigned render identically,
 * which is exactly why the question went unnoticed.
 *
 * HalyardPairingRecord.WakeCredential() renders it UNSIGNED, and WakeClientTests has a test asserting
 * "4294967295" whose comment reads "the credential must be unsigned". That reasoning is about what fits
 * in a C# type; the spec's is about what a real vendor client put on a real wire. This file follows the
 * spec, per the rule halyard_discovery.h states for exactly this situation: if the two disagree, trust
 * the spec over the .NET code, since both were meant to implement the same document.
 *
 * The practical consequence, if the spec is right: a key whose ASCII hex is >= 0x80000000 - roughly half
 * of them - gets a credential the console does not recognise, and the only symptom is a console that
 * does not wake. There is no error to read.
 */
int halyard_wake_credential(const uint8_t *registkey, size_t registkey_length,
                            char *out, size_t out_size);

/*
 * Builds the WAKEUP datagram for `profile` into `buf`, using a credential from the function above.
 * Returns the number of bytes written, or 0 if `buf` is too small - fail closed rather than send a
 * truncated datagram, the same contract halyard_discovery_build_probe has.
 *
 * The payload is pure and there is no socket here on purpose: a port can assert these bytes against the
 * spec without opening anything, which is what tests/discovery_test.c does.
 */
size_t halyard_wake_build_payload(const halyard_discovery_profile *profile, const char *credential,
                                  char *buf, size_t buf_size);

#endif /* HALYARD_WAKE_H */
