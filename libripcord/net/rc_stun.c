/*
 * libripcord - the STUN message layer. See rc_stun.h; the reference is StunMessage.cs.
 *
 * Every length in here comes from a datagram off the internet, before anything is authenticated. The
 * walk below is written so that no offset is ever formed past the end of the region being read: each
 * check compares a length against what REMAINS, rather than adding to an offset and comparing after.
 */
#include "rc_stun.h"

#include <string.h>

#define ATTR_MAPPED_ADDRESS 0x0001u
#define ATTR_XOR_MAPPED_ADDRESS 0x0020u
/* The pre-standardization, comprehension-optional XOR form. Seen in the wild - the .NET side notes the
 * vendor's own relay returns it - and it encodes the same endpoint as 0x0020. */
#define ATTR_XOR_MAPPED_ADDRESS_LEGACY 0x8020u

static uint16_t read_u16(const uint8_t *p)
{
    return (uint16_t)(((unsigned)p[0] << 8) | (unsigned)p[1]);
}

static uint32_t read_u32(const uint8_t *p)
{
    return ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16) | ((uint32_t)p[2] << 8) | (uint32_t)p[3];
}

size_t rc_stun_build_binding_request(const uint8_t *transaction_id, size_t transaction_id_len,
                                     uint8_t *out, size_t out_size)
{
    if (transaction_id == NULL || out == NULL || out_size < RC_STUN_HEADER_SIZE)
        return 0;
    if (transaction_id_len != RC_STUN_TRANSACTION_ID_SIZE
        && transaction_id_len != RC_STUN_CLASSIC_TRANSACTION_ID_SIZE)
        return 0;

    out[0] = (uint8_t)(RC_STUN_BINDING_REQUEST >> 8);
    out[1] = (uint8_t)RC_STUN_BINDING_REQUEST;
    out[2] = 0; /* no attributes on the requests we send */
    out[3] = 0;
    if (transaction_id_len == RC_STUN_TRANSACTION_ID_SIZE) {
        out[4] = (uint8_t)(RC_STUN_MAGIC_COOKIE >> 24);
        out[5] = (uint8_t)(RC_STUN_MAGIC_COOKIE >> 16);
        out[6] = (uint8_t)(RC_STUN_MAGIC_COOKIE >> 8);
        out[7] = (uint8_t)RC_STUN_MAGIC_COOKIE;
        memcpy(out + 8, transaction_id, RC_STUN_TRANSACTION_ID_SIZE);
    } else {
        memcpy(out + 4, transaction_id, RC_STUN_CLASSIC_TRANSACTION_ID_SIZE);
    }
    return RC_STUN_HEADER_SIZE;
}

/*
 * Reads one address attribute's value: 1 reserved byte, 1 family byte, 2 port bytes, then 4 or 16
 * address bytes. `header_id` is the response's bytes 8..20, needed only to unmask an XORed IPv6 address.
 *
 * The XOR mask is the CONSTANT cookie followed by those 12 bytes, as in .NET, not whatever bytes 4..8 of
 * this particular header hold. For an RFC 5389 response the two are the same, because the cookie was
 * checked. For an RFC 3489 one they are not, but a server that predates the cookie defines no XOR
 * attribute to decode, so the choice only matters for a server that is already wrong.
 */
static int read_address(const uint8_t *value, size_t value_len, int xor_form, const uint8_t *header_id,
                        rc_stun_address *out)
{
    uint8_t family;
    size_t address_len;
    uint16_t port;
    size_t i;

    if (value_len < 4)
        return 0;

    family = value[1];
    if (family == RC_STUN_FAMILY_IPV4)
        address_len = 4;
    else if (family == RC_STUN_FAMILY_IPV6)
        address_len = 16;
    else
        return 0;
    /* Longer than needed is accepted, as .NET accepts it: the address is the leading bytes. */
    if (value_len - 4 < address_len)
        return 0;

    port = read_u16(value + 2);
    memset(out, 0, sizeof(*out));
    out->family = family;
    memcpy(out->address, value + 4, address_len);

    if (xor_form) {
        uint8_t mask[16];

        /* Port against the cookie's high half; address against cookie || transaction id, of which IPv4
         * uses only the cookie. Both are obscured so that a NAT rewriting any copy of its own address it
         * finds in a payload cannot corrupt the very address being reported. */
        port = (uint16_t)(port ^ (uint16_t)(RC_STUN_MAGIC_COOKIE >> 16));
        mask[0] = (uint8_t)(RC_STUN_MAGIC_COOKIE >> 24);
        mask[1] = (uint8_t)(RC_STUN_MAGIC_COOKIE >> 16);
        mask[2] = (uint8_t)(RC_STUN_MAGIC_COOKIE >> 8);
        mask[3] = (uint8_t)RC_STUN_MAGIC_COOKIE;
        memcpy(mask + 4, header_id, RC_STUN_TRANSACTION_ID_SIZE);
        for (i = 0; i < address_len; i++)
            out->address[i] = (uint8_t)(out->address[i] ^ mask[i]);
    }
    out->port = port;
    return 1;
}

/* StunMessage.ReadMappedAddress. Returns 1 with *out set if any address attribute was readable. */
static int read_mapped_address(const uint8_t *attributes, size_t length, const uint8_t *header_id,
                               rc_stun_address *out)
{
    size_t offset = 0;
    int have_plain = 0;
    rc_stun_address plain;
    rc_stun_address candidate;

    memset(&plain, 0, sizeof(plain));
    while (length - offset >= 4) {
        uint16_t type = read_u16(attributes + offset);
        size_t value_len = read_u16(attributes + offset + 2);
        size_t value_start = offset + 4;
        size_t padding = (4u - (value_len & 3u)) & 3u;
        const uint8_t *value;

        /* An attribute claiming more than the region holds ends the walk; whatever was already found
         * still counts, as it does in .NET, which `break`s here rather than returning nothing. */
        if (value_len > length - value_start)
            break;
        value = attributes + value_start;

        if (type == ATTR_XOR_MAPPED_ADDRESS || type == ATTR_XOR_MAPPED_ADDRESS_LEGACY) {
            /* Preferred, and taken immediately: it is what a modern server returns, and the form a
             * payload-rewriting NAT cannot damage. An unreadable one is stepped over, not fatal. */
            if (read_address(value, value_len, 1, header_id, &candidate)) {
                *out = candidate;
                return 1;
            }
        } else if (type == ATTR_MAPPED_ADDRESS && !have_plain) {
            /* Kept as a fallback, and the scan continues in case an XOR form follows. */
            if (read_address(value, value_len, 0, header_id, &candidate)) {
                plain = candidate;
                have_plain = 1;
            }
        }

        /* Values are padded to a 4-byte boundary and the padding is not counted in the length. The
         * final attribute's padding may be missing entirely; .NET's loop condition then simply fails,
         * and so does this one, because an offset past the end is never formed. */
        if (padding > length - value_start - value_len)
            break;
        offset = value_start + value_len + padding;
    }

    if (have_plain)
        *out = plain;
    return have_plain;
}

/* The header checks common to both framings, except the cookie. Returns the attribute length or -1. */
static long check_header(const uint8_t *data, size_t length)
{
    uint16_t raw_type;
    size_t attributes_len;

    if (length < RC_STUN_HEADER_SIZE)
        return -1;
    raw_type = read_u16(data);
    attributes_len = read_u16(data + 2);
    /* The two most significant bits of a STUN message type are always zero; that plus the cookie is the
     * standard "is this STUN" test that keeps an arbitrary datagram from being misread. */
    if ((raw_type & 0xC000u) != 0)
        return -1;
    if (attributes_len > length - RC_STUN_HEADER_SIZE)
        return -1;
    return (long)attributes_len;
}

int rc_stun_parse(const uint8_t *data, size_t length, rc_stun_message *out)
{
    long attributes_len;

    if (data == NULL || out == NULL)
        return 0;
    attributes_len = check_header(data, length);
    if (attributes_len < 0 || read_u32(data + 4) != RC_STUN_MAGIC_COOKIE)
        return 0;

    memset(out, 0, sizeof(*out));
    out->type = read_u16(data);
    memcpy(out->transaction_id, data + 8, RC_STUN_TRANSACTION_ID_SIZE);
    out->has_mapped_address = read_mapped_address(data + RC_STUN_HEADER_SIZE, (size_t)attributes_len,
                                                  data + 8, &out->mapped_address);
    return 1;
}

rc_stun_response_status rc_stun_parse_binding_response(const uint8_t *data, size_t length,
                                                       const uint8_t *transaction_id,
                                                       size_t transaction_id_len, rc_stun_address *out)
{
    long attributes_len;
    rc_stun_address mapped;

    if (data == NULL || transaction_id == NULL || out == NULL)
        return RC_STUN_RESPONSE_BAD_ARGUMENT;
    if (transaction_id_len != RC_STUN_TRANSACTION_ID_SIZE
        && transaction_id_len != RC_STUN_CLASSIC_TRANSACTION_ID_SIZE)
        return RC_STUN_RESPONSE_BAD_ARGUMENT;

    attributes_len = check_header(data, length);
    if (attributes_len < 0)
        return RC_STUN_RESPONSE_NOT_STUN;

    /* Modern framing: the cookie makes it STUN, then the 12 bytes after it must be ours. Classic
     * framing has no cookie to test, so the 16-byte id is the whole of both questions and a stranger's
     * reply is indistinguishable from a non-STUN datagram - reported as a mismatch either way. */
    if (transaction_id_len == RC_STUN_TRANSACTION_ID_SIZE) {
        if (read_u32(data + 4) != RC_STUN_MAGIC_COOKIE)
            return RC_STUN_RESPONSE_NOT_STUN;
        if (read_u16(data) != RC_STUN_BINDING_SUCCESS)
            return RC_STUN_RESPONSE_NOT_SUCCESS;
        if (memcmp(data + 8, transaction_id, RC_STUN_TRANSACTION_ID_SIZE) != 0)
            return RC_STUN_RESPONSE_MISMATCH;
    } else {
        if (read_u16(data) != RC_STUN_BINDING_SUCCESS)
            return RC_STUN_RESPONSE_NOT_SUCCESS;
        if (memcmp(data + 4, transaction_id, RC_STUN_CLASSIC_TRANSACTION_ID_SIZE) != 0)
            return RC_STUN_RESPONSE_MISMATCH;
    }

    if (!read_mapped_address(data + RC_STUN_HEADER_SIZE, (size_t)attributes_len, data + 8, &mapped))
        return RC_STUN_RESPONSE_NO_ADDRESS;
    *out = mapped;
    return RC_STUN_RESPONSE_OK;
}
