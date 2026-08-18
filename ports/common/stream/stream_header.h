/*
 * ripcord-3ds - the v1 A/V data-packet header, bit-exact per docs/protocol/ps5-remoteplay-v1-spec.md
 * sec6.1 (all multi-byte integers big-endian). Ported from HalyardStreamHeader.cs.
 *
 *   off 0      u8   low nibble = type (2=video, 3=audio, 0x12=FEC); bit 4 = extended-header flag
 *   off 1..2   u16  packet_index          (per-packet sequence, +1 per packet)
 *   off 3..4   u16  frame_index
 *   off 5..8   u32  video: bits[31:21] unit_index | bits[20:10] total_units-1 | bits[9:0] parity_units
 *                   audio: byte-wide fields (see stream_header_parse)
 *   off 9      u8   codec
 *   off 10..13 u32  4-byte GMAC tag (zeroed for the GMAC computation)
 *   off 14..17 u32  key position (running byte counter; drives the crypto nonce - NOT a timestamp)
 *   off 18..   ...  media payload; +3 bytes for video (size_extension u16 + adaptive_stream_index),
 *                   +2 for audio (an "unknown" byte plus a haptics-indicator byte), +3 more if the
 *                   extended-header flag is set
 *
 * There is deliberately no STREAM_HEADER_TYPE_FEC constant. A raw byte-0 of 0x12 is often described as
 * "the FEC type", but 0x12 is low-nibble 2 (video) with bit 4 (extended header) set - since type is masked
 * to the low nibble, a `type == 0x12` comparison would be unconditionally false. Parity units are not a
 * packet type at all: they are video packets whose unit index falls at or above source_units. Use
 * stream_header_is_parity_unit.
 */
#ifndef STREAM_HEADER_H
#define STREAM_HEADER_H

#include <stdint.h>
#include <stddef.h>

#define STREAM_HEADER_LENGTH 18

#define STREAM_HEADER_TYPE_VIDEO 0x02
#define STREAM_HEADER_TYPE_AUDIO 0x03

#define STREAM_HEADER_TAG_OFFSET 10
#define STREAM_HEADER_KEY_POSITION_OFFSET 14

typedef struct {
    uint8_t type;               /* STREAM_HEADER_TYPE_VIDEO or STREAM_HEADER_TYPE_AUDIO */
    int has_extended_header;
    uint16_t packet_index;
    uint16_t frame_index;
    int unit_index;
    int total_units;
    int parity_units;           /* audio: retained raw only - see stream_header_parse */
    uint8_t codec;
    uint32_t key_position;
} stream_header;

/* Number of source units in the frame (total minus FEC units). */
static inline int stream_header_source_units(const stream_header *header)
{
    return header->total_units - header->parity_units;
}

/* True when this unit is one of the frame's Reed-Solomon parity units rather than a source unit - a
 * function of the unit index, not of the packet type. Parity units carry no size-extension: they are
 * already exactly the frame's coded unit length. */
static inline int stream_header_is_parity_unit(const stream_header *header)
{
    return header->unit_index >= stream_header_source_units(header);
}

/* Where the (encrypted) payload begins: the 18-byte base, then a type-specific prefix, then the optional
 * extended header. Video adds 3 (size_extension u16 + adaptive_stream_index). Audio adds 2 (a 1-byte
 * "unknown" field plus a 1-byte haptics indicator) - missing the haptics byte misaligns the audio CTR
 * keystream by one and Opus decodes garbage. */
static inline int stream_header_payload_offset(const stream_header *header)
{
    return STREAM_HEADER_LENGTH + (header->type == STREAM_HEADER_TYPE_VIDEO ? 3 : 2) +
        (header->has_extended_header ? 3 : 0);
}

/* Parses the fixed 18-byte header from the front of `packet` (packet_length may exceed 18; only the
 * header is read). Returns 0 if packet_length < STREAM_HEADER_LENGTH. */
int stream_header_parse(const uint8_t *packet, size_t packet_length, stream_header *out);

/* Writes the fixed 18-byte header to `out` (exactly STREAM_HEADER_LENGTH bytes) - the inverse of
 * stream_header_parse, for constructing test fixtures and any future client-originated stream packet
 * (e.g. feedback). The tag region (bytes 10..13, STREAM_HEADER_TAG_OFFSET) is not part of this struct -
 * it is written separately by stream_packet_crypto once the rest of the packet exists - and is zeroed
 * here, matching stream_packet_crypto_compute_tag's own AAD-zeroing convention for that region. Returns 0
 * if header->type is neither STREAM_HEADER_TYPE_VIDEO nor STREAM_HEADER_TYPE_AUDIO, or a field does not
 * fit its wire width. */
int stream_header_build(const stream_header *header, uint8_t out[STREAM_HEADER_LENGTH]);

#endif /* STREAM_HEADER_H */
