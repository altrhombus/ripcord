/*
 * ripcord - the structural half of PIN registration: HTTP framing, the request field, and parsing the
 * console's reply into a pairing record.
 *
 * Ported from src/Ripcord.Protocol.Halyard.Common/Crypto/HalyardRegistrationMessage.cs, which carries the
 * provenance. Nothing here is secret and nothing here is crypto - the transport key and the field cipher
 * are halyard_registration.h. That split is the .NET side's and is kept, because it is what lets this
 * half be tested against synthetic fixtures with no tables present at all.
 *
 * THE FLOW, so the order is not something a caller has to infer:
 *
 *   1. UDP search probe (SRC3 for PS5, SRC2 for PS4) to :9295. NOT optional - the console refuses a
 *      registration POST from a client it has not just heard from, with the generic 80108bff, and the
 *      vendor's own client always searches first. halyard_control_arm.h does this.
 *   2. Build a random 0x1e0 context, wrap the material into it, encrypt the field, POST the lot to
 *      /sie/ps5/rp/sess/rgst on TCP 9295.
 *   3. Decrypt the reply with the same key and read the registkey and companion out of it.
 *
 * WHAT THE CONSOLE IS TOLD IT IS TALKING TO is not ours to choose - see halyard_sess_fields.h. The
 * User-Agent here is the same string the session path sends for the same reason.
 */
#ifndef HALYARD_REGIST_MESSAGE_H
#define HALYARD_REGIST_MESSAGE_H

#include <stddef.h>
#include <stdint.h>

/* Registration is HTTP/1.1 over TCP on this port. */
#define HALYARD_REGIST_PORT 9295

/*
 * The Client-Type value: a FIXED 64-hex-character client identifier, not a per-device one. Identical
 * for every client and tied to no account or console, which is why it is in source rather than in the
 * dirty room - see CLAUDE.md, which lists it beside the bundled constants for exactly this reason.
 */
#define HALYARD_REGIST_CLIENT_TYPE_HEX \
    "dabfa2ec873de5839bee8d3f4c0239c4282c07c25c6077a2931afcf0adc0d34f"

/* The /sie/{family}/rp/sess/rgst path. Returns a pointer to a small internal static buffer. */
const char *halyard_regist_path(int is_ps5);

/*
 * The request field's PLAINTEXT: the Client-Type and Np-AccountId lines the field cipher then encrypts.
 * `account_id` is the PSN account id as the console expects it. Returns bytes written, 0 if too small.
 */
size_t halyard_regist_field_plaintext(const char *account_id, char *buf, size_t buf_size);

/*
 * The full HTTP request: head plus the already-encrypted body. `client_ip` is this machine's own LAN
 * address, which goes in an uppercase HOST header with no port - the vendor's shape, not a convention.
 * Returns bytes written, or 0 if the buffer is too small.
 */
size_t halyard_regist_build_request(int is_ps5, const char *client_ip,
                                    const uint8_t *encrypted_body, size_t body_length,
                                    uint8_t *buf, size_t buf_size);

/*
 * Split a raw HTTP response into its status and body, and report the console's own
 * RP-Application-Reason when it sends one.
 *
 * THAT HEADER IS THE CONSOLE EXPLAINING ITSELF and discarding it makes every refusal look alike: a
 * wrong key, the wrong transport for this route, and a plain no all arrive as the same status. It is a
 * hex application code, and it is the difference between a diagnosis and another guess.
 *
 * Returns 1 when the response parsed, whatever the status. `out_reason` is left empty when absent.
 */
int halyard_regist_split_response(const uint8_t *response, size_t response_length,
                                  int *out_status, const uint8_t **out_body, size_t *out_body_length,
                                  char *out_reason, size_t reason_size);

/* What the console sends back, once decrypted. */
typedef struct {
    uint8_t  registration_key[16];
    size_t   registration_key_length;
    uint8_t  companion[16];
    int      key_type;
    int      is_ps5;          /* the console names the field by its own family, so the reply says which */
} halyard_regist_record;

/*
 * Parse the DECRYPTED reply. Returns 1 on success.
 *
 * The RegistKey field is the hex encoding of the raw key bytes, and those raw bytes are THEMSELVES ASCII
 * hex digits - a key reading "1a2b3c4d" arrives as the 16 characters "3161326233633464". Decoding to the
 * raw 8 bytes is what /sess/init re-encodes for RP-Registkey; storing the hex string instead
 * double-encodes it and the console answers 403.
 */
int halyard_regist_parse_record(const uint8_t *decrypted, size_t length, halyard_regist_record *out);

#endif /* HALYARD_REGIST_MESSAGE_H */
