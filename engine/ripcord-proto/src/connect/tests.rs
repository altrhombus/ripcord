//! The whole LAN sequence against the scripted console, with time under the test's control.

use super::*;
use crate::crypto::ecdh::RustCryptoEcdh;
use crate::sess::ctrl;
use crate::takion::control as tc;
use crate::testing::scripted_lan_console::{self as console, ScriptedLanConsole, Tcp};

const CONSOLE: [u8; 4] = [192, 168, 1, 50];

fn counting() -> crate::RandomSource {
    let mut n = 0u8;
    Box::new(move |b: &mut [u8]| {
        b.iter_mut().for_each(|x| {
            *x = n.wrapping_mul(31).wrapping_add(17);
            n = n.wrapping_add(1);
        })
    })
}

fn config() -> Config {
    let pairing = Pairing::new(true, vec![0xab; 8], console::COMPANION);
    Config::new(Route::Local, CONSOLE, pairing, vec![0x22; 32])
}

struct Rig {
    s: Session,
    c: ScriptedLanConsole,
    now: u64,
    events: Vec<Event>,
    passcodes: Vec<Option<String>>,
    tcp_open: bool,
    closed_sockets: Vec<Socket>,
}

impl Rig {
    fn new(cfg: Config, c: ScriptedLanConsole) -> Self {
        let s = Session::new(cfg, Box::new(RustCryptoEcdh), counting());
        Self {
            s,
            c,
            now: 1_000_000,
            events: Vec::new(),
            passcodes: Vec::new(),
            tcp_open: false,
            closed_sockets: Vec::new(),
        }
    }

    /// Routes every queued I/O to the console and its answers back, until both are quiet.
    fn pump(&mut self) {
        loop {
            let mut moved = false;
            while let Some(io) = self.s.poll_io() {
                moved = true;
                match io {
                    Io::UdpOpen { socket, .. } => self.s.on_udp_opened(self.now, socket, 50_000, 1 << 20),
                    Io::UdpClose(socket) => self.closed_sockets.push(socket),
                    Io::UdpSend { socket, to, data } => {
                        for reply in self.c.on_udp(to.port, &data) {
                            let from = Endpoint::new(CONSOLE, to.port);
                            self.s.on_datagram(self.now, socket, from, &reply);
                        }
                    }
                    Io::TcpConnect(_) => {
                        self.c.tcp_open();
                        self.tcp_open = true;
                        self.s.on_tcp_connected(self.now);
                    }
                    Io::TcpSend(data) => {
                        for t in self.c.on_tcp(&data) {
                            match t {
                                Tcp::Data(d) => self.s.on_tcp_data(self.now, &d),
                                Tcp::Close => {
                                    self.tcp_open = false;
                                    self.s.on_tcp_closed(self.now, false);
                                }
                            }
                        }
                    }
                    Io::TcpClose => self.tcp_open = false,
                }
            }
            while let Some(e) = self.s.poll_event() {
                moved = true;
                if let Event::PasscodeWanted { .. } = e {
                    let answer = if self.passcodes.is_empty() { None } else { self.passcodes.remove(0) };
                    self.events.push(e);
                    self.s.submit_passcode(self.now, answer);
                    continue;
                }
                self.events.push(e);
            }
            if !moved {
                return;
            }
        }
    }

    /// Advances the clock to the machine's next deadline (at most `step`), pumping between.
    fn run_until(&mut self, done: impl Fn(&Session) -> bool, limit_us: u64) {
        let end = self.now + limit_us;
        self.pump();
        while !done(&self.s) && self.now < end {
            let next = self.s.next_timeout().unwrap_or(self.now + 10_000).max(self.now + 1).min(end);
            self.now = next;
            self.s.handle_timeout(self.now);
            self.pump();
        }
    }

    fn feed_stream(&mut self, datagram: Vec<u8>) {
        self.s.on_datagram(self.now, Socket::Stream, Endpoint::new(CONSOLE, 9296), &datagram);
        self.pump();
    }

    fn stages(&self) -> Vec<Stage> {
        self.events.iter().filter_map(|e| if let Event::Stage(s) = e { Some(*s) } else { None }).collect()
    }
}

#[test]
fn a_lan_session_runs_from_the_arm_probe_to_video() {
    let mut r = Rig::new(config(), ScriptedLanConsole::new(true));
    r.s.connect(r.now);
    r.run_until(Session::is_streaming, 60_000_000);
    assert!(r.s.is_streaming(), "stage {:?}, events {:?}", r.s.stage(), r.events);

    assert_eq!(r.c.arm_probes, 2, "unicast and broadcast");
    assert!(r.c.requests[0].starts_with("GET /sie/ps5/rp/sess/init"));
    assert!(r.c.requests[1].contains("RP-ConPath: 1\r\n"));
    assert!(r.c.senkusha_messages >= 2, "the version and the keyless session exchange");
    assert!(r.s.outcome().senkusha_ok);
    assert!(r.closed_sockets.contains(&Socket::Senkusha), "senkusha's socket goes when it is done");
    assert_eq!(r.c.stream_messages[..2], [tc::PROTOCOL_VERSION_REQUEST, tc::SESSION_REQUEST]);
    assert_eq!(r.c.stream_info_acks, 1);
    assert!(r.events.contains(&Event::StreamInfo {
        width: console::WIDTH,
        height: console::HEIGHT,
        hevc: false,
        video_header: console::SPS_PPS.to_vec()
    }));
    assert_eq!(r.s.outcome().stream_version, crate::takion::negotiator::CLIENT_VERSION);

    // Blind at the start: the IDR latch asks straight away, then heartbeats and congestion run.
    r.s.set_controller(Some(crate::input::State { left_x: 100, ..Default::default() }));
    r.run_until(|_| false, 1_000_000);
    assert!(r.c.idr_requests >= 2, "re-asked every 200 ms until a keyframe: {}", r.c.idr_requests);
    assert!(r.c.heartbeats >= 1);
    assert!(r.c.congestion_packets >= 4);
    assert!(r.c.input_packets >= 1);
    assert!(r.events.iter().any(|e| matches!(e, Event::Stats(_))));

    // Two keyframes: the first is delivered when the second begins, and the latch clears.
    let k1 = r.c.video_keyframe().unwrap();
    let k2 = r.c.video_keyframe().unwrap();
    r.feed_stream(k1);
    r.feed_stream(k2);
    let video: Vec<_> = r.events.iter().filter(|e| matches!(e, Event::Video { .. })).collect();
    assert_eq!(video.len(), 1);
    assert!(matches!(video[0], Event::Video { keyframe: true, .. }));
    assert_eq!(r.s.stage(), Stage::Streaming);
    let asked = r.c.idr_requests;
    r.run_until(|_| false, 1_000_000);
    assert_eq!(r.c.idr_requests, asked, "a delivered keyframe stops the requests");

    // Goodbye: REST_MODE when asked, then the Takion DISCONNECT, then everything closes.
    r.s.disconnect(r.now, true);
    r.pump();
    assert!(r.s.is_ended());
    assert!(r.c.rest_requested);
    assert!(r.c.disconnect_received);
    assert_eq!(r.events.last(), Some(&Event::Ended(EndReason::UserDisconnect)));
    assert!(r.closed_sockets.contains(&Socket::Stream));
    assert!(!r.tcp_open);
    assert_eq!(
        r.stages(),
        [
            Stage::ControlOpen,
            Stage::SignedIn,
            Stage::SenkushaUp,
            Stage::TakionUp,
            Stage::StreamKeys,
            Stage::StreamReady,
            Stage::Streaming,
            Stage::Ended
        ]
    );
}

#[test]
fn the_sign_in_gate_retries_a_refusal_and_then_streams() {
    let mut c = ScriptedLanConsole::new(true);
    c.passcode = Some("1234".into());
    let mut r = Rig::new(config(), c);
    r.passcodes = vec![Some("9999".into()), Some("1234".into())];
    r.s.connect(r.now);
    r.run_until(Session::is_streaming, 60_000_000);
    assert!(r.s.is_streaming(), "{:?}", r.events);
    assert_eq!(r.c.login_attempts, 2);
    assert!(r.events.contains(&Event::PasscodeWanted { retry: 0 }));
    assert!(r.events.contains(&Event::PasscodeWanted { retry: 1 }));
    assert_eq!(r.s.outcome().login_verdict, Some(true));
}

#[test]
fn a_stored_passcode_is_used_first_and_a_cancel_ends_it() {
    let mut c = ScriptedLanConsole::new(true);
    c.passcode = Some("1234".into());
    let mut cfg = config();
    cfg.pairing.login_pin = Some("1234".into());
    let mut r = Rig::new(cfg, c);
    r.s.connect(r.now);
    r.run_until(Session::is_streaming, 60_000_000);
    assert!(r.s.is_streaming());
    assert!(!r.events.iter().any(|e| matches!(e, Event::PasscodeWanted { .. })));

    let mut c = ScriptedLanConsole::new(true);
    c.passcode = Some("1234".into());
    let mut r = Rig::new(config(), c);
    r.passcodes = vec![None];
    r.s.connect(r.now);
    r.run_until(Session::is_ended, 60_000_000);
    assert_eq!(r.s.outcome().end_reason, Some(EndReason::SigninCancelled));
}

#[test]
fn no_session_id_is_fatal_by_default_on_the_lan() {
    let mut c = ScriptedLanConsole::new(true);
    c.send_session_id = false;
    let mut r = Rig::new(config(), c);
    r.s.connect(r.now);
    let start = r.now;
    r.run_until(Session::is_ended, 60_000_000);
    assert_eq!(r.s.outcome().end_reason, Some(EndReason::Timeout));
    assert_eq!(r.s.stage(), Stage::SignedIn, "signed in by not being asked, then no SESSION_ID");
    assert!(r.now - start >= 20_000_000, "the whole prompt window");
}

#[test]
fn a_console_refusing_the_stream_ends_it_with_its_reason() {
    let mut c = ScriptedLanConsole::new(true);
    c.refuse_with = Some("bad launch spec");
    let mut r = Rig::new(config(), c);
    r.s.connect(r.now);
    r.run_until(Session::is_ended, 60_000_000);
    assert_eq!(r.s.outcome().end_reason, Some(EndReason::ConsoleClosed));
    assert_eq!(r.s.outcome().console_disconnect_reason.as_deref(), Some("bad launch spec"));
    assert_eq!(r.s.stage(), Stage::TakionUp);
}

#[test]
fn a_datagram_from_anyone_but_the_console_is_dropped() {
    let mut r = Rig::new(config(), ScriptedLanConsole::new(true));
    r.s.connect(r.now);
    r.run_until(Session::is_streaming, 60_000_000);
    let frame = r.c.video_keyframe().unwrap();
    r.s.on_datagram(r.now, Socket::Stream, Endpoint::new([10, 0, 0, 9], 9296), &frame);
    assert_eq!(r.s.outcome().stray_dropped, 1);
}

#[test]
fn a_console_that_hangs_up_mid_stream_ends_the_session() {
    let mut r = Rig::new(config(), ScriptedLanConsole::new(true));
    r.s.connect(r.now);
    r.run_until(Session::is_streaming, 60_000_000);
    let bye = r.c.disconnect("Server shutting down");
    r.feed_stream(bye);
    assert!(r.s.is_ended());
    assert_eq!(r.s.outcome().end_reason, Some(EndReason::ConsoleClosed));
}

#[test]
fn the_declared_mtu_follows_the_interface() {
    let mut cfg = config();
    cfg.interface_mtu = Some(1500);
    let mut r = Rig::new(cfg, ScriptedLanConsole::new(true));
    r.s.connect(r.now);
    r.run_until(Session::is_streaming, 60_000_000);
    assert!(r.s.is_streaming());
    assert_eq!(r.s.declared().0, 1454, "1500 less 46");
    let mut cfg = config();
    cfg.interface_mtu = Some(576);
    let s = Session::new(cfg, Box::new(RustCryptoEcdh), counting());
    assert_eq!(s.declared().0, 530);
}

/// The rendezvous route: the datagram console answers the 9303 association and /sess on the control
/// leg and the A/V leg's prelude; the LAN console's Takion answers senkusha and the stream on the media
/// leg, one after the other on the one socket.
struct RvRig {
    s: Session,
    ctl: crate::testing::scripted_console::ScriptedConsole,
    media: crate::testing::scripted_console::ScriptedConsole,
    lan: ScriptedLanConsole,
    now: u64,
    events: Vec<Event>,
    session_id_sent: bool,
    stream_ready_sent: bool,
}

const MEDIA: Endpoint = Endpoint::new(CONSOLE, 9297);
const CONTROL: Endpoint = Endpoint::new(CONSOLE, 9303);

impl RvRig {
    fn new() -> Self {
        let pairing = Pairing::new(true, vec![0xab; 8], console::COMPANION);
        let cfg = Config::new(Route::Rendezvous, CONSOLE, pairing, vec![0x22; 32]);
        let mut ctl = crate::testing::scripted_console::ScriptedConsole::new();
        let nonce = crate::base64::encode(&console::NONCE);
        ctl.init_reply =
            Some(format!("HTTP/1.1 200 OK\r\nRP-Nonce: {nonce}\r\nContent-Length: 0\r\n\r\n").into_bytes());
        let mut lan = ScriptedLanConsole::new(true);
        lan.use_nonce();
        Self {
            s: Session::new(cfg, Box::new(RustCryptoEcdh), counting()),
            ctl,
            media: crate::testing::scripted_console::ScriptedConsole::new(),
            lan,
            now: 1_000_000,
            events: Vec::new(),
            session_id_sent: false,
            stream_ready_sent: false,
        }
    }

    fn pump(&mut self) {
        loop {
            let mut moved = false;
            while let Some(io) = self.s.poll_io() {
                moved = true;
                match io {
                    Io::UdpOpen { socket, .. } => self.s.on_udp_opened(self.now, socket, 40_000, 1 << 20),
                    Io::UdpSend { socket: Socket::ControlLeg, data, .. } => {
                        for reply in self.ctl.on_datagram(&data) {
                            self.s.on_datagram(self.now, Socket::ControlLeg, CONTROL, &reply);
                        }
                    }
                    Io::UdpSend { socket: Socket::MediaLeg, data, .. } => {
                        let replies = if crate::dgram::wire::Prelude::parse(&data).is_some() {
                            self.media.on_datagram(&data)
                        } else if self.lan.senkusha_done || !crate::takion::connection::is_control(&data) {
                            self.lan.on_udp(9296, &data)
                        } else {
                            self.lan.on_udp(9297, &data)
                        };
                        for reply in replies {
                            self.s.on_datagram(self.now, Socket::MediaLeg, MEDIA, &reply);
                        }
                    }
                    _ => {}
                }
            }
            while let Some(e) = self.s.poll_event() {
                moved = true;
                if let Event::MediaWanted(_) = e {
                    let peer = Peer {
                        endpoint: MEDIA,
                        console_hashed_id: crate::testing::scripted_console::console_id(),
                    };
                    self.events.push(e);
                    self.s.media_peer(self.now, Some(peer));
                    continue;
                }
                self.events.push(e);
            }
            // The console's side of the choreography: SESSION_ID once the A/V leg is preluded, and
            // STREAM_READY once PROBE_REPORT has arrived.
            if self.s.outcome().media_prelude_ok && !self.session_id_sent {
                self.session_id_sent = true;
                let d = self.ctl.push(&ctrl::build(ctrl::SESSION_ID, &[])).unwrap();
                self.s.on_datagram(self.now, Socket::ControlLeg, CONTROL, &d);
                moved = true;
            }
            let probe = |f: &Vec<u8>| ctrl::parse(f).is_some_and(|(k, _, _)| k == ctrl::PROBE_REPORT);
            if !self.stream_ready_sent && self.ctl.frames.iter().any(probe) {
                self.stream_ready_sent = true;
                let d = self.ctl.push(&ctrl::build(ctrl::STREAM_READY, &[])).unwrap();
                self.s.on_datagram(self.now, Socket::ControlLeg, CONTROL, &d);
                moved = true;
            }
            if !moved {
                return;
            }
        }
    }

    fn run_until(&mut self, done: impl Fn(&Session) -> bool, limit_us: u64) {
        let end = self.now + limit_us;
        self.pump();
        while !done(&self.s) && self.now < end {
            self.now = self.s.next_timeout().unwrap_or(self.now + 10_000).max(self.now + 1).min(end);
            self.s.handle_timeout(self.now);
            self.pump();
        }
    }
}

#[test]
fn a_rendezvous_session_runs_over_both_legs_to_the_stream() {
    let mut r = RvRig::new();
    assert!(r.s.rendezvous_prepare(r.now));
    r.pump();
    assert!(r.events.iter().any(|e| matches!(e, Event::ControlLegReady(Leg { local_port: 40_000, .. }))));
    let console =
        Peer { endpoint: CONTROL, console_hashed_id: crate::testing::scripted_console::console_id() };
    assert!(r.s.rendezvous_begin(r.now, crate::testing::scripted_console::client_id(), console));
    r.s.connect(r.now);
    r.run_until(Session::is_streaming, 120_000_000);
    assert!(r.s.is_streaming(), "stage {:?}, events {:?}", r.s.stage(), r.events);

    let o = r.s.outcome().clone();
    assert!(o.media_prelude_ok);
    assert!(o.probe_report_sent);
    assert!(o.stream_ready_seen);
    assert!(o.senkusha_ok, "senkusha on the A/V leg's own socket");
    assert!(r.lan.senkusha_done, "and it said goodbye before the stream's association");
    assert_eq!(o.session_ready_waited_ms, Some(0));
    let ctrl_request = String::from_utf8_lossy(&r.ctl.requests[1]).into_owned();
    assert!(ctrl_request.contains("RP-ConPath: 3\r\n"));
    assert_eq!(r.lan.stream_info_acks, 1);

    // A stray on the A/V socket is dropped; the goodbye closes the control connection politely.
    r.s.on_datagram(r.now, Socket::MediaLeg, Endpoint::new([10, 0, 0, 9], 9297), &[0x02; 40]);
    assert_eq!(r.s.outcome().stray_dropped, 1);
    let closes = r.ctl.closes_received;
    r.s.disconnect(r.now, false);
    r.pump();
    assert!(r.s.is_ended());
    assert_eq!(r.ctl.closes_received, closes + 1);
    assert!(r.lan.disconnect_received);
}
