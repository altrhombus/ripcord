//! The v1 A/V data-packet header, per `docs/protocol/ps5-remoteplay-v1-spec.md` sec 6.1 (multi-byte
//! integers big-endian). Ported from `libripcord/stream/stream_header.c`.
//!
//! ```text
//! off 0      u8   low nibble = type (2 video, 3 audio); bit 4 = extended-header flag
//! off 1..2   u16  packet_index
//! off 3..4   u16  frame_index
//! off 5..8   u32  video: [31:21] unit_index | [20:10] total_units-1 | [9:0] parity_units
//!                 audio: byte-wide fields (see `parse`)
//! off 9      u8   codec
//! off 10..13      4-byte GMAC tag (zeroed for the GMAC computation)
//! off 14..17 u32  key position (a running byte counter that drives the nonce, not a timestamp)
//! off 18..        payload, after a type-specific prefix and the optional extended header
//! ```
//!
//! There is deliberately no FEC packet type. A byte 0 of `0x12` is video (low nibble 2) with the
//! extended-header bit set; parity units are video packets whose unit index is at or above the source
//! unit count. Use [`StreamHeader::is_parity_unit`].

pub const LENGTH: usize = 18;
pub const TYPE_VIDEO: u8 = 0x02;
pub const TYPE_AUDIO: u8 = 0x03;
pub const TAG_OFFSET: usize = 10;
pub const KEY_POSITION_OFFSET: usize = 14;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct StreamHeader {
    /// The low nibble of byte 0. [`TYPE_VIDEO`], [`TYPE_AUDIO`], or anything else, which the demuxer
    /// routes as a control packet.
    pub packet_type: u8,
    pub has_extended_header: bool,
    pub packet_index: u16,
    pub frame_index: u16,
    pub unit_index: u16,
    /// 1..=0x800 for video, 1..=0x100 for audio.
    pub total_units: u16,
    /// Video: the frame's parity unit count. Audio: the raw low 16 bits, kept for diagnostics only.
    pub parity_units: u16,
    pub codec: u8,
    pub key_position: u32,
}

impl StreamHeader {
    /// Source units in the frame: total minus parity. Negative geometry (more parity than total) is
    /// reported as zero, which every caller treats as "not a frame".
    pub fn source_units(&self) -> usize {
        usize::from(self.total_units).saturating_sub(usize::from(self.parity_units))
    }

    /// Whether this unit is one of the frame's Reed-Solomon parity units: a function of the unit index,
    /// not of the packet type.
    pub fn is_parity_unit(&self) -> bool {
        usize::from(self.unit_index) >= self.source_units()
    }

    /// Where the (encrypted) payload begins: the 18-byte base, then 3 bytes for video (size extension
    /// u16 plus adaptive stream index) or 2 for audio (an unknown byte plus the haptics indicator, whose
    /// omission misaligns the audio keystream by one), then 3 more if the extended header is present.
    pub fn payload_offset(&self) -> usize {
        LENGTH
            + if self.packet_type == TYPE_VIDEO { 3 } else { 2 }
            + if self.has_extended_header { 3 } else { 0 }
    }

    /// Parses the fixed header from the front of `packet`. `None` if it is shorter than [`LENGTH`].
    pub fn parse(packet: &[u8]) -> Option<Self> {
        let h: &[u8; LENGTH] = packet.get(..LENGTH)?.try_into().ok()?;
        let packet_type = h[0] & 0x0f;
        let packed = u32::from_be_bytes([h[5], h[6], h[7], h[8]]);

        // The bytes 5..8 field is laid out differently for video and audio; parsing audio with the
        // video layout yields nonsense unit counts.
        let (unit_index, total_units, parity_units) = if packet_type == TYPE_VIDEO {
            (packed >> 21, ((packed >> 10) & 0x7ff) + 1, packed & 0x3ff)
        } else {
            // The low 16 bits are a firmware-specific packing; the demuxer derives the audio unit size
            // from the payload length instead, so this is retained raw.
            ((packed >> 24) & 0xff, ((packed >> 16) & 0xff) + 1, packed & 0xffff)
        };

        Some(Self {
            packet_type,
            has_extended_header: h[0] & 0x10 != 0,
            packet_index: u16::from_be_bytes([h[1], h[2]]),
            frame_index: u16::from_be_bytes([h[3], h[4]]),
            // Each is at most 12 bits (video) or 16 bits (audio parity), so the narrowing is exact.
            unit_index: unit_index as u16,
            total_units: total_units as u16,
            parity_units: parity_units as u16,
            codec: h[9],
            key_position: u32::from_be_bytes([h[14], h[15], h[16], h[17]]),
        })
    }

    /// The inverse of [`parse`](Self::parse), for fixtures and client-originated packets. The tag region
    /// is written as zeros, which is what the AAD expects before the tag is computed. `None` if the type
    /// is neither video nor audio, or a field does not fit its wire width.
    pub fn build(&self) -> Option<[u8; LENGTH]> {
        let unit = u32::from(self.unit_index);
        let total = u32::from(self.total_units);
        let parity = u32::from(self.parity_units);
        let packed = match self.packet_type {
            TYPE_VIDEO => {
                if unit > 0x7ff || !(1..=0x800).contains(&total) || parity > 0x3ff {
                    return None;
                }
                (unit << 21) | ((total - 1) << 10) | parity
            }
            TYPE_AUDIO => {
                if unit > 0xff || !(1..=0x100).contains(&total) {
                    return None;
                }
                (unit << 24) | ((total - 1) << 16) | parity
            }
            _ => return None,
        };

        let mut out = [0u8; LENGTH];
        out[0] = (self.packet_type & 0x0f) | if self.has_extended_header { 0x10 } else { 0 };
        out[1..3].copy_from_slice(&self.packet_index.to_be_bytes());
        out[3..5].copy_from_slice(&self.frame_index.to_be_bytes());
        out[5..9].copy_from_slice(&packed.to_be_bytes());
        out[9] = self.codec;
        out[KEY_POSITION_OFFSET..LENGTH].copy_from_slice(&self.key_position.to_be_bytes());
        Some(out)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn round_trip(label: &str, h: &StreamHeader) {
        let bytes = h.build().unwrap_or_else(|| panic!("{label}: build failed"));
        assert_eq!(&bytes[TAG_OFFSET..TAG_OFFSET + 4], &[0; 4], "{label}: tag region not zeroed");
        assert_eq!(StreamHeader::parse(&bytes).as_ref(), Some(h), "{label}: round trip");
    }

    #[test]
    fn video_round_trips() {
        let mut h = StreamHeader {
            packet_type: TYPE_VIDEO,
            packet_index: 1,
            frame_index: 7,
            total_units: 12,
            parity_units: 4,
            codec: 1,
            key_position: 0x1000,
            ..Default::default()
        };
        round_trip("video basic", &h);

        h = StreamHeader {
            has_extended_header: true,
            packet_index: 0xffff,
            frame_index: 0xffff,
            unit_index: 0x7ff,
            total_units: 0x800,
            parity_units: 0x3ff,
            codec: 0xff,
            key_position: u32::MAX,
            ..h
        };
        round_trip("video max fields", &h);

        h = StreamHeader { has_extended_header: false, unit_index: 8, total_units: 12, parity_units: 4, ..h };
        assert_eq!(h.source_units(), 8);
        assert!(h.is_parity_unit());
        h.unit_index = 7;
        assert!(!h.is_parity_unit());
    }

    #[test]
    fn audio_round_trips() {
        let mut h = StreamHeader {
            packet_type: TYPE_AUDIO,
            packet_index: 42,
            frame_index: 99,
            total_units: 2,
            parity_units: 0x1234,
            codec: 5,
            key_position: 0xdead_beef,
            ..Default::default()
        };
        round_trip("audio basic", &h);
        h = StreamHeader {
            has_extended_header: true,
            unit_index: 0xff,
            total_units: 0x100,
            parity_units: 0xffff,
            ..h
        };
        round_trip("audio max fields", &h);
    }

    #[test]
    fn payload_offset() {
        let mut h = StreamHeader { packet_type: TYPE_VIDEO, ..Default::default() };
        assert_eq!(h.payload_offset(), 21);
        h.has_extended_header = true;
        assert_eq!(h.payload_offset(), 24);
        h = StreamHeader { packet_type: TYPE_AUDIO, ..Default::default() };
        assert_eq!(h.payload_offset(), 20);
        h.has_extended_header = true;
        assert_eq!(h.payload_offset(), 23);
    }

    #[test]
    fn rejects_short_packet_and_bad_fields() {
        assert_eq!(StreamHeader::parse(&[0; LENGTH - 1]), None);
        assert_eq!(StreamHeader { packet_type: 0x0f, total_units: 1, ..Default::default() }.build(), None);
        assert_eq!(
            StreamHeader { packet_type: TYPE_VIDEO, unit_index: 0x800, total_units: 1, ..Default::default() }
                .build(),
            None
        );
        assert_eq!(
            StreamHeader { packet_type: TYPE_VIDEO, total_units: 0, ..Default::default() }.build(),
            None
        );
    }
}
