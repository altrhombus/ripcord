//! The 9303 association's staging: what `HalyardDatagramControlChannel` and
//! `libripcord/session/halyard_dgram_channel.c` do around it, without the socket. It holds no protocol
//! knowledge. It re-sends on a quiet receive window and gives up at a stage deadline with a message a
//! person can act on; every protocol decision is the association's.
//!
//! The host starts a stage, feeds every datagram and the time, sends what [`Channel::poll_transmit`]
//! returns, and reads the stage's result from [`Channel::poll_stage`].

use super::assoc::{Addressing, Association, Event, Phase, SendError};
use super::wire;

/// `HalyardDatagramControlOptions`' defaults, which the C core shares.
#[derive(Clone, Copy, Debug)]
pub struct Options {
    /// One receive window before re-sending: 5 s.
    pub receive_timeout_us: u64,
    /// How long each stage may take: 30 s.
    pub stage_timeout_us: u64,
    /// The shape every capture carries.
    pub hello_addressing: Addressing,
}

impl Default for Options {
    fn default() -> Self {
        Self {
            receive_timeout_us: 5_000_000,
            stage_timeout_us: 30_000_000,
            hello_addressing: Addressing::PortPair,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Goal {
    Prelude,
    Connected,
    DataOrClose,
    Response,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Quiet {
    Nothing,
    Retry,
    Reopen,
}

struct Stage {
    goal: Goal,
    quiet: Quiet,
    started_us: u64,
    window_us: u64,
    timeout_message: &'static str,
}

/// How a stage ended.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum StageResult {
    /// The prelude is established, a connection is open, or payload is waiting: whichever was asked.
    Done,
    /// The complete HTTP response to a request.
    Response(Vec<u8>),
    /// The console closed the connection before answering, or with nothing pending.
    PeerClosed,
    /// The stage deadline passed; the message says what that normally means.
    Timeout(&'static str),
}

pub struct Channel {
    assoc: Association,
    options: Options,
    stage: Option<Stage>,
    result: Option<StageResult>,
    saw_close: bool,
    events: Vec<Event>,
}

impl Channel {
    pub fn new(assoc: Association, options: Options) -> Self {
        Self { assoc, options, stage: None, result: None, saw_close: false, events: Vec::new() }
    }

    pub fn phase(&self) -> Phase {
        self.assoc.phase()
    }

    pub fn poll_transmit(&mut self) -> Option<Vec<u8>> {
        self.assoc.poll_transmit()
    }

    /// The association's events since the last call, for a host's log (unhandled datagrams included).
    pub fn take_events(&mut self) -> Vec<Event> {
        std::mem::take(&mut self.events)
    }

    fn start(&mut self, now_us: u64, goal: Goal, quiet: Quiet, timeout_message: &'static str) {
        self.result = None;
        self.stage = Some(Stage { goal, quiet, started_us: now_us, window_us: now_us, timeout_message });
        self.check_goal();
    }

    /// Puts our opening Init on the wire without waiting: the console cannot answer until signaling has
    /// told it our candidate, so the Init goes out after our OFFER and before our ACCEPT.
    pub fn begin(&mut self) {
        self.assoc.open();
    }

    /// Completes the prelude in whichever role the peer leaves us, re-sending our Init on every quiet
    /// window. Also what the A/V leg runs on its own socket before Takion, as the hole punch.
    pub fn establish(&mut self, now_us: u64) {
        self.assoc.open();
        self.start(
            now_us,
            Goal::Prelude,
            Quiet::Retry,
            "The console did not complete the control prelude. On a LAN this normally means 9303 is unreachable; \
             the account route also requires that the candidate exchange has completed, since the prelude names \
             both peers by their signaling id.",
        );
    }

    /// Opens a chunk connection, re-sending the hello on every quiet window (the console routinely ignores
    /// the first), and waits for Connected specifically: the previous connection's teardown often arrives
    /// after the next hello.
    pub fn open_connection(&mut self, now_us: u64) {
        self.assoc.open_connection(self.options.hello_addressing);
        self.saw_close = false;
        self.start(now_us, Goal::Connected, Quiet::Reopen, "The console did not open a control connection.");
    }

    /// Sends raw bytes on the open connection.
    pub fn send(&mut self, data: &[u8]) -> Result<(), SendError> {
        self.assoc.send(data)
    }

    /// Sends an HTTP request on the open connection and waits for the complete response.
    pub fn request(&mut self, now_us: u64, request: &[u8]) -> Result<(), SendError> {
        self.assoc.send(request)?;
        self.start(now_us, Goal::Response, Quiet::Nothing, "The console did not answer the request.");
        Ok(())
    }

    /// Waits for payload on the open connection (or its close). Read it with [`Channel::take_bytes`].
    pub fn await_data(&mut self, now_us: u64) {
        self.saw_close = false;
        self.start(
            now_us,
            Goal::DataOrClose,
            Quiet::Nothing,
            "The console sent nothing on the control connection.",
        );
    }

    /// Takes up to `max` bytes of received payload, leaving the rest.
    pub fn take_bytes(&mut self, max: usize) -> Vec<u8> {
        let take = self.assoc.inbound()[..max.min(self.assoc.inbound().len())].to_vec();
        self.assoc.consume_inbound(take.len());
        take
    }

    /// Everything received and not yet taken.
    pub fn inbound(&self) -> &[u8] {
        self.assoc.inbound()
    }

    /// Ends the open connection politely. Never an error: a console that never hears it is no worse off.
    pub fn close_connection(&mut self) {
        self.assoc.close_connection();
    }

    pub fn on_datagram(&mut self, now_us: u64, datagram: &[u8]) {
        self.assoc.on_datagram(datagram);
        while let Some(e) = self.assoc.poll_event() {
            if e == Event::PeerClosed {
                self.saw_close = true;
            }
            self.events.push(e);
        }
        if let Some(stage) = self.stage.as_mut() {
            stage.window_us = now_us; // a datagram starts a new quiet window
        }
        self.check_goal();
    }

    pub fn next_timeout(&self) -> Option<u64> {
        let s = self.stage.as_ref()?;
        Some(
            (s.window_us + self.options.receive_timeout_us).min(s.started_us + self.options.stage_timeout_us),
        )
    }

    pub fn handle_timeout(&mut self, now_us: u64) {
        let Some(stage) = self.stage.as_mut() else { return };
        if now_us >= stage.started_us + self.options.stage_timeout_us {
            self.result = Some(StageResult::Timeout(stage.timeout_message));
            self.stage = None;
            return;
        }
        if now_us >= stage.window_us + self.options.receive_timeout_us {
            stage.window_us = now_us;
            match stage.quiet {
                Quiet::Retry => self.assoc.retry(),
                Quiet::Reopen => self.assoc.reopen_connection(),
                Quiet::Nothing => {}
            }
        }
    }

    fn check_goal(&mut self) {
        let Some(stage) = &self.stage else { return };
        let phase = self.assoc.phase();
        let inbound = self.assoc.inbound();
        let result = match stage.goal {
            Goal::Prelude => {
                (!matches!(phase, Phase::Idle | Phase::Handshaking)).then_some(StageResult::Done)
            }
            Goal::Connected => (phase == Phase::Connected).then_some(StageResult::Done),
            Goal::DataOrClose if !inbound.is_empty() => Some(StageResult::Done),
            Goal::DataOrClose => self.saw_close.then_some(StageResult::PeerClosed),
            Goal::Response if !inbound.is_empty() && wire::http_complete(inbound) => {
                let response = inbound.to_vec();
                self.assoc.clear_inbound();
                Some(StageResult::Response(response))
            }
            Goal::Response => (phase == Phase::Closed).then_some(StageResult::PeerClosed),
        };
        if result.is_some() {
            self.result = result;
            self.stage = None;
        }
    }

    /// The current stage's result once it has ended, taken once.
    pub fn poll_stage(&mut self) -> Option<StageResult> {
        self.result.take()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
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

    /// Runs datagrams between the channel and the scripted console until neither has more to say.
    fn pump(c: &mut Channel, console: &mut ScriptedConsole, now: u64) {
        while let Some(d) = c.poll_transmit() {
            for reply in console.on_datagram(&d) {
                c.on_datagram(now, &reply);
            }
        }
    }

    #[test]
    fn an_exchange_runs_against_the_scripted_console() {
        let mut console = ScriptedConsole::new();
        console.init_reply = Some(b"HTTP/1.1 200 OK\r\nRP-Nonce: abc\r\nContent-Length: 0\r\n\r\n".to_vec());
        let mut c = channel();
        c.establish(0);
        pump(&mut c, &mut console, 0);
        assert_eq!(c.poll_stage(), Some(StageResult::Done));
        c.open_connection(0);
        pump(&mut c, &mut console, 0);
        assert_eq!(c.poll_stage(), Some(StageResult::Done));
        c.request(0, b"GET /sie/ps5/rp/sess/init HTTP/1.1\r\n\r\n").unwrap();
        pump(&mut c, &mut console, 0);
        match c.poll_stage() {
            Some(StageResult::Response(r)) => {
                assert!(r.ends_with(b"RP-Nonce: abc\r\nContent-Length: 0\r\n\r\n"))
            }
            other => panic!("{other:?}"),
        }
        assert_eq!(c.phase(), Phase::Closed, "the console closes after answering");
        assert_eq!(console.request_count, 1);
    }

    #[test]
    fn a_quiet_window_resends_and_the_stage_deadline_ends_it() {
        let mut c = channel();
        c.establish(0);
        let first = c.poll_transmit().unwrap();
        c.handle_timeout(4_999_999);
        assert!(c.poll_transmit().is_none());
        c.handle_timeout(5_000_000);
        assert_eq!(c.poll_transmit(), Some(first), "the same Init again");
        c.handle_timeout(30_000_000);
        assert!(matches!(c.poll_stage(), Some(StageResult::Timeout(m)) if m.contains("prelude")));
    }

    #[test]
    fn a_console_that_closes_before_answering() {
        let mut console = ScriptedConsole::new();
        console.close_before_answering = true;
        let mut c = channel();
        c.establish(0);
        pump(&mut c, &mut console, 0);
        c.poll_stage();
        c.open_connection(0);
        pump(&mut c, &mut console, 0);
        c.poll_stage();
        c.request(0, b"POST /sie/ps5/rp/sess/rgst HTTP/1.1\r\n\r\n").unwrap();
        pump(&mut c, &mut console, 0);
        assert_eq!(c.poll_stage(), Some(StageResult::PeerClosed));
    }
}
