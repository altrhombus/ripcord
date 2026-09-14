/*
 * ripcord-3ds - Phase 6a: the control-plane protobuf envelope, SESSION_REQUEST and SESSION_REPLY.
 *
 * Ported from the two messages of docs/protocol/stream_control.proto that the stream key agreement
 * needs, as consumed by Ripcord.Protocol.Halyard.Takion.TakionSessionNegotiator. These ride the Takion
 * reliable channel (TAKION_CHANNEL_SESSION, 0x0001) after the SCTP handshake completes and before GMAC
 * sealing is switched on - see takion_session_negotiator.h for the sequence.
 *
 * WHY HAND-ROLLED RATHER THAN A GENERATED CODEC. The .NET side compiles the whole 41-message schema with
 * Google.Protobuf + Grpc.Tools, which is the right call there and the wrong one here: this port needs
 * exactly two messages, has no malloc anywhere, and adding a code generator to a cross-compiled C
 * makefile would cost more than the ~200 lines below. What is NOT negotiable is the wire contract -
 * field numbers and wire types are interoperability facts (protobuf transmits numbers, never names), and
 * the numbers here are read straight from the committed .proto, which is the authority. Field *names* in
 * this file follow that .proto's own descriptive naming, which is Ripcord's, not the vendor's.
 *
 * WIRE SUBSET IMPLEMENTED. Only wire types 0 (varint), 2 (length-delimited), 1 (64-bit) and 5 (32-bit)
 * are understood, which covers every field in these two messages; parsing skips unknown field numbers so
 * a console that sends more than we model is not a parse failure. Groups (wire types 3/4, deprecated in
 * proto2 and absent from this schema) are rejected rather than skipped, because skipping a group
 * correctly requires matching END_GROUP tags and there is nothing here to test that against.
 *
 * PROTO2 `required` IS ENFORCED ON BOTH SIDES. Building emits every required field; parsing fails if one
 * is missing. That matters more than it looks: `launchSpecJson` is required, and it is the only channel
 * through which the console ever learns the handshakeKey that authenticates the ECDH exchange (spec
 * sec4.3/sec5.1), so a SESSION_REQUEST that omits it is not a degraded request, it is an unusable one.
 *
 * STRINGS AND BYTES ARE NOT COPIED ON PARSE. Every out-pointer points INTO the caller's buffer and is
 * NOT NUL-terminated, the same convention takion_data_chunk.h already uses for payloads. Copy what you
 * need to keep before the receive buffer is reused.
 */
#ifndef TAKION_CONTROL_PROTO_H
#define TAKION_CONTROL_PROTO_H

#include <stddef.h>
#include <stdint.h>

/*
 * ControlMessage.MessageType. Only the values this port acts on are named; the enum is much larger (34
 * values) and parsing reports the raw number for anything else rather than pretending it is unknown.
 */
#define TAKION_CONTROL_SESSION_REQUEST 0u
#define TAKION_CONTROL_SESSION_REPLY   1u
#define TAKION_CONTROL_HEARTBEAT       3u
#define TAKION_CONTROL_DISCONNECT      8u
#define TAKION_CONTROL_STREAM_INFO     13u
#define TAKION_CONTROL_STREAM_INFO_ACK 14u
#define TAKION_CONTROL_BANDWIDTH_PROBE 12u

/* The version negotiation pair. 31 is the value the senkusha bring-up already sends as a literal; 32 is
 * what the console answers with, and its payload names the version it actually chose. */
#define TAKION_CONTROL_PROTOCOL_VERSION_REQUEST 31u
#define TAKION_CONTROL_PROTOCOL_VERSION_ACK     32u
#define TAKION_CONTROL_IDR_REQUEST     25u

/* Uncompressed SEC1 point sizes, for callers sizing buffers. See rc_ecdh.h for the curve selection. */
#define TAKION_ECDH_PUBKEY_MAX 133u
#define TAKION_ECDH_SIGNATURE_LENGTH 32u

/*
 * SESSION_REQUEST inputs. Pointer fields are borrowed for the duration of the build call only.
 *
 * `launch_spec_json` is the ALREADY-ENCRYPTED, ALREADY-BASE64 launch spec, not plaintext JSON - the
 * AES-128-OFB("out1") pass belongs to the control plane (halyard_control_streaminfo_crypt) and happens
 * before this struct is filled. The field name is the vendor-facing wire name; do not let it mislead you
 * into passing raw JSON.
 *
 * `ecdh_public_key`/`ecdh_signature` are proto2-optional and may be left NULL/0 to omit them, which is
 * only useful for testing the envelope - a real request carries both.
 */
typedef struct {
    uint32_t client_version;
    const char *session_key;
    size_t session_key_length;
    const char *launch_spec_json;
    size_t launch_spec_json_length;
    const uint8_t *encrypted_key;
    size_t encrypted_key_length;
    const uint8_t *ecdh_public_key;
    size_t ecdh_public_key_length;
    const uint8_t *ecdh_signature;
    size_t ecdh_signature_length;
} takion_session_request;

/*
 * SESSION_REPLY, as parsed. Every pointer aims into the buffer passed to the parser.
 *
 * `has_ecdh` distinguishes "the console accepted us but sent no key" from "the key was 133 zero bytes",
 * which a length check alone cannot: both ecdh fields are optional in the schema, and a reply without
 * them means the exchange cannot proceed rather than that it proceeded with an empty key.
 */
typedef struct {
    uint32_t server_version;
    uint32_t token;
    int encrypted_key_accepted;
    int version_accepted;
    const char *session_key;
    size_t session_key_length;
    const char *server_version_string; /* optional; NULL when absent */
    size_t server_version_string_length;
    const uint8_t *ecdh_public_key;
    size_t ecdh_public_key_length;
    const uint8_t *ecdh_signature;
    size_t ecdh_signature_length;
    int has_ecdh;
} takion_session_reply;

/*
 * Builds a complete ControlMessage{type=SESSION_REQUEST, sessionRequestPayload=...}.
 *
 * Returns the number of bytes written, or 0 if `req` is incomplete (a required field is NULL) or the
 * buffer is too small. Fields are emitted in field-number order, which is what Google.Protobuf does on
 * the .NET side and therefore what the cross-language vectors compare against.
 */
size_t takion_control_build_session_request(const takion_session_request *req,
                                            uint8_t *buf, size_t buf_size);

/*
 * Reads just the envelope's `type` field, so a receiver can dispatch before committing to a payload
 * shape. Returns 1 and sets *out_type on success, 0 if the message is malformed or carries no type.
 */
int takion_control_peek_type(const uint8_t *data, size_t length, uint32_t *out_type);

/*
 * Parses a ControlMessage{type=SESSION_REPLY, sessionReplyPayload=...} into `out_reply`.
 *
 * Returns 1 on success, 0 if the message is malformed, is not a SESSION_REPLY, carries no reply payload,
 * or omits one of that payload's five required fields. On failure `out_reply` is left indeterminate.
 */
int takion_control_parse_session_reply(const uint8_t *data, size_t length,
                                       takion_session_reply *out_reply);

/*
 * Extracts DISCONNECT's reason string. Returns 1 and points *out_reason INTO `data` on success.
 *
 * Worth having as its own entry point because a console that hangs up says why, and discarding that
 * turns a stated reason into a debugging session - which is exactly what happened when a rejected
 * launch spec produced two 70-odd-byte DISCONNECT messages that this port logged only as "type 8".
 */
int takion_control_parse_disconnect(const uint8_t *data, size_t length,
                                    const char **out_reason, size_t *out_reason_length);

/*
 * Walks every field of a ControlMessage without interpreting it, returning 1 if the whole message is
 * well-formed protobuf and 0 otherwise.
 *
 * Exists because hand-written literals are the one place this port still encodes protobuf by eye - the
 * senkusha PROTOCOL_VERSION_REQUEST in source/connect/main.c being the live example. Field 31
 * length-delimited is (31 << 3) | 2 = 250, which needs a TWO-byte varint tag; writing the single byte
 * the arithmetic suggests produces a message whose first field still reads correctly and whose
 * remainder is garbage, so peeking the type is not a check. This walks to the end, which is.
 */
int takion_control_validate(const uint8_t *data, size_t length);

/*
 * STREAM_INFO, as parsed. Pointers aim into the caller's buffer.
 *
 * `video_header` is the SPS/PPS parameter-set blob from the FIRST resolution entry. It matters more than
 * its size suggests: those parameter sets are NOT present in the video stream itself, so a decoder that
 * never receives them cannot decode the first IDR - which is why stream_demux takes them separately and
 * re-prepends them. The console sends this unprompted once the session is sealed; the client only acks.
 */
typedef struct {
    uint32_t width;
    uint32_t height;
    const uint8_t *video_header;
    size_t video_header_length;
    const uint8_t *audio_header;
    size_t audio_header_length;
    int has_resolution;
} takion_stream_info;

/*
 * Parses a ControlMessage{type=STREAM_INFO, streamInfoPayload=...}. Returns 1 on success, 0 if the
 * message is malformed or is not a STREAM_INFO. A message with no resolution entry parses successfully
 * with has_resolution clear - the audio header can still be useful on its own.
 */
int takion_control_parse_stream_info(const uint8_t *data, size_t length,
                                     takion_stream_info *out_info);

/*
 * Builds a bare ControlMessage carrying only `type` and no payload - the shape HEARTBEAT and the
 * STREAM_INFO_ACK-style acknowledgements use. Returns bytes written, or 0 if the buffer is too small.
 */
size_t takion_control_build_bare(uint32_t type, uint8_t *buf, size_t buf_size);

/*
 * PROTOCOL_VERSION_REQUEST carrying the versions we are willing to speak, highest last.
 *
 * This matters more than it looks. The CURVE the session's ECDH runs on is chosen from the NEGOTIATED
 * version, not the one we asked for - so a client that assumes its own top version generates a key on a
 * curve the console may not have picked, and the mismatch does not surface until the peer's point is
 * rejected, long after a signature over it has verified. Ask, then read the answer.
 */
size_t takion_control_build_protocol_version_request(const uint32_t *versions, size_t version_count,
                                                     uint8_t *buf, size_t buf_size);

/*
 * Reads the version the console chose out of a PROTOCOL_VERSION_ACK. Returns 1 if the field was present.
 * A missing field is not an error at this layer - the caller falls back to what it requested, which is
 * what the .NET reference does.
 */
int takion_control_parse_protocol_version_ack(const uint8_t *data, size_t length, uint32_t *out_version);

/*
 * Builds ControlMessage{type=BANDWIDTH_PROBE, bandwidthProbePayload={command=ECHO_COMMAND,
 * echoCommand={state}}} - the message that puts the console into echo mode and takes it back out.
 *
 * Field numbers from docs/protocol/bandwidth_probe.proto: BandwidthProbePayload.command = 1 (ECHO_COMMAND
 * = 0), .echoCommand = 2, EchoCommand.state = 1. The payload hangs off ControlMessage field 14. Goes on
 * TAKION_CHANNEL_BANDWIDTH (0x0008), not the session channel.
 *
 * `command` is `required` in the schema, so it is emitted even though ECHO_COMMAND is zero and proto2
 * would otherwise let it be omitted - a required field the peer expects to find is not a default to
 * elide. Returns bytes written, or 0 if the buffer is too small.
 */
size_t takion_control_build_echo_command(int enabled, uint8_t *buf, size_t buf_size);

/*
 * The two MTU legs, spec 6.4's MTU-in and MTU-out. Both go on TAKION_CHANNEL_BANDWIDTH.
 *
 * DOWNSTREAM: MTU_COMMAND{id, mtuReq, num} asks the console to send `num` datagrams of `mtuReq` bytes.
 * Their ARRIVAL is the entire result - a datagram of that size reaching us intact is what "this MTU
 * works" means, so the contents are never inspected. The console's reply reports what it SENT, which is
 * not the same question.
 *
 * UPSTREAM: CLIENT_MTU_COMMAND{id, mtuReq, state} puts the console into echo mode for one large packet;
 * we send it in the echo format at that size and it comes back. state=false closes the test, and MUST be
 * sent on every exit path - a console left in client-MTU mode has no other way to be cleared.
 *
 * Field numbers from docs/protocol/bandwidth_probe.proto: MtuCommand{id=1, mtuReq=2, num=4} under
 * BandwidthProbePayload field 3; ClientMtuCommand{id=1, mtuReq=2, state=3, mtuDown=4} under field 5.
 */
size_t takion_control_build_mtu_command(uint32_t id, uint32_t mtu_req, uint32_t num,
                                        uint8_t *buf, size_t buf_size);
size_t takion_control_build_client_mtu_command(uint32_t id, uint32_t mtu_req, int state,
                                               uint8_t *buf, size_t buf_size);

#endif /* TAKION_CONTROL_PROTO_H */
