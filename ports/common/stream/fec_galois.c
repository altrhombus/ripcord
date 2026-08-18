#include "fec_galois.h"

#define PRIMITIVE_POLYNOMIAL 0x11d

static uint8_t g_exp[512]; /* exp[i] = generator^i; doubled so Multiply's a+b (< 510) needs no modulo */
static uint8_t g_log[256]; /* log[exp[i]] = i; log[0] is unused */
static int g_ready;

void fec_galois_init(void)
{
    int x = 1;
    int i;

    if (g_ready)
        return;

    for (i = 0; i < 255; i++) {
        g_exp[i] = (uint8_t)x;
        g_log[x] = (uint8_t)i;
        x <<= 1;
        if ((x & 0x100) != 0)
            x ^= PRIMITIVE_POLYNOMIAL;
    }
    for (i = 255; i < 512; i++)
        g_exp[i] = g_exp[i - 255];

    g_ready = 1;
}

uint8_t fec_galois_multiply(uint8_t a, uint8_t b)
{
    if (a == 0 || b == 0)
        return 0;
    return g_exp[(int)g_log[a] + (int)g_log[b]];
}

int fec_galois_divide(uint8_t a, uint8_t b, uint8_t *out)
{
    if (b == 0)
        return 0;
    if (a == 0) {
        *out = 0;
        return 1;
    }
    *out = g_exp[(int)g_log[a] - (int)g_log[b] + 255];
    return 1;
}

int fec_galois_inverse(uint8_t a, uint8_t *out)
{
    return fec_galois_divide(1, a, out);
}
