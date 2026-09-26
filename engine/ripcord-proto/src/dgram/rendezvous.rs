//! The rendezvous route's control plane over the 9303 association: one request and its answer on a
//! fresh connection ([`Exchange`], which is how /sess/rgst travels on this route), and the whole
//! /sess/init → /sess/ctrl → binary channel sequence ([`ControlPlane`]). Composed from
//! `HalyardDatagramSessionControlChannel.cs` and `HalyardDatagramRegistrationTransport.cs`, and
//! `libripcord/session/halyard_dgram_session.c`, which agree on the order: init on its own connection,
//! which the console closes after answering; a reopened connection for /sess/ctrl, which the console
//! keeps; and every byte after the /sess/ctrl response belongs to the [`ControlSession`].
//!
//! What is deliberately not here: the cloud signaling that must finish before the console will answer
//! (the host's), sockets, and the overall deadline, which the connect sequence owns. Each stage keeps
//! the channel's own 30 s limit, as .NET's per-read wait does; the C core waits 5 s per /sess response.

use std::collections::VecDeque;

use super::channel::{Channel, StageResult};
use crate::halyard::control::ControlField;
use crate::sess::http::Response;
use crate::sess::requests::{self, Addressing, CtrlFields, InitError};
use crate::sess::session::{self, ControlSession};

/// Why the control plane or an exchange stopped.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Error {
    /// A stage ran out of time; the message says what that normally means.
    Timeout(&'static str),
    /// The console closed the connection before answering.
    PeerClosed,
    /// The request does not fit one datagram's payload.
    TooLong,
    /// /sess/init answered without a usable nonce, or refused.
    Init(InitError),
    /// /sess/ctrl was refused, or its answer did not parse.
    CtrlRefused { status: u16 },
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum ExchangeStep {
    Establish,
    Open,
    Request,
    Done,
}

/// One request on a new connection and its complete answer. The connection is left for the console to
/// close, as both references do.
pub struct Exchange {
    channel: Channel,
    request: Vec<u8>,
    step: ExchangeStep,
    result: Option<Result<Vec<u8>, Error>>,
}

impl Exchange {
    /// Starts at once: the prelude if it is not established yet (idempotent otherwise), then a connection.
    pub fn new(channel: Channel, request: Vec<u8>, now_us: u64) -> Self {
        let mut e = Self { channel, request, step: ExchangeStep::Establish, result: None };
        e.channel.establish(now_us);
        e.advance(now_us);
        e
    }

    pub fn poll_transmit(&mut self) -> Option<Vec<u8>> {
        self.channel.poll_transmit()
    }

    pub fn on_datagram(&mut self, now_us: u64, datagram: &[u8]) {
        self.channel.on_datagram(now_us, datagram);
        self.advance(now_us);
    }

    pub fn next_timeout(&self) -> Option<u64> {
        self.channel.next_timeout()
    }

    pub fn handle_timeout(&mut self, now_us: u64) {
        self.channel.handle_timeout(now_us);
        self.advance(now_us);
    }

    fn advance(&mut self, now_us: u64) {
        while self.step != ExchangeStep::Done {
            let Some(stage) = self.channel.poll_stage() else { return };
            let next = match (self.step, stage) {
                (ExchangeStep::Establish, StageResult::Done) => {
                    self.channel.open_connection(now_us);
                    ExchangeStep::Open
                }
                (ExchangeStep::Open, StageResult::Done) => {
                    match self.channel.request(now_us, &self.request) {
                        Ok(()) => ExchangeStep::Request,
                        Err(_) => return self.finish(Err(Error::TooLong)),
                    }
                }
                (ExchangeStep::Request, StageResult::Response(r)) => return self.finish(Ok(r)),
                (_, StageResult::Timeout(m)) => return self.finish(Err(Error::Timeout(m))),
                (_, _) => return self.finish(Err(Error::PeerClosed)),
            };
            self.step = next;
        }
    }

    fn finish(&mut self, result: Result<Vec<u8>, Error>) {
        self.step = ExchangeStep::Done;
        self.result = Some(result);
    }

    /// The complete answer, or why there is none, once; `None` while the exchange is running.
    pub fn poll_result(&mut self) -> Option<Result<Vec<u8>, Error>> {
        self.result.take()
    }

    /// The channel back, for the next exchange or the control plane.
    pub fn into_channel(self) -> Channel {
        self.channel
    }
}

/// The owned values behind [`CtrlFields`], kept until the /sess/init answer supplies the field crypto.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CtrlValues {
    pub registration_key: Vec<u8>,
    pub device_id: Vec<u8>,
    pub os_major: i32,
    pub os_minor: i32,
    pub start_bitrate_kbps: i32,
    pub streaming_type: i32,
}

/// What the control plane reports.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Event {
    /// /sess/ctrl was answered: the binary channel is running and [`ControlPlane::session`] is live.
    Connected,
    /// Something the console said on the binary channel.
    Session(session::Event),
    /// The console closed the binary channel. Latched, as the C pipe does: .NET loses a close that
    /// arrives in the same read as data.
    Closed,
    /// The sequence stopped before the channel ran.
    Failed(Error),
}

enum Step {
    Init(Exchange),
    Ctrl { channel: Channel, field: ControlField },
    Running { channel: Channel },
    Stopped,
}

/// /sess/init, /sess/ctrl and the binary channel after it, over the 9303 association.
pub struct ControlPlane {
    step: Step,
    is_ps5: bool,
    addressing: Addressing,
    companion: [u8; 16],
    values: CtrlValues,
    session: Option<ControlSession>,
    events: VecDeque<Event>,
    pending: VecDeque<Vec<u8>>,
}

impl ControlPlane {
    /// Starts /sess/init at once on `channel`, whose prelude may or may not be established yet.
    /// `addressing` should name [`requests::ConnectionPath::Rendezvous`] and the console's host at 9303.
    pub fn new(
        channel: Channel,
        is_ps5: bool,
        addressing: Addressing,
        companion: [u8; 16],
        values: CtrlValues,
        now_us: u64,
    ) -> Self {
        let request = requests::init(is_ps5, &addressing, &values.registration_key);
        let mut plane = Self {
            step: Step::Init(Exchange::new(channel, request, now_us)),
            is_ps5,
            addressing,
            companion,
            values,
            session: None,
            events: VecDeque::new(),
            pending: VecDeque::new(),
        };
        plane.advance(now_us);
        plane
    }

    /// The binary channel's session once [`Event::Connected`] has been reported: for the sign-in, the
    /// PROBE_REPORT and anything else the connect sequence sends. What it queues goes out on the next
    /// [`ControlPlane::poll_transmit`].
    pub fn session(&mut self) -> Option<&mut ControlSession> {
        self.session.as_mut()
    }

    pub fn poll_transmit(&mut self) -> Option<Vec<u8>> {
        if let Some(d) = self.pending.pop_front() {
            return Some(d);
        }
        match &mut self.step {
            Step::Init(e) => e.poll_transmit(),
            Step::Ctrl { channel, .. } => channel.poll_transmit(),
            Step::Running { channel } => {
                if let Some(session) = self.session.as_mut() {
                    while let Some(frame) = session.poll_transmit() {
                        // Refused only after the console has closed, when nothing is listening.
                        let _ = channel.send(&frame);
                    }
                }
                channel.poll_transmit()
            }
            Step::Stopped => None,
        }
    }

    pub fn poll_event(&mut self) -> Option<Event> {
        if let Some(session) = self.session.as_mut() {
            while let Some(e) = session.poll_event() {
                self.events.push_back(Event::Session(e));
            }
        }
        self.events.pop_front()
    }

    pub fn on_datagram(&mut self, now_us: u64, datagram: &[u8]) {
        match &mut self.step {
            Step::Init(e) => e.on_datagram(now_us, datagram),
            Step::Ctrl { channel, .. } => channel.on_datagram(now_us, datagram),
            Step::Running { channel } => {
                channel.on_datagram(now_us, datagram);
                let bytes = channel.take_bytes(usize::MAX);
                if let Some(session) = self.session.as_mut()
                    && !bytes.is_empty()
                {
                    session.on_bytes(&bytes);
                }
                if channel.take_events().contains(&super::assoc::Event::PeerClosed) {
                    self.events.push_back(Event::Closed);
                    self.drain_and_stop();
                    return;
                }
            }
            Step::Stopped => return,
        }
        self.advance(now_us);
    }

    pub fn next_timeout(&self) -> Option<u64> {
        match &self.step {
            Step::Init(e) => e.next_timeout(),
            Step::Ctrl { channel, .. } => channel.next_timeout(),
            _ => None,
        }
    }

    pub fn handle_timeout(&mut self, now_us: u64) {
        match &mut self.step {
            Step::Init(e) => e.handle_timeout(now_us),
            Step::Ctrl { channel, .. } => channel.handle_timeout(now_us),
            _ => return,
        }
        self.advance(now_us);
    }

    /// Ends the binary channel politely. Never an error.
    pub fn close(&mut self) {
        if let Step::Running { channel } = &mut self.step {
            channel.close_connection();
        }
        self.drain_and_stop();
    }

    /// Keeps whatever the channel has queued (the close, above) and stops.
    fn drain_and_stop(&mut self) {
        if let Step::Running { channel } | Step::Ctrl { channel, .. } = &mut self.step {
            while let Some(d) = channel.poll_transmit() {
                self.pending.push_back(d);
            }
        }
        self.step = Step::Stopped;
    }

    fn fail(&mut self, error: Error) {
        self.events.push_back(Event::Failed(error));
        self.step = Step::Stopped;
    }

    fn advance(&mut self, now_us: u64) {
        loop {
            match std::mem::replace(&mut self.step, Step::Stopped) {
                Step::Init(mut e) => {
                    let Some(result) = e.poll_result() else {
                        self.step = Step::Init(e);
                        return;
                    };
                    let response = match result {
                        Ok(r) => r,
                        Err(error) => return self.fail(error),
                    };
                    // A complete response always parses unless its status line has no code.
                    let Some(reply) = Response::parse(&response) else {
                        return self.fail(Error::Init(InitError::Refused { status: 0, reason: None }));
                    };
                    let field = match requests::open_init(self.is_ps5, &reply, &self.companion) {
                        Ok(f) => f,
                        Err(error) => return self.fail(Error::Init(error)),
                    };
                    // The console closed the init connection; /sess/ctrl needs a new one.
                    let mut channel = e.into_channel();
                    channel.open_connection(now_us);
                    self.step = Step::Ctrl { channel, field };
                }
                Step::Ctrl { mut channel, field } => match channel.poll_stage() {
                    None => {
                        self.step = Step::Ctrl { channel, field };
                        return;
                    }
                    Some(StageResult::Done) => {
                        let v = &self.values;
                        let request = requests::ctrl(
                            self.is_ps5,
                            &self.addressing,
                            &field,
                            &CtrlFields {
                                registration_key: &v.registration_key,
                                device_id: &v.device_id,
                                os_major: v.os_major,
                                os_minor: v.os_minor,
                                start_bitrate_kbps: v.start_bitrate_kbps,
                                streaming_type: v.streaming_type,
                            },
                        );
                        if channel.request(now_us, &request).is_err() {
                            return self.fail(Error::TooLong);
                        }
                        self.step = Step::Ctrl { channel, field };
                    }
                    Some(StageResult::Response(response)) => {
                        let reply = Response::parse(&response);
                        let Some(reply) = reply.filter(Response::is_success) else {
                            let status = Response::parse(&response).map_or(0, |r| r.status);
                            return self.fail(Error::CtrlRefused { status });
                        };
                        let session = ControlSession::new(field, self.is_ps5, &response[reply.consumed..]);
                        self.session = Some(session);
                        self.events.push_back(Event::Connected);
                        // The init connection's close is history, not this channel's.
                        channel.take_events();
                        self.step = Step::Running { channel };
                        return;
                    }
                    Some(StageResult::Timeout(m)) => return self.fail(Error::Timeout(m)),
                    Some(StageResult::PeerClosed) => return self.fail(Error::PeerClosed),
                },
                other => {
                    self.step = other;
                    return;
                }
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::dgram::assoc::Association;
    use crate::dgram::channel::Options;
    use crate::sess::ctrl;
    use crate::sess::requests::ConnectionPath;
    use crate::testing::scripted_console::{self, ScriptedConsole};

    fn channel() -> Channel {
        let mut n = 0x40u8;
        let random = Box::new(move |b: &mut [u8]| {
            b.iter_mut().for_each(|x| {
                *x = n;
                n = n.wrapping_add(1)
            })
        });
        let assoc = Association::new(
            random,
            scripted_console::client_id(),
            scripted_console::console_id(),
            [192, 0, 2, 7],
            9303,
        );
        Channel::new(assoc, Options::default())
    }

    fn values() -> CtrlValues {
        CtrlValues {
            registration_key: vec![0x11; 8],
            device_id: vec![0x22; 32],
            os_major: 10,
            os_minor: 0,
            start_bitrate_kbps: 15_000,
            streaming_type: 1,
        }
    }

    fn plane_pump(p: &mut ControlPlane, console: &mut ScriptedConsole, now: u64) {
        while let Some(d) = p.poll_transmit() {
            for reply in console.on_datagram(&d) {
                p.on_datagram(now, &reply);
            }
        }
    }

    const INIT_REPLY: &[u8] =
        b"HTTP/1.1 200 OK\r\nRP-Nonce: AAAAAAAAAAAAAAAAAAAAAA==\r\nContent-Length: 0\r\n\r\n";

    #[test]
    fn init_then_ctrl_then_the_binary_channel() {
        let mut console = ScriptedConsole::new();
        console.init_reply = Some(INIT_REPLY.to_vec());
        let addressing = Addressing::new([192, 0, 2, 7], 9303, ConnectionPath::Rendezvous);
        let mut p = ControlPlane::new(channel(), true, addressing, [0x33; 16], values(), 0);
        plane_pump(&mut p, &mut console, 0);
        assert_eq!(p.poll_event(), Some(Event::Connected));
        assert_eq!(console.request_count, 2);
        assert!(console.requests[0].starts_with(b"GET /sie/ps5/rp/sess/init"));
        let ctrl_text = String::from_utf8_lossy(&console.requests[1]).into_owned();
        assert!(ctrl_text.contains("RP-ConPath: 3\r\n"), "{ctrl_text}");
        assert!(ctrl_text.contains("Host: 192.  0.  2.  7:9303\r\n"), "{ctrl_text}");
        assert_eq!(console.hellos, 2, "one connection per request");

        // A frame from the session reaches the console as a control frame on the kept connection.
        let before = console.frames.len();
        p.session().unwrap().send(ctrl::HEARTBEAT_REP, &[]);
        plane_pump(&mut p, &mut console, 0);
        assert_eq!(console.frames.len(), before + 1);

        // A console-originated heartbeat is answered without the host.
        let heartbeat = console.push(&ctrl::build(ctrl::HEARTBEAT_REQ, &[])).unwrap();
        p.on_datagram(0, &heartbeat);
        assert_eq!(p.poll_event(), Some(Event::Session(session::Event::Heartbeat)));
        plane_pump(&mut p, &mut console, 0);
        assert_eq!(console.frames.len(), before + 2, "the answer went out");

        p.on_datagram(0, &ScriptedConsole::close());
        assert_eq!(p.poll_event(), Some(Event::Closed));
        assert_eq!(p.poll_transmit(), None);
    }

    #[test]
    fn a_missing_nonce_stops_before_ctrl() {
        let mut console = ScriptedConsole::new();
        console.init_reply = Some(b"HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n".to_vec());
        let addressing = Addressing::new([192, 0, 2, 7], 9303, ConnectionPath::Rendezvous);
        let mut p = ControlPlane::new(channel(), true, addressing, [0x33; 16], values(), 0);
        plane_pump(&mut p, &mut console, 0);
        assert_eq!(p.poll_event(), Some(Event::Failed(Error::Init(InitError::NoNonce))));
        assert_eq!(console.request_count, 1, "no /sess/ctrl, so nothing goes out unauthenticated");
    }

    #[test]
    fn an_exchange_answers_and_a_silent_console_times_out() {
        let mut console = ScriptedConsole::new();
        console.other_reply = Some(b"HTTP/1.1 200 OK\r\nContent-Length: 3\r\n\r\nabc".to_vec());
        let mut e = Exchange::new(channel(), b"POST /sie/ps5/rp/sess/rgst HTTP/1.1\r\n\r\n".to_vec(), 0);
        while let Some(d) = e.poll_transmit() {
            for reply in console.on_datagram(&d) {
                e.on_datagram(0, &reply);
            }
        }
        assert!(e.poll_result().unwrap().unwrap().ends_with(b"\r\n\r\nabc"));

        let mut e = Exchange::new(channel(), b"POST / HTTP/1.1\r\n\r\n".to_vec(), 0);
        while e.poll_transmit().is_some() {}
        let mut now = 0;
        while e.poll_result().is_none() {
            now = e.next_timeout().unwrap();
            e.handle_timeout(now);
        }
        assert_eq!(now, 30_000_000);
    }
}
