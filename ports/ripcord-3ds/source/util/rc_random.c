/* See rc_random.h for why this hard-fails instead of degrading, and why there is no host implementation. */

#include "util/rc_random.h"

#include <string.h>

#ifdef __3DS__

#include <3ds.h>

static int s_ready;

int rc_random_init(void)
{
    if (s_ready)
        return 1;
    if (R_FAILED(psInit()))
        return 0;
    s_ready = 1;
    return 1;
}

void rc_random_exit(void)
{
    if (s_ready) {
        psExit();
        s_ready = 0;
    }
}

int rc_random_bytes(uint8_t *out, size_t length)
{
    if (out == NULL)
        return 0;
    if (length == 0)
        return 1;

    /* Zero first, so that every failure path below leaves an obviously-wrong buffer rather than stack
     * residue that might pass a casual glance at a hexdump. */
    memset(out, 0, length);

    if (!s_ready)
        return 0;
    if (R_FAILED(PS_GenerateRandomBytes(out, length))) {
        memset(out, 0, length);
        return 0;
    }
    return 1;
}

#else /* !__3DS__ */

/*
 * No host implementation, deliberately - see the header. A host build that reaches for real key material
 * should fail to link, not quietly succeed with something weaker than it thinks.
 */

int rc_random_init(void)
{
    return 0;
}

void rc_random_exit(void)
{
}

int rc_random_bytes(uint8_t *out, size_t length)
{
    if (out != NULL && length != 0)
        memset(out, 0, length);
    return 0;
}

#endif /* __3DS__ */

int rc_random_rng_callback(void *ctx, uint8_t *out, size_t length)
{
    (void)ctx;
    /* Inverted on purpose: mbedtls treats 0 as success. */
    return rc_random_bytes(out, length) ? 0 : -1;
}
