//! Splits the multiplexed A/V stream by packet type, authenticates and decrypts each media packet
//! through the [`PacketOpener`] seam, and reassembles video units into whole Annex-B frames, recovering
//! lost source units by FEC when enough survive. Ported from `libripcord/stream/stream_demux.c`, and
//! deliberately exact: the same caps, the same drop rules and the same loss reports, so differential
//! runs against the C core compare outputs directly.
//!
//! Video reassembly keys on the frame index. Units collect in stride-sized slots while the index holds,
//! and the frame is flushed when a new index arrives (spec sec 6.1). Audio carries one Opus frame plus
//! redundant copies of the same 10 ms; only the first unit is emitted.
//!
//! Everything is allocated in [`StreamDemux::new`]. `ingest` never allocates, and a frame whose
//! declared geometry exceeds the caps is skipped rather than grown into.

use super::fec::{self, DecodeScratch};
use super::header::{self, StreamHeader};
use super::packet_crypto::{self, PacketCrypto};

pub const VIDEO_UNIT_PREFIX_LENGTH: usize = 2;
pub const OPUS_CODEC: u8 = 5;
pub const MAX_UNIT_STRIDE: usize = 4096;
/// Slots per frame, which is not the FEC group size: a 1080p frame needs well over a hundred units,
/// and FEC recovers groups of up to [`fec::MAX_TOTAL_UNITS`]. See `stream_demux.h` for what conflating
/// the two cost.
pub const MAX_UNITS_PER_FRAME: usize = 512;
pub const VIDEO_HEADER_CAPACITY: usize = 512;
pub const ASSEMBLY_CAPACITY: usize = MAX_UNITS_PER_FRAME * MAX_UNIT_STRIDE + VIDEO_HEADER_CAPACITY;

/// The crypto seam: verify one media packet's tag and, only if it holds, decrypt
/// `packet[payload_offset..]` in place. `false` drops the packet.
pub trait PacketOpener {
    fn open(&mut self, packet: &mut [u8], key_position: u32, payload_offset: usize) -> bool;
}

/// Identity, matching `PassthroughHalyardSessionCrypto`: lets framing, reassembly and FEC run against
/// plaintext fixtures.
pub struct Passthrough;

impl PacketOpener for Passthrough {
    fn open(&mut self, _: &mut [u8], _: u32, _: usize) -> bool {
        true
    }
}

impl PacketOpener for PacketCrypto {
    fn open(&mut self, packet: &mut [u8], key_position: u32, payload_offset: usize) -> bool {
        // A/V: tag at offset 10, key position not zeroed (confirmed byte for byte against a live
        // capture; see HalyardPacketCrypto.ComputeTag).
        let key_pos = u64::from(key_position);
        if payload_offset > packet.len() || !self.verify(key_pos, packet, header::TAG_OFFSET, false) {
            return false;
        }
        self.crypt_payload(key_pos, &mut packet[payload_offset..]);
        true
    }
}

impl<T: PacketOpener + ?Sized> PacketOpener for &mut T {
    fn open(&mut self, packet: &mut [u8], key_position: u32, payload_offset: usize) -> bool {
        (**self).open(packet, key_position, payload_offset)
    }
}

impl<T: PacketOpener + ?Sized> PacketOpener for Box<T> {
    fn open(&mut self, packet: &mut [u8], key_position: u32, payload_offset: usize) -> bool {
        (**self).open(packet, key_position, payload_offset)
    }
}

/// What the demuxer reports. Every slice is borrowed for the call only; copy what you keep.
pub trait DemuxSink {
    fn video_frame(&mut self, _data: &[u8], _is_keyframe: bool) {}
    fn audio_frame(&mut self, _data: &[u8]) {}
    /// An inclusive range of lost frame indices: a frame flushed incomplete, or the index jumped. The
    /// session reports these (CORRUPT_FRAME) and asks for a fresh IDR.
    fn video_loss(&mut self, _first_frame_index: u16, _last_frame_index: u16) {}
    fn control_packet(&mut self, _header: &StreamHeader, _data: &[u8]) {}
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct DemuxCounters {
    pub auth_failures: u64,
    /// Frames refused because they wanted more than [`MAX_UNITS_PER_FRAME`] slots. Counted, because a
    /// silent refusal looked exactly like a network that had lost everything.
    pub frames_too_many_units: u64,
}

pub struct StreamDemux<O> {
    opener: O,

    video_header: Vec<u8>,
    video_is_hevc: bool,

    assembly: Vec<u8>,
    assembly_overflowed: bool,

    frame_index: Option<u16>,
    frame_allocated: bool,
    source_expected: usize,
    fec_expected: usize,
    fec_actual: usize,
    source_received: usize,
    fec_received: usize,
    unit_padded_size: usize,
    unit_stride: usize,
    slot_buf: Box<[u8]>,
    slot_present: Box<[bool]>,
    slot_data_size: Box<[usize]>,
    scratch: Box<DecodeScratch>,

    units_received: u64,
    units_lost: u64,
    counters: DemuxCounters,
}

/// Whether out-of-band parameter sets are HEVC rather than H.264. Only the parameter sets can decide
/// this: H.264's 1-byte NAL header types them 7 or 8, HEVC's 2-byte header 32, 33 or 34, which H.264
/// cannot express. Slice headers are not safe to classify this way.
fn looks_like_hevc_parameter_sets(header: &[u8]) -> bool {
    let len = header.len();
    let mut i = 0;
    while i + 2 < len {
        if header[i] != 0 || header[i + 1] != 0 {
            i += 1;
            continue;
        }
        let payload = if header[i + 2] == 0x01 {
            i + 3
        } else if header[i + 2] == 0x00 && i + 3 < len && header[i + 3] == 0x01 {
            i + 4
        } else {
            i += 1;
            continue;
        };
        if payload + 1 >= len {
            break;
        }
        let (b0, b1) = (header[payload], header[payload + 1]);
        // The whole 2-byte HEVC header: forbidden bit clear, layer 0, temporal id + 1 == 1. A bare type
        // check lets H.264 bytes such as 0x41 through.
        let hevc_type = (b0 >> 1) & 0x3f;
        if b0 & 0x81 == 0 && b1 == 0x01 && (32..=34).contains(&hevc_type) {
            return true;
        }
        if b0 & 0x80 == 0 && matches!(b0 & 0x1f, 7 | 8) {
            return false;
        }
        i = payload + 1;
    }
    false
}

fn be16(bytes: &[u8]) -> usize {
    usize::from(u16::from_be_bytes([bytes[0], bytes[1]]))
}

impl<O: PacketOpener> StreamDemux<O> {
    pub fn new(opener: O) -> Self {
        Self {
            opener,
            video_header: Vec::with_capacity(VIDEO_HEADER_CAPACITY),
            video_is_hevc: false,
            assembly: Vec::with_capacity(ASSEMBLY_CAPACITY),
            assembly_overflowed: false,
            frame_index: None,
            frame_allocated: false,
            source_expected: 0,
            fec_expected: 0,
            fec_actual: 0,
            source_received: 0,
            fec_received: 0,
            unit_padded_size: 0,
            unit_stride: 0,
            slot_buf: vec![0; MAX_UNITS_PER_FRAME * MAX_UNIT_STRIDE].into_boxed_slice(),
            slot_present: vec![false; MAX_UNITS_PER_FRAME].into_boxed_slice(),
            slot_data_size: vec![0; MAX_UNITS_PER_FRAME].into_boxed_slice(),
            scratch: Box::default(),
            units_received: 0,
            units_lost: 0,
            counters: DemuxCounters::default(),
        }
    }

    pub fn opener_mut(&mut self) -> &mut O {
        &mut self.opener
    }

    /// The SPS/PPS (or VPS/SPS/PPS) from STREAM_INFO, prepended to every emitted keyframe. Also
    /// classifies the stream's codec. Truncated to [`VIDEO_HEADER_CAPACITY`]; empty input is ignored.
    pub fn set_video_header(&mut self, parameter_sets: &[u8]) {
        if parameter_sets.is_empty() {
            return;
        }
        self.video_header.clear();
        self.video_header
            .extend_from_slice(&parameter_sets[..parameter_sets.len().min(VIDEO_HEADER_CAPACITY)]);
        self.video_is_hevc = looks_like_hevc_parameter_sets(&self.video_header);
    }

    pub fn video_is_hevc(&self) -> bool {
        self.video_is_hevc
    }

    pub fn counters(&self) -> DemuxCounters {
        self.counters
    }

    /// The wire unit counts (received, lost) since the last call, for congestion feedback. Resets them.
    pub fn take_packet_stats(&mut self) -> (u64, u64) {
        let stats = (self.units_received, self.units_lost);
        self.units_received = 0;
        self.units_lost = 0;
        stats
    }

    /// Feed one whole UDP payload from the stream port.
    pub fn ingest(&mut self, packet: &[u8], sink: &mut impl DemuxSink) {
        let Some(header) = StreamHeader::parse(packet) else {
            return;
        };
        match header.packet_type {
            header::TYPE_VIDEO => self.ingest_video(&header, packet, sink),
            header::TYPE_AUDIO => self.ingest_audio(&header, packet, sink),
            _ => sink.control_packet(&header, &packet[header.payload_offset().min(packet.len())..]),
        }
    }

    /// Copies, verifies and decrypts a media packet into `opened`, returning its length.
    fn open_media(
        &mut self,
        header: &StreamHeader,
        packet: &[u8],
        opened: &mut [u8; packet_crypto::MAX_PACKET],
    ) -> Option<usize> {
        let buf = opened.get_mut(..packet.len())?;
        buf.copy_from_slice(packet);
        if self.opener.open(buf, header.key_position, header.payload_offset()) {
            Some(packet.len())
        } else {
            self.counters.auth_failures += 1;
            None
        }
    }

    fn ingest_video(&mut self, header: &StreamHeader, packet: &[u8], sink: &mut impl DemuxSink) {
        let payload_offset = header.payload_offset();
        if packet.len() <= payload_offset {
            return;
        }
        let mut opened = [0u8; packet_crypto::MAX_PACKET];
        let Some(opened_len) = self.open_media(header, packet, &mut opened) else {
            return;
        };
        let payload = &opened[payload_offset..opened_len];

        if self.frame_index != Some(header.frame_index) {
            if let Some(current) = self.frame_index {
                // A straggler from an older frame is dropped rather than restarting reassembly.
                if header.frame_index.wrapping_sub(current) >= 0x8000 {
                    return;
                }
                self.check_for_frame_gap(current, header.frame_index, sink);
                self.flush_video_frame(sink);
            }
            self.frame_index = Some(header.frame_index);
            self.allocate_frame(header, payload);
        }

        if self.frame_allocated {
            self.place_unit(header, payload);
        }
    }

    fn check_for_frame_gap(&self, current: u16, new_index: u16, sink: &mut impl DemuxSink) {
        let expected = current.wrapping_add(1);
        let gap = new_index.wrapping_sub(expected);
        if gap > 0 && gap < 0x8000 {
            sink.video_loss(expected, new_index.wrapping_sub(1));
        }
    }

    /// Sizes the slots for a new frame from its first-arriving unit, which fixes the common coded unit
    /// length. Leaves the frame unallocated, and so skipped, on invalid or oversized geometry.
    fn allocate_frame(&mut self, header: &StreamHeader, payload: &[u8]) {
        self.frame_allocated = false;

        let source = header.source_units();
        let fec = usize::from(header.parity_units).max(1);
        let slots = source + fec;
        if source == 0 {
            return;
        }
        if slots > MAX_UNITS_PER_FRAME {
            self.counters.frames_too_many_units += 1;
            return;
        }

        // Source units carry a size extension added to their transmitted size to reach the common
        // coded length; parity units are that length already. Either arrival order gives the same value.
        let mut padded = payload.len();
        if usize::from(header.unit_index) < source && payload.len() >= VIDEO_UNIT_PREFIX_LENGTH {
            padded += be16(payload);
        }
        let stride = (padded + 0xf) & !0xf;
        if stride > MAX_UNIT_STRIDE {
            return;
        }

        self.slot_buf[..slots * stride].fill(0);
        self.slot_present[..slots].fill(false);
        self.slot_data_size[..slots].fill(0);

        self.source_expected = source;
        self.fec_expected = fec;
        self.fec_actual = usize::from(header.parity_units);
        self.unit_padded_size = padded;
        self.unit_stride = stride;
        self.source_received = 0;
        self.fec_received = 0;
        self.frame_allocated = true;
    }

    /// Copies one decrypted unit into its slot, indexed by unit index so arrival order is irrelevant.
    fn place_unit(&mut self, header: &StreamHeader, payload: &[u8]) {
        let idx = usize::from(header.unit_index);
        if idx >= self.source_expected + self.fec_expected
            || self.slot_present[idx]
            || payload.len() > self.unit_padded_size
        {
            return;
        }
        let off = idx * self.unit_stride;
        self.slot_buf[off..off + payload.len()].copy_from_slice(payload);
        self.slot_present[idx] = true;
        self.slot_data_size[idx] = payload.len();
        if idx < self.source_expected {
            self.source_received += 1;
        } else {
            self.fec_received += 1;
        }
    }

    /// Whether the first present source unit's slice is an IDR (H.264 NAL type 5) or an HEVC IRAP
    /// picture (types 16 to 21).
    fn first_source_slice_is_idr(&self) -> bool {
        for i in 0..self.source_expected {
            let size = self.slot_data_size[i];
            if !self.slot_present[i] || size <= VIDEO_UNIT_PREFIX_LENGTH {
                continue;
            }
            let off = i * self.unit_stride;
            let slice = &self.slot_buf[off + VIDEO_UNIT_PREFIX_LENGTH..off + size];
            let p = slice.iter().take_while(|&&b| b == 0).count();
            return match slice.get(p..p + 2) {
                Some(&[0x01, nal]) if self.video_is_hevc => (16..=21).contains(&((nal >> 1) & 0x3f)),
                Some(&[0x01, nal]) => nal & 0x1f == 5,
                _ => false,
            };
        }
        false
    }

    fn append_to_assembly(&mut self, from_slot: Option<(usize, usize)>) {
        if self.assembly_overflowed {
            return;
        }
        let data = match from_slot {
            Some((start, end)) => &self.slot_buf[start..end],
            None => &self.video_header[..],
        };
        if self.assembly.len() + data.len() > ASSEMBLY_CAPACITY {
            self.assembly_overflowed = true;
            return;
        }
        self.assembly.extend_from_slice(data);
    }

    fn flush_video_frame(&mut self, sink: &mut impl DemuxSink) {
        if !self.frame_allocated || self.source_expected == 0 {
            self.frame_allocated = false;
            return;
        }
        let (source, fec, stride, padded) =
            (self.source_expected, self.fec_expected, self.unit_stride, self.unit_padded_size);

        // Wire loss for congestion feedback, before FEC: recovery does not change what the network
        // dropped. fec_actual, not the one-slot minimum, so a frame with no parity is not false loss.
        let expected_units = (source + self.fec_actual) as u64;
        let received_units = (self.source_received + self.fec_received) as u64;
        self.units_received += received_units;
        self.units_lost += expected_units.saturating_sub(received_units);

        // Recovery is bounded by what the FEC module holds, which is fewer units than a frame may occupy.
        if source + fec <= fec::MAX_TOTAL_UNITS
            && self.source_received < source
            && self.source_received + self.fec_received >= source
            && fec::decode(
                &mut self.scratch,
                &mut self.slot_buf,
                padded,
                stride,
                source,
                fec,
                &self.slot_present[..source + fec],
            )
        {
            for i in 0..source {
                if self.slot_present[i] {
                    continue;
                }
                let padding = be16(&self.slot_buf[i * stride..]);
                if padding < padded {
                    self.slot_data_size[i] = padded - padding;
                    self.slot_present[i] = true;
                }
            }
        }

        let is_key = self.first_source_slice_is_idr();

        self.assembly.clear();
        self.assembly_overflowed = false;
        if is_key && !self.video_header.is_empty() {
            self.append_to_assembly(None);
        }
        let mut incomplete = false;
        for i in 0..source {
            if !self.slot_present[i] {
                incomplete = true;
                continue;
            }
            let size = self.slot_data_size[i];
            if size > VIDEO_UNIT_PREFIX_LENGTH {
                self.append_to_assembly(Some((i * stride + VIDEO_UNIT_PREFIX_LENGTH, i * stride + size)));
            }
        }
        self.frame_allocated = false;

        // Slices FEC could not recover: ask for a fresh IDR rather than accumulate corruption.
        if incomplete || self.assembly_overflowed {
            let index = self.frame_index.unwrap_or(0);
            sink.video_loss(index, index);
        }
        if !self.assembly.is_empty() && !self.assembly_overflowed {
            sink.video_frame(&self.assembly, is_key);
        }
    }

    fn ingest_audio(&mut self, header: &StreamHeader, packet: &[u8], sink: &mut impl DemuxSink) {
        let payload_offset = header.payload_offset();
        if packet.len() <= payload_offset || header.codec != OPUS_CODEC {
            return;
        }
        let mut opened = [0u8; packet_crypto::MAX_PACKET];
        let Some(opened_len) = self.open_media(header, packet, &mut opened) else {
            return;
        };
        // total_units equal-size units back to back: unit 0 is the Opus frame, the rest redundant copies
        // for loss concealment. Feeding the decoder all of them corrupts every band above about 5 kHz.
        let unit_size = (opened_len - payload_offset) / usize::from(header.total_units).max(1);
        if unit_size > 0 {
            sink.audio_frame(&opened[payload_offset..payload_offset + unit_size]);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[derive(Default)]
    struct Capture {
        video: Vec<(Vec<u8>, bool)>,
        audio: Vec<Vec<u8>>,
        loss: Vec<(u16, u16)>,
        control: Vec<(u8, Vec<u8>)>,
    }

    impl DemuxSink for Capture {
        fn video_frame(&mut self, data: &[u8], is_keyframe: bool) {
            self.video.push((data.to_vec(), is_keyframe));
        }
        fn audio_frame(&mut self, data: &[u8]) {
            self.audio.push(data.to_vec());
        }
        fn video_loss(&mut self, first: u16, last: u16) {
            self.loss.push((first, last));
        }
        fn control_packet(&mut self, header: &StreamHeader, data: &[u8]) {
            self.control.push((header.packet_type, data.to_vec()));
        }
    }

    /// The fixture `stream_demux_test.c` builds: header, the wire's 3-byte video prefix, then the
    /// demuxer's 2-byte size extension and the slice.
    fn video_packet(
        frame: u16,
        unit: u16,
        total: u16,
        parity: u16,
        extra_padding: u16,
        slice: &[u8],
    ) -> Vec<u8> {
        let h = StreamHeader {
            packet_type: header::TYPE_VIDEO,
            packet_index: unit,
            frame_index: frame,
            unit_index: unit,
            total_units: total,
            parity_units: parity,
            ..Default::default()
        };
        let mut p = h.build().unwrap().to_vec();
        p.extend_from_slice(&[0, 0, 0]);
        p.extend_from_slice(&extra_padding.to_be_bytes());
        p.extend_from_slice(slice);
        p
    }

    fn audio_packet(frame: u16, total: u16, payload: &[u8]) -> Vec<u8> {
        let h = StreamHeader {
            packet_type: header::TYPE_AUDIO,
            packet_index: frame,
            frame_index: frame,
            total_units: total,
            codec: OPUS_CODEC,
            ..Default::default()
        };
        let mut p = h.build().unwrap().to_vec();
        p.extend_from_slice(&[0, 0]);
        p.extend_from_slice(payload);
        p
    }

    #[test]
    fn single_unit_keyframe() {
        let sps_pps = [0x00, 0x00, 0x00, 0x01, 0x67, 0xaa, 0xbb];
        let idr = [0x00, 0x00, 0x00, 0x01, 0x65, 0xde, 0xad, 0xbe, 0xef];
        let mut d = StreamDemux::new(Passthrough);
        let mut c = Capture::default();
        d.set_video_header(&sps_pps);
        assert!(!d.video_is_hevc());

        d.ingest(&video_packet(0, 0, 1, 0, 0, &idr), &mut c);
        assert!(c.video.is_empty(), "a frame flushes only when the next index arrives");
        d.ingest(&video_packet(1, 0, 1, 0, 0, &idr), &mut c);

        assert_eq!(c.video.len(), 1);
        let (data, key) = &c.video[0];
        assert!(key);
        assert_eq!(data, &[&sps_pps[..], &idr[..]].concat());
        assert!(c.loss.is_empty());
    }

    #[test]
    fn frame_with_more_units_than_the_fec_cap() {
        let units = 96u16;
        let mut d = StreamDemux::new(Passthrough);
        let mut c = Capture::default();
        let mut expect = Vec::new();
        for i in 0..units {
            let slice: Vec<u8> = (0..8).map(|b| (i * 8 + b) as u8).collect();
            expect.extend_from_slice(&slice);
            d.ingest(&video_packet(0, i, units, 0, 0, &slice), &mut c);
        }
        d.ingest(&video_packet(1, 0, 1, 0, 0, &[0; 8]), &mut c);
        assert_eq!(c.video.len(), 1);
        assert_eq!(c.video[0].0, expect);
        assert!(c.loss.is_empty());
    }

    #[test]
    fn fec_recovers_dropped_source_units() {
        let (k, m, unit_size) = (4usize, 2usize, 32usize);
        let mut frame = vec![0u8; (k + m) * unit_size];
        let mut expected = Vec::new();
        for u in 0..k {
            for t in 0..30 {
                frame[u * unit_size + 2 + t] = (0x10 * u + t) as u8;
            }
            expected.extend_from_slice(&frame[u * unit_size + 2..(u + 1) * unit_size]);
        }
        assert!(fec::encode(&mut frame, unit_size, unit_size, k, m));

        let mut d = StreamDemux::new(Passthrough);
        let mut c = Capture::default();
        for u in [1usize, 3, 4, 5] {
            let unit = &frame[u * unit_size..(u + 1) * unit_size];
            let pad = u16::from_be_bytes([unit[0], unit[1]]);
            d.ingest(&video_packet(5, u as u16, (k + m) as u16, m as u16, pad, &unit[2..]), &mut c);
        }
        d.ingest(&video_packet(6, 0, 1, 0, 0, b"x"), &mut c);

        assert_eq!(c.video.len(), 1);
        assert!(!c.video[0].1, "no parameter sets, so not flagged as a keyframe");
        assert!(c.loss.is_empty(), "FEC should have recovered the frame");
        assert_eq!(c.video[0].0, expected);
        assert_eq!(d.take_packet_stats(), (4, 2), "wire loss is counted before recovery");
    }

    #[test]
    fn incomplete_frame_reports_loss_but_still_emits() {
        let slice = [0x00, 0x00, 0x00, 0x01, 0x41, 0x01, 0x02, 0x03];
        let mut d = StreamDemux::new(Passthrough);
        let mut c = Capture::default();
        d.ingest(&video_packet(10, 0, 2, 0, 0, &slice), &mut c);
        d.ingest(&video_packet(11, 0, 1, 0, 0, &slice), &mut c);
        assert_eq!(c.video.len(), 1);
        assert_eq!(c.loss, [(10, 10)]);
    }

    #[test]
    fn frame_index_gap_reports_missing_frames() {
        let slice = [0x00, 0x00, 0x00, 0x01, 0x41, 0x01];
        let mut d = StreamDemux::new(Passthrough);
        let mut c = Capture::default();
        d.ingest(&video_packet(20, 0, 1, 0, 0, &slice), &mut c);
        d.ingest(&video_packet(23, 0, 1, 0, 0, &slice), &mut c);
        assert_eq!(c.loss, [(21, 22)]);
    }

    #[test]
    fn frame_index_gap_wraps_at_16_bits() {
        let slice = [0x00, 0x00, 0x01, 0x41, 0x01];
        let mut d = StreamDemux::new(Passthrough);
        let mut c = Capture::default();
        d.ingest(&video_packet(0xfffe, 0, 1, 0, 0, &slice), &mut c);
        d.ingest(&video_packet(1, 0, 1, 0, 0, &slice), &mut c);
        assert_eq!(c.loss, [(0xffff, 0)]);
        // A straggler from before the wrap is dropped, not treated as a new frame.
        d.ingest(&video_packet(0xfffe, 0, 1, 0, 0, &slice), &mut c);
        assert_eq!(c.video.len(), 1);
    }

    #[test]
    fn audio_strips_redundant_units() {
        let payload = [1, 2, 3, 4, 5, 6, 7, 8, 9, 9, 9, 9, 9, 9, 9, 9, 7, 7, 7, 7, 7, 7, 7, 7];
        let mut d = StreamDemux::new(Passthrough);
        let mut c = Capture::default();
        d.ingest(&audio_packet(0, 3, &payload), &mut c);
        assert_eq!(c.audio, [payload[..8].to_vec()]);
    }

    #[test]
    fn control_packet_passthrough() {
        let mut packet = vec![0u8; header::LENGTH + 2];
        packet[0] = 0x05;
        packet.extend_from_slice(&[0xaa, 0xbb, 0xcc, 0xdd]);
        let mut d = StreamDemux::new(Passthrough);
        let mut c = Capture::default();
        d.ingest(&packet, &mut c);
        assert_eq!(c.control, [(0x05, vec![0xaa, 0xbb, 0xcc, 0xdd])]);
    }

    #[test]
    fn classifies_hevc_parameter_sets() {
        let mut d = StreamDemux::new(Passthrough);
        d.set_video_header(&[0, 0, 0, 1, 0x40, 0x01, 0x0c, 0, 0, 1, 0x42, 0x01]);
        assert!(d.video_is_hevc());
        d.set_video_header(&[0, 0, 1, 0x67, 0x42]);
        assert!(!d.video_is_hevc());
    }

    #[test]
    fn real_crypto_round_trip_and_tamper_drop() {
        let key = [0x11; 16];
        let iv = [0x22; 16];
        let mut sender = PacketCrypto::new(&key, &iv);
        let slice = [0x00, 0x00, 0x00, 0x01, 0x65, 0x01, 0x02];
        let mut seal = |frame: u16, key_position: u32| {
            let mut p = video_packet(frame, 0, 1, 0, 0, &slice);
            p[header::KEY_POSITION_OFFSET..header::LENGTH].copy_from_slice(&key_position.to_be_bytes());
            let off = StreamHeader::parse(&p).unwrap().payload_offset();
            sender.crypt_payload(u64::from(key_position), &mut p[off..]);
            assert!(sender.seal(u64::from(key_position), &mut p, header::TAG_OFFSET, false));
            p
        };
        let a = seal(0, 0);
        let mut b = seal(1, 50_000);
        let c_pkt = seal(2, 100_000);

        let mut d = StreamDemux::new(PacketCrypto::new(&key, &iv));
        let mut c = Capture::default();
        d.ingest(&a, &mut c);
        let last = b.len() - 1;
        b[last] ^= 0x80;
        d.ingest(&b, &mut c);
        d.ingest(&c_pkt, &mut c);
        assert_eq!(d.counters().auth_failures, 1);
        assert_eq!(c.video.len(), 1, "frame 0 flushed decrypted; frame 1 was dropped");
        assert_eq!(c.video[0].0, &slice[..]);
        assert_eq!(c.loss, [(1, 1)], "the dropped frame shows up as a gap");
    }

    /// Until cargo-fuzz runs (it needs a nightly toolchain), a deterministic sweep of hostile packets on
    /// stable: valid-looking headers with arbitrary geometry, lengths and bodies, through both seams.
    /// The demuxer must drop what it cannot use and never panic.
    #[test]
    fn random_packets_never_panic() {
        let mut state = 0x9e37_79b9_7f4a_7c15u64;
        let mut next = move || {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            state
        };
        let mut plain = StreamDemux::new(Passthrough);
        let mut real = StreamDemux::new(PacketCrypto::new(&[3; 16], &[4; 16]));
        let mut sink = Capture::default();
        let mut packet = [0u8; 2100];
        for i in 0..20_000 {
            let len = (next() % 2100) as usize;
            for b in packet[..len].iter_mut() {
                *b = next() as u8;
            }
            if len > 0 {
                // Mostly video and audio, so the reassembly paths see the traffic.
                packet[0] = [0x02, 0x12, 0x03, 0x13, 0x05][(next() % 5) as usize];
            }
            if len > 4 && i % 3 == 0 {
                // Hold the frame index for runs of packets, so frames actually assemble and flush.
                packet[3] = 0;
                packet[4] = (i / 40) as u8;
            }
            plain.ingest(&packet[..len], &mut sink);
            real.ingest(&packet[..len], &mut sink);
            if sink.video.len() > 64 {
                sink = Capture::default();
            }
        }
        assert!(real.counters().auth_failures > 0, "random bytes should fail authentication");
    }

    #[test]
    fn hostile_geometry_does_not_panic() {
        let mut d = StreamDemux::new(Passthrough);
        let mut c = Capture::default();
        // Parity above total, a size extension that pushes the stride past the cap, maximal indices.
        for (unit, total, parity, pad) in
            [(0, 1, 0x3ff, 0), (0, 4, 1, 0xffff), (0x7ff, 0x800, 0x3ff, 0), (3, 4, 0, 0)]
        {
            d.ingest(&video_packet(7, unit, total, parity, pad, &[1, 2, 3]), &mut c);
        }
        d.ingest(&video_packet(8, 0, 1, 0, 0, &[1]), &mut c);
        d.ingest(&[0x12; header::LENGTH + 3], &mut c);
        d.ingest(&[0x02; 3000], &mut c);
    }
}
