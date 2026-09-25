/*
 * libripcord fuzzing - the STUN Binding Response parser.
 *
 * The only parser in the core that reads bytes from the open internet rather than the LAN or a console:
 * whichever public server answers, or anything that can reach the socket's reflexive address, which is
 * by construction reachable from outside the NAT. Nothing is authenticated.
 *
 * Raw input almost never passes the cookie check, so most of it would stop at the header. Each input is
 * therefore also spliced behind a valid header whose declared length is the input's own, which puts the
 * fuzzer's bytes straight into the attribute walk, where every length it trusts is a read.
 */
#include "../../net/rc_stun.h"

#include <stddef.h>
#include <stdint.h>
#include <string.h>

int LLVMFuzzerTestOneInput(const uint8_t *data, size_t size);

#define FUZZ_STUN_MAX_ATTRIBUTES 2048

static const uint8_t k_id[RC_STUN_CLASSIC_TRANSACTION_ID_SIZE] = {
    0x21, 0x12, 0xA4, 0x42, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12
};

int LLVMFuzzerTestOneInput(const uint8_t *data, size_t size)
{
    uint8_t spliced[RC_STUN_HEADER_SIZE + FUZZ_STUN_MAX_ATTRIBUTES];
    rc_stun_message message;
    rc_stun_address address;
    size_t body = size < FUZZ_STUN_MAX_ATTRIBUTES ? size : FUZZ_STUN_MAX_ATTRIBUTES;

    /* As received: both framings, since each takes a different path through the header. */
    (void)rc_stun_parse(data, size, &message);
    (void)rc_stun_parse_binding_response(data, size, k_id + 4, RC_STUN_TRANSACTION_ID_SIZE, &address);
    (void)rc_stun_parse_binding_response(data, size, k_id, RC_STUN_CLASSIC_TRANSACTION_ID_SIZE, &address);

    /* Behind a header that passes: a Binding Success for k_id, declaring exactly the bytes that follow. */
    spliced[0] = 0x01;
    spliced[1] = 0x01;
    spliced[2] = (uint8_t)(body >> 8);
    spliced[3] = (uint8_t)body;
    memcpy(spliced + 4, k_id, sizeof(k_id));
    if (body > 0)
        memcpy(spliced + RC_STUN_HEADER_SIZE, data, body);
    (void)rc_stun_parse(spliced, RC_STUN_HEADER_SIZE + body, &message);
    (void)rc_stun_parse_binding_response(spliced, RC_STUN_HEADER_SIZE + body, k_id + 4,
                                         RC_STUN_TRANSACTION_ID_SIZE, &address);
    return 0;
}
