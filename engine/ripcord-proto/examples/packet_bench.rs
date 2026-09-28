//! Per-packet stream crypto cost: the workload `src/Ripcord.Mac/RipcordKit/Diagnostics/
//! PacketCryptoBenchmark.swift` runs against the C core, reproduced exactly so the two figures compare.
//!
//! Each iteration does what the demuxer does to every A/V packet: verify the 4-byte GMAC, then
//! CTR-decrypt the payload. 256 distinct packets are sealed up front at key positions 64 payloads
//! apart. That spacing puts every packet in its own GMAC rotation window, so each one pays for a key
//! derivation and a fresh authenticator: the worst case, and the same case the C figure measured.
//!
//! `cargo run --release --example packet_bench [packets] [packet_bytes]`

use std::hint::black_box;
use std::time::Instant;

use ripcord_proto::stream::packet_crypto::{AV_TAG_OFFSET, PacketCrypto};

const HEADER_LENGTH: usize = 18;
const DISTINCT: usize = 256;

fn run(packets: usize, packet_bytes: usize, window_stride: u64) -> f64 {
    let mut ctx = PacketCrypto::new(&[0x5a; 16], &[0xa5; 16]);
    let template: Vec<u8> = (0..packet_bytes).map(|i| (i.wrapping_mul(31)) as u8).collect();

    let mut sealed = Vec::with_capacity(DISTINCT);
    let mut positions = Vec::with_capacity(DISTINCT);
    let mut position = 0u64;
    for _ in 0..DISTINCT {
        let mut packet = template.clone();
        assert!(ctx.seal(position, &mut packet, AV_TAG_OFFSET, false));
        sealed.push(packet);
        positions.push(position);
        position += (packet_bytes - HEADER_LENGTH) as u64 * window_stride;
    }

    let mut working = sealed[0].clone();
    let mut failures = 0usize;
    let started = Instant::now();
    for n in 0..packets {
        let i = n % DISTINCT;
        working.copy_from_slice(&sealed[i]);
        if !ctx.verify(positions[i], &working, AV_TAG_OFFSET, false) {
            failures += 1;
        }
        ctx.crypt_payload(positions[i], &mut working[HEADER_LENGTH..]);
        black_box(&working);
    }
    let elapsed = started.elapsed();
    assert_eq!(failures, 0, "a sealed packet failed to verify: the benchmark is measuring the wrong thing");
    elapsed.as_secs_f64() * 1e6 / packets as f64
}

fn main() {
    let mut args = std::env::args().skip(1);
    let packets: usize = args.next().map_or(20_000, |a| a.parse().expect("packets"));
    let packet_bytes: usize = args.next().map_or(1426, |a| a.parse().expect("packet bytes"));

    for (label, stride) in [
        ("a new rotation window every packet (the C figure's workload)", 64),
        ("wire spacing, one payload apart", 1),
    ] {
        run(2_000, packet_bytes, stride); // warm caches and the branch predictor
        let us = run(packets, packet_bytes, stride);
        let pps = 1e6 / us;
        println!(
            "{label}:\n  {packets} packets of {packet_bytes} bytes: {us:.2} us/packet, {pps:.0} packets/s, {:.0} Mb/s on one core",
            pps * packet_bytes as f64 * 8.0 / 1e6
        );
    }
}
