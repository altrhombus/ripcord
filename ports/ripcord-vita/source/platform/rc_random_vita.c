/* See ports/common/util/rc_random.h for why this hard-fails instead of degrading. Ported from
 * ports/ripcord-3ds/source/util/rc_random.c, which carries the argument in full; the only difference
 * here is which kernel provides the bytes. */

#include "util/rc_random.h"

#include <string.h>

#include <psp2/kernel/rng.h>

/*
 * The Vita needs no init/exit pair. The 3DS reaches PS_GenerateRandomBytes through the `ps` service,
 * which has to be opened with psInit() first and can fail; sceKernelGetRandomNumber is a plain kernel
 * call with no session to establish. The two functions below therefore exist only to satisfy the seam's
 * shape - callers already written against the 3DS call them, and a port that quietly dropped them would
 * make the shared connect flow platform-specific for no reason.
 */

int rc_random_init(void)
{
    return 1;
}

void rc_random_exit(void)
{
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

    /* [X] sceKernelGetRandomNumber returns 0 on success (psp2/kernel/rng.h declares
     * `int sceKernelGetRandomNumber(void *output, SceSize size)`). The documented maximum request is
     * reported as 64 bytes in places; this port's largest single ask is a P-521 private key at 66
     * bytes, which would exceed it. So the loop below is not defensive padding - it is the difference
     * between a working key agreement and a 66-byte buffer with 2 zero bytes on the end.
     *
     * VERIFY ON HARDWARE: whether a >64-byte request fails or silently short-fills. If it short-fills
     * without reporting an error, the chunked loop is the only thing standing between this port and
     * predictable key material, and no test on the host will ever catch it. */
    {
        size_t done = 0;
        while (done < length) {
            size_t chunk = length - done;
            if (chunk > 64u)
                chunk = 64u;
            if (sceKernelGetRandomNumber(out + done, (unsigned int)chunk) < 0) {
                memset(out, 0, length);
                return 0;
            }
            done += chunk;
        }
    }

    return 1;
}

int rc_random_rng_callback(void *ctx, uint8_t *out, size_t length)
{
    (void)ctx;
    /* mbedtls's f_rng convention: 0 on success, non-zero on failure - the inverse of rc_random_bytes. */
    return rc_random_bytes(out, length) ? 0 : -1;
}
