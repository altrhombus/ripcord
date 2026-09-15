/* See takion_control_proto.h for the wire contract, the field numbers' provenance, and why this is
 * hand-rolled rather than generated. */

#include "takion_control_proto.h"

#include <string.h>

/* Field numbers, straight from docs/protocol/stream_control.proto. Changing one of these is a wire
 * break, not a refactor - the .proto's own header says so, and it is the authority for all three
 * implementations that read it. */
#define F_MSG_TYPE            1u
#define F_MSG_SESSION_REQUEST 2u
#define F_MSG_SESSION_REPLY   3u
#define F_MSG_STREAM_INFO     15u
#define F_MSG_DISCONNECT      10u
#define F_DISC_REASON          1u

#define F_SI_RESOLUTION   1u
#define F_SI_AUDIO_HEADER 2u

#define F_RES_WIDTH        1u
#define F_RES_HEIGHT       2u
#define F_RES_VIDEO_HEADER 3u

#define F_REQ_CLIENT_VERSION 1u

/* ControlMessage's two protocol-version members, and the single field inside each. From
 * docs/protocol/stream_control.proto - ProtocolVersionRequestPayload{repeated uint32 supportedVersions=1}
 * and ProtocolVersionAckPayload{optional uint32 protocolVersion=1}. */
#define F_MSG_PROTOCOL_VERSION_REQUEST 31u
#define F_MSG_PROTOCOL_VERSION_ACK     32u
#define F_PV_SUPPORTED_VERSIONS         1u
#define F_PV_PROTOCOL_VERSION           1u
#define F_REQ_SESSION_KEY    2u
#define F_REQ_LAUNCH_SPEC    3u
#define F_REQ_ENCRYPTED_KEY  4u
#define F_REQ_ECDH_PUBKEY    5u
#define F_REQ_ECDH_SIGNATURE 6u

#define F_RPL_SERVER_VERSION    1u
#define F_RPL_TOKEN             2u
#define F_RPL_ENCKEY_ACCEPTED   3u
#define F_RPL_VERSION_ACCEPTED  4u
#define F_RPL_SESSION_KEY       5u
#define F_RPL_SERVER_VERSTRING  7u
#define F_RPL_ECDH_PUBKEY       8u
#define F_RPL_ECDH_SIGNATURE    9u

#define WT_VARINT 0u
#define WT_64BIT  1u
#define WT_LEN    2u
#define WT_32BIT  5u

/* Required-field presence bits, for the proto2 enforcement the header promises. */
#define RPL_REQ_SERVER_VERSION   0x01u
#define RPL_REQ_TOKEN            0x02u
#define RPL_REQ_ENCKEY_ACCEPTED  0x04u
#define RPL_REQ_VERSION_ACCEPTED 0x08u
#define RPL_REQ_SESSION_KEY      0x10u
#define RPL_REQ_ALL              0x1Fu

/* ---- varint primitives ---- */

static size_t varint_size(uint64_t value)
{
    size_t n = 1;
    while (value >= 0x80u) {
        value >>= 7;
        n++;
    }
    return n;
}

static size_t varint_write(uint64_t value, uint8_t *buf)
{
    size_t n = 0;
    while (value >= 0x80u) {
        buf[n++] = (uint8_t)((value & 0x7Fu) | 0x80u);
        value >>= 7;
    }
    buf[n++] = (uint8_t)value;
    return n;
}

/*
 * Reads a varint at *offset. Returns 1 on success. Rejects anything longer than 10 bytes, and rejects a
 * final byte that would overflow 64 bits - a truncated read here would otherwise silently resynchronise
 * mid-message and parse garbage as valid fields.
 */
static int varint_read(const uint8_t *data, size_t length, size_t *offset, uint64_t *out)
{
    uint64_t value = 0;
    unsigned shift = 0;
    size_t pos = *offset;

    while (pos < length) {
        uint8_t byte = data[pos++];
        if (shift == 63u && (byte & 0x7Fu) > 1u) {
            return 0;
        }
        value |= (uint64_t)(byte & 0x7Fu) << shift;
        if ((byte & 0x80u) == 0u) {
            *offset = pos;
            *out = value;
            return 1;
        }
        shift += 7u;
        if (shift > 63u) {
            return 0;
        }
    }
    return 0;
}

static size_t tag_size(uint32_t field)
{
    return varint_size((uint64_t)field << 3);
}

static size_t tag_write(uint32_t field, unsigned wire_type, uint8_t *buf)
{
    return varint_write(((uint64_t)field << 3) | wire_type, buf);
}

/* Length-delimited field: tag + length prefix + payload. */
static size_t len_field_size(uint32_t field, size_t payload_length)
{
    return tag_size(field) + varint_size((uint64_t)payload_length) + payload_length;
}

static size_t varint_field_size(uint32_t field, uint64_t value)
{
    return tag_size(field) + varint_size(value);
}

/*
 * Advances *offset past the value of a field whose tag has already been consumed. Groups are rejected
 * rather than skipped - see the header for why.
 */
static int skip_value(const uint8_t *data, size_t length, size_t *offset, unsigned wire_type)
{
    uint64_t scratch;

    switch (wire_type) {
    case WT_VARINT:
        return varint_read(data, length, offset, &scratch);
    case WT_64BIT:
        if (length - *offset < 8u) {
            return 0;
        }
        *offset += 8u;
        return 1;
    case WT_32BIT:
        if (length - *offset < 4u) {
            return 0;
        }
        *offset += 4u;
        return 1;
    case WT_LEN:
        if (!varint_read(data, length, offset, &scratch)) {
            return 0;
        }
        if (scratch > (uint64_t)(length - *offset)) {
            return 0;
        }
        *offset += (size_t)scratch;
        return 1;
    default:
        return 0;
    }
}

/*
 * Reads a length-delimited field's bounds without copying. On success *out points into `data`.
 */
static int read_bytes(const uint8_t *data, size_t length, size_t *offset,
                      const uint8_t **out, size_t *out_length)
{
    uint64_t declared;

    if (!varint_read(data, length, offset, &declared)) {
        return 0;
    }
    if (declared > (uint64_t)(length - *offset)) {
        return 0;
    }
    *out = data + *offset;
    *out_length = (size_t)declared;
    *offset += (size_t)declared;
    return 1;
}

/* ---- SESSION_REQUEST ---- */

static size_t session_request_payload_size(const takion_session_request *req)
{
    size_t size = 0;

    size += varint_field_size(F_REQ_CLIENT_VERSION, req->client_version);
    size += len_field_size(F_REQ_SESSION_KEY, req->session_key_length);
    size += len_field_size(F_REQ_LAUNCH_SPEC, req->launch_spec_json_length);
    size += len_field_size(F_REQ_ENCRYPTED_KEY, req->encrypted_key_length);
    if (req->ecdh_public_key != NULL) {
        size += len_field_size(F_REQ_ECDH_PUBKEY, req->ecdh_public_key_length);
    }
    if (req->ecdh_signature != NULL) {
        size += len_field_size(F_REQ_ECDH_SIGNATURE, req->ecdh_signature_length);
    }
    return size;
}

static size_t write_len_field(uint32_t field, const uint8_t *payload, size_t payload_length,
                              uint8_t *buf)
{
    size_t n = tag_write(field, WT_LEN, buf);
    n += varint_write((uint64_t)payload_length, buf + n);
    if (payload_length != 0u) {
        memcpy(buf + n, payload, payload_length);
    }
    return n + payload_length;
}

static size_t write_session_request_payload(const takion_session_request *req, uint8_t *buf)
{
    size_t n = 0;

    n += tag_write(F_REQ_CLIENT_VERSION, WT_VARINT, buf + n);
    n += varint_write(req->client_version, buf + n);
    n += write_len_field(F_REQ_SESSION_KEY, (const uint8_t *)req->session_key,
                         req->session_key_length, buf + n);
    n += write_len_field(F_REQ_LAUNCH_SPEC, (const uint8_t *)req->launch_spec_json,
                         req->launch_spec_json_length, buf + n);
    n += write_len_field(F_REQ_ENCRYPTED_KEY, req->encrypted_key, req->encrypted_key_length, buf + n);
    if (req->ecdh_public_key != NULL) {
        n += write_len_field(F_REQ_ECDH_PUBKEY, req->ecdh_public_key,
                             req->ecdh_public_key_length, buf + n);
    }
    if (req->ecdh_signature != NULL) {
        n += write_len_field(F_REQ_ECDH_SIGNATURE, req->ecdh_signature,
                             req->ecdh_signature_length, buf + n);
    }
    return n;
}

size_t takion_control_build_session_request(const takion_session_request *req,
                                            uint8_t *buf, size_t buf_size)
{
    size_t payload_size;
    size_t total;
    size_t n;

    if (req == NULL || buf == NULL) {
        return 0;
    }
    /* proto2 `required`: a NULL here is a caller bug, and emitting a message the console will reject is
     * a worse outcome than refusing to build it. Zero-length values are legal, NULL pointers are not. */
    if (req->session_key == NULL || req->launch_spec_json == NULL || req->encrypted_key == NULL) {
        return 0;
    }

    payload_size = session_request_payload_size(req);
    total = varint_field_size(F_MSG_TYPE, TAKION_CONTROL_SESSION_REQUEST)
          + len_field_size(F_MSG_SESSION_REQUEST, payload_size);
    if (total > buf_size) {
        return 0;
    }

    n = tag_write(F_MSG_TYPE, WT_VARINT, buf);
    n += varint_write(TAKION_CONTROL_SESSION_REQUEST, buf + n);
    n += tag_write(F_MSG_SESSION_REQUEST, WT_LEN, buf + n);
    n += varint_write((uint64_t)payload_size, buf + n);
    n += write_session_request_payload(req, buf + n);
    return n;
}

size_t takion_control_build_protocol_version_request(const uint32_t *versions, size_t version_count,
                                                     uint8_t *buf, size_t buf_size)
{
    uint8_t inner[64];
    size_t inner_len = 0u;
    size_t n = 0u;
    size_t i;

    if (buf == NULL || versions == NULL || version_count == 0u)
        return 0;

    /*
     * supportedVersions is `repeated uint32` and NOT packed - the schema has no [packed=true] and this
     * predates proto3's default, so each entry carries its own tag. Writing it packed produces a message
     * the console parses as one enormous version number rather than as a list.
     */
    for (i = 0; i < version_count; i++) {
        size_t need = varint_field_size(F_PV_SUPPORTED_VERSIONS, versions[i]);

        if (inner_len + need > sizeof(inner))
            return 0;
        inner_len += tag_write(F_PV_SUPPORTED_VERSIONS, WT_VARINT, inner + inner_len);
        inner_len += varint_write(versions[i], inner + inner_len);
    }

    if (varint_field_size(F_MSG_TYPE, TAKION_CONTROL_PROTOCOL_VERSION_REQUEST)
        + tag_size(F_MSG_PROTOCOL_VERSION_REQUEST) + varint_size(inner_len) + inner_len > buf_size)
        return 0;

    n = tag_write(F_MSG_TYPE, WT_VARINT, buf);
    n += varint_write(TAKION_CONTROL_PROTOCOL_VERSION_REQUEST, buf + n);
    n += tag_write(F_MSG_PROTOCOL_VERSION_REQUEST, WT_LEN, buf + n);
    n += varint_write(inner_len, buf + n);
    memcpy(buf + n, inner, inner_len);
    return n + inner_len;
}

int takion_control_parse_protocol_version_ack(const uint8_t *data, size_t length, uint32_t *out_version)
{
    size_t offset = 0u;
    int found = 0;

    if (data == NULL || out_version == NULL)
        return 0;
    *out_version = 0u;

    while (offset < length) {
        uint64_t tag = 0u;
        uint32_t field;
        unsigned wire;

        if (!varint_read(data, length, &offset, &tag))
            return 0;
        field = (uint32_t)(tag >> 3);
        wire = (unsigned)(tag & 7u);

        if (field == F_MSG_PROTOCOL_VERSION_ACK && wire == WT_LEN) {
            uint64_t inner_len = 0u;
            size_t inner_end;

            if (!varint_read(data, length, &offset, &inner_len)
                || offset + (size_t)inner_len > length)
                return 0;
            inner_end = offset + (size_t)inner_len;

            while (offset < inner_end) {
                uint64_t inner_tag = 0u;
                uint64_t value = 0u;

                if (!varint_read(data, inner_end, &offset, &inner_tag))
                    return 0;
                if ((uint32_t)(inner_tag >> 3) == F_PV_PROTOCOL_VERSION
                    && (unsigned)(inner_tag & 7u) == WT_VARINT) {
                    if (!varint_read(data, inner_end, &offset, &value))
                        return 0;
                    *out_version = (uint32_t)value;
                    found = 1;
                } else if (!skip_value(data, inner_end, &offset, (unsigned)(inner_tag & 7u))) {
                    return 0;
                }
            }
            offset = inner_end;
        } else if (!skip_value(data, length, &offset, wire)) {
            return 0;
        }
    }
    return found;
}

size_t takion_control_build_bare(uint32_t type, uint8_t *buf, size_t buf_size)
{
    size_t total;
    size_t n;

    if (buf == NULL) {
        return 0;
    }
    total = varint_field_size(F_MSG_TYPE, type);
    if (total > buf_size) {
        return 0;
    }
    n = tag_write(F_MSG_TYPE, WT_VARINT, buf);
    n += varint_write(type, buf + n);
    return n;
}

/*
 * Wraps an already-encoded sub-command in BandwidthProbePayload{command, <field_no>=inner} and then in
 * ControlMessage{type=BANDWIDTH_PROBE, bandwidthProbePayload=inner}. Returns bytes written, 0 if short.
 *
 * The three probe commands differ only in their enum value and which optional field carries the body, so
 * the two layers of framing are written once here rather than three times with the field numbers
 * transposed - which is exactly the kind of thing that encodes cleanly and is rejected on the wire.
 */
static size_t wrap_bandwidth_probe(uint32_t command, uint32_t field_no,
                                   const uint8_t *inner, size_t inner_len,
                                   uint8_t *buf, size_t buf_size)
{
    uint8_t payload[48];
    size_t payload_len = 0, n = 0;

    if (buf == NULL || inner_len + 8u > sizeof(payload))
        return 0;

    payload_len += tag_write(1u, WT_VARINT, payload + payload_len);
    payload_len += varint_write(command, payload + payload_len);
    payload_len += tag_write(field_no, WT_LEN, payload + payload_len);
    payload_len += varint_write((uint32_t)inner_len, payload + payload_len);
    memcpy(payload + payload_len, inner, inner_len);
    payload_len += inner_len;

    if (varint_field_size(F_MSG_TYPE, TAKION_CONTROL_BANDWIDTH_PROBE) + 1u + 1u + payload_len > buf_size)
        return 0;

    n += tag_write(F_MSG_TYPE, WT_VARINT, buf + n);
    n += varint_write(TAKION_CONTROL_BANDWIDTH_PROBE, buf + n);
    n += tag_write(14u, WT_LEN, buf + n);
    n += varint_write((uint32_t)payload_len, buf + n);
    memcpy(buf + n, payload, payload_len);
    return n + payload_len;
}

size_t takion_control_build_mtu_command(uint32_t id, uint32_t mtu_req, uint32_t num,
                                        uint8_t *buf, size_t buf_size)
{
    uint8_t inner[24];
    size_t n = 0;

    n += tag_write(1u, WT_VARINT, inner + n);   /* id */
    n += varint_write(id, inner + n);
    n += tag_write(2u, WT_VARINT, inner + n);   /* mtuReq */
    n += varint_write(mtu_req, inner + n);
    n += tag_write(4u, WT_VARINT, inner + n);   /* num */
    n += varint_write(num, inner + n);
    return wrap_bandwidth_probe(1u /* MTU_COMMAND */, 3u, inner, n, buf, buf_size);
}

size_t takion_control_build_client_mtu_command(uint32_t id, uint32_t mtu_req, int state,
                                               uint8_t *buf, size_t buf_size)
{
    uint8_t inner[32];
    size_t n = 0;

    n += tag_write(1u, WT_VARINT, inner + n);   /* id */
    n += varint_write(id, inner + n);
    n += tag_write(2u, WT_VARINT, inner + n);   /* mtuReq */
    n += varint_write(mtu_req, inner + n);
    n += tag_write(3u, WT_VARINT, inner + n);   /* state */
    n += varint_write(state ? 1u : 0u, inner + n);
    n += tag_write(4u, WT_VARINT, inner + n);   /* mtuDown - the same figure, as the .NET side sends */
    n += varint_write(mtu_req, inner + n);
    return wrap_bandwidth_probe(4u /* CLIENT_MTU_COMMAND */, 5u, inner, n, buf, buf_size);
}

size_t takion_control_build_echo_command(int enabled, uint8_t *buf, size_t buf_size)
{
    uint8_t inner[4];
    size_t n = 0;

    n += tag_write(1u, WT_VARINT, inner + n);   /* state */
    n += varint_write(enabled ? 1u : 0u, inner + n);
    return wrap_bandwidth_probe(0u /* ECHO_COMMAND */, 2u, inner, n, buf, buf_size);
}

/*
 * A protobuf double: IEEE-754, little-endian, eight bytes. The union reinterprets the value's own bit
 * pattern and the shifts then extract bits arithmetically, so this emits little-endian on a big-endian
 * target as well - which is the case that matters here and the one a memcpy would get wrong.
 */
static size_t double_write(double value, uint8_t *out)
{
    union { double d; uint64_t u; } conv;
    int i;

    conv.d = value;
    for (i = 0; i < 8; i++)
        out[i] = (uint8_t)((conv.u >> (8 * i)) & 0xffu);
    return 8u;
}

size_t takion_control_build_connection_quality(uint32_t target_bitrate_kbps,
                                               double rtt_ms, double loss_percent,
                                               uint8_t *buf, size_t buf_size)
{
    uint8_t payload[40];
    size_t payload_len = 0;
    size_t n = 0;

    if (buf == NULL)
        return 0;

    payload_len += tag_write(1u, WT_VARINT, payload + payload_len);      /* targetBitrate */
    payload_len += varint_write(target_bitrate_kbps, payload + payload_len);
    payload_len += tag_write(5u, WT_64BIT, payload + payload_len);       /* rtt */
    payload_len += double_write(rtt_ms, payload + payload_len);
    payload_len += tag_write(7u, WT_64BIT, payload + payload_len);       /* lossPercent */
    payload_len += double_write(loss_percent, payload + payload_len);

    if (varint_field_size(F_MSG_TYPE, TAKION_CONTROL_CONNECTION_QUALITY) + 1u + 1u + payload_len > buf_size)
        return 0;

    n += tag_write(F_MSG_TYPE, WT_VARINT, buf + n);
    n += varint_write(TAKION_CONTROL_CONNECTION_QUALITY, buf + n);
    n += tag_write(17u, WT_LEN, buf + n);                                /* connectionQualityPayload */
    n += varint_write((uint32_t)payload_len, buf + n);
    memcpy(buf + n, payload, payload_len);
    return n + payload_len;
}

/* ---- parsing ---- */

int takion_control_peek_type(const uint8_t *data, size_t length, uint32_t *out_type)
{
    size_t offset = 0;

    if (data == NULL || out_type == NULL) {
        return 0;
    }

    while (offset < length) {
        uint64_t tag;
        uint32_t field;
        unsigned wire_type;

        if (!varint_read(data, length, &offset, &tag)) {
            return 0;
        }
        field = (uint32_t)(tag >> 3);
        wire_type = (unsigned)(tag & 0x07u);
        if (field == 0u) {
            return 0;
        }

        if (field == F_MSG_TYPE && wire_type == WT_VARINT) {
            uint64_t value;
            if (!varint_read(data, length, &offset, &value)) {
                return 0;
            }
            *out_type = (uint32_t)value;
            return 1;
        }
        if (!skip_value(data, length, &offset, wire_type)) {
            return 0;
        }
    }
    return 0;
}

/* ResolutionPayload{width=1, height=2, videoHeader=3}. */
static int parse_resolution(const uint8_t *data, size_t length, takion_stream_info *out)
{
    size_t offset = 0;

    while (offset < length) {
        uint64_t tag;
        uint32_t field;
        unsigned wire_type;
        uint64_t value;

        if (!varint_read(data, length, &offset, &tag))
            return 0;
        field = (uint32_t)(tag >> 3);
        wire_type = (unsigned)(tag & 0x07u);
        if (field == 0u)
            return 0;

        if (field == F_RES_WIDTH && wire_type == WT_VARINT) {
            if (!varint_read(data, length, &offset, &value))
                return 0;
            out->width = (uint32_t)value;
        } else if (field == F_RES_HEIGHT && wire_type == WT_VARINT) {
            if (!varint_read(data, length, &offset, &value))
                return 0;
            out->height = (uint32_t)value;
        } else if (field == F_RES_VIDEO_HEADER && wire_type == WT_LEN) {
            if (!read_bytes(data, length, &offset, &out->video_header, &out->video_header_length))
                return 0;
        } else if (!skip_value(data, length, &offset, wire_type)) {
            return 0;
        }
    }
    return 1;
}

/* StreamInfoPayload{resolution=1 (repeated), audioHeader=2, ...}. Only the first resolution is taken. */
static int parse_stream_info_payload(const uint8_t *data, size_t length, takion_stream_info *out)
{
    size_t offset = 0;

    while (offset < length) {
        uint64_t tag;
        uint32_t field;
        unsigned wire_type;

        if (!varint_read(data, length, &offset, &tag))
            return 0;
        field = (uint32_t)(tag >> 3);
        wire_type = (unsigned)(tag & 0x07u);
        if (field == 0u)
            return 0;

        if (field == F_SI_RESOLUTION && wire_type == WT_LEN) {
            const uint8_t *nested;
            size_t nested_length;

            if (!read_bytes(data, length, &offset, &nested, &nested_length))
                return 0;
            /* Repeated: later entries are alternative rungs of the ladder we offered. The first is the
             * one in force, and taking only it matches what the .NET side reads. */
            if (!out->has_resolution) {
                if (!parse_resolution(nested, nested_length, out))
                    return 0;
                out->has_resolution = 1;
            }
        } else if (field == F_SI_AUDIO_HEADER && wire_type == WT_LEN) {
            if (!read_bytes(data, length, &offset, &out->audio_header, &out->audio_header_length))
                return 0;
        } else if (!skip_value(data, length, &offset, wire_type)) {
            return 0;
        }
    }
    return 1;
}

int takion_control_parse_stream_info(const uint8_t *data, size_t length, takion_stream_info *out_info)
{
    size_t offset = 0;
    int have_type = 0;
    const uint8_t *payload = NULL;
    size_t payload_length = 0;

    if (data == NULL || out_info == NULL)
        return 0;
    memset(out_info, 0, sizeof(*out_info));

    while (offset < length) {
        uint64_t tag;
        uint32_t field;
        unsigned wire_type;

        if (!varint_read(data, length, &offset, &tag))
            return 0;
        field = (uint32_t)(tag >> 3);
        wire_type = (unsigned)(tag & 0x07u);
        if (field == 0u)
            return 0;

        if (field == F_MSG_TYPE && wire_type == WT_VARINT) {
            uint64_t value;
            if (!varint_read(data, length, &offset, &value))
                return 0;
            if (value != TAKION_CONTROL_STREAM_INFO)
                return 0;
            have_type = 1;
            continue;
        }
        if (field == F_MSG_STREAM_INFO && wire_type == WT_LEN) {
            if (!read_bytes(data, length, &offset, &payload, &payload_length))
                return 0;
            continue;
        }
        if (!skip_value(data, length, &offset, wire_type))
            return 0;
    }

    if (!have_type || payload == NULL)
        return 0;
    return parse_stream_info_payload(payload, payload_length, out_info);
}

int takion_control_parse_disconnect(const uint8_t *data, size_t length,
                                    const char **out_reason, size_t *out_reason_length)
{
    size_t offset = 0;
    const uint8_t *payload = NULL;
    size_t payload_length = 0;

    if (data == NULL || out_reason == NULL || out_reason_length == NULL)
        return 0;
    *out_reason = NULL;
    *out_reason_length = 0;

    while (offset < length) {
        uint64_t tag;
        uint32_t field;
        unsigned wire_type;

        if (!varint_read(data, length, &offset, &tag))
            return 0;
        field = (uint32_t)(tag >> 3);
        wire_type = (unsigned)(tag & 0x07u);
        if (field == 0u)
            return 0;

        if (field == F_MSG_DISCONNECT && wire_type == WT_LEN) {
            if (!read_bytes(data, length, &offset, &payload, &payload_length))
                return 0;
            continue;
        }
        if (!skip_value(data, length, &offset, wire_type))
            return 0;
    }

    if (payload == NULL)
        return 0;

    offset = 0;
    while (offset < payload_length) {
        uint64_t tag;
        uint32_t field;
        unsigned wire_type;

        if (!varint_read(payload, payload_length, &offset, &tag))
            return 0;
        field = (uint32_t)(tag >> 3);
        wire_type = (unsigned)(tag & 0x07u);
        if (field == 0u)
            return 0;

        if (field == F_DISC_REASON && wire_type == WT_LEN) {
            const uint8_t *bytes;
            size_t bytes_length;
            if (!read_bytes(payload, payload_length, &offset, &bytes, &bytes_length))
                return 0;
            *out_reason = (const char *)bytes;
            *out_reason_length = bytes_length;
            return 1;
        }
        if (!skip_value(payload, payload_length, &offset, wire_type))
            return 0;
    }
    return 0;
}

int takion_control_validate(const uint8_t *data, size_t length)
{
    size_t offset = 0;

    if (data == NULL)
        return 0;

    while (offset < length) {
        uint64_t tag;
        unsigned wire_type;

        if (!varint_read(data, length, &offset, &tag))
            return 0;
        if ((uint32_t)(tag >> 3) == 0u)
            return 0;
        wire_type = (unsigned)(tag & 0x07u);
        if (!skip_value(data, length, &offset, wire_type))
            return 0;
    }
    return 1;
}

static int parse_session_reply_payload(const uint8_t *data, size_t length,
                                       takion_session_reply *out_reply)
{
    size_t offset = 0;
    unsigned seen = 0;

    memset(out_reply, 0, sizeof(*out_reply));

    while (offset < length) {
        uint64_t tag;
        uint32_t field;
        unsigned wire_type;
        uint64_t value;

        if (!varint_read(data, length, &offset, &tag)) {
            return 0;
        }
        field = (uint32_t)(tag >> 3);
        wire_type = (unsigned)(tag & 0x07u);
        if (field == 0u) {
            return 0;
        }

        switch (field) {
        case F_RPL_SERVER_VERSION:
            if (wire_type != WT_VARINT || !varint_read(data, length, &offset, &value)) {
                return 0;
            }
            out_reply->server_version = (uint32_t)value;
            seen |= RPL_REQ_SERVER_VERSION;
            break;
        case F_RPL_TOKEN:
            if (wire_type != WT_VARINT || !varint_read(data, length, &offset, &value)) {
                return 0;
            }
            out_reply->token = (uint32_t)value;
            seen |= RPL_REQ_TOKEN;
            break;
        case F_RPL_ENCKEY_ACCEPTED:
            if (wire_type != WT_VARINT || !varint_read(data, length, &offset, &value)) {
                return 0;
            }
            out_reply->encrypted_key_accepted = (value != 0u) ? 1 : 0;
            seen |= RPL_REQ_ENCKEY_ACCEPTED;
            break;
        case F_RPL_VERSION_ACCEPTED:
            if (wire_type != WT_VARINT || !varint_read(data, length, &offset, &value)) {
                return 0;
            }
            out_reply->version_accepted = (value != 0u) ? 1 : 0;
            seen |= RPL_REQ_VERSION_ACCEPTED;
            break;
        case F_RPL_SESSION_KEY: {
            const uint8_t *bytes;
            size_t bytes_length;
            if (wire_type != WT_LEN || !read_bytes(data, length, &offset, &bytes, &bytes_length)) {
                return 0;
            }
            out_reply->session_key = (const char *)bytes;
            out_reply->session_key_length = bytes_length;
            seen |= RPL_REQ_SESSION_KEY;
            break;
        }
        case F_RPL_SERVER_VERSTRING: {
            const uint8_t *bytes;
            size_t bytes_length;
            if (wire_type != WT_LEN || !read_bytes(data, length, &offset, &bytes, &bytes_length)) {
                return 0;
            }
            out_reply->server_version_string = (const char *)bytes;
            out_reply->server_version_string_length = bytes_length;
            break;
        }
        case F_RPL_ECDH_PUBKEY:
            if (wire_type != WT_LEN
                || !read_bytes(data, length, &offset,
                               &out_reply->ecdh_public_key, &out_reply->ecdh_public_key_length)) {
                return 0;
            }
            break;
        case F_RPL_ECDH_SIGNATURE:
            if (wire_type != WT_LEN
                || !read_bytes(data, length, &offset,
                               &out_reply->ecdh_signature, &out_reply->ecdh_signature_length)) {
                return 0;
            }
            break;
        default:
            if (!skip_value(data, length, &offset, wire_type)) {
                return 0;
            }
            break;
        }
    }

    if ((seen & RPL_REQ_ALL) != RPL_REQ_ALL) {
        return 0;
    }
    out_reply->has_ecdh = (out_reply->ecdh_public_key != NULL && out_reply->ecdh_signature != NULL)
                              ? 1 : 0;
    return 1;
}

int takion_control_parse_session_reply(const uint8_t *data, size_t length,
                                       takion_session_reply *out_reply)
{
    size_t offset = 0;
    int have_type = 0;
    const uint8_t *payload = NULL;
    size_t payload_length = 0;

    if (data == NULL || out_reply == NULL) {
        return 0;
    }

    while (offset < length) {
        uint64_t tag;
        uint32_t field;
        unsigned wire_type;

        if (!varint_read(data, length, &offset, &tag)) {
            return 0;
        }
        field = (uint32_t)(tag >> 3);
        wire_type = (unsigned)(tag & 0x07u);
        if (field == 0u) {
            return 0;
        }

        if (field == F_MSG_TYPE && wire_type == WT_VARINT) {
            uint64_t value;
            if (!varint_read(data, length, &offset, &value)) {
                return 0;
            }
            if (value != TAKION_CONTROL_SESSION_REPLY) {
                return 0;
            }
            have_type = 1;
            continue;
        }
        if (field == F_MSG_SESSION_REPLY && wire_type == WT_LEN) {
            if (!read_bytes(data, length, &offset, &payload, &payload_length)) {
                return 0;
            }
            continue;
        }
        if (!skip_value(data, length, &offset, wire_type)) {
            return 0;
        }
    }

    if (!have_type || payload == NULL) {
        return 0;
    }
    return parse_session_reply_payload(payload, payload_length, out_reply);
}
