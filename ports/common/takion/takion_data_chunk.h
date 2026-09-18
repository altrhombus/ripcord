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
 * Chunk VALUE, continuation fragment (a message too large for one chunk). "Which fragment is 'first'" is
 * NOT a wire bit - a receiver decides from its own reassembly state - which is why parsing first vs.
 * continuation fragments are two separate entry points below rather than one auto-detecting parser:
 *   offset 0, 4 bytes: sequence number (TSN), big-endian
 *   offset 4, 2 bytes: channel - THE SAME FIELD AS IN A FIRST FRAGMENT, at the same offset
 *   offset 6, 2 bytes: reserved (zero)
 *   offset 8         : payload
 *
 * THE CHANNEL IS PRESENT IN CONTINUATIONS, AND THIS PORT USED TO GET IT WRONG. Only the reserved region
 * differs between the two fragment shapes - 3 bytes in a first fragment, 2 in a continuation - so the
 * payload lands at 9 vs 8. The channel field itself is in both. This file previously described offset
 * 4..8 of a continuation as four unconfirmed reserved bytes and zero-filled them, which sent every
 * continuation labelled channel 0 - and 0 is the channel the CONSOLE sends on. It went unnoticed because
 * nothing this port sent had ever exceeded one chunk until the 1716-byte SESSION_REQUEST of the connect
 * flow, whose first two hardware runs reached Takion ESTABLISHED and then got silence. Corrected against
 * TakionDataChunk.Build, which writes the channel unconditionally and shrinks only the reserved region.
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
 * Builds a continuation-fragment DATA chunk. `channel` must be the SAME channel as the first fragment -
 * see the header comment on why passing 0 here is a real bug rather than a harmless default.
 */
size_t takion_data_build_continuation(uint32_t seq_num, unsigned channel, int ending,
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
                                   unsigned *out_channel, int *out_ending,
                                   const uint8_t **out_payload, size_t *out_payload_length);

#endif /* TAKION_DATA_CHUNK_H */
