/*
 * libripcord fuzzing - what the account (no-PIN) route reads from a console.
 *
 * Two surfaces, both console-supplied and both reached before anything is authenticated:
 *
 *   - customData1, the double-base64 seed ciphertext the console publishes over the cloud. The cloud
 *     tier hands it to the core as an opaque string, so its two decodes and the recovery are the core's
 *     to get right whatever arrives;
 *   - the /sess/rgst reply over 9303: the completeness check the transport runs on a partial stream,
 *     then the HTTP split, the decrypt and the pairing-record parse - which the PIN route shares, and
 *     which had no harness before this one.
 *
 * The exchange the reply is read against is built from fixed inputs with the real registration tables
 * (the fuzz core links the registration build of the constants). A build without them falls back to a
 * hand-filled field cipher: the reply path reads nothing else, so the input still reaches the parser.
 */
#include "../../halyard/halyard_account_seed.h"
#include "../../session/halyard_account_regist.h"

#include <stddef.h>
#include <stdint.h>
#include <string.h>

int LLVMFuzzerTestOneInput(const uint8_t *data, size_t size);

int LLVMFuzzerTestOneInput(const uint8_t *data, size_t size)
{
    static const uint8_t d1[16] = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
    static const uint8_t d2[16] = { 16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1 };
    uint8_t ciphertext[HALYARD_ACCOUNT_SEED_MAX_CIPHERTEXT];
    uint8_t seed[16];
    halyard_account_regist_exchange ex;
    halyard_regist_record record;
    char reason[32];
    int http_status;
    size_t n;

    /* customData1, as a string of whatever length arrived. */
    n = halyard_account_seed_decode_custom_data1((const char *)data, size, ciphertext, sizeof(ciphertext));
    if (n > 0) {
        (void)halyard_account_seed_recover(1, d1, d2, ciphertext, n, seed);
        (void)halyard_account_seed_recover(0, d1, d2, ciphertext, n, seed);
    }
    (void)halyard_account_seed_recover_custom_data1(1, d1, d2, (const char *)data, size, seed);

    /* The reply, read with a real exchange built from fixed inputs, or a hand-filled one without tables. */
    {
        static uint8_t context[HALYARD_REGISTRATION_CONTEXT_LENGTH];
        static uint8_t body[HALYARD_ACCOUNT_REGIST_MAX_BODY];

        if (halyard_account_regist_build_body(&ex, 1, d1, "1234567890123456", context, d2, body,
                                              sizeof(body)) == 0) {
            memset(&ex, 0, sizeof(ex));
            ex.is_ps5 = 1;
            memcpy(ex.field.key, d1, sizeof(ex.field.key));
            memcpy(ex.field.material, d2, sizeof(ex.field.material));
            memcpy(ex.field.context_key, d1, sizeof(ex.field.context_key));
        }
    }

    (void)halyard_account_regist_response_complete(data, size);
    (void)halyard_account_regist_open_response(&ex, data, size, &http_status, reason, sizeof(reason), &record);

    /*
     * And the record parser on the input as if it were already decrypted. A fixed AES key means the
     * fuzzer cannot steer what the decrypt above yields, so without this the parser behind it would only
     * ever see noise.
     */
    (void)halyard_regist_parse_record(data, size, &record);
    return 0;
}
