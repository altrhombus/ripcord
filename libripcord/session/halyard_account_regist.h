/*
 * ripcord - the account ("web"/no-PIN) route's /sess/rgst: building the request and opening the reply.
 *
 * The account half of src/Ripcord.Protocol.Halyard.Common/Crypto/V1/HalyardRegistrationCipher.cs
 * (BuildAccountRequest, and DecryptResponse on an exchange that carries a seed), with the response
 * handling HalyardRegistrationClient.RegisterAsync does after the transport returns. The PIN route's
 * equivalents are halyard_regist_message.h and the middle of halyard_regist_flow.c.
 *
 * WHAT DIFFERS FROM THE PIN ROUTE, which is less than it looks:
 *
 *   - the transport key is seed XOR registration_table[selector], with no passcode;
 *   - the material is wrapped with the ACCOUNT transform (halyard_registration.h says why that matters);
 *   - the request travels over UDP 9303, inside the control association the cloud rendezvous set up,
 *     rather than TCP 9295 - a console reached this way answers a 9295 registration with its generic
 *     403 / 80108bff.
 *
 * Everything else - the 0x1e0 context, the Client-Type / Np-AccountId field, the HTTP head, the
 * pairing-record reply - is the PIN route's, reused rather than copied.
 *
 * PURE. The caller supplies the random context and material, so a known-answer vector can pin every
 * byte and a fuzzer can reach the reply parser with no platform linked. halyard_account_regist_flow.h is
 * the layer that draws the randomness and hands the bytes to a transport.
 */
#ifndef HALYARD_ACCOUNT_REGIST_H
#define HALYARD_ACCOUNT_REGIST_H

#include "halyard_regist_message.h"
#include "../halyard/halyard_registration.h"
#include "../halyard/halyard_v1.h"

#include <stddef.h>
#include <stdint.h>

/*
 * The UDP port the account route's control plane runs on - rgst, init and ctrl alike. A required
 * on-wire value (HalyardDatagramRegistrationTransport.Port).
 */
#define HALYARD_ACCOUNT_CONTROL_PORT 9303

/*
 * The field plaintext is two short header lines, so 256 bytes is the PIN flow's bound too; the body is
 * the context plus that. The reply is the encrypted pairing record: 195 and 276 bytes have been seen.
 */
#define HALYARD_ACCOUNT_REGIST_MAX_FIELD 256
#define HALYARD_ACCOUNT_REGIST_MAX_BODY  (HALYARD_REGISTRATION_CONTEXT_LENGTH + HALYARD_ACCOUNT_REGIST_MAX_FIELD)
#define HALYARD_ACCOUNT_REGIST_MAX_RECORD 1024

/*
 * The account route's own statuses. Not the PIN flow's enum, because its words are wrong here: the
 * PIN flow's "bad record" means "wrong PIN", and on this route there is no PIN - a reply that will not
 * parse means the seed, and therefore the key, was not the one the console used.
 */
typedef enum {
    HALYARD_ACCOUNT_REGIST_OK = 0,
    HALYARD_ACCOUNT_REGIST_ERR_NO_TABLES,   /* this build carries no registration constants         */
    HALYARD_ACCOUNT_REGIST_ERR_BAD_PARAMS,  /* no account id or client address, or a buffer too small */
    HALYARD_ACCOUNT_REGIST_ERR_NO_RANDOM,   /* the platform could not produce random bytes           */
    HALYARD_ACCOUNT_REGIST_ERR_TRANSPORT,   /* the 9303 exchange did not return a reply              */
    HALYARD_ACCOUNT_REGIST_ERR_MALFORMED,   /* not an HTTP response this understands                 */
    HALYARD_ACCOUNT_REGIST_ERR_REFUSED,     /* non-2xx; http_status and the console's reason say more */
    HALYARD_ACCOUNT_REGIST_ERR_BAD_RECORD   /* 2xx, but it does not decrypt to a pairing record      */
} halyard_account_regist_status;

/* A short, user-facing sentence for a status. Never NULL. */
const char *halyard_account_regist_status_text(halyard_account_regist_status status);

/*
 * What one exchange must remember between building the request and reading the reply. The same key
 * protects both directions, so the reply cannot be read without this. It holds a seed-derived key:
 * treat it as secret, and clear it when done.
 */
typedef struct {
    int                  is_ps5;
    uint8_t              context[HALYARD_REGISTRATION_CONTEXT_LENGTH];  /* as sent, material scattered in */
    uint8_t              material[HALYARD_MATERIAL_LENGTH];
    halyard_control_field field;
} halyard_account_regist_exchange;

/*
 * Build the request BODY: `random_context` with the account-wrapped `random_material` scattered into it,
 * then the Client-Type / Np-AccountId field encrypted under the seed key. Fills `exchange` for the reply.
 *
 * `seed` is the 16 bytes halyard_account_seed_recover produced. `account_id` is the PSN account id as the
 * cloud tier knows it; it is encoded exactly as the PIN route encodes it.
 *
 * Returns the body length, or 0 if the tables are absent, a parameter is missing, or `body_size` is too
 * small (HALYARD_ACCOUNT_REGIST_MAX_BODY always suffices).
 */
size_t halyard_account_regist_build_body(halyard_account_regist_exchange *exchange, int is_ps5,
                                         const uint8_t seed[16], const char *account_id,
                                         const uint8_t random_context[HALYARD_REGISTRATION_CONTEXT_LENGTH],
                                         const uint8_t random_material[16],
                                         uint8_t *body, size_t body_size);

/*
 * The whole HTTP request: the body above behind the PIN route's head (halyard_regist_build_request),
 * whose HOST header is `client_ip` - this machine's own address toward the console, a plain dotted quad.
 * The console validates it and refuses otherwise, with a 403 that looks exactly like a crypto failure.
 * Returns the request length, or 0.
 */
size_t halyard_account_regist_build_request(halyard_account_regist_exchange *exchange, int is_ps5,
                                            const uint8_t seed[16], const char *account_id,
                                            const char *client_ip,
                                            const uint8_t random_context[HALYARD_REGISTRATION_CONTEXT_LENGTH],
                                            const uint8_t random_material[16],
                                            uint8_t *buf, size_t buf_size);

/*
 * Whether `message` holds a complete HTTP message: the head is terminated, and at least Content-Length
 * bytes follow it (or there is no Content-Length). From HalyardDatagramControlChannel.
 * IsCompleteHttpMessage, because on 9303 a datagram boundary says nothing about a message boundary and
 * the reply arrives as a byte stream the transport accumulates. A Content-Length that is not a number is
 * treated as absent, as .NET's int.TryParse falling through does.
 */
int halyard_account_regist_response_complete(const uint8_t *message, size_t length);

/*
 * Open the console's reply: split the HTTP, require 2xx, decrypt the body under the exchange's key and
 * parse the pairing record. `out_http_status` and `out_reason` (the console's RP-Application-Reason,
 * when it gives one) are filled whenever the head parsed, success or not; either may be NULL.
 *
 * A reply body longer than HALYARD_ACCOUNT_REGIST_MAX_RECORD is refused as a bad record rather than
 * truncated, because the record's fields may sit anywhere in it.
 */
halyard_account_regist_status halyard_account_regist_open_response(
    const halyard_account_regist_exchange *exchange,
    const uint8_t *response, size_t response_length,
    int *out_http_status, char *out_reason, size_t reason_size,
    halyard_regist_record *out_record);

#endif /* HALYARD_ACCOUNT_REGIST_H */
