//! The whole connect machine, which the C core cannot fuzz because it owns its sockets. Each record is an
//! event the host could deliver, chosen by its first byte: bytes on the control connection, a datagram
//! on one of the five sockets, or time passing. Whatever the input, the session must neither panic nor
//! loop, and every I/O it asks for is answered as a host would.
#![no_main]
use libfuzzer_sys::fuzz_target;
use ripcord_fuzz::{Records, counting};
use ripcord_proto::connect::{Config, Endpoint, Io, Pairing, Route, Session, Socket};
use ripcord_proto::crypto::ecdh::RustCryptoEcdh;

const CONSOLE: [u8; 4] = [192, 0, 2, 7];

fuzz_target!(|data: &[u8]| {
    let route = if data.first().is_some_and(|b| b & 1 == 1) { Route::Rendezvous } else { Route::Local };
    let pairing = Pairing::new(true, vec![0xab; 8], [3; 16]);
    let mut s = Session::new(Config::new(route, CONSOLE, pairing, vec![2; 16]), Box::new(RustCryptoEcdh), counting());
    let mut now = 1u64;
    if route == Route::Rendezvous {
        s.rendezvous_prepare(now);
        answer(&mut s, now);
        let peer = ripcord_proto::connect::Peer { endpoint: Endpoint::new(CONSOLE, 9303), console_hashed_id: [9; 20] };
        s.rendezvous_begin(now, [8; 20], peer);
    }
    s.connect(now);
    for record in Records::new(data) {
        answer(&mut s, now);
        let Some((&op, body)) = record.split_first() else { continue };
        let socket = [Socket::Arm, Socket::Senkusha, Socket::Stream, Socket::ControlLeg, Socket::MediaLeg][usize::from(op >> 5) % 5];
        match op & 0x0f {
            0 => s.on_tcp_data(now, body),
            1 => s.on_tcp_closed(now, op & 0x10 != 0),
            2 => now += u64::from(u16::from_be_bytes([body.first().copied().unwrap_or(0), body.get(1).copied().unwrap_or(0)])) * 1000,
            3 => s.submit_passcode(now, Some(String::from_utf8_lossy(body).into_owned())),
            4 => s.media_peer(now, Some(ripcord_proto::connect::Peer { endpoint: Endpoint::new(CONSOLE, 9297), console_hashed_id: [9; 20] })),
            _ => s.on_datagram(now, socket, Endpoint::new(CONSOLE, 9296), body),
        }
        if let Some(t) = s.next_timeout()
            && t <= now
        {
            s.handle_timeout(now);
        }
        if s.is_ended() {
            break;
        }
    }
});

/// Answers what the session asked for, as a host would: sockets open, connects succeed, sends vanish.
fn answer(s: &mut Session, now: u64) {
    for _ in 0..64 {
        let Some(io) = s.poll_io() else { break };
        match io {
            Io::UdpOpen { socket, .. } => s.on_udp_opened(now, socket, 50_000, 1 << 20),
            Io::TcpConnect(_) => s.on_tcp_connected(now),
            _ => {}
        }
    }
    while s.poll_event().is_some() {}
}
