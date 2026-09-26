//! STUN, the candidate choice and the 9303 association: the Rust engine against the C core. The
//! association runs generated operation sequences with both engines on the same counting random source,
//! the transcripts' own, well past what the .NET transcripts cover.

use ripcord_diff::*;
use ripcord_proto::dgram::assoc::{Addressing, Association, Event, Phase};
use ripcord_proto::dgram::wire::{self, Prelude};
use ripcord_proto::net::{candidates, stun};

#[test]
fn stun_parsing() {
    let mut rng = Rng::new(0x5eed_0301);
    let mut with_address = 0;
    for iteration in 0..20_000 {
        let txid = rng.bytes::<12>();
        let mut attrs = Vec::new();
        for _ in 0..rng.below(4) {
            let kind: u16 = [0x0001, 0x0020, 0x8020, 0x8022, 0x0006][rng.below(5) as usize];
            let family = [1u8, 2, 3][rng.below(3) as usize];
            let mut v = vec![0, family];
            v.extend(rng.bytes::<2>());
            v.extend(rng.vec(if family == 2 { 16 } else { 4 }));
            if rng.below(6) == 0 {
                v.truncate(rng.below(v.len() as u64) as usize);
            }
            attrs.extend(kind.to_be_bytes());
            attrs.extend((v.len() as u16).to_be_bytes());
            attrs.extend(&v);
            attrs.resize(attrs.len().next_multiple_of(4), 0);
        }
        let mut m = [0x0101u16, 0x0001, 0x0111, 0xC101][rng.below(4) as usize].to_be_bytes().to_vec();
        m.extend((attrs.len() as u16).to_be_bytes());
        m.extend(if rng.below(10) == 0 { rng.bytes::<4>() } else { stun::MAGIC_COOKIE.to_be_bytes() });
        m.extend(txid);
        m.extend(attrs);
        if rng.below(8) == 0 {
            m.truncate(rng.below(m.len() as u64) as usize);
        }
        let rust = stun::parse(&m).map(|p| {
            let mapped = p.mapped.map(|a| match a {
                stun::Address::V4(b, port) => (1u8, port, b.to_vec()),
                stun::Address::V6(b, port) => (2u8, port, b.to_vec()),
            });
            (p.kind, p.transaction_id, mapped)
        });
        assert_eq!(rust, c_stun_parse(&m), "iteration {iteration}");
        with_address += usize::from(rust.as_ref().is_some_and(|r| r.2.is_some()));
    }
    assert!(with_address > 3_000, "{with_address} messages carried an address");
}

#[test]
fn candidate_choice() {
    let mut rng = Rng::new(0x5eed_0302);
    let pool = [
        "192.168.1.20",
        "10.0.0.7",
        "203.0.113.9",
        "console.local",
        "1.2.3",
        "256.1.1.1",
        "192.168.1.020",
        "",
    ];
    for iteration in 0..10_000 {
        let n = rng.below(5) as usize;
        let addresses: Vec<&str> = (0..n).map(|_| pool[rng.below(pool.len() as u64) as usize]).collect();
        let nics: Vec<([u8; 4], [u8; 4])> = (0..rng.below(3))
            .map(|_| {
                (
                    [[192, 168, 1, 5], [10, 0, 0, 1], [172, 31, 0, 1]][rng.below(3) as usize],
                    [[255, 255, 255, 0], [255, 0, 0, 0], [0; 4]][rng.below(3) as usize],
                )
            })
            .collect();
        let host = (rng.below(2) == 0).then_some("console.local");
        let cands: Vec<candidates::Candidate> = addresses
            .iter()
            .map(|a| candidates::Candidate { kind: "X".into(), address: (*a).into(), port: 9303 })
            .collect();
        let ifaces: Vec<candidates::Interface> =
            nics.iter().map(|&(address, netmask)| candidates::Interface { address, netmask }).collect();
        let rust = candidates::choose(&cands, &ifaces, host).map_or(-1, |i| i as i32);
        assert_eq!(rust, c_choose_candidate(&addresses, &nics, host), "iteration {iteration}: {addresses:?}");
        for a in &addresses {
            assert_eq!(candidates::parse_ipv4(a), c_parse_ipv4(a), "parse {a:?}");
        }
    }
}

fn counting() -> ripcord_proto::RandomSource {
    let mut next = 0x40u8;
    Box::new(move |b: &mut [u8]| {
        b.iter_mut().for_each(|x| {
            *x = next;
            next = next.wrapping_add(1)
        })
    })
}

fn phase_index(p: Phase) -> i32 {
    match p {
        Phase::Idle => 0,
        Phase::Handshaking => 1,
        Phase::Established => 2,
        Phase::Connected => 3,
        Phase::Closed => 4,
    }
}

fn event_name(e: &Event) -> String {
    match e {
        Event::PreludeEstablished => "established".into(),
        Event::ConnectionOpened { by_peer } => format!("opened:{}", u8::from(*by_peer)),
        Event::DataReceived { data, .. } => {
            format!("data:{}", data.iter().map(|b| format!("{b:02x}")).collect::<String>())
        }
        Event::PeerClosed => "closed".into(),
        Event::Unhandled { .. } => "unhandled".into(),
    }
}

/// A peer's datagram, plausible or broken: preludes of either kind, and chunk bundles of every type.
fn peer_datagram(rng: &mut Rng, tag_pair: u32, seq: &mut u16) -> Vec<u8> {
    match rng.below(10) {
        0 | 1 => Prelude {
            kind: if rng.below(2) == 0 { wire::PRELUDE_INIT } else { wire::PRELUDE_COOKIE_ECHO },
            tag_pair: if rng.below(3) == 0 { rng.next_u64() as u32 } else { wire::swap_halves(tag_pair) },
            token: rng.next_u64() as u32,
            ..Default::default()
        }
        .write()
        .to_vec(),
        9 => rng.vec_of(0, 40),
        _ => {
            let mut d = Vec::new();
            for _ in 0..rng.below(3) + 1 {
                let kind = [
                    wire::CHUNK_DATA,
                    wire::CHUNK_DATA_RETRANSMIT,
                    wire::CHUNK_ACK,
                    wire::CHUNK_HELLO,
                    wire::CHUNK_HELLO_ECHO,
                    wire::CHUNK_ACCEPT,
                    wire::CHUNK_CLOSE,
                    wire::CHUNK_COOKIE,
                    0x55,
                ][rng.below(9) as usize];
                let mut body = Vec::new();
                if matches!(kind, wire::CHUNK_DATA | wire::CHUNK_DATA_RETRANSMIT) {
                    *seq = if rng.below(4) == 0 {
                        seq.wrapping_sub(rng.below(3) as u16)
                    } else {
                        seq.wrapping_add(1)
                    };
                    body.extend(seq.to_be_bytes());
                    if kind == wire::CHUNK_DATA_RETRANSMIT {
                        body.extend([0; 6]);
                    }
                }
                body.extend(rng.vec_of(0, 24));
                d.extend(wire::write_chunk(kind, 0x30, &body, 1 + rng.below(3) as usize).unwrap());
            }
            d
        }
    }
}

#[test]
fn association_sequences() {
    let (local, peer, address, port) = ([1u8; 20], [2u8; 20], [192, 0, 2, 7], 9303);
    let mut totals = [0usize; 3];
    for seed in 0..300u64 {
        let mut rng = Rng::new(0xa55_0000 + seed);
        let mut c = CAssoc::new(&local, &peer, address, port);
        let mut r = Association::new(counting(), local, peer, address, port);
        let mut seq = rng.next_u64() as u16;
        for step in 0..80 {
            let (op, arg, data) = match rng.below(12) {
                0 => (0, 0, vec![]),
                1 => (1, 0, vec![]),
                2 => (2, 1 + rng.below(3) as i32, vec![]),
                3 => (3, 0, vec![]),
                4 => (4, 0, vec![]),
                5 => (5, 0, rng.vec_of(0, 40)),
                6 => (7, 0, vec![]),
                _ => (6, 0, peer_datagram(&mut rng, 0x0001_4041, &mut seq)),
            };
            match op {
                0 => r.open(),
                1 => r.retry(),
                2 => r.open_connection(
                    [Addressing::PeerOnly, Addressing::SinglePort, Addressing::PortPair][arg as usize - 1],
                ),
                3 => r.reopen_connection(),
                4 => r.close_connection(),
                5 => {
                    let _ = r.send(&data);
                }
                6 => r.on_datagram(&data),
                _ => r.clear_inbound(),
            }
            let rust_sends: Vec<Vec<u8>> = std::iter::from_fn(|| r.poll_transmit()).collect();
            let rust_events: Vec<String> =
                std::iter::from_fn(|| r.poll_event()).map(|e| event_name(&e)).collect();
            let (c_phase, c_sends, c_events) = c.op(op, arg, &data);
            assert_eq!(phase_index(r.phase()), c_phase, "phase, seed {seed} step {step} op {op}");
            assert_eq!(rust_sends, c_sends, "sends, seed {seed} step {step} op {op}");
            assert_eq!(rust_events, c_events, "events, seed {seed} step {step} op {op}");
            totals[0] += rust_sends.len();
            totals[1] += rust_events.iter().filter(|e| e.starts_with("data:")).count();
            totals[2] += usize::from(r.phase() == Phase::Connected);
        }
    }
    assert!(totals.iter().all(|&n| n > 500), "sends, data events, connected steps: {totals:?}");
}
