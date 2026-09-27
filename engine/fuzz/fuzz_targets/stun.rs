//! STUN and the candidates (fuzz_stun.c and fuzz_wan.c): the message parser, a gatherer taking arbitrary
//! answers, and the candidate choice.
#![no_main]
use libfuzzer_sys::fuzz_target;
use ripcord_fuzz::{Records, counting};
use ripcord_proto::net::{candidates, stun};

fuzz_target!(|data: &[u8]| {
    let mut g = stun::Gatherer::new(vec![0u8, 1], 3, 500_000, 2, counting());
    g.start(0);
    let mut cands = Vec::new();
    for (i, d) in Records::new(data).enumerate() {
        let _ = stun::parse(d);
        g.on_datagram(i as u64, d);
        g.handle_timeout(i as u64 * 100_000);
        while g.poll_transmit().is_some() {}
        let text = String::from_utf8_lossy(d).into_owned();
        let _ = candidates::parse_ipv4(&text);
        cands.push(candidates::Candidate { kind: "LOCAL".into(), address: text, port: 9303 });
    }
    let nics = [candidates::Interface { address: [192, 168, 1, 5], netmask: [255, 255, 255, 0] }];
    let _ = candidates::choose(&cands, &nics, Some("192.168.1.9"));
});
