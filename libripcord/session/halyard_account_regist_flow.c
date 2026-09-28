/* See halyard_account_regist_flow.h - especially on which half of account pairing is here. */
#include "halyard_account_regist_flow.h"

#include "../platform/rc_platform.h"

#include <string.h>

/*
 * The request is the head plus at most HALYARD_ACCOUNT_REGIST_MAX_BODY; the reply is the pairing record
 * behind a short head. Statics rather than stack, for the same handhelds halyard_regist_flow.c serves.
 */
static uint8_t s_request[HALYARD_ACCOUNT_REGIST_MAX_BODY + 512];
static uint8_t s_response[HALYARD_ACCOUNT_REGIST_MAX_RECORD + 512];

int halyard_account_regist_generate_key_material(uint8_t data1[16], uint8_t data2[16])
{
    if (data1 == NULL || data2 == NULL)
        return 0;
    /*
     * Fresh per `commands` call, and two independent draws: .NET's GenerateEphemeralKeyMaterial makes
     * two, and a key equal to its own material would be a pointless weakness to invent.
     */
    if (!rc_random_bytes(data1, 16) || !rc_random_bytes(data2, 16)) {
        memset(data1, 0, 16);
        memset(data2, 0, 16);
        return 0;
    }
    return 1;
}

static int finish(halyard_account_regist_result *out, halyard_account_regist_status status)
{
    out->status = status;
    return status == HALYARD_ACCOUNT_REGIST_OK;
}

int halyard_account_regist_run(const halyard_account_regist_params *params,
                               halyard_account_regist_exchange_fn exchange, void *user,
                               halyard_account_regist_result *out)
{
    uint8_t context[HALYARD_REGISTRATION_CONTEXT_LENGTH];
    uint8_t material[16];
    halyard_account_regist_exchange ex;
    halyard_account_regist_status status;
    size_t request_len;
    size_t response_len = 0;

    if (out == NULL)
        return 0;
    memset(out, 0, sizeof(*out));

    if (params == NULL || exchange == NULL || params->account_id[0] == '\0'
        || params->client_ip[0] == '\0')
        return finish(out, HALYARD_ACCOUNT_REGIST_ERR_BAD_PARAMS);
    if (!halyard_registration_available())
        return finish(out, HALYARD_ACCOUNT_REGIST_ERR_NO_TABLES);

    /* Both fresh, for the reason halyard_regist_flow.c gives: reuse hands an observer related keys. */
    if (!rc_random_bytes(context, sizeof(context)) || !rc_random_bytes(material, sizeof(material)))
        return finish(out, HALYARD_ACCOUNT_REGIST_ERR_NO_RANDOM);

    request_len = halyard_account_regist_build_request(&ex, params->is_ps5, params->seed,
                                                       params->account_id, params->client_ip,
                                                       context, material,
                                                       s_request, sizeof(s_request));
    memset(material, 0, sizeof(material));
    if (request_len == 0) {
        memset(&ex, 0, sizeof(ex));
        /* The tables are present (checked above), so what is left is an input that would not encode. */
        return finish(out, HALYARD_ACCOUNT_REGIST_ERR_BAD_PARAMS);
    }

    if (!exchange(user, s_request, request_len, s_response, sizeof(s_response), &response_len)
        || response_len == 0 || response_len > sizeof(s_response)) {
        memset(&ex, 0, sizeof(ex));
        return finish(out, HALYARD_ACCOUNT_REGIST_ERR_TRANSPORT);
    }

    status = halyard_account_regist_open_response(&ex, s_response, response_len, &out->http_status,
                                                  out->console_reason, sizeof(out->console_reason),
                                                  &out->record);
    memset(&ex, 0, sizeof(ex));
    return finish(out, status);
}
