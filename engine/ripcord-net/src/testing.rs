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
