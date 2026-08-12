/*
 * ripcord-3ds - Phase 5 Takion transport: the DATA chunk.
 *
 * Ported from Ripcord.Protocol.Halyard.Takion.TakionDataChunk. Carries SESSION_REQUEST/REPLY,
 * STREAMINFO and input reliably (acked by SACK, see takion_sack_chunk.h) - the unreliable A/V/feedback/
 * congestion base types never use DATA chunks at all; they rely on FEC instead (out of scope for this
 * phase, and for good reason: it lives entirely in the A/V demux/reassembly layer, not here).
 *
 * WIRE FORMAT, chunk header (SCTP-standard shape):
 *   offset 0, 1 byte : chunk type = TAKION_CHUNK_DATA (0x00)
 *   offset 1, 1 byte : flags - only bit 0x01 is used, the "ending" bit (this chunk completes the message)
 *   offset 2, 2 bytes: chunk length, big-endian, = 4 + the value length below
 *
 * Chunk VALUE, first (or only) fragment - confirmed byte-for-byte against three captured DATA chunks in
 * tests/takion_test.c:
 *   offset 0, 4 bytes: sequence number (TSN), big-endian
 *   offset 4, 2 bytes: channel - the per-message-class stream id (see the TAKION_CHANNEL_* constants)
 *   offset 6, 3 bytes: reserved
 *   offset 9         : payload
 *
 * Chunk VALUE, continuation fragment (a message too large for one chunk) - the payload offset (8) is
 * confirmed structurally by the .NET implementation, but no continuation-fragment vector was available
 * to pin what the 4 bytes before it mean; this port neither reads nor writes them, and zero-fills them
 * when building. "Which fragment is 'first'" is NOT a wire bit - a receiver decides from its own
 * reassembly state (nothing pending for this channel yet = first), which is exactly why parsing first
 * vs. continuation fragments are two separate entry points below rather than one auto-detecting parser:
 *   offset 0, 4 bytes: sequence number (TSN), big-endian
 *   offset 4, 4 bytes: reserved (meaning unconfirmed - not this port's business either way)
 *   offset 8         : payload
 */
#ifndef TAKION_DATA_CHUNK_H
#define TAKION_DATA_CHUNK_H

#include <stddef.h>
#include <stdint.h>

#define TAKION_DATA_FLAG_ENDING 0x01u

/* Per-class channel ids, wire-confirmed. Server replies ride channel 0. */
#define TAKION_CHANNEL_SERVER_REPLY       0x0000u
#define TAKION_CHANNEL_SESSION            0x0001u
#define TAKION_CHANNEL_BANDWIDTH          0x0008u
#define TAKION_CHANNEL_STREAM_INFO        0x0009u
#define TAKION_CHANNEL_PROTOCOL_VERSION   0x0015u

/*
 * Builds a first-fragment DATA chunk. Returns the bytes written, or 0 if buf_size is too small.
 * `ending` should be 1 unless the message continues in a further build_continuation() call.
 */
size_t takion_data_build_first(uint32_t seq_num, unsigned channel, int ending,
                               const uint8_t *payload, size_t payload_length,
                               uint8_t *buf, size_t buf_size);

/*
 * Builds a continuation-fragment DATA chunk. See the header comment above for the confidence caveat on
 * this fragment shape - it is not yet exercised by anything this port sends (nothing built so far
 * exceeds one chunk), so treat this as unverified until a real multi-fragment send is tested.
 */
size_t takion_data_build_continuation(uint32_t seq_num, int ending,
                                      const uint8_t *payload, size_t payload_length,
                                      uint8_t *buf, size_t buf_size);

/*
 * Parses a first-fragment DATA chunk. Returns 1 and fills the out-params on success (out_payload points
 * INTO data, not a copy), 0 if data is not a well-formed first-fragment DATA chunk.
 */
int takion_data_parse_first(const uint8_t *data, size_t length, uint32_t *out_seq_num,
                            unsigned *out_channel, int *out_ending,
                            const uint8_t **out_payload, size_t *out_payload_length);

/*
 * Parses a continuation-fragment DATA chunk. Returns 1 and fills the out-params on success, 0 if data
 * is not a well-formed continuation-fragment DATA chunk.
 */
int takion_data_parse_continuation(const uint8_t *data, size_t length, uint32_t *out_seq_num,
                                   int *out_ending, const uint8_t **out_payload, size_t *out_payload_length);

#endif /* TAKION_DATA_CHUNK_H */
