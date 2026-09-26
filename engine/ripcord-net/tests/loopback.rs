//! The driver against the scripted console over real sockets on loopback: the whole LAN sequence, video,
//! and the goodbye. Nothing leaves the machine: the arm probe's broadcast is off and every port is local.

use std::time::{Duration, Instant};

use ripcord_net::testing::{LoopbackConsole, console};
use ripcord_net::*;
use ripcord_proto::crypto::ecdh::RustCryptoEcdh;

#[derive(Default)]
struct Recorder {
    stages: Vec<Stage>,
    video: usize,
    ended: Option<EndReason>,
    info: bool,
    disconnect: bool,
}

impl Host for Recorder {
    fn event(&mut self, event: Event) {
        match event {
            Event::Stage(s) => self.stages.push(s),
            Event::Video { .. } => self.video += 1,
            Event::StreamInfo { .. } => self.info = true,
            Event::Ended(r) => self.ended = Some(r),
            _ => {}
        }
    }

    fn commands(&mut self) -> Commands {
        Commands { disconnect: self.disconnect, ..Default::default() }
    }
}

fn counting() -> ripcord_proto::RandomSource {
    let mut n = 0u8;
    Box::new(move |b: &mut [u8]| {
        b.iter_mut().for_each(|x| {
            *x = n.wrapping_mul(31).wrapping_add(17);
            n = n.wrapping_add(1);
        })
    })
}

#[test]
fn a_lan_session_over_loopback_sockets() {
    let c = LoopbackConsole::start(|_| {});
    let pairing = Pairing::new(true, vec![0xab; 8], console::COMPANION);
    let mut cfg = Config::new(Route::Local, [127, 0, 0, 1], pairing, vec![0x22; 32]);
    (cfg.control_port, cfg.senkusha_port, cfg.stream_port) = c.ports;
    cfg.arm_broadcast = false;
    let mut client = Client::new(cfg, Box::new(RustCryptoEcdh), counting());
    let mut host = Recorder::default();

    let stage = client.connect(&mut host);
    assert!(
        stage >= Stage::StreamReady,
        "stage {stage:?}, stages {:?}, outcome {:?}",
        host.stages,
        client.outcome()
    );
    assert!(host.info);

    let start = Instant::now();
    while client.pump(&mut host, Duration::from_millis(2)) && start.elapsed() < Duration::from_secs(10) {
        if host.video >= 5 {
            host.disconnect = true;
        }
    }
    assert!(host.video >= 5, "{} frames", host.video);
    assert!(client.is_ended());
    assert_eq!(client.stage(), Stage::Streaming, "the furthest stage reached");
    assert_eq!(host.ended, Some(EndReason::UserDisconnect));
    assert!(client.outcome().heartbeats_sent >= 1);
    assert!(client.outcome().rcvbuf_granted > 0);

    // The goodbye reaches the console.
    let deadline = Instant::now() + Duration::from_secs(2);
    while !c.state.lock().unwrap().disconnect_received && Instant::now() < deadline {
        std::thread::sleep(Duration::from_millis(5));
    }
    assert!(c.state.lock().unwrap().disconnect_received);
}

#[test]
fn pin_registration_over_loopback() {
    use ripcord_net::pairing::{PinError, PinParams, register_pin};
    use ripcord_proto::sess::regist::RegistError;

    let c = LoopbackConsole::start(|s| s.regist_pin = 87_654_321);
    let mut params = PinParams::new([127, 0, 0, 1], true, "1234567890123456789", 87_654_321, "127.0.0.1");
    (params.port, params.arm_broadcast) = (c.ports.0, false);
    let mut random = counting();
    let outcome = register_pin(&params, &mut random);
    assert!(outcome.saw_arm_reply);
    let record = outcome.result.expect("a pairing record");
    assert_eq!(record.registration_key, console::REGISTRATION_KEY);
    assert_eq!(record.companion, console::COMPANION);
    assert!(record.is_ps5);
    assert!(c.state.lock().unwrap().requests.iter().any(|r| r.starts_with("POST /sie/ps5/rp/sess/rgst")));

    // A wrong PIN decrypts to noise, which is not a record.
    params.passcode = 11_111_111;
    assert_eq!(register_pin(&params, &mut random).result, Err(PinError::Regist(RegistError::BadRecord)));
    drop(c);

    let c = LoopbackConsole::start(|s| s.regist_refuse = Some("80108b10"));
    (params.port, params.passcode) = (c.ports.0, 87_654_321);
    assert_eq!(
        register_pin(&params, &mut random).result,
        Err(PinError::Regist(RegistError::Refused { status: 403, reason: Some("80108b10".into()) }))
    );
}
