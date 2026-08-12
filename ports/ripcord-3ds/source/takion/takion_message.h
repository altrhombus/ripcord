/*
 * ripcord-3ds - Phase 5 Takion transport: the common 13-byte message header.
 *
 * Ported from Ripcord.Protocol.Halyard.Takion.TakionMessageHeader. Takion is "essentially SCTP over
 * UDP" (docs/protocol/ps5-remoteplay-v1-spec.md sec8) - the handshake chunk types, cookie mechanism and
 * verification-tag scheme all match real SCTP (RFC 4960), just carried in UDP datagrams instead of IP
 * protocol 132, with this 13-byte header in front of every one.
 *
 * WIRE FORMAT (all big-endian), wire-validated against captures/rudp_control_setup.pcapng:
 *   offset  0, 1 byte : base type - which of control/video/audio/feedback/congestion/input this is.
 *                       Phase 5 only builds/parses TAKION_BASE_TYPE_CONTROL: the SCTP handshake and
 *                       DATA/SACK chunks all ride base type 0, confirmed by every vector below.
 *   offset  1, 4 bytes: verification tag - the PEER's tag (the one THEY generated during the handshake
 *                       and told you to echo), not your own. Zero before a tag has been negotiated (as
 *                       in the client's own INIT).
 *   offset  5, 4 bytes: GMAC tag - authenticates the packet once stream keys exist; zero before that
 *                       (which is everything Phase 5 handles - GMAC sealing is a later phase).
 *   offset  9, 4 bytes: key position - a running byte counter tied to the stream cipher; zero before
 *                       stream keys exist, same as the GMAC tag.
 *   offset 13          : one or more SCTP-shaped chunks (see takion_handshake.h / takion_data_chunk.h /
 *                        takion_sack_chunk.h).
 *
 * Confirmed byte-for-byte against ported known-answer vectors in tests/takion_test.c - see that file
 * for where each one came from.
 */
#ifndef TAKION_MESSAGE_H
#define TAKION_MESSAGE_H

#include <stddef.h>
#include <stdint.h>

#define TAKION_HEADER_SIZE 13

#define TAKION_BASE_TYPE_CONTROL 0x00u /* SCTP handshake + DATA/SACK - the only base type Phase 5 uses */

typedef struct {
    unsigned base_type;
    uint32_t verification_tag;
    uint32_t gmac_tag;
    uint32_t key_position;
} takion_message_header;

/*
 * Serializes `header` followed by `chunk_data`/`chunk_length` into buf. Returns the total bytes
 * written, or 0 if buf_size is too small.
 */
size_t takion_message_build(const takion_message_header *header, const uint8_t *chunk_data,
                            size_t chunk_length, uint8_t *buf, size_t buf_size);

/*
 * Parses the 13-byte header from the front of [data, length). On success, returns
 * TAKION_HEADER_SIZE, fills *out_header, and sets *out_chunk and *out_chunk_length to the remaining bytes
 * (a pointer INTO data, not a copy). Returns 0 if fewer than TAKION_HEADER_SIZE bytes are present.
 */
size_t takion_message_parse(const uint8_t *data, size_t length, takion_message_header *out_header,
                            const uint8_t **out_chunk, size_t *out_chunk_length);

#endif /* TAKION_MESSAGE_H */
