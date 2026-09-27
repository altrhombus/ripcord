//! Takion (fuzz_takion.c): the chunk parsers, and a connection past its handshake taking arbitrary
//! datagrams, with sealing and verification on so the GMAC path is reached too.
#![no_main]
use libfuzzer_sys::fuzz_target;
use ripcord_fuzz::Records;
use ripcord_proto::takion::chunks::{self, Header};
use ripcord_proto::takion::connection::{Config, Connection};

fuzz_target!(|data: &[u8]| {
    let mut c = Connection::new(0x4823, Config { max_attempts: 3, attempt_timeout_us: 100_000 });
    c.connect(0);
    let _ = c.poll_transmit();
    let ack = Header::control(0x4823).build(&chunks::build_init_ack(0xb18c_cf00, 0x00b1_8ccf, &[7; chunks::COOKIE_SIZE]));
    let _ = c.on_datagram(0, &ack);
    let _ = c.on_datagram(0, &Header::control(0x4823).build(&chunks::build_cookie_ack()));
    let mut now = 1;
    for (i, datagram) in Records::new(data).enumerate() {
        if i == 3 {
            c.enable_sealing(&[3; 16], &[4; 16]);
            c.enable_verification(&[5; 16], &[6; 16], i % 2 == 0);
        }
        if let Some((_, chunk)) = Header::parse(datagram) {
            let _ = chunks::parse_data_first(chunk);
            let _ = chunks::parse_sack(chunk);
            let _ = chunks::parse_init_ack(chunk);
        }
        let _ = c.on_datagram(now, datagram);
        let _ = c.send(now, 1, datagram);
        now += 150_000;
        c.handle_timeout(now);
        while c.poll_transmit().is_some() {}
    }
});
