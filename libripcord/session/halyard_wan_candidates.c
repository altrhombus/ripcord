/*
 * libripcord - candidate construction and selection for an internet connect. See halyard_wan_candidates.h.
 *
 * Pure: no sockets, no OS, no allocation, so the fuzzer reaches it with nothing else linked. The one
 * parser here reads addresses that arrived from the network through the cloud tier.
 */
#include "halyard_wan_candidates.h"

#include <string.h>

int halyard_wan_parse_ipv4(const char *text, uint8_t out[4])
{
    uint8_t parsed[4];
    int part;
    size_t i = 0;

    if (text == NULL || out == NULL)
        return 0;

    for (part = 0; part < 4; part++) {
        unsigned value = 0;
        int digits = 0;

        if (part > 0) {
            if (text[i] != '.')
                return 0;
            i++;
        }
        while (text[i] >= '0' && text[i] <= '9') {
            if (++digits > 3)
                return 0;
            value = value * 10u + (unsigned)(text[i] - '0');
            i++;
        }
        if (digits == 0 || value > 255u)
            return 0;
        parsed[part] = (uint8_t)value;
    }
    if (text[i] != '\0')
        return 0;

    memcpy(out, parsed, 4);
    return 1;
}

size_t halyard_wan_format_ipv4(const uint8_t address[4], char *out, size_t out_size)
{
    char buf[16];
    size_t n = 0;
    int part;

    if (address == NULL || out == NULL)
        return 0;

    for (part = 0; part < 4; part++) {
        unsigned value = address[part];

        if (part > 0)
            buf[n++] = '.';
        if (value >= 100)
            buf[n++] = (char)('0' + value / 100);
        if (value >= 10)
            buf[n++] = (char)('0' + (value / 10) % 10);
        buf[n++] = (char)('0' + value % 10);
    }
    if (n + 1 > out_size)
        return 0;
    memcpy(out, buf, n);
    out[n] = '\0';
    return n;
}

int halyard_wan_shares_subnet(const uint8_t address[4], const halyard_wan_interface *interfaces,
                              size_t interface_count)
{
    size_t i;

    if (address == NULL || interfaces == NULL)
        return 0;

    for (i = 0; i < interface_count; i++) {
        const halyard_wan_interface *nic = &interfaces[i];
        int zero_mask = 1;
        int match = 1;
        int b;

        for (b = 0; b < 4; b++) {
            if (nic->netmask[b] != 0)
                zero_mask = 0;
            if ((nic->address[b] & nic->netmask[b]) != (address[b] & nic->netmask[b]))
                match = 0;
        }
        if (!zero_mask && match)
            return 1;
    }
    return 0;
}

/* Copies `text` into a fixed field, refusing rather than truncating: a truncated address is a wrong one. */
static int copy_field(char *dst, size_t dst_size, const char *text)
{
    size_t n = strlen(text);

    if (n + 1 > dst_size)
        return 0;
    memcpy(dst, text, n + 1);
    return 1;
}

static int add(halyard_wan_candidate *out, int count, const char *type, const char *address, uint16_t port)
{
    halyard_wan_candidate *c = &out[count];

    if (!copy_field(c->type, sizeof(c->type), type) || !copy_field(c->address, sizeof(c->address), address))
        return -1;
    c->port = port;
    return count + 1;
}

int halyard_wan_our_candidates(const char *local_address, uint16_t local_port, const char *reflexive_address,
                               uint16_t reflexive_port, halyard_wan_candidate *out, size_t out_capacity)
{
    int count = 0;

    if (out == NULL || out_capacity < HALYARD_WAN_OUR_CANDIDATES_MAX)
        return -1;

    if (reflexive_address != NULL) {
        if ((count = add(out, count, "STUN", reflexive_address, reflexive_port)) < 0)
            return -1;

        /* Only when it differs: a port-preserving NAT makes the guess identical to the mapping. */
        if (local_address != NULL && reflexive_port != local_port
            && (count = add(out, count, "STATIC", reflexive_address, local_port)) < 0)
            return -1;
    }

    if (local_address != NULL && (count = add(out, count, "LOCAL", local_address, local_port)) < 0)
        return -1;

    return count;
}

int halyard_wan_choose_candidate(const halyard_wan_candidate *candidates, size_t candidate_count,
                                 const halyard_wan_interface *interfaces, size_t interface_count,
                                 const char *console_host)
{
    int first_parsed = -1;
    size_t i;

    if (candidates == NULL || candidate_count == 0)
        return -1;

    for (i = 0; i < candidate_count; i++) {
        uint8_t address[4];

        /* A field the cloud tier filled without a terminator is read as not parseable, never overrun. */
        if (memchr(candidates[i].address, '\0', sizeof(candidates[i].address)) == NULL
            || !halyard_wan_parse_ipv4(candidates[i].address, address))
            continue;
        if (interfaces != NULL && halyard_wan_shares_subnet(address, interfaces, interface_count))
            return (int)i;
        if (first_parsed < 0)
            first_parsed = (int)i;
    }
    if (first_parsed >= 0)
        return first_parsed;

    if (console_host != NULL) {
        size_t host_length = strlen(console_host);

        for (i = 0; i < candidate_count; i++) {
            if (host_length < sizeof(candidates[i].address)
                && memcmp(candidates[i].address, console_host, host_length + 1) == 0)
                return (int)i;
        }
    }
    return 0;
}
