/* See halyard_account_id.h. */
#include "halyard_account_id.h"

#include <stdint.h>
#include <stdio.h>
#include <string.h>

const char *halyard_account_id_status_text(halyard_account_id_status status)
{
    switch (status) {
    case HALYARD_ACCOUNT_ID_OK:
        return "ok";
    case HALYARD_ACCOUNT_ID_EMPTY:
        return "Nothing was entered";
    case HALYARD_ACCOUNT_ID_BASE64:
        return "That is the base64 form - enter the decimal or hex one instead";
    case HALYARD_ACCOUNT_ID_UNREADABLE:
        return "That is not an account id - it is 19 digits, or 16 hex characters";
    }
    return "?";
}

static int hex_value(char c)
{
    if (c >= '0' && c <= '9')
        return c - '0';
    if (c >= 'a' && c <= 'f')
        return c - 'a' + 10;
    if (c >= 'A' && c <= 'F')
        return c - 'A' + 10;
    return -1;
}

/*
 * STANDARD BASE64 ONLY - not the URL-safe alphabet. Accepting '-' and '_' made "123-456-789" eleven
 * base64 characters, so a number typed with separators in it was reported as "that is the base64 form",
 * which is worse than saying nothing: it sends someone looking for a conversion they do not need.
 */
static int is_base64_char(char c)
{
    return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ||
           c == '+' || c == '/' || c == '=';
}

static int is_space(char c)
{
    return c == ' ' || c == '\t' || c == '\r' || c == '\n';
}

halyard_account_id_status halyard_account_id_normalise(const char *in, char *out, size_t size)
{
    const char *begin;
    const char *end;
    size_t length;
    uint64_t value = 0u;
    int all_decimal = 1;
    int all_hex = 1;
    int all_base64 = 1;
    size_t i;

    if (out == NULL || size < HALYARD_ACCOUNT_ID_TEXT_MAX)
        return HALYARD_ACCOUNT_ID_UNREADABLE;
    out[0] = '\0';
    if (in == NULL)
        return HALYARD_ACCOUNT_ID_EMPTY;

    begin = in;
    while (*begin != '\0' && is_space(*begin))
        begin++;
    end = begin + strlen(begin);
    while (end > begin && is_space(end[-1]))
        end--;
    if (end == begin)
        return HALYARD_ACCOUNT_ID_EMPTY;

    /* An 0x prefix settles the question before any of the counting below. */
    if ((size_t)(end - begin) > 2u && begin[0] == '0' && (begin[1] == 'x' || begin[1] == 'X')) {
        begin += 2;
        if ((size_t)(end - begin) > 16u)
            return HALYARD_ACCOUNT_ID_UNREADABLE;
        for (; begin < end; begin++) {
            int digit = hex_value(*begin);

            if (digit < 0)
                return HALYARD_ACCOUNT_ID_UNREADABLE;
            value = (value << 4) | (uint64_t)digit;
        }
        if (value == 0u)
            return HALYARD_ACCOUNT_ID_UNREADABLE;
        (void)snprintf(out, size, "%llu", (unsigned long long)value);
        return HALYARD_ACCOUNT_ID_OK;
    }

    length = (size_t)(end - begin);
    for (i = 0; i < length; i++) {
        char c = begin[i];

        if (c < '0' || c > '9')
            all_decimal = 0;
        if (hex_value(c) < 0)
            all_hex = 0;
        if (!is_base64_char(c))
            all_base64 = 0;
    }

    /*
     * DECIMAL WINS A TIE, and sixteen digits is the tie: it is a valid account id written out and a
     * valid hex string at the same time. Decimal is the form the wire wants, the form this port stores
     * and the form every id it has seen is quoted in, so reading it the other way would be choosing the
     * unlikely meaning of an ambiguous input.
     */
    if (all_decimal) {
        if (length > 20u)
            return HALYARD_ACCOUNT_ID_UNREADABLE;
        for (i = 0; i < length; i++) {
            uint64_t digit = (uint64_t)(begin[i] - '0');

            /* Overflow is refused rather than wrapped: a wrapped id is a plausible-looking wrong
             * number, which is the one outcome this whole file exists to prevent. */
            if (value > (UINT64_MAX - digit) / 10u)
                return HALYARD_ACCOUNT_ID_UNREADABLE;
            value = value * 10u + digit;
        }
        if (value == 0u)
            return HALYARD_ACCOUNT_ID_UNREADABLE;
        (void)snprintf(out, size, "%llu", (unsigned long long)value);
        return HALYARD_ACCOUNT_ID_OK;
    }

    /* Sixteen hex characters is a 64-bit value written out in full and cannot be anything else. */
    if (all_hex && length == 16u) {
        for (i = 0; i < length; i++)
            value = (value << 4) | (uint64_t)hex_value(begin[i]);
        if (value == 0u)
            return HALYARD_ACCOUNT_ID_UNREADABLE;
        (void)snprintf(out, size, "%llu", (unsigned long long)value);
        return HALYARD_ACCOUNT_ID_OK;
    }

    /*
     * Eight bytes base64'd is eleven characters, or twelve with the padding. Recognised so it can be
     * NAMED - see the header on why it is not decoded.
     */
    if (all_base64 && (length == 11u || length == 12u))
        return HALYARD_ACCOUNT_ID_BASE64;

    return HALYARD_ACCOUNT_ID_UNREADABLE;
}
