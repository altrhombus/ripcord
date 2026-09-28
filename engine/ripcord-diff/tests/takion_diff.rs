//! Takion, the control protobuf codec, the 9303 wire and the scripted console: the Rust engine against the
//! C core, on valid messages and on their mutations. A failure prints the seed and iteration.

use ripcord_diff::*;
use ripcord_proto::dgram::wire;
use ripcord_proto::takion::{chunks, control, sealer, senkusha};
use ripcord_proto::testing::scripted_console::ScriptedConsole;

/// A valid input, then (most of the time) damaged: truncated, a byte flipped, or bytes appended.
fn mutate(rng: &mut Rng, mut v: Vec<u8>) -> Vec<u8> {
    match rng.below(5) {
        0 if !v.is_empty() => v.truncate(rng.below(v.len() as u64) as usize),
        1 if !v.is_empty() => {
            let i = rng.below(v.len() as u64) as usize;
            v[i] ^= 1 << rng.below(8);
        }
        2 => v.extend(rng.vec_of(1, 8)),
        _ => {}
    }
    v
}

fn protobuf_sample(rng: &mut Rng) -> Vec<u8> {
    let bytes = |rng: &mut Rng| rng.vec_of(0, 40);
    match rng.below(9) {
        0 => {
            let (sk, spec, pk, sig) = (bytes(rng), bytes(rng), bytes(rng), bytes(rng));
            control::SessionRequest {
                client_version: rng.next_u64() as u32,
                session_key: &sk,
                launch_spec: &spec,
                encrypted_key: &[0; 4],
                ecdh_public_key: (rng.below(2) == 0).then_some(&pk[..]),
                ecdh_signature: (rng.below(2) == 0).then_some(&sig[..]),
            }
            .build()
        }
        1 | 2 => {
            let (sk, vs, pk, sig) = (bytes(rng), bytes(rng), bytes(rng), bytes(rng));
            control::SessionReply {
                server_version: rng.next_u64() as u32,
                token: rng.below(1 << 20) as u32,
                encrypted_key_accepted: rng.below(2) == 1,
                version_accepted: rng.below(2) == 1,
                session_key: &sk,
                server_version_string: (rng.below(2) == 0).then_some(&vs[..]),
                ecdh_public_key: (rng.below(2) == 0).then_some(&pk[..]),
                ecdh_signature: (rng.below(2) == 0).then_some(&sig[..]),
            }
            .build()
        }
        3 => control::build_disconnect(&bytes(rng)),
        4 => control::build_protocol_version_request(&[9, 13, 17]).unwrap(),
        5 => {
            // A PROTOCOL_VERSION_ACK and a STREAM_INFO, built by hand from the schema's field numbers.
            let v = rng.below(300) as u8;
            vec![0x08, 0x20, 0x82, 0x02, 0x03, 0x08, v, 0x01]
        }
        6 => {
            let vh = bytes(rng);
            let mut res = vec![0x08, 0x80, 0x0f, 0x10, 0xb8, 0x08, 0x1a, vh.len() as u8];
            res.extend(&vh);
            let mut payload = vec![0x0a, res.len() as u8];
            payload.extend(&res);
            payload.extend([0x12, 0x02, 0xaa, 0xbb]);
            let mut m = vec![0x08, 0x0d, 0x7a, payload.len() as u8];
            m.extend(&payload);
            m
        }
        7 => control::build_connection_quality(rng.below(100_000) as u32, 3.5, 0.25),
        _ => rng.vec_of(0, 60),
    }
}

#[test]
fn control_protobuf() {
    let mut rng = Rng::new(0x5eed_0101);
    let mut parsed = [0usize; 4];
    for iteration in 0..20_000 {
        let base = protobuf_sample(&mut rng);
        let m = mutate(&mut rng, base);
        let at = format!("iteration {iteration}");
        assert_eq!(control::peek_type(&m), c_control_peek(&m), "peek, {at}");
        assert_eq!(control::validate(&m), c_control_validate(&m), "validate, {at}");
        let rust = control::parse_session_reply(&m).map(|r| {
            let n = [
                r.server_version,
                r.token,
                u32::from(r.encrypted_key_accepted),
                u32::from(r.version_accepted),
            ];
            let own = |x: Option<&[u8]>| x.map(<[u8]>::to_vec);
            (
                n,
                Some(r.session_key.to_vec()),
                own(r.server_version_string),
                own(r.ecdh_public_key),
                own(r.ecdh_signature),
            )
        });
        assert_eq!(rust, c_control_reply(&m), "session reply, {at}");
        let si = control::parse_stream_info(&m).map(|i| {
            (
                [i.width, i.height, u32::from(i.has_resolution)],
                i.video_header.to_vec(),
                i.audio_header.to_vec(),
            )
        });
        assert_eq!(si, c_control_stream_info(&m), "stream info, {at}");
        assert_eq!(
            control::parse_disconnect(&m).map(<[u8]>::to_vec),
            c_control_disconnect(&m),
            "disconnect, {at}"
        );
        assert_eq!(control::parse_protocol_version_ack(&m), c_control_version_ack(&m), "version ack, {at}");
        parsed[0] += usize::from(rust.is_some());
        parsed[1] += usize::from(si.is_some());
        parsed[2] += usize::from(control::parse_disconnect(&m).is_some());
        parsed[3] += usize::from(control::parse_protocol_version_ack(&m).is_some());
    }
    // Agreement on "none of it parsed" would prove nothing.
    assert!(parsed.iter().all(|&n| n > 300), "each parser accepted a real share of inputs: {parsed:?}");
}

#[test]
fn takion_chunks() {
    let mut rng = Rng::new(0x5eed_0102);
    let mut parsed = [0usize; 3];
    for iteration in 0..20_000 {
        let payload = rng.vec_of(0, 30);
        let tsn = rng.next_u64() as u32;
        let base = match rng.below(5) {
            0 => chunks::build_data_first(tsn, rng.below(20) as u16, rng.below(2) == 1, &payload).unwrap(),
            1 => chunks::build_data_continuation(tsn, rng.below(20) as u16, rng.below(2) == 1, &payload)
                .unwrap(),
            2 => chunks::build_sack(tsn, chunks::INIT_A_RWND),
            3 => chunks::build_init_ack(tsn, rng.next_u64() as u32, &rng.bytes::<32>()),
            _ => rng.vec_of(0, 40),
        };
        let c = mutate(&mut rng, base);
        let at = format!("iteration {iteration}");
        for first in [true, false] {
            let parse = if first { chunks::parse_data_first } else { chunks::parse_data_continuation };
            let rust = parse(&c).map(|d| (d.tsn, d.channel, d.ending, d.payload.to_vec()));
            assert_eq!(rust, c_takion_data(first, &c), "data (first {first}), {at}");
        }
        let sack = chunks::parse_sack(&c)
            .map(|s| [s.cumulative_tsn_ack, s.a_rwnd, s.gap_ack_blocks.into(), s.duplicate_tsns.into()]);
        assert_eq!(sack, c_takion_sack(&c), "sack, {at}");
        let ack = chunks::parse_init_ack(&c).map(|a| (a.server_tag, a.initial_tsn, a.cookie));
        assert_eq!(ack, c_takion_init_ack(&c), "init ack, {at}");
        assert_eq!(senkusha::echo_sequence(&c), c_senkusha_echo(&c), "senkusha, {at}");
        parsed[0] += usize::from(chunks::parse_data_first(&c).is_some());
        parsed[1] += usize::from(sack.is_some());
        parsed[2] += usize::from(ack.is_some());
    }
    assert!(parsed.iter().all(|&n| n > 1_000), "each parser accepted a real share of inputs: {parsed:?}");
}

#[test]
fn sealer_sequences_and_verification() {
    let mut rng = Rng::new(0x5eed_0103);
    for iteration in 0..500 {
        let (key, iv) = (rng.bytes::<16>(), rng.bytes::<16>());
        let mut packets: Vec<(i32, Vec<u8>, usize)> = (0..rng.below(12) + 1)
            .map(|_| {
                let kind = rng.below(3) as i32;
                let p = rng.vec_of(0, 200);
                let offset = rng.below(p.len() as u64 + 3) as usize;
                (kind, p, offset)
            })
            .collect();
        let mut rust = packets.clone();
        c_seal_sequence(&key, &iv, &mut packets);
        let mut s = sealer::Sealer::new(&key, &iv);
        for (kind, p, offset) in rust.iter_mut() {
            match kind {
                0 => s.seal_control(p),
                1 => s.seal_congestion(p),
                _ => s.seal_input(p, *offset),
            }
        }
        assert_eq!(rust, packets, "sealed bytes, iteration {iteration}");
        for (kind, p, _) in &rust {
            if *kind == 0 {
                let d = mutate(&mut rng, p.clone());
                assert_eq!(
                    sealer::Verifier::new(&key, &iv).check(&d),
                    c_verify_control(&key, &iv, &d),
                    "verify, iteration {iteration}"
                );
            }
        }
    }
}

#[test]
fn dgram_wire() {
    let mut rng = Rng::new(0x5eed_0104);
    for iteration in 0..20_000 {
        let base = match rng.below(4) {
            0 => wire::Prelude {
                kind: [6, 7, 5][rng.below(3) as usize],
                sender_id: rng.bytes(),
                peer_id: rng.bytes(),
                tag_pair: rng.next_u64() as u32,
                request_word: rng.below(0x80) as u32,
                token: rng.next_u64() as u32,
                tail: rng.bytes(),
            }
            .write()
            .to_vec(),
            1 => {
                let mut d = Vec::new();
                for _ in 0..rng.below(4) + 1 {
                    let body = rng.vec_of(0, 20);
                    d.extend(
                        wire::write_chunk(
                            [0x02, 0x20, 0x80, 0xC0][rng.below(4) as usize],
                            0x30,
                            &body,
                            1 + rng.below(3) as usize,
                        )
                        .unwrap(),
                    );
                }
                d
            }
            2 => {
                let len = rng.below(12);
                let body = "x".repeat(rng.below(14) as usize);
                format!("HTTP/1.1 200 OK\r\nContent-Length: {len}\r\nX: y\r\n\r\n{body}").into_bytes()
            }
            _ => rng.vec_of(0, 100),
        };
        let d = mutate(&mut rng, base);
        let at = format!("iteration {iteration}");
        assert_eq!(wire::Prelude::parse(&d).map(|p| p.write()), c_dgram_prelude(&d), "prelude, {at}");
        let rust: Vec<_> = wire::chunks(&d)
            .map(|c| (c.kind, c.flags, c.body.to_vec(), c.source_port, c.destination_port, c.word_count))
            .collect();
        assert_eq!(rust, c_dgram_chunks(&d), "chunks, {at}");
        assert_eq!(
            wire::http_complete(&d),
            c_http_complete(&d),
            "http, {at}: {:?}",
            String::from_utf8_lossy(&d)
        );
    }
}

/// The client's side of a 9303 exchange, scripted loosely: every step a client takes, in plausible and
/// implausible orders, with some datagrams damaged.
fn client_datagram(rng: &mut Rng, seq: &mut u16) -> Vec<u8> {
    let data = |seq: &mut u16, payload: &[u8]| {
        *seq = seq.wrapping_add(1);
        let mut body = seq.to_be_bytes().to_vec();
        body.extend_from_slice(payload);
        wire::write_chunk(wire::CHUNK_DATA, 0x30, &body, 3).unwrap()
    };
    let d = match rng.below(9) {
        0 => wire::Prelude {
            kind: wire::PRELUDE_INIT,
            tag_pair: rng.next_u64() as u32,
            token: rng.next_u64() as u32,
            ..Default::default()
        }
        .write()
        .to_vec(),
        1 => wire::write_chunk(wire::CHUNK_HELLO, 0, &rng.vec_of(14, 1), 3).unwrap(),
        2 => wire::write_chunk(wire::CHUNK_HELLO_ECHO, 0, &rng.vec_of(2, 4), 3).unwrap(),
        3 => wire::write_chunk(wire::CHUNK_CLOSE, 0, &[0; 8], 3).unwrap(),
        4 => data(seq, b"GET /sie/ps5/rp/sess/init HTTP/1.1\r\nHost: x\r\n\r\n"),
        5 => data(seq, b"GET /sie/ps5/rp/sess/ctrl HTTP/1.1\r\n\r\n"),
        6 => data(seq, b"POST /sie/ps5/rp/sess/rgst HTTP/1.1\r\nContent-Length: 4\r\n\r\nabcd"),
        7 => {
            let frame = rng.vec_of(1, 320);
            data(seq, &frame)
        }
        _ => rng.vec_of(0, 90),
    };
    mutate(rng, d)
}

#[test]
fn scripted_console_matches_the_c_fixture() {
    for seed in 0..200u64 {
        let mut rng = Rng::new(0xc0_0000 + seed);
        let init = (rng.below(3) > 0)
            .then_some("HTTP/1.1 200 OK\r\nRP-Nonce: AAAAAAAAAAAAAAAAAAAAAA==\r\nContent-Length: 0\r\n\r\n");
        let other = (rng.below(2) == 0).then_some("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n");
        let close = rng.below(4) == 0;
        let mut c = CConsole::new(init, other, close);
        let mut r = ScriptedConsole::new();
        r.init_reply = init.map(|s| s.as_bytes().to_vec());
        r.other_reply = other.map(|s| s.as_bytes().to_vec());
        r.close_before_answering = close;
        let mut seq = 0u16;
        for step in 0..60 {
            let d = client_datagram(&mut rng, &mut seq);
            assert_eq!(r.on_datagram(&d), c.on_datagram(&d), "replies, seed {seed} step {step}");
        }
        let counts = c.counts();
        let rust =
            [r.inits, r.hellos, r.closes_received, r.request_count, usize::from(r.ctrl_open), r.frames.len()]
                .map(|n| n as i32);
        assert_eq!(rust, counts, "counts, seed {seed}");
        for (i, f) in r.frames.iter().enumerate() {
            assert_eq!(*f, c.frame(i), "frame {i}, seed {seed}");
        }
        for (i, q) in r.requests.iter().enumerate() {
            assert_eq!(*q, c.request(i), "request {i}, seed {seed}");
        }
    }
}
