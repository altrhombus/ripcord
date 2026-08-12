#include "stream_header.h"

int stream_header_parse(const uint8_t *packet, size_t packet_length, stream_header *out)
{
    uint8_t byte0;
    uint32_t packed;

    if (packet_length < STREAM_HEADER_LENGTH)
        return 0;

    byte0 = packet[0];
    out->type = (uint8_t)(byte0 & 0x0f);
    out->has_extended_header = (byte0 & 0x10) != 0;
    out->packet_index = (uint16_t)(((uint16_t)packet[1] << 8) | packet[2]);
    out->frame_index = (uint16_t)(((uint16_t)packet[3] << 8) | packet[4]);

    /* The bytes-5..8 packed field is laid out differently for video and audio. Video packs three
     * 11/11/10-bit fields; audio packs byte-wide fields (unit_index in the top byte, total_units-1 in the
     * next). Parsing audio with the video layout yields nonsense unit counts. */
    packed = ((uint32_t)packet[5] << 24) | ((uint32_t)packet[6] << 16) |
        ((uint32_t)packet[7] << 8) | (uint32_t)packet[8];

    if (out->type == STREAM_HEADER_TYPE_VIDEO) {
        out->unit_index = (int)(packed >> 21);
        out->total_units = (int)((packed >> 10) & 0x7ffu) + 1;
        out->parity_units = (int)(packed & 0x3ffu);
    } else {
        out->unit_index = (int)((packed >> 24) & 0xffu);
        out->total_units = (int)((packed >> 16) & 0xffu) + 1;
        /* The low 16 bits encode a firmware-specific source/parity/unit-size packing that does not match
         * the earlier protocol revision's layout; the demuxer derives the unit size from the payload
         * length and total_units instead, so this raw value is retained only for diagnostics. */
        out->parity_units = (int)(packed & 0xffffu);
    }

    out->codec = packet[9];
    out->key_position = ((uint32_t)packet[14] << 24) | ((uint32_t)packet[15] << 16) |
        ((uint32_t)packet[16] << 8) | (uint32_t)packet[17];

    return 1;
}

int stream_header_build(const stream_header *header, uint8_t out[STREAM_HEADER_LENGTH])
{
    uint32_t packed;
    uint8_t byte0;

    if (header->type == STREAM_HEADER_TYPE_VIDEO) {
        if (header->unit_index < 0 || header->unit_index > 0x7ff)
            return 0;
        if (header->total_units < 1 || header->total_units > 0x800)
            return 0;
        if (header->parity_units < 0 || header->parity_units > 0x3ff)
            return 0;
        packed = ((uint32_t)header->unit_index << 21) |
            (((uint32_t)(header->total_units - 1) & 0x7ffu) << 10) |
            ((uint32_t)header->parity_units & 0x3ffu);
    } else if (header->type == STREAM_HEADER_TYPE_AUDIO) {
        if (header->unit_index < 0 || header->unit_index > 0xff)
            return 0;
        if (header->total_units < 1 || header->total_units > 0x100)
            return 0;
        if (header->parity_units < 0 || header->parity_units > 0xffff)
            return 0;
        packed = ((uint32_t)header->unit_index << 24) |
            (((uint32_t)(header->total_units - 1) & 0xffu) << 16) |
            ((uint32_t)header->parity_units & 0xffffu);
    } else {
        return 0;
    }

    byte0 = (uint8_t)(header->type & 0x0fu);
    if (header->has_extended_header)
        byte0 = (uint8_t)(byte0 | 0x10u);

    out[0] = byte0;
    out[1] = (uint8_t)(header->packet_index >> 8);
    out[2] = (uint8_t)(header->packet_index & 0xffu);
    out[3] = (uint8_t)(header->frame_index >> 8);
    out[4] = (uint8_t)(header->frame_index & 0xffu);
    out[5] = (uint8_t)(packed >> 24);
    out[6] = (uint8_t)(packed >> 16);
    out[7] = (uint8_t)(packed >> 8);
    out[8] = (uint8_t)packed;
    out[9] = header->codec;
    /* Tag region (STREAM_HEADER_TAG_OFFSET..+3) is not part of this struct - stream_packet_crypto fills it
     * once the rest of the packet exists, over an AAD that expects it zeroed here first. */
    out[10] = 0;
    out[11] = 0;
    out[12] = 0;
    out[13] = 0;
    out[14] = (uint8_t)(header->key_position >> 24);
    out[15] = (uint8_t)(header->key_position >> 16);
    out[16] = (uint8_t)(header->key_position >> 8);
    out[17] = (uint8_t)header->key_position;

    return 1;
}
