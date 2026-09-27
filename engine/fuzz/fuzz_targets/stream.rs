//! The A/V plane (libripcord's fuzz_stream.c): headers, and the demuxer's reassembly and FEC across
//! packets, in the clear and through the packet crypto.
#![no_main]
use libfuzzer_sys::fuzz_target;
use ripcord_fuzz::Records;
use ripcord_proto::stream::demux::{DemuxSink, Passthrough, StreamDemux};
use ripcord_proto::stream::header::StreamHeader;
use ripcord_proto::stream::packet_crypto::PacketCrypto;

struct Touch(usize);
impl DemuxSink for Touch {
    fn video_frame(&mut self, d: &[u8], _: bool) {
        self.0 += d.len();
    }
    fn audio_frame(&mut self, d: &[u8]) {
        self.0 += d.len();
    }
}

fuzz_target!(|data: &[u8]| {
    let mut clear = StreamDemux::new(Passthrough);
    let mut sealed = StreamDemux::new(PacketCrypto::new(&[7; 16], &[9; 16]));
    let mut sink = Touch(0);
    for packet in Records::new(data) {
        let _ = StreamHeader::parse(packet);
        clear.ingest(packet, &mut sink);
        sealed.ingest(packet, &mut sink);
    }
    let _ = clear.take_packet_stats();
});
