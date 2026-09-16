/* See halyard_regist_flow.h - especially on why every failure here has a name. */
#include "halyard_regist_flow.h"

#include "halyard_control_arm.h"
#include "halyard_registration.h"
#include "halyard_v1.h"
#include "rc_platform.h"
#include "rc_tcp.h"

#include <string.h>

#include <unistd.h>

/*
 * The request is the 0x1e0 context plus an encrypted field of a couple of hundred bytes; the reply is
 * the pairing record, which is smaller still. Both are bounded and small, so they are stack-free
 * statics rather than allocations - a handheld or a console with no heap to spare gets the same code.
 */
static uint8_t s_request[HALYARD_REGISTRATION_CONTEXT_LENGTH + 512];
static uint8_t s_response[1024];

const char *halyard_regist_status_text(halyard_regist_status status)
{
    switch (status) {
    case HALYARD_REGIST_OK:               return "registered";
    case HALYARD_REGIST_ERR_NO_TABLES:    return "this build cannot register";
    case HALYARD_REGIST_ERR_BAD_PARAMS:   return "missing console address, account or PIN";
    case HALYARD_REGIST_ERR_NO_RANDOM:    return "could not generate random material";
    case HALYARD_REGIST_ERR_CONNECT:      return "could not reach the console";
    case HALYARD_REGIST_ERR_SEND:         return "the connection dropped while sending";
    case HALYARD_REGIST_ERR_NO_REPLY:     return "the console did not answer";
    case HALYARD_REGIST_ERR_MALFORMED:    return "the console's answer was not understood";
    case HALYARD_REGIST_ERR_REFUSED:      return "the console refused the registration";
    case HALYARD_REGIST_ERR_BAD_RECORD:   return "wrong PIN, or the console changed its mind";
    default:                              return "registration failed";
    }
}

static int finish(halyard_regist_result *out, halyard_regist_status status)
{
    out->status = status;
    return status == HALYARD_REGIST_OK;
}

int halyard_regist_run(const halyard_regist_params *params, halyard_regist_result *out)
{
    uint8_t context[HALYARD_REGISTRATION_CONTEXT_LENGTH];
    uint8_t material[16];
    uint8_t wrapped[16];
    halyard_control_field field;
    char field_plain[256];
    size_t field_len, body_len, request_len;
    int sock;
    ssize_t n;

    if (out == NULL)
        return 0;
    memset(out, 0, sizeof(*out));

    if (params == NULL || params->host[0] == '\0' || params->account_id[0] == '\0'
        || params->client_ip[0] == '\0')
        return finish(out, HALYARD_REGIST_ERR_BAD_PARAMS);
    if (!halyard_registration_available())
        return finish(out, HALYARD_REGIST_ERR_NO_TABLES);

    /*
     * THE CONTEXT AND THE MATERIAL ARE BOTH FRESHLY RANDOM AND BOTH MATTER. The context selects the
     * table entry the key comes from and carries the material; the material derives the field IV.
     * Reusing either across registrations would hand an observer two ciphertexts under related keys.
     */
    if (!rc_random_bytes(context, sizeof(context)) || !rc_random_bytes(material, sizeof(material)))
        return finish(out, HALYARD_REGIST_ERR_NO_RANDOM);

    if (!halyard_registration_wrap_material(params->is_ps5, material, context, sizeof(context), wrapped)
        || !halyard_registration_scatter(wrapped, context, sizeof(context)))
        return finish(out, HALYARD_REGIST_ERR_NO_TABLES);

    /* The key is derived AFTER the material is scattered in: it reads the context as it will be sent. */
    if (!halyard_registration_field_init(&field, params->is_ps5, context, sizeof(context),
                                         params->passcode, material))
        return finish(out, HALYARD_REGIST_ERR_NO_TABLES);

    field_len = halyard_regist_field_plaintext(params->account_id, field_plain, sizeof(field_plain));
    if (field_len == 0)
        return finish(out, HALYARD_REGIST_ERR_BAD_PARAMS);

    /* Body = the context as it will be sent, then the encrypted field. */
    body_len = sizeof(context) + field_len;
    if (body_len > sizeof(s_request))
        return finish(out, HALYARD_REGIST_ERR_BAD_PARAMS);
    memcpy(s_request, context, sizeof(context));
    memcpy(s_request + sizeof(context), field_plain, field_len);
    halyard_control_field_encrypt(&field, HALYARD_REGISTRATION_FIELD_COUNTER,
                                  s_request + sizeof(context), s_request + sizeof(context), field_len);

    /*
     * ARM THE CONSOLE FIRST. Not optional and not a courtesy: a POST from a client the console has not
     * just heard from is refused with the generic 80108bff, and a byte-perfect request rejected for a
     * reason that says nothing is the worst kind of failure to debug. Recorded either way, because
     * "the probe went unanswered" is the first thing worth knowing when the POST is then refused.
     */
    out->saw_search_reply = halyard_control_arm_probe(params->host, params->is_ps5);

    {
        uint8_t body[sizeof(s_request)];

        memcpy(body, s_request, body_len);
        request_len = halyard_regist_build_request(params->is_ps5, params->client_ip, body, body_len,
                                                   s_request, sizeof(s_request));
    }
    if (request_len == 0)
        return finish(out, HALYARD_REGIST_ERR_BAD_PARAMS);

    sock = rc_tcp_connect(params->host, HALYARD_REGIST_PORT);
    if (sock < 0)
        return finish(out, HALYARD_REGIST_ERR_CONNECT);

    if (rc_tcp_send_all(sock, s_request, request_len) != 0) {
        close(sock);
        return finish(out, HALYARD_REGIST_ERR_SEND);
    }

    /*
     * READ UNTIL THE PEER CLOSES. The request says "Connection: close", so the console's own close is
     * the end of the message - there is no content length to trust on the way back, and stopping at the
     * first short read would truncate a record that arrived in two segments.
     */
    {
        size_t total = 0;
        uint64_t deadline = rc_time_ms() + 5000u;

        while (total < sizeof(s_response) && rc_time_ms() < deadline) {
            n = rc_tcp_recv(sock, s_response + total, sizeof(s_response) - total);
            if (n == 0)
                break;              /* the peer closed - the message is complete */
            if (n < 0) {
                rc_sleep_ms(10);
                continue;
            }
            total += (size_t)n;
        }
        close(sock);

        if (total == 0)
            return finish(out, HALYARD_REGIST_ERR_NO_REPLY);

        {
            const uint8_t *resp_body = NULL;
            size_t resp_len = 0;

            if (!halyard_regist_split_response(s_response, total, &out->http_status,
                                               &resp_body, &resp_len,
                                               out->console_reason, sizeof(out->console_reason)))
                return finish(out, HALYARD_REGIST_ERR_MALFORMED);

            if (out->http_status < 200 || out->http_status > 299)
                return finish(out, HALYARD_REGIST_ERR_REFUSED);

            /*
             * The SAME key decrypts the reply. A wrong PIN does not fail here loudly - it produces
             * plausible-looking bytes that are not a pairing record, which is why the status for that
             * says "wrong PIN" rather than "malformed".
             */
            if (resp_len == 0 || resp_len > sizeof(s_response))
                return finish(out, HALYARD_REGIST_ERR_BAD_RECORD);
            {
                uint8_t plain[sizeof(s_response)];

                halyard_control_field_decrypt(&field, HALYARD_REGISTRATION_FIELD_COUNTER,
                                              resp_body, plain, resp_len);
                if (!halyard_regist_parse_record(plain, resp_len, &out->record))
                    return finish(out, HALYARD_REGIST_ERR_BAD_RECORD);
            }
        }
    }

    return finish(out, HALYARD_REGIST_OK);
}
