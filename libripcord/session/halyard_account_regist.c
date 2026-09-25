/* See halyard_account_regist.h, and HalyardRegistrationCipher.cs for the provenance. */
#include "halyard_account_regist.h"

#include <string.h>

const char *halyard_account_regist_status_text(halyard_account_regist_status status)
{
    switch (status) {
    case HALYARD_ACCOUNT_REGIST_OK:             return "registered";
    case HALYARD_ACCOUNT_REGIST_ERR_NO_TABLES:  return "this build cannot register";
    case HALYARD_ACCOUNT_REGIST_ERR_BAD_PARAMS: return "missing account or client address";
    case HALYARD_ACCOUNT_REGIST_ERR_NO_RANDOM:  return "could not generate random material";
    case HALYARD_ACCOUNT_REGIST_ERR_TRANSPORT:  return "the console did not answer over the control association";
    case HALYARD_ACCOUNT_REGIST_ERR_MALFORMED:  return "the console's answer was not understood";
    case HALYARD_ACCOUNT_REGIST_ERR_REFUSED:    return "the console refused the registration";
    case HALYARD_ACCOUNT_REGIST_ERR_BAD_RECORD: return "the console's answer did not decrypt - the seed was not the one it sent";
    default:                                    return "registration failed";
    }
}

size_t halyard_account_regist_build_body(halyard_account_regist_exchange *exchange, int is_ps5,
                                         const uint8_t seed[16], const char *account_id,
                                         const uint8_t random_context[HALYARD_REGISTRATION_CONTEXT_LENGTH],
                                         const uint8_t random_material[16],
                                         uint8_t *body, size_t body_size)
{
    char field_plain[HALYARD_ACCOUNT_REGIST_MAX_FIELD];
    uint8_t wrapped[16];
    size_t field_len;

    if (exchange == NULL || seed == NULL || account_id == NULL || random_context == NULL
        || random_material == NULL || body == NULL)
        return 0;
    memset(exchange, 0, sizeof(*exchange));
    exchange->is_ps5 = is_ps5 ? 1 : 0;

    memcpy(exchange->context, random_context, sizeof(exchange->context));
    memcpy(exchange->material, random_material, sizeof(exchange->material));

    /*
     * Wrap with the ACCOUNT transform, then scatter - and only then derive the key, because the key's
     * selector byte is read from the context as it will be sent. Same order as BuildAccountRequest.
     */
    if (!halyard_registration_wrap_account_material(exchange->is_ps5, exchange->material,
                                                    exchange->context, sizeof(exchange->context), wrapped)
        || !halyard_registration_scatter(wrapped, exchange->context, sizeof(exchange->context))
        || !halyard_registration_account_field_init(&exchange->field, exchange->is_ps5,
                                                    exchange->context, sizeof(exchange->context),
                                                    seed, exchange->material))
        return 0;

    field_len = halyard_regist_field_plaintext(account_id, field_plain, sizeof(field_plain));
    if (field_len == 0 || sizeof(exchange->context) + field_len > body_size)
        return 0;

    memcpy(body, exchange->context, sizeof(exchange->context));
    halyard_control_field_encrypt(&exchange->field, HALYARD_REGISTRATION_FIELD_COUNTER,
                                  (const uint8_t *)field_plain, body + sizeof(exchange->context), field_len);
    return sizeof(exchange->context) + field_len;
}

size_t halyard_account_regist_build_request(halyard_account_regist_exchange *exchange, int is_ps5,
                                            const uint8_t seed[16], const char *account_id,
                                            const char *client_ip,
                                            const uint8_t random_context[HALYARD_REGISTRATION_CONTEXT_LENGTH],
                                            const uint8_t random_material[16],
                                            uint8_t *buf, size_t buf_size)
{
    uint8_t body[HALYARD_ACCOUNT_REGIST_MAX_BODY];
    size_t body_len;

    if (client_ip == NULL || client_ip[0] == '\0')
        return 0;
    body_len = halyard_account_regist_build_body(exchange, is_ps5, seed, account_id,
                                                 random_context, random_material, body, sizeof(body));
    if (body_len == 0)
        return 0;
    /* The PIN route's head, unchanged: the two routes differ below the HTTP, never in it. */
    return halyard_regist_build_request(exchange->is_ps5, client_ip, body, body_len, buf, buf_size);
}

/* Case-insensitive match of `name` against the start of a header line. */
static int header_named(const char *line, size_t line_length, const char *name)
{
    size_t n = strlen(name);
    size_t i;

    if (line_length < n)
        return 0;
    for (i = 0; i < n; i++) {
        char a = line[i];
        char b = name[i];

        if (a >= 'A' && a <= 'Z')
            a = (char)(a - 'A' + 'a');
        if (b >= 'A' && b <= 'Z')
            b = (char)(b - 'A' + 'a');
        if (a != b)
            return 0;
    }
    return 1;
}

int halyard_account_regist_response_complete(const uint8_t *message, size_t length)
{
    const char *text = (const char *)message;
    size_t head_end = 0;
    size_t i;
    size_t line_start;
    int found = 0;

    if (message == NULL)
        return 0;
    for (i = 0; i + 3 < length; i++) {
        if (text[i] == '\r' && text[i + 1] == '\n' && text[i + 2] == '\r' && text[i + 3] == '\n') {
            head_end = i;
            found = 1;
            break;
        }
    }
    if (!found)
        return 0;

    /* Walk the head for Content-Length. The first line is the status line and cannot match. */
    line_start = 0;
    for (i = 0; i <= head_end; i++) {
        const char *line;
        size_t line_length;

        if (i != head_end && !(text[i] == '\r' && text[i + 1] == '\n'))
            continue;
        line = text + line_start;
        line_length = i - line_start;
        line_start = i + 2u;

        if (header_named(line, line_length, "Content-Length:")) {
            size_t p = strlen("Content-Length:");
            size_t declared = 0;
            int digits = 0;

            while (p < line_length && (line[p] == ' ' || line[p] == '\t'))
                p++;
            /*
             * Bounded rather than trusted: nine digits cannot overflow a size_t on any target, and a
             * longer number is no reply this core would buffer anyway. Anything that is not a plain
             * number falls through to "no Content-Length", as .NET's int.TryParse does.
             */
            while (p < line_length && line[p] >= '0' && line[p] <= '9' && digits < 9) {
                declared = declared * 10u + (size_t)(line[p] - '0');
                p++;
                digits++;
            }
            while (p < line_length && (line[p] == ' ' || line[p] == '\t'))
                p++;
            if (digits == 0 || p != line_length)
                continue;
            return length - (head_end + 4u) >= declared;
        }
        if (i == head_end)
            break;
        i++;
    }
    return 1;
}

halyard_account_regist_status halyard_account_regist_open_response(
    const halyard_account_regist_exchange *exchange,
    const uint8_t *response, size_t response_length,
    int *out_http_status, char *out_reason, size_t reason_size,
    halyard_regist_record *out_record)
{
    uint8_t plain[HALYARD_ACCOUNT_REGIST_MAX_RECORD];
    const uint8_t *body = NULL;
    size_t body_length = 0;
    int status = 0;

    if (out_http_status != NULL)
        *out_http_status = 0;
    if (out_reason != NULL && reason_size > 0)
        out_reason[0] = '\0';
    if (exchange == NULL || response == NULL || out_record == NULL)
        return HALYARD_ACCOUNT_REGIST_ERR_BAD_PARAMS;
    memset(out_record, 0, sizeof(*out_record));

    if (!halyard_regist_split_response(response, response_length, &status, &body, &body_length,
                                       out_reason, reason_size))
        return HALYARD_ACCOUNT_REGIST_ERR_MALFORMED;
    if (out_http_status != NULL)
        *out_http_status = status;

    /*
     * The console's own reason is the useful half of a refusal - see halyard_regist_split_response.
     * 80108b09 in particular was this route's signature for a material wrapped with the PIN transform.
     */
    if (status < 200 || status > 299)
        return HALYARD_ACCOUNT_REGIST_ERR_REFUSED;

    if (body_length == 0 || body_length > sizeof(plain))
        return HALYARD_ACCOUNT_REGIST_ERR_BAD_RECORD;

    /*
     * The SAME key decrypts the reply. A wrong seed does not fail loudly - CFB has no tag - it produces
     * bytes that are not a pairing record, which is what the parse below finds.
     */
    halyard_control_field_decrypt(&exchange->field, HALYARD_REGISTRATION_FIELD_COUNTER,
                                  body, plain, body_length);
    if (!halyard_regist_parse_record(plain, body_length, out_record)) {
        memset(plain, 0, sizeof(plain));
        return HALYARD_ACCOUNT_REGIST_ERR_BAD_RECORD;
    }
    memset(plain, 0, sizeof(plain));
    return HALYARD_ACCOUNT_REGIST_OK;
}
