//! The driver against the scripted console over real sockets on loopback: the whole LAN sequence, video,
//! and the goodbye. Nothing leaves the machine: the arm probe's broadcast is off and every port is local.

use std::io::{ErrorKind, Read, Write};
use std::net::{TcpListener, TcpStream, UdpSocket};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use ripcord_net::*;
use ripcord_proto::crypto::ecdh::RustCryptoEcdh;
use ripcord_proto::testing::scripted_lan_console::{self as console, ScriptedLanConsole, Tcp};

struct Console {
    stop: Arc<AtomicBool>,
    state: Arc<Mutex<ScriptedLanConsole>>,
    ports: (u16, u16, u16),
    thread: Option<std::thread::JoinHandle<()>>,
}

impl Console {
    fn start() -> Self {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let control = listener.local_addr().unwrap().port();
        let arm = UdpSocket::bind(("127.0.0.1", control)).unwrap();
        let senkusha = UdpSocket::bind("127.0.0.1:0").unwrap();
        let stream = UdpSocket::bind("127.0.0.1:0").unwrap();
        let ports = (control, senkusha.local_addr().unwrap().port(), stream.local_addr().unwrap().port());
        for s in [&arm, &senkusha, &stream] {
            s.set_nonblocking(true).unwrap();
        }
        listener.set_nonblocking(true).unwrap();
        let stop = Arc::new(AtomicBool::new(false));
        let state = Arc::new(Mutex::new(ScriptedLanConsole::new(true)));
        let (stop2, state2) = (stop.clone(), state.clone());
        let thread = std::thread::spawn(move || {
            let mut tcp: Option<TcpStream> = None;
            let mut client_stream = None;
            let mut last_frame = Instant::now();
            let mut buf = vec![0u8; 65_536];
            while !stop2.load(Ordering::Relaxed) {
                let mut c = state2.lock().unwrap();
                if let Ok((t, _)) = listener.accept() {
                    t.set_nonblocking(true).unwrap();
                    c.tcp_open();
                    tcp = Some(t);
                }
                if let Some(t) = tcp.as_mut() {
                    match t.read(&mut buf) {
                        Ok(0) => tcp = None,
                        Ok(n) => {
                            for out in c.on_tcp(&buf[..n]) {
                                match out {
                                    Tcp::Data(d) => {
                                        let _ = tcp.as_mut().map(|t| t.write_all(&d));
                                    }
                                    Tcp::Close => tcp = None,
                                }
                            }
                        }
                        Err(e) if e.kind() == ErrorKind::WouldBlock => {}
                        Err(_) => tcp = None,
                    }
                }
                for (sock, port) in [(&arm, 9295), (&senkusha, 9297), (&stream, 9296)] {
                    while let Ok((n, from)) = sock.recv_from(&mut buf) {
                        if port == 9296 {
                            client_stream = Some(from);
                        }
                        for reply in c.on_udp(port, &buf[..n]) {
                            let _ = sock.send_to(&reply, from);
                        }
                    }
                }
                // Once the stream is keyed, a keyframe every 20 ms.
                if let Some(to) = client_stream
                    && last_frame.elapsed() > Duration::from_millis(20)
                    && let Some(frame) = c.video_keyframe()
                {
                    last_frame = Instant::now();
                    let _ = stream.send_to(&frame, to);
                }
                drop(c);
                std::thread::sleep(Duration::from_micros(500));
            }
        });
        Self { stop, state, ports, thread: Some(thread) }
    }
}

impl Drop for Console {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Relaxed);
        let _ = self.thread.take().map(|t| t.join());
    }
}

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
    let c = Console::start();
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
