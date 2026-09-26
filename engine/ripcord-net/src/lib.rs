//! The I/O driver: runs `ripcord-proto`'s connect sequence ([`Session`]) on `std::net` sockets, on the
//! caller's thread. It owns every socket and the clock and holds no protocol knowledge; everything the
//! protocol decides is the session's.
//!
//! The threading contract is the C client's (`halyard_client.h`): one host thread calls
//! [`Client::connect`] and then [`Client::pump`] until it returns `false`. The driver creates no threads
//! and takes no locks. Everything the host wants to tell the session is pulled through [`Host`] on that
//! thread, so each host synchronises in its own terms.
//!
//! Waiting is by polling every socket without blocking and sleeping briefly in between, as the C core
//! does: `std` has no portable readiness API, and the sleep is bounded by the session's next deadline.
#![forbid(unsafe_code)]

use std::collections::HashMap;
use std::io::{ErrorKind, Read, Write};
use std::net::{Ipv4Addr, SocketAddr, SocketAddrV4, TcpStream, UdpSocket};
use std::time::{Duration, Instant};

use ripcord_proto::RandomSource;
pub use ripcord_proto::connect::{
    Config, Controller, EndReason, Endpoint, Event, Io, Leg, LogLevel, Outcome, Pairing, Peer, Route,
    Session, Socket, Stage, Stats,
};
use ripcord_proto::crypto::ecdh::Ecdh;
use ripcord_proto::dgram::rendezvous;

/// The most datagrams read from one socket before the others get a turn: a burst's worth (C's
/// RC_AV_DRAIN_BURST).
const DRAIN_BURST: usize = 256;
/// The longest idle sleep. A host that needs a tighter loop passes a shorter wait to [`Client::pump`].
const IDLE_SLEEP: Duration = Duration::from_millis(1);
/// The TCP connect's own bound; the session's control deadline is the one that decides.
const TCP_CONNECT: Duration = Duration::from_secs(4);
/// The rendezvous pre-connect calls give up after this long with nothing to show.
const PREPARE_LIMIT: Duration = Duration::from_secs(30);

/// An answer the host may not have yet.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Answer<T> {
    /// Not yet: the session keeps running and asks again.
    Pending,
    Ready(T),
    GiveUp,
}

/// The host's commands, pulled on every turn.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Commands {
    pub cancel: bool,
    /// The host's decoder lost the chain.
    pub keyframe: bool,
    /// Goodbye, then end. With `rest_console`, ask the console to rest first.
    pub disconnect: bool,
    pub rest_console: bool,
}

/// What the host provides, all called on the pump thread. Buffers in events are the host's to keep.
pub trait Host {
    /// Every event: logs, stages, stream info, frames, stats, the ending. [`Event::PasscodeWanted`] and
    /// [`Event::MediaWanted`] are delivered here too, and then answered through the polls below.
    fn event(&mut self, event: Event);

    /// The current pad; `None` when no controller is attached, and then nothing is sent.
    fn controller(&mut self) -> Controller {
        None
    }

    /// The console wants its user's passcode; `retry` counts refusals. Take as long as a person needs.
    fn passcode(&mut self, _retry: u32) -> Answer<String> {
        Answer::GiveUp
    }

    /// Rendezvous: the A/V leg is ready; negotiate media with the cloud and answer with the console's end.
    fn media(&mut self, _leg: &Leg) -> Answer<Peer> {
        Answer::GiveUp
    }

    fn commands(&mut self) -> Commands {
        Commands::default()
    }
}

fn addr(e: Endpoint) -> SocketAddr {
    SocketAddr::V4(SocketAddrV4::new(Ipv4Addr::from(e.address), e.port))
}

fn endpoint(a: SocketAddr) -> Option<Endpoint> {
    match a {
        SocketAddr::V4(v4) => Some(Endpoint::new(v4.ip().octets(), v4.port())),
        SocketAddr::V6(_) => None,
    }
}

fn open_udp(socket: Socket, local_port: u16, rcvbuf: usize) -> std::io::Result<(UdpSocket, usize)> {
    let s = socket2::Socket::new(socket2::Domain::IPV4, socket2::Type::DGRAM, Some(socket2::Protocol::UDP))?;
    if socket == Socket::Arm {
        s.set_broadcast(true)?;
    }
    if rcvbuf > 0 {
        // A hint: the granted size is reported, since a platform may cap it.
        let _ = s.set_recv_buffer_size(rcvbuf);
    }
    s.bind(&SocketAddr::V4(SocketAddrV4::new(Ipv4Addr::UNSPECIFIED, local_port)).into())?;
    s.set_nonblocking(true)?;
    let granted = s.recv_buffer_size().unwrap_or(0);
    Ok((s.into(), granted))
}

/// One session and its sockets.
pub struct Client {
    session: Session,
    udp: HashMap<Socket, UdpSocket>,
    tcp: Option<TcpStream>,
    epoch: Instant,
    want_passcode: Option<u32>,
    want_media: Option<Leg>,
    control_leg: Option<Leg>,
    rx: Vec<u8>,
}

impl Client {
    /// `random` must be the platform's CSPRNG; `ecdh` its key agreement.
    pub fn new(config: Config, ecdh: Box<dyn Ecdh + Send>, random: RandomSource) -> Self {
        Self {
            session: Session::new(config, ecdh, random),
            udp: HashMap::new(),
            tcp: None,
            epoch: Instant::now(),
            want_passcode: None,
            want_media: None,
            control_leg: None,
            rx: vec![0; 65_536],
        }
    }

    fn now(&self) -> u64 {
        self.epoch.elapsed().as_micros() as u64 + 1
    }

    pub fn outcome(&self) -> &Outcome {
        self.session.outcome()
    }

    pub fn stage(&self) -> Stage {
        self.session.stage()
    }

    pub fn is_ended(&self) -> bool {
        self.session.is_ended()
    }

    /// Carries out everything the session asked for, including what doing so makes it ask next.
    fn run_io(&mut self) {
        while let Some(io) = self.session.poll_io() {
            let now = self.now();
            match io {
                Io::UdpOpen { socket, local_port, rcvbuf } => match open_udp(socket, local_port, rcvbuf) {
                    Ok((s, granted)) => {
                        let port = s.local_addr().map_or(0, |a| a.port());
                        self.udp.insert(socket, s);
                        self.session.on_udp_opened(now, socket, port, granted);
                    }
                    Err(_) => self.session.on_udp_failed(now, socket),
                },
                Io::UdpSend { socket, to, data } => {
                    // Best-effort, as UDP is: a full buffer or an unreachable host drops the datagram.
                    if let Some(s) = self.udp.get(&socket) {
                        let _ = s.send_to(&data, addr(to));
                    }
                }
                Io::UdpClose(socket) => {
                    self.udp.remove(&socket);
                }
                Io::TcpConnect(to) => match TcpStream::connect_timeout(&addr(to), TCP_CONNECT) {
                    Ok(t) => {
                        let _ = t.set_nodelay(true);
                        let _ = t.set_nonblocking(true);
                        self.tcp = Some(t);
                        self.session.on_tcp_connected(now);
                    }
                    Err(_) => self.session.on_tcp_closed(now, true),
                },
                Io::TcpSend(data) => {
                    let ok = self.tcp.as_mut().is_some_and(|t| {
                        let _ = t.set_nonblocking(false);
                        let ok = t.write_all(&data).is_ok();
                        let _ = t.set_nonblocking(true);
                        ok
                    });
                    if !ok && self.tcp.take().is_some() {
                        self.session.on_tcp_closed(now, true);
                    }
                }
                Io::TcpClose => {
                    self.tcp = None;
                }
            }
        }
    }

    /// Reads what every socket has, without blocking. `true` if anything arrived.
    fn read_sockets(&mut self) -> bool {
        let mut any = false;
        let sockets: Vec<Socket> = self.udp.keys().copied().collect();
        for socket in sockets {
            for _ in 0..DRAIN_BURST {
                let Some(s) = self.udp.get(&socket) else { break };
                match s.recv_from(&mut self.rx) {
                    Ok((n, from)) => {
                        any = true;
                        if let Some(from) = endpoint(from) {
                            let now = self.now();
                            let data = self.rx[..n].to_vec();
                            self.session.on_datagram(now, socket, from, &data);
                            self.run_io();
                        }
                    }
                    // A would-block is "nothing yet"; an ICMP error surfacing here is too, as in C.
                    Err(_) => break,
                }
            }
        }
        let now = self.now();
        if let Some(t) = self.tcp.as_mut() {
            match t.read(&mut self.rx) {
                Ok(0) => {
                    self.tcp = None;
                    self.session.on_tcp_closed(now, false);
                    any = true;
                }
                Ok(n) => {
                    let data = self.rx[..n].to_vec();
                    self.session.on_tcp_data(now, &data);
                    any = true;
                }
                Err(e) if e.kind() == ErrorKind::WouldBlock || e.kind() == ErrorKind::Interrupted => {}
                Err(_) => {
                    self.tcp = None;
                    self.session.on_tcp_closed(now, true);
                    any = true;
                }
            }
        }
        any
    }

    /// Hands events to the host and remembers the two that want answers.
    fn deliver(&mut self, host: &mut impl Host) {
        while let Some(e) = self.session.poll_event() {
            match &e {
                Event::PasscodeWanted { retry } => self.want_passcode = Some(*retry),
                Event::MediaWanted(leg) => self.want_media = Some(*leg),
                Event::ControlLegReady(leg) => self.control_leg = Some(*leg),
                _ => {}
            }
            host.event(e);
        }
    }

    /// Pulls the host's side: commands, the pad, and any answer the session is waiting for.
    fn pull(&mut self, host: &mut impl Host) {
        let now = self.now();
        let c = host.commands();
        if c.keyframe {
            self.session.request_keyframe(now);
        }
        // DISCONNECT before CANCEL when both arrive: it is the more specific request.
        if c.disconnect {
            self.session.disconnect(now, c.rest_console);
        }
        if c.cancel {
            self.session.cancel(now);
        }
        self.session.set_controller(host.controller());
        if let Some(retry) = self.want_passcode {
            match host.passcode(retry) {
                Answer::Pending => {}
                Answer::Ready(digits) => {
                    self.want_passcode = None;
                    self.session.submit_passcode(now, Some(digits));
                }
                Answer::GiveUp => {
                    self.want_passcode = None;
                    self.session.submit_passcode(now, None);
                }
            }
        }
        if let Some(leg) = self.want_media {
            match host.media(&leg) {
                Answer::Pending => {}
                Answer::Ready(peer) => {
                    self.want_media = None;
                    self.session.media_peer(now, Some(peer));
                }
                Answer::GiveUp => {
                    self.want_media = None;
                    self.session.media_peer(now, None);
                }
            }
        }
    }

    /// One turn: I/O, deadlines, events, the host's side. Sleeps up to `max_wait` when nothing happened.
    fn turn(&mut self, host: &mut impl Host, max_wait: Duration) {
        self.run_io();
        let active = self.read_sockets();
        let now = self.now();
        if self.session.next_timeout().is_some_and(|t| t <= now) {
            self.session.handle_timeout(now);
        }
        self.run_io();
        self.deliver(host);
        self.pull(host);
        self.run_io();
        self.deliver(host);
        if !active && !self.session.is_ended() {
            let now = self.now();
            let until = self
                .session
                .next_timeout()
                .map_or(max_wait, |t| Duration::from_micros(t.saturating_sub(now)));
            let sleep = until.min(max_wait).min(IDLE_SLEEP);
            if !sleep.is_zero() {
                std::thread::sleep(sleep);
            }
        }
    }

    /// Runs the sequence to streaming, or to its end. Blocking, bounded by the session's budgets, and
    /// cancellable through [`Host::commands`]. Returns the stage reached; [`Stage::StreamReady`] or later
    /// means [`Client::pump`] should follow.
    pub fn connect(&mut self, host: &mut impl Host) -> Stage {
        let now = self.now();
        self.session.connect(now);
        while !self.session.is_streaming() && !self.session.is_ended() {
            self.turn(host, IDLE_SLEEP);
        }
        self.run_io();
        self.session.stage()
    }

    /// Services a running session for at most about `max_wait`: A/V, control, input, the periodic sends.
    /// `true` while the session is alive.
    pub fn pump(&mut self, host: &mut impl Host, max_wait: Duration) -> bool {
        if self.session.is_ended() {
            return false;
        }
        self.turn(host, max_wait);
        if self.session.is_ended() {
            self.run_io();
            return false;
        }
        true
    }

    /// Goodbye: REST_MODE first when `rest_console`, then the Takion DISCONNECT; the session ends here.
    pub fn disconnect(&mut self, host: &mut impl Host, rest_console: bool) {
        let now = self.now();
        self.session.disconnect(now, rest_console);
        self.run_io();
        self.deliver(host);
    }

    // ---- The rendezvous route's pre-connect calls ----------------------------------------------------

    /// Binds the control leg and asks STUN about it. Blocking, at most servers x attempts x timeout.
    pub fn rendezvous_prepare(&mut self, host: &mut impl Host) -> Option<Leg> {
        let now = self.now();
        if !self.session.rendezvous_prepare(now) {
            return None;
        }
        let start = Instant::now();
        while self.control_leg.is_none() && !self.session.is_ended() && start.elapsed() < PREPARE_LIMIT {
            self.turn(host, IDLE_SLEEP);
        }
        self.control_leg
    }

    /// After our OFFER, before our ACCEPT: aims the control leg and sends our Init, without waiting.
    pub fn rendezvous_begin(&mut self, local_hashed_id: [u8; 20], console: Peer) -> bool {
        let now = self.now();
        let ok = self.session.rendezvous_begin(now, local_hashed_id, console);
        self.run_io();
        ok
    }

    /// One request and its answer on the control leg (registration on this route), between begin and
    /// connect. Blocking, bounded by the 9303 stage deadline.
    pub fn rendezvous_exchange(
        &mut self,
        host: &mut impl Host,
        request: Vec<u8>,
    ) -> Option<Result<Vec<u8>, rendezvous::Error>> {
        let now = self.now();
        if !self.session.rendezvous_exchange(now, request) {
            return None;
        }
        loop {
            if let Some(result) = self.session.poll_exchange() {
                return Some(result);
            }
            if self.session.is_ended() {
                return None;
            }
            self.turn(host, IDLE_SLEEP);
        }
    }
}
