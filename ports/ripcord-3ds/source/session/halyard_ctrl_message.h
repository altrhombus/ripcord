/*
 * ripcord-3ds - Phase 4 binary control-channel frame.
 *
 * One message on the persistent binary control channel that follows the /sess/ctrl HTTP handshake on
 * the same TCP connection. Re-derived from this project's own corrected reading, not from
 * docs/protocol/ps5-session-transport.md's "RPCS" description: that doc file predates a correction
 * recorded in docs/protocol-research-log.md (2026-08-03) and in the .NET implementation's own doc
 * comment (Ripcord.Protocol.Halyard.Common/Control/HalyardCtrlMessage.cs) - no "RPCS" magic appears on
 * the wire. Treat those two as authoritative over the stale doc file if the two ever seem to disagree
 * again.
 *
 * WIRE FORMAT (all big-endian):
 *   offset 0, 4 bytes: payload length (bytes after this 8-byte header)
 *   offset 4, 2 bytes: message type
 *   offset 6, 2 bytes: reserved, always 0
 *   offset 8, N bytes: payload (control-plane-encrypted when non-empty; heartbeats carry none)
 *
 * The console drives this channel - most importantly it sends TYPE_HEARTBEAT_REQ every few seconds and
 * disconnects the whole session if the client does not answer with TYPE_HEARTBEAT_REP (both
 * empty-payload, so no crypto is needed for the keep-alive itself).
 */
#ifndef HALYARD_CTRL_MESSAGE_H
#define HALYARD_CTRL_MESSAGE_H

#include <stddef.h>
#include <stdint.h>

#define HALYARD_CTRL_HEADER_SIZE 8

/* Console -> client: the account is locked; a login passcode is required before the stream can open.
 * Until this is answered with TYPE_LOGIN_SUBMIT the console silently drops every stream handshake
 * attempt. */
#define HALYARD_CTRL_TYPE_LOGIN_PROMPT   0x0004u

/* Client -> console: the entered passcode, its ASCII digits encrypted at the control-field cipher's
 * counter 5 (HALYARD_SESS_COUNTER_LOGIN_PIN_START in halyard_sess_fields.h) - the field counter is one
 * running per-connection value, and this is the sixth field-encrypt after the five /sess/ctrl headers. */
#define HALYARD_CTRL_TYPE_LOGIN_SUBMIT   0x8004u

/* Console -> client: login result (a single opaque byte). Informational - TYPE_SESSION_ID is the
 * reliable "you may stream now" signal. */
#define HALYARD_CTRL_TYPE_LOGIN          0x0005u

/* Console -> client: session-ready. After a login this is what gates bringing the stream up. */
#define HALYARD_CTRL_TYPE_SESSION_ID     0x0033u

/* Client -> console: put the console into rest mode on this disconnect (empty payload); console acks
 * with TYPE_REST_MODE_ACK. Omit to leave the console awake. */
#define HALYARD_CTRL_TYPE_REST_MODE      0x0050u
#define HALYARD_CTRL_TYPE_REST_MODE_ACK  0x8050u

#define HALYARD_CTRL_TYPE_HEARTBEAT_REQ  0x00feu
#define HALYARD_CTRL_TYPE_HEARTBEAT_REP  0x01feu

/*
 * Serialize one frame (header + payload) into buf. Returns the total bytes written
 * (HALYARD_CTRL_HEADER_SIZE + payload_length), or 0 if buf_size is too small. `payload` may be NULL iff
 * payload_length is 0 (e.g. every heartbeat).
 */
size_t halyard_ctrl_message_build(unsigned type, const uint8_t *payload, size_t payload_length,
                                  uint8_t *buf, size_t buf_size);

/*
 * Parse one frame from the front of [data, data + length). On success, returns the number of bytes the
 * frame occupied (header + payload) and fills *out_type, *out_payload (a pointer INTO data, not a copy)
 * and *out_payload_length. Returns 0 if fewer than HALYARD_CTRL_HEADER_SIZE bytes are present, or the
 * header's declared payload length would extend past `length` - either case means "wait for more data
 * from the stream," not "malformed," since TCP delivers a byte stream, not message boundaries.
 */
size_t halyard_ctrl_message_parse(const uint8_t *data, size_t length,
                                  unsigned *out_type, const uint8_t **out_payload, size_t *out_payload_length);

#endif /* HALYARD_CTRL_MESSAGE_H */
