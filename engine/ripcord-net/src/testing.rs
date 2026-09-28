//! Test support: the scripted LAN console served on real loopback sockets, for this crate's tests and,
//! behind the `loopback-console` feature, for the C ABI's test exports. Never in a shipping engine.

use std::io::{ErrorKind, Read, Write};
use std::net::{TcpListener, TcpStream, UdpSocket};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

pub use ripcord_proto::testing::scripted_lan_console::{self as console, ScriptedLanConsole, Tcp};

/// A scripted LAN console on 127.0.0.1, on its own thread: TCP and the arm probe on one port, senkusha and
/// the stream on two more. Stopped when dropped.
pub struct LoopbackConsole {
    stop: Arc<AtomicBool>,
    /// The console's state, for a test's assertions.
    pub state: Arc<Mutex<ScriptedLanConsole>>,
    /// (control, senkusha, stream).
    pub ports: (u16, u16, u16),
    thread: Option<std::thread::JoinHandle<()>>,
}

impl LoopbackConsole {
    /// `configure` adjusts the scripted console before it starts answering.
    pub fn start(configure: impl FnOnce(&mut ScriptedLanConsole)) -> Self {
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
        let mut scripted = ScriptedLanConsole::new(true);
        configure(&mut scripted);
        let state = Arc::new(Mutex::new(scripted));
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

impl Drop for LoopbackConsole {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Relaxed);
        let _ = self.thread.take().map(|t| t.join());
    }
}

/// What the loopback 9303 console saw.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct DgramReport {
    pub inits: usize,
    pub rgst_requests: usize,
    pub rgst_field_ok: bool,
    /// Each HTTP request's path, in order: `/sess/rgst`, `/sess/init` or `/sess/ctrl`.
    pub requests: Vec<String>,
}

/// The scripted 9303 console on a loopback socket, on its own thread: the association, /sess/init with a
/// nonce, /sess/ctrl, and the account route's /sess/rgst under `seed`. It offers no A/V leg.
pub struct LoopbackDgramConsole {
    stop: Arc<AtomicBool>,
    state: Arc<Mutex<ripcord_proto::testing::scripted_console::ScriptedConsole>>,
    pub port: u16,
    thread: Option<std::thread::JoinHandle<()>>,
}

impl LoopbackDgramConsole {
    pub fn start(is_ps5: bool, seed: [u8; 16], nonce: [u8; 16]) -> std::io::Result<Self> {
        use ripcord_proto::testing::scripted_console::ScriptedConsole;
        let socket = UdpSocket::bind("127.0.0.1:0")?;
        socket.set_read_timeout(Some(Duration::from_millis(2)))?;
        let port = socket.local_addr()?.port();
        let mut console = ScriptedConsole::new();
        console.is_ps5 = is_ps5;
        console.account_seed = Some(seed);
        let nonce = ripcord_proto::base64::encode(&nonce);
        console.init_reply =
            Some(format!("HTTP/1.1 200 OK\r\nRP-Nonce: {nonce}\r\nContent-Length: 0\r\n\r\n").into_bytes());
        let stop = Arc::new(AtomicBool::new(false));
        let state = Arc::new(Mutex::new(console));
        let (stop2, state2) = (stop.clone(), state.clone());
        let thread = std::thread::spawn(move || {
            let mut buf = [0u8; 4096];
            while !stop2.load(Ordering::Relaxed) {
                if let Ok((n, from)) = socket.recv_from(&mut buf) {
                    let replies = state2.lock().unwrap().on_datagram(&buf[..n]);
                    for r in replies {
                        let _ = socket.send_to(&r, from);
                    }
                }
            }
        });
        Ok(Self { stop, state, port, thread: Some(thread) })
    }

    pub fn report(&self) -> DgramReport {
        let c = self.state.lock().unwrap();
        let path = |r: &Vec<u8>| {
            ["/sess/rgst", "/sess/init", "/sess/ctrl"]
                .into_iter()
                .find(|p| r.windows(p.len()).any(|w| w == p.as_bytes()))
                .unwrap_or("?")
                .to_string()
        };
        DgramReport {
            inits: c.inits,
            rgst_requests: c.rgst_requests,
            rgst_field_ok: c.rgst_field_ok,
            requests: c.requests.iter().map(path).collect(),
        }
    }
}

impl Drop for LoopbackDgramConsole {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Relaxed);
        let _ = self.thread.take().map(|t| t.join());
    }
}
