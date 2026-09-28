//! The 9303 association (fuzz_dgram.c and fuzz_rendezvous.c): the wire codec, the association in every
//! phase, and the channel staging around it.
#![no_main]
use libfuzzer_sys::fuzz_target;
use ripcord_fuzz::{Records, counting};
use ripcord_proto::dgram::assoc::{Addressing, Association};
use ripcord_proto::dgram::channel::{Channel, Options};
use ripcord_proto::dgram::wire;

fuzz_target!(|data: &[u8]| {
    let mut a = Association::new(counting(), [1; 20], [2; 20], [192, 0, 2, 7], 9303);
    let mut channel = Channel::new(Association::new(counting(), [1; 20], [2; 20], [192, 0, 2, 7], 9303), Options::default());
    channel.establish(0);
    for (i, d) in Records::new(data).enumerate() {
        let _ = wire::Prelude::parse(d);
        let _ = wire::chunks(d).count();
        let _ = wire::http_complete(d);
        match d.first().map(|b| b % 8) {
            Some(0) => a.open(),
            Some(1) => a.open_connection([Addressing::PeerOnly, Addressing::SinglePort, Addressing::PortPair][i % 3]),
            Some(2) => a.reopen_connection(),
            Some(3) => a.close_connection(),
            Some(4) => {
                let _ = a.send(d);
            }
            _ => a.on_datagram(d),
        }
        while a.poll_transmit().is_some() {}
        while a.poll_event().is_some() {}
        let now = i as u64 * 1_000_000;
        channel.on_datagram(now, d);
        channel.handle_timeout(now);
        while channel.poll_transmit().is_some() {}
        let _ = channel.poll_stage();
        let _ = channel.take_bytes(64);
    }
});
