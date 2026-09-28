/*
 * libripcord - is the reflexive address usable by a console? See rc_stun.h; the reference is
 * src/Ripcord.Core.Net/StunReflexiveAddress.cs.
 *
 * Its own translation unit, beside rc_stun_client.c, because it calls rc_stun_gather and so reaches the
 * CSPRNG; rc_stun_address_equal would be pure on its own but has no caller that is.
 */
#include "rc_stun.h"

#include <string.h>

#include <netinet/in.h>

int rc_stun_address_equal(const rc_stun_address *a, const rc_stun_address *b)
{
    size_t length;

    if (a == NULL || b == NULL || a->family != b->family || a->port != b->port)
        return 0;
    length = a->family == RC_STUN_FAMILY_IPV6 ? 16u : 4u;
    return memcmp(a->address, b->address, length) == 0;
}

rc_stun_gather_status rc_stun_discover_mapping(int sock, const struct sockaddr_in *servers, size_t server_count,
                                               unsigned attempts_per_server, uint32_t per_attempt_timeout_ms,
                                               rc_stun_mapping *out)
{
    size_t server;
    int have_first = 0;

    if (sock < 0 || servers == NULL || out == NULL)
        return RC_STUN_GATHER_BAD_ARGUMENT;

    for (server = 0; server < server_count; server++) {
        rc_stun_address mapped;
        size_t index;
        rc_stun_gather_status status = rc_stun_gather(sock, &servers[server], 1, attempts_per_server,
                                                      per_attempt_timeout_ms, &mapped, &index);

        /* No answer from this one says nothing about the others: try the next. Anything else is fatal to
         * the socket or the entropy source, and asking another server would not help. */
        if (status == RC_STUN_GATHER_NO_ANSWER)
            continue;
        if (status != RC_STUN_GATHER_OK)
            return status;

        if (!have_first) {
            out->reflexive = mapped;
            out->endpoint_independent = -1;
            have_first = 1;
            continue;
        }

        out->endpoint_independent = rc_stun_address_equal(&out->reflexive, &mapped);
        return RC_STUN_GATHER_OK;
    }

    return have_first ? RC_STUN_GATHER_OK : RC_STUN_GATHER_NO_ANSWER;
}
