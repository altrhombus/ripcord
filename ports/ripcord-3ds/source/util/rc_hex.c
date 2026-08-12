#include "rc_hex.h"

#include <string.h>

void rc_hex_encode(const uint8_t *input, size_t input_length, char *out)
{
    static const char kDigits[] = "0123456789abcdef";
    size_t i;

    for (i = 0; i < input_length; i++) {
        out[i * 2] = kDigits[input[i] >> 4];
        out[i * 2 + 1] = kDigits[input[i] & 0x0f];
    }
    out[input_length * 2] = '\0';
}

static int nibble(char c)
{
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

size_t rc_hex_decode(const char *text, uint8_t *out, size_t out_size)
{
    size_t length = strlen(text);
    size_t i;

    if (length % 2 != 0 || length / 2 > out_size)
        return (size_t)-1;

    for (i = 0; i < length; i += 2) {
        int hi = nibble(text[i]);
        int lo = nibble(text[i + 1]);
        if (hi < 0 || lo < 0)
            return (size_t)-1;
        out[i / 2] = (uint8_t)((hi << 4) | lo);
    }
    return length / 2;
}
