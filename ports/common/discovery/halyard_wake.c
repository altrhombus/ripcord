/*
 * ripcord ports - the LAN wake datagram. See halyard_wake.h for the spec derivation, for why the lines
 * are LF-terminated, and for the signed-versus-unsigned credential finding.
 */
#include "halyard_wake.h"

#include <stdio.h>

/* Value of one ASCII hex digit, or -1. Not isxdigit()/strtoul(): those accept whitespace, a sign and a
 * 0x prefix, and would quietly turn a corrupt key into a plausible credential. The key is eight fixed
 * characters or it is not a key. */
static int halyard_wake_hex_digit(uint8_t c)
{
    if (c >= '0' && c <= '9') { return (int)(c - '0'); }
    if (c >= 'a' && c <= 'f') { return (int)(c - 'a') + 10; }
    if (c >= 'A' && c <= 'F') { return (int)(c - 'A') + 10; }
    return -1;
}

int halyard_wake_credential(const uint8_t *registkey, size_t registkey_length,
                            char *out, size_t out_size)
{
    uint32_t value = 0u;
    size_t i;
    int written;

    if (registkey == NULL || out == NULL || out_size < HALYARD_WAKE_CREDENTIAL_MAX) {
        return 0;
    }

    /* Eight hex digits is 32 bits exactly. A longer key would silently discard its leading digits, which
     * is the one failure here that produces a valid-looking credential for the wrong console. */
    if (registkey_length == 0u || registkey_length > 8u) {
        return 0;
    }

    for (i = 0u; i < registkey_length; i++) {
        int digit = halyard_wake_hex_digit(registkey[i]);
        if (digit < 0) {
            return 0;
        }
        value = (value << 4) | (uint32_t)digit;
    }

    /*
     * Signed, per the spec - see the header. The cast is written as an explicit two's-complement fold
     * rather than (int32_t)value because converting an out-of-range uint32_t to int32_t is
     * implementation-defined in C99, and this port runs on a big-endian PPC and two little-endian ARMs.
     * Being right by accident on all three is not the same as being right.
     */
    {
        long credential = (value <= 0x7fffffffu)
            ? (long)value
            : -(long)(0xffffffffu - value) - 1L;

        written = snprintf(out, out_size, "%ld", credential);
    }

    if (written < 0 || (size_t)written >= out_size) {
        return 0;
    }
    return 1;
}

size_t halyard_wake_build_payload(const halyard_discovery_profile *profile, const char *credential,
                                  char *buf, size_t buf_size)
{
    int written;

    if (profile == NULL || credential == NULL || buf == NULL) {
        return 0;
    }

    /* Verbatim from ps5-local-discovery.md's WAKEUP block. The \n are deliberate and are not a typo for
     * \r\n; the same document's SRCH block uses CRLF, and both are reproduced as captured. */
    written = snprintf(buf, buf_size,
        "WAKEUP * HTTP/1.1\n"
        "client-type:vr\n"
        "auth-type:R\n"
        "model:w\n"
        "app-type:r\n"
        "user-credential:%s\n"
        "device-discovery-protocol-version:%s\n",
        credential, profile->protocol_version);

    if (written < 0 || (size_t)written >= buf_size) {
        return 0;
    }
    return (size_t)written;
}
