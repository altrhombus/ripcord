/* See halyard_regist_message.h, and HalyardRegistrationMessage.cs for the provenance of the wire shape. */
#include "halyard_regist_message.h"

#include "rc_base64.h"
#include "rc_hex.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static char s_path[40];

const char *halyard_regist_path(int is_ps5)
{
    snprintf(s_path, sizeof(s_path), "/sie/%s/rp/sess/rgst", is_ps5 ? "ps5" : "ps4");
    return s_path;
}

/*
 * A numeric PSN id travels base64-encoded as a LITTLE-endian uint64, which is wire-confirmed and is not
 * what anyone would guess: everything else on this wire that is multi-byte is big-endian. A non-numeric
 * id is base64'd as its own UTF-8 bytes.
 */
static size_t encode_account_id(const char *account_id, char *out, size_t out_size)
{
    const char *p;
    uint64_t numeric = 0u;
    int is_numeric;

    if (account_id == NULL || account_id[0] == '\0')
        return 0;

    is_numeric = 1;
    for (p = account_id; *p != '\0'; p++) {
        if (*p < '0' || *p > '9') {
            is_numeric = 0;
            break;
        }
        numeric = numeric * 10u + (uint64_t)(*p - '0');
    }

    if (is_numeric) {
        uint8_t le[8];
        int i;

        for (i = 0; i < 8; i++)
            le[i] = (uint8_t)(numeric >> (i * 8));
        return rc_base64_encode(le, sizeof(le), out, out_size);
    }
    return rc_base64_encode((const uint8_t *)account_id, strlen(account_id), out, out_size);
}

size_t halyard_regist_field_plaintext(const char *account_id, char *buf, size_t buf_size)
{
    char account_b64[64];
    int written;

    if (buf == NULL || encode_account_id(account_id, account_b64, sizeof(account_b64)) == 0)
        return 0;

    written = snprintf(buf, buf_size, "Client-Type: %s\r\nNp-AccountId: %s\r\n",
                       HALYARD_REGIST_CLIENT_TYPE_HEX, account_b64);
    if (written < 0 || (size_t)written >= buf_size)
        return 0;
    return (size_t)written;
}

size_t halyard_regist_build_request(int is_ps5, const char *client_ip,
                                    const uint8_t *encrypted_body, size_t body_length,
                                    uint8_t *buf, size_t buf_size)
{
    int head_len;

    if (client_ip == NULL || client_ip[0] == '\0' || encrypted_body == NULL || buf == NULL)
        return 0;

    /*
     * THE HEADER SET IS THE VENDOR'S AND IS NOT NEGOTIABLE. Uppercase HOST carrying THIS machine's
     * address with no port; no Content-Type; no Np-AccountId header, because the account id travels
     * inside the encrypted body. Adding a reasonable-looking header is a good way to be refused.
     */
    head_len = snprintf((char *)buf, buf_size,
                        "POST %s HTTP/1.1\r\n"
                        "HOST: %s\r\n"
                        "User-Agent: remoteplay Windows\r\n"
                        "Connection: close\r\n"
                        "Content-Length: %u\r\n"
                        "RP-Version: %s\r\n"
                        "\r\n",
                        halyard_regist_path(is_ps5), client_ip,
                        (unsigned)body_length, is_ps5 ? "1.0" : "10.0");
    if (head_len < 0 || (size_t)head_len >= buf_size)
        return 0;
    if ((size_t)head_len + body_length > buf_size)
        return 0;

    memcpy(buf + head_len, encrypted_body, body_length);
    return (size_t)head_len + body_length;
}

/* Case-insensitive prefix match, for header names on a wire that does not promise a case. */
static int header_is(const char *line, size_t line_length, const char *name)
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

int halyard_regist_split_response(const uint8_t *response, size_t response_length,
                                  int *out_status, const uint8_t **out_body, size_t *out_body_length,
                                  char *out_reason, size_t reason_size)
{
    size_t i;
    size_t head_end = 0;
    int found = 0;
    const char *text = (const char *)response;

    if (response == NULL || out_status == NULL || out_body == NULL || out_body_length == NULL)
        return 0;
    if (out_reason != NULL && reason_size > 0)
        out_reason[0] = '\0';

    for (i = 0; i + 3 < response_length; i++) {
        if (text[i] == '\r' && text[i + 1] == '\n' && text[i + 2] == '\r' && text[i + 3] == '\n') {
            head_end = i;
            found = 1;
            break;
        }
    }
    if (!found)
        return 0;

    /* "HTTP/1.1 200 OK" - the status is the second space-separated token. */
    {
        size_t sp = 0;

        while (sp < head_end && text[sp] != ' ')
            sp++;
        if (sp >= head_end)
            return 0;
        *out_status = atoi(text + sp + 1);
    }

    /* Walk the header lines for the console's own explanation. */
    {
        size_t line_start = 0;

        for (i = 0; i < head_end; i++) {
            if (!(text[i] == '\r' && i + 1 < head_end && text[i + 1] == '\n'))
                continue;
            {
                const char *line = text + line_start;
                size_t line_length = i - line_start;

                if (out_reason != NULL && reason_size > 0
                    && header_is(line, line_length, "RP-Application-Reason:")) {
                    size_t skip = strlen("RP-Application-Reason:");
                    size_t n;

                    while (skip < line_length && (line[skip] == ' ' || line[skip] == '\t'))
                        skip++;
                    n = line_length - skip;
                    if (n >= reason_size)
                        n = reason_size - 1u;
                    memcpy(out_reason, line + skip, n);
                    out_reason[n] = '\0';
                }
            }
            line_start = i + 2u;
            i++;
        }
    }

    *out_body = response + head_end + 4u;
    *out_body_length = response_length - (head_end + 4u);
    return 1;
}

/* Find a "Name: value" line and copy its value. Returns 0 when absent. */
static size_t find_field(const uint8_t *body, size_t length, const char *name,
                         char *out, size_t out_size)
{
    const char *text = (const char *)body;
    size_t line_start = 0;
    size_t i;

    for (i = 0; i <= length; i++) {
        if (i != length && text[i] != '\n')
            continue;
        {
            size_t line_length = i - line_start;
            const char *line = text + line_start;

            while (line_length > 0 && (line[line_length - 1] == '\r' || line[line_length - 1] == ' '))
                line_length--;
            if (header_is(line, line_length, name)) {
                size_t skip = strlen(name);
                size_t n;

                while (skip < line_length && (line[skip] == ' ' || line[skip] == '\t'))
                    skip++;
                n = line_length - skip;
                if (n >= out_size)
                    n = out_size - 1u;
                memcpy(out, line + skip, n);
                out[n] = '\0';
                return n;
            }
        }
        line_start = i + 1u;
    }
    return 0;
}

int halyard_regist_parse_record(const uint8_t *decrypted, size_t length, halyard_regist_record *out)
{
    char value[128];
    size_t n;

    if (decrypted == NULL || out == NULL)
        return 0;
    memset(out, 0, sizeof(*out));

    /* The console names the registkey field by its own family, so the reply also says which control-KDF
     * variant a later session must use. */
    if (find_field(decrypted, length, "PS5-RegistKey:", value, sizeof(value)) > 0) {
        out->is_ps5 = 1;
    } else if (find_field(decrypted, length, "PS4-RegistKey:", value, sizeof(value)) > 0) {
        out->is_ps5 = 0;
    } else {
        return 0;
    }

    /*
     * Hex-DECODED, not stored as the string - see the header. The raw bytes are themselves ASCII hex
     * digits, so this halves a 16-character field to the 8 bytes /sess/init re-encodes.
     */
    n = rc_hex_decode(value, out->registration_key, sizeof(out->registration_key));
    if (n == 0)
        return 0;
    out->registration_key_length = n;

    if (find_field(decrypted, length, "RP-Key:", value, sizeof(value)) == 0)
        return 0;
    if (rc_hex_decode(value, out->companion, sizeof(out->companion)) != sizeof(out->companion))
        return 0;

    if (find_field(decrypted, length, "RP-KeyType:", value, sizeof(value)) > 0)
        out->key_type = atoi(value);
    return 1;
}
