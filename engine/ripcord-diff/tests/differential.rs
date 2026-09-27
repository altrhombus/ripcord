//! The Rust engine against the C core: the same inputs through both, every output compared. A failure
//! prints the seed and iteration, which replay it exactly.

use ripcord_diff::*;
use ripcord_proto::base64;
use ripcord_proto::halyard::{account_seed, control, registration};
use ripcord_proto::stream::demux::{DemuxSink, Passthrough, StreamDemux};
use ripcord_proto::stream::fec::{self, DecodeScratch};
use ripcord_proto::stream::header::{self, StreamHeader};
use ripcord_proto::stream::packet_crypto::PacketCrypto;

#[test]
fn packet_crypto() {
    let mut rng = Rng::new(0x5eed_0001);
    for iteration in 0..3_000 {
        let (key, iv) = (rng.bytes::<16>(), rng.bytes::<16>());
        let mut rust = PacketCrypto::new(&key, &iv);
        let mut c = CPacketCrypto::new(&key, &iv);
        // Key positions across rotation windows, near the 32-bit edge, and near the 64-bit wrap.
        let key_pos = match rng.below(4) {
            0 => rng.below(200_000),
            1 => (1 << 32) - 100 + rng.below(200),
            2 => u64::MAX - rng.below(64),
            _ => rng.next_u64(),
        };
        let len = rng.below(2_100) as usize;
        let packet = rng.vec(len);
        let offset = rng.below(len as u64 + 8) as usize;
        let zero = rng.below(2) == 1;
        assert_eq!(
            rust.compute_tag(key_pos, &packet, offset, zero),
            c.compute_tag(key_pos, &packet, offset, zero),
            "tag, iteration {iteration}"
        );
        let (mut a, mut b) = (packet.clone(), packet);
        rust.crypt_payload(key_pos, &mut a);
        c.crypt_payload(key_pos, &mut b);
        assert_eq!(a, b, "CTR, iteration {iteration}");
    }
}

#[test]
fn fec_decode() {
    let mut rng = Rng::new(0x5eed_0002);
    let mut scratch = DecodeScratch::default();
    for iteration in 0..800 {
        let k = 1 + rng.below(40) as usize;
        let m = 1 + rng.below(24) as usize;
        let unit = 1 + rng.below(300) as usize;
        let stride = unit + rng.below(20) as usize;
        let mut frame = vec![0u8; (k + m) * stride];
        for u in 0..k {
            rng.fill(&mut frame[u * stride..u * stride + unit]);
        }
        let encoded_rust = fec::encode(&mut frame.clone(), unit, stride, k, m);
        assert_eq!(
            encoded_rust,
            c_fec_encode(&mut frame, unit, stride, k, m),
            "encode accepted, iteration {iteration}"
        );
        // Erasures at every density, including more than m (unrecoverable) and none at all.
        let present: Vec<bool> = (0..k + m).map(|_| rng.below(100) >= rng.below(60)).collect();
        for (u, &p) in present.iter().enumerate() {
            if !p {
                frame[u * stride..(u + 1) * stride].fill(0);
            }
        }
        let (mut a, mut b) = (frame.clone(), frame);
        let ra = fec::decode(&mut scratch, &mut a, unit, stride, k, m, &present);
        let rb = c_fec_decode(&mut b, unit, stride, k, m, &present);
        assert_eq!(ra, rb, "decode result, iteration {iteration} (k {k}, m {m})");
        assert_eq!(a, b, "decoded frame, iteration {iteration}");
    }
}

#[test]
fn control_plane() {
    let mut rng = Rng::new(0x5eed_0003);
    for iteration in 0..3_000 {
        let (nonce, companion) = (rng.bytes::<16>(), rng.bytes::<16>());
        // Selectors past the defined ones exercise the fallbacks.
        let version = rng.below(4) as i32 - 1;
        let codec = rng.below(12) as i32 - 1;
        let rust = control::kdf(&nonce, &companion, version).map(|k| (k.key, k.material));
        assert_eq!(rust, c_kdf(&nonce, &companion, version), "kdf, iteration {iteration}");
        assert_eq!(
            *control::context_key(codec, version).unwrap(),
            c_context_key(codec, version),
            "context key, iteration {iteration}"
        );

        let counter = if rng.below(2) == 0 { rng.below(8) } else { rng.next_u64() };
        let data = rng.vec_of(0, 90);
        let field = control::ControlField::new(&nonce, &companion, codec, version);
        for mode in 0..3 {
            let (mut a, mut b) = (data.clone(), data.clone());
            let c_ok = c_control_crypt(&nonce, &companion, codec, version, counter, mode, &mut b);
            assert_eq!(field.is_some(), c_ok, "field init, iteration {iteration}");
            if let Some(f) = &field {
                match mode {
                    0 => f.encrypt(counter, &mut a),
                    1 => f.decrypt(counter, &mut a),
                    _ => f.streaminfo_crypt(counter, &mut a),
                }
                assert_eq!(a, b, "mode {mode}, iteration {iteration}");
            }
        }
    }
}

#[test]
fn registration() {
    let mut rng = Rng::new(0x5eed_0004);
    for iteration in 0..3_000 {
        let is_ps5 = rng.below(2) == 1;
        // Contexts around the selector offset and the scatter offsets, as well as full-length ones.
        let len = match rng.below(3) {
            0 => rng.below(20) as usize,
            1 => 390 + rng.below(20) as usize,
            _ => registration::CONTEXT_LENGTH,
        };
        let context = rng.vec(len);
        let passcode = rng.next_u64() as u32;
        let input = rng.bytes::<16>();
        assert_eq!(
            registration::derive_key(is_ps5, &context, passcode),
            c_regist_key(is_ps5, &context, passcode),
            "key, iteration {iteration}"
        );
        assert_eq!(
            registration::derive_account_key(is_ps5, &context, &input),
            c_regist_account_key(is_ps5, &context, &input),
            "account key, iteration {iteration}"
        );
        let rust = [
            registration::wrap_material(is_ps5, &input, &context),
            registration::unwrap_material(is_ps5, &input, &context),
            registration::wrap_account_material(is_ps5, &input, &context),
            registration::unwrap_account_material(is_ps5, &input, &context),
        ];
        let c = [
            c_regist_wrap(is_ps5, false, false, &input, &context),
            c_regist_wrap(is_ps5, false, true, &input, &context),
            c_regist_wrap(is_ps5, true, false, &input, &context),
            c_regist_wrap(is_ps5, true, true, &input, &context),
        ];
        assert_eq!(rust, c, "wraps, iteration {iteration}");
    }
}

#[test]
fn account_seed_decoding() {
    let mut rng = Rng::new(0x5eed_0005);
    let alphabet = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/= \n-_";
    for iteration in 0..5_000 {
        let text: Vec<u8> = match rng.below(3) {
            // A genuine double encoding of up to 80 bytes, across the 64-byte bound.
            0 => account_seed::encode_custom_data1(&rng.vec_of(0, 80)).into_bytes(),
            // One layer only, or a genuine encoding with one character changed.
            1 => {
                let mut t = base64::encode(&rng.vec_of(1, 40)).into_bytes();
                if rng.below(2) == 0 {
                    let i = rng.below(t.len() as u64) as usize;
                    t[i] = alphabet[rng.below(alphabet.len() as u64) as usize];
                }
                t
            }
            _ => (0..rng.below(64)).map(|_| alphabet[rng.below(alphabet.len() as u64) as usize]).collect(),
        };
        let rust = account_seed::decode_custom_data1(&text).filter(|v| !v.is_empty());
        assert_eq!(
            rust,
            c_seed_decode(&text),
            "decode, iteration {iteration}, text {:?}",
            String::from_utf8_lossy(&text)
        );
        let (d1, d2, is_ps5) = (rng.bytes::<16>(), rng.bytes::<16>(), rng.below(2) == 1);
        assert_eq!(
            account_seed::recover_custom_data1(is_ps5, &d1, &d2, &text),
            c_seed_recover(is_ps5, &d1, &d2, &text),
            "recover, iteration {iteration}"
        );
    }
}

// ---- the demuxer ----

#[derive(Default)]
struct Capture(Vec<Event>);

impl DemuxSink for Capture {
    fn video_frame(&mut self, data: &[u8], keyframe: bool) {
        self.0.push(Event::Video { data: data.to_vec(), keyframe });
    }
    fn audio_frame(&mut self, data: &[u8]) {
        self.0.push(Event::Audio(data.to_vec()));
    }
    fn video_loss(&mut self, first: u16, last: u16) {
        self.0.push(Event::Loss { first, last });
    }
    fn control_packet(&mut self, header: &StreamHeader, data: &[u8]) {
        self.0.push(Event::Control { packet_type: header.packet_type, data: data.to_vec() });
    }
}

/// A plausible, hostile A/V stream: frames with random geometry, FEC-coded parity (inside and beyond the
/// 64-unit recovery cap), drops, reordering, duplicates, stragglers, index jumps, audio, control packets,
/// truncation and corruption.
fn stream(rng: &mut Rng, sealer: Option<&mut PacketCrypto>) -> Vec<Vec<u8>> {
    let mut packets = Vec::new();
    let mut frame: u16 = rng.next_u64() as u16;
    let mut key_pos: u32 = rng.below(1 << 20) as u32;
    let mut sealer = sealer;
    let mut push = |mut p: Vec<u8>, rng: &mut Rng, packets: &mut Vec<Vec<u8>>| {
        if let Some(s) = sealer.as_deref_mut() {
            p[header::KEY_POSITION_OFFSET..header::LENGTH].copy_from_slice(&key_pos.to_be_bytes());
            let off = StreamHeader::parse(&p).map_or(p.len(), |h| h.payload_offset()).min(p.len());
            s.crypt_payload(u64::from(key_pos), &mut p[off..]);
            s.seal(u64::from(key_pos), &mut p, header::TAG_OFFSET, false);
            key_pos = key_pos.wrapping_add((p.len() - off.min(p.len())) as u32);
        }
        match rng.below(40) {
            0 => p.truncate(rng.below(p.len() as u64) as usize),
            1 => {
                let i = rng.below(p.len() as u64) as usize;
                p[i] ^= 1 << rng.below(8);
            }
            _ => {}
        }
        packets.push(p);
    };

    for _ in 0..40 {
        match rng.below(10) {
            0 => {
                // Audio: several equal units, sometimes the wrong codec.
                let h = StreamHeader {
                    packet_type: header::TYPE_AUDIO,
                    frame_index: frame,
                    total_units: 1 + rng.below(4) as u16,
                    codec: if rng.below(8) == 0 { 3 } else { 5 },
                    has_extended_header: rng.below(6) == 0,
                    ..Default::default()
                };
                let mut p = h.build().unwrap().to_vec();
                p.extend(rng.vec_of(2, 160));
                push(p, rng, &mut packets);
            }
            1 => {
                // A control packet, of a type that is neither video nor audio.
                let mut p = rng.vec_of(header::LENGTH, 40);
                p[0] = [0x05, 0x06, 0x00, 0x0f][rng.below(4) as usize];
                packets.push(p);
            }
            _ => {
                let parity = if rng.below(3) == 0 { 0 } else { 1 + rng.below(12) as usize };
                let source = 1 + if rng.below(8) == 0 { rng.below(140) } else { rng.below(20) } as usize;
                let total = source + parity;
                let unit = 16 + rng.below(1_300) as usize;
                // Coded units: [padding u16][slice], zero-filled to `unit`. Source unit i transmits its
                // first `unit - padding` bytes; parity units transmit all of theirs.
                let mut coded = vec![0u8; total * unit];
                let mut lengths = vec![unit; total];
                for (i, len) in lengths.iter_mut().enumerate().take(source) {
                    let l = 3 + rng.below(unit as u64 - 2) as usize;
                    *len = l;
                    let u = &mut coded[i * unit..(i + 1) * unit];
                    u[..2].copy_from_slice(&((unit - l) as u16).to_be_bytes());
                    rng.fill(&mut u[2..l]);
                    if i == 0 && rng.below(3) == 0 {
                        u[2..7].copy_from_slice(&[0, 0, 0, 1, 0x65]); // an IDR slice
                    }
                }
                if parity > 0 {
                    fec::encode(&mut coded, unit, unit, source, parity);
                }
                let mut order: Vec<usize> = (0..total).collect();
                if rng.below(3) == 0 {
                    for i in (1..order.len()).rev() {
                        order.swap(i, rng.below(i as u64 + 1) as usize);
                    }
                }
                let extended = rng.below(8) == 0;
                for &i in &order {
                    if rng.below(100) < 8 {
                        continue; // dropped
                    }
                    let h = StreamHeader {
                        packet_type: header::TYPE_VIDEO,
                        has_extended_header: extended,
                        frame_index: frame,
                        unit_index: i as u16,
                        total_units: total.min(0x800) as u16,
                        parity_units: parity as u16,
                        ..Default::default()
                    };
                    let mut p = h.build().unwrap().to_vec();
                    p.extend(rng.vec(if extended { 6 } else { 3 }));
                    p.extend_from_slice(&coded[i * unit..i * unit + lengths[i]]);
                    let dup = rng.below(30) == 0;
                    if dup {
                        push(p.clone(), rng, &mut packets);
                    }
                    push(p, rng, &mut packets);
                }
                frame = match rng.below(12) {
                    0 => frame.wrapping_add(2 + rng.below(5) as u16), // frames lost whole
                    1 => frame.wrapping_sub(1 + rng.below(3) as u16), // a straggler run
                    _ => frame.wrapping_add(1),
                };
            }
        }
    }
    packets
}

fn run_demux(seed: u64, real_crypto: bool) -> (Vec<Event>, Stats) {
    let mut rng = Rng::new(seed);
    let (key, iv) = (rng.bytes::<16>(), rng.bytes::<16>());
    let mut sealer = PacketCrypto::new(&key, &iv);
    let packets = stream(&mut rng, real_crypto.then_some(&mut sealer));

    let mut c = CDemux::new(real_crypto.then_some((&key, &iv)));
    let mut rust_events = Capture::default();
    let parameter_sets: &[u8] = match rng.below(3) {
        0 => &[0, 0, 0, 1, 0x67, 0x64, 0, 0, 0, 1, 0x68, 0xee],
        1 => &[0, 0, 0, 1, 0x40, 0x01, 0x0c, 0, 0, 0, 1, 0x42, 0x01, 0x01],
        _ => &[],
    };
    c.set_video_header(parameter_sets);

    let rust_stats = if real_crypto {
        let mut d = StreamDemux::new(PacketCrypto::new(&key, &iv));
        d.set_video_header(parameter_sets);
        assert_eq!(d.video_is_hevc(), c.is_hevc(), "codec classification, seed {seed:#x}");
        for p in &packets {
            d.ingest(p, &mut rust_events);
        }
        let (r, l) = d.take_packet_stats();
        Stats {
            received: r,
            lost: l,
            auth_failures: d.counters().auth_failures,
            frames_too_many_units: d.counters().frames_too_many_units,
        }
    } else {
        let mut d = StreamDemux::new(Passthrough);
        d.set_video_header(parameter_sets);
        for p in &packets {
            d.ingest(p, &mut rust_events);
        }
        let (r, l) = d.take_packet_stats();
        Stats {
            received: r,
            lost: l,
            auth_failures: d.counters().auth_failures,
            frames_too_many_units: d.counters().frames_too_many_units,
        }
    };
    for p in &packets {
        c.ingest(p);
    }
    let c_events = c.take_events();

    if rust_events.0 != c_events {
        let at = rust_events
            .0
            .iter()
            .zip(&c_events)
            .position(|(a, b)| a != b)
            .unwrap_or(rust_events.0.len().min(c_events.len()));
        panic!(
            "events diverge at {at} (seed {seed:#x}, crypto {real_crypto}): rust {} events, C {}\n  rust: {:?}\n  C:    {:?}",
            rust_events.0.len(),
            c_events.len(),
            rust_events.0.get(at).map(summary),
            c_events.get(at).map(summary),
        );
    }
    assert_eq!(rust_stats, c.stats(), "stats, seed {seed:#x}, crypto {real_crypto}");
    (c_events, rust_stats)
}

/// Agreement only means something if the streams reach every path. Two demuxers that both emitted
/// nothing would agree perfectly.
fn assert_coverage(runs: &[(Vec<Event>, Stats)], real_crypto: bool) {
    let count = |f: &dyn Fn(&Event) -> bool| runs.iter().flat_map(|(e, _)| e).filter(|e| f(e)).count();
    let videos = count(&|e| matches!(e, Event::Video { .. }));
    let keyframes = count(&|e| matches!(e, Event::Video { keyframe: true, .. }));
    let audio = count(&|e| matches!(e, Event::Audio(_)));
    let losses = count(&|e| matches!(e, Event::Loss { .. }));
    let controls = count(&|e| matches!(e, Event::Control { .. }));
    let lost: u64 = runs.iter().map(|(_, s)| s.lost).sum();
    let auth: u64 = runs.iter().map(|(_, s)| s.auth_failures).sum();
    eprintln!(
        "coverage (crypto {real_crypto}): {videos} frames ({keyframes} key), {audio} audio, {losses} loss reports, \
         {controls} control, {lost} units lost on the wire, {auth} auth failures"
    );
    assert!(
        videos > 1_000 && keyframes > 50 && audio > 100 && losses > 100 && controls > 100 && lost > 1_000
    );
    if real_crypto {
        assert!(auth > 50, "corrupted packets should fail authentication in both engines");
    }
}

fn summary(e: &Event) -> String {
    match e {
        Event::Video { data, keyframe } => format!("video {} bytes key {keyframe}", data.len()),
        Event::Audio(d) => format!("audio {} bytes", d.len()),
        Event::Loss { first, last } => format!("loss {first}..={last}"),
        Event::Control { packet_type, data } => format!("control {packet_type} {} bytes", data.len()),
    }
}

#[test]
fn demux_passthrough() {
    let runs: Vec<_> = (0..150u64).map(|seed| run_demux(0xde_0000 + seed, false)).collect();
    assert_coverage(&runs, false);
}

#[test]
fn demux_real_crypto() {
    let runs: Vec<_> = (0..150u64).map(|seed| run_demux(0xde_1000 + seed, true)).collect();
    assert_coverage(&runs, true);
}
