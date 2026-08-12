#include "rc_base64.h"

static const char kEncodeTable[] =
    "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

size_t rc_base64_encode(const uint8_t *input, size_t input_length, char *out, size_t out_size)
{
    size_t needed = RC_BASE64_ENCODED_SIZE(input_length);
    size_t i = 0;
    size_t o = 0;
    size_t remainder;

    if (out_size < needed)
        return 0;

    while (i + 3 <= input_length) {
        uint32_t triple = ((uint32_t)input[i] << 16) | ((uint32_t)input[i + 1] << 8) | (uint32_t)input[i + 2];
        out[o++] = kEncodeTable[(triple >> 18) & 0x3f];
        out[o++] = kEncodeTable[(triple >> 12) & 0x3f];
        out[o++] = kEncodeTable[(triple >> 6) & 0x3f];
        out[o++] = kEncodeTable[triple & 0x3f];
        i += 3;
    }

    remainder = input_length - i;
    if (remainder == 1) {
        uint32_t triple = (uint32_t)input[i] << 16;
        out[o++] = kEncodeTable[(triple >> 18) & 0x3f];
        out[o++] = kEncodeTable[(triple >> 12) & 0x3f];
        out[o++] = '=';
        out[o++] = '=';
    } else if (remainder == 2) {
        uint32_t triple = ((uint32_t)input[i] << 16) | ((uint32_t)input[i + 1] << 8);
        out[o++] = kEncodeTable[(triple >> 18) & 0x3f];
        out[o++] = kEncodeTable[(triple >> 12) & 0x3f];
        out[o++] = kEncodeTable[(triple >> 6) & 0x3f];
        out[o++] = '=';
    }

    out[o] = '\0';
    return o;
}

static int decode_char(char c)
{
    if (c >= 'A' && c <= 'Z') return c - 'A';
    if (c >= 'a' && c <= 'z') return c - 'a' + 26;
    if (c >= '0' && c <= '9') return c - '0' + 52;
    if (c == '+') return 62;
    if (c == '/') return 63;
    return -1;
}

size_t rc_base64_decode(const char *input, size_t input_length, uint8_t *out, size_t out_size)
{
    size_t i;
    size_t o = 0;
    int pad = 0;

    if (input_length == 0 || input_length % 4 != 0)
        return (size_t)-1;

    /* Padding is only meaningful in the trailing two characters of the last group; anywhere else '=' is
     * simply not in the alphabet and decode_char() rejects it below. */
    if (input[input_length - 1] == '=')
        pad++;
    if (input[input_length - 2] == '=')
        pad++;

    for (i = 0; i + 4 <= input_length; i += 4) {
        int is_last_group = (i + 4 == input_length);
        int skip_third = is_last_group && pad >= 2;
        int skip_fourth = is_last_group && pad >= 1;
        int c0 = decode_char(input[i]);
        int c1 = decode_char(input[i + 1]);
        int c2 = skip_third ? 0 : decode_char(input[i + 2]);
        int c3 = skip_fourth ? 0 : decode_char(input[i + 3]);
        uint32_t triple;

        if (c0 < 0 || c1 < 0 || (!skip_third && c2 < 0) || (!skip_fourth && c3 < 0))
            return (size_t)-1;

        triple = ((uint32_t)c0 << 18) | ((uint32_t)c1 << 12) | ((uint32_t)c2 << 6) | (uint32_t)c3;

        if (o >= out_size)
            return (size_t)-1;
        out[o++] = (uint8_t)(triple >> 16);

        if (!skip_third) {
            if (o >= out_size)
                return (size_t)-1;
            out[o++] = (uint8_t)(triple >> 8);
        }
        if (!skip_fourth) {
            if (o >= out_size)
                return (size_t)-1;
            out[o++] = (uint8_t)triple;
        }
    }

    return o;
}
