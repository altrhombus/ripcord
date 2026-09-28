//! The binary control channel after /sess/ctrl, as a sans-IO machine. Ported from the service loop in
//! `libripcord/session/halyard_control_session.c` and `HalyardStreamingSession.RunCtrlKeepAliveAsync`.
//!
//! The host feeds it every byte the connection delivers (starting with whatever followed the /sess/ctrl
//! response) and sends what [`ControlSession::poll_transmit`] returns. Heartbeats are answered without the
//! host's involvement: a console that stops hearing them closes the session about 37 s later.
//!
//! Two counters, one per direction. Ours continues from the /sess/ctrl fields (5 on PS5, 4 on PS4) and
//! every payload we encrypt takes the next; the console's starts at 1 and every payload-carrying frame it
//! sends takes the next, heartbeats spending none. Reusing either reuses an IV.

use std::collections::VecDeque;

use super::{ctrl, fields};
use crate::halyard::control::ControlField;

/// The largest frame a sane console sends. A header claiming more is taken as the stream being out of
/// step, which C's hardware runs saw: an 80-minute session resynced 14 times, always discarding exactly 8
/// bytes. .NET has no resynchronisation.
pub const MAX_FRAME: usize = 4096;

/// What the console said.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Event {
    /// HEARTBEAT_REQ, already answered.
    Heartbeat,
    /// The account is locked: answer with [`ControlSession::submit_login`]. Until then the console drops
    /// every stream handshake silently.
    LoginPrompt,
    /// The verdict on a passcode.
    LoginResult { accepted: bool },
    /// SESSION_ID: the console is willing to stream.
    SessionReady,
    /// STREAM_READY: the stream service is up (the rendezvous route's A/V leg waits for it).
    StreamReady,
    /// Anything else, with its payload decrypted at `counter` when it carried one.
    Frame { kind: u16, payload: Vec<u8>, plaintext: Option<Vec<u8>>, counter: u64 },
}

pub struct ControlSession {
    field: ControlField,
    next_counter: u64,
    recv_counter: u64,
    inbound: Vec<u8>,
    outbox: VecDeque<Vec<u8>>,
    session_ready: bool,
    /// Resynchronisations and the bytes they discarded; should stay zero.
    pub resyncs: u64,
    pub resync_discarded: u64,
}

impl ControlSession {
    /// A session on a connection whose /sess/ctrl was answered. `leftover` is what followed the response
    /// in the same read: the first bytes of the channel.
    pub fn new(field: ControlField, is_ps5: bool, leftover: &[u8]) -> Self {
        Self {
            field,
            next_counter: fields::login_pin_counter(is_ps5),
            recv_counter: fields::COUNTER_CONSOLE_START,
            inbound: leftover.to_vec(),
            outbox: VecDeque::new(),
            session_ready: false,
            resyncs: 0,
            resync_discarded: 0,
        }
    }

    pub fn session_ready(&self) -> bool {
        self.session_ready
    }

    /// The counter our next encrypted payload takes.
    pub fn next_counter(&self) -> u64 {
        self.next_counter
    }

    /// The field cipher, for the launch spec (the streaminfo cipher shares it).
    pub fn field(&self) -> &ControlField {
        &self.field
    }

    /// Bytes from the connection.
    pub fn on_bytes(&mut self, data: &[u8]) {
        self.inbound.extend_from_slice(data);
    }

    pub fn poll_transmit(&mut self) -> Option<Vec<u8>> {
        self.outbox.pop_front()
    }

    /// Queues a frame with a plaintext payload (none of ours needs one but the heartbeat reply and rest).
    pub fn send(&mut self, kind: u16, payload: &[u8]) {
        self.outbox.push_back(ctrl::build(kind, payload));
    }

    /// Queues a frame whose payload is a control field, encrypted at our next counter (the rendezvous
    /// route's PROBE_REPORT, for instance).
    pub fn send_field(&mut self, kind: u16, plaintext: &[u8]) {
        let mut payload = plaintext.to_vec();
        self.field.encrypt(self.next_counter, &mut payload);
        self.next_counter += 1;
        self.send(kind, &payload);
    }

    /// Answers the sign-in gate. Every call, a retry included, takes a fresh counter. `false` for a
    /// passcode that is empty, longer than 32 digits or not digits, which spends no counter.
    pub fn submit_login(&mut self, pin: &str) -> bool {
        match ctrl::login_submit_payload(&self.field, self.next_counter, pin) {
            Some(payload) => {
                self.next_counter += 1;
                self.send(ctrl::LOGIN_SUBMIT, &payload);
                true
            }
            None => false,
        }
    }

    /// Puts the console to rest: an empty REST_MODE, sent before the stream is disconnected.
    pub fn request_rest(&mut self) {
        self.send(ctrl::REST_MODE, &[]);
    }

    /// If the buffered header claims an impossible length, skip to the first offset where a plausible
    /// header begins (reserved bytes zero, length in bounds), as the C core does.
    fn resync(&mut self) {
        let claimed = |b: &[u8]| u32::from_be_bytes([b[0], b[1], b[2], b[3]]) as usize;
        if self.inbound.len() < ctrl::HEADER_SIZE || claimed(&self.inbound) <= MAX_FRAME {
            return;
        }
        let skip = (1..=self.inbound.len() - ctrl::HEADER_SIZE).find(|&s| {
            self.inbound[s + 6] == 0 && self.inbound[s + 7] == 0 && claimed(&self.inbound[s..]) <= MAX_FRAME
        });
        if let Some(skip) = skip {
            self.resyncs += 1;
            self.resync_discarded += skip as u64;
            self.inbound.drain(..skip);
        }
    }

    /// The next complete frame's event, or `None` until more bytes arrive.
    pub fn poll_event(&mut self) -> Option<Event> {
        self.resync();
        let (kind, payload, used) = ctrl::parse(&self.inbound).map(|(k, p, u)| (k, p.to_vec(), u))?;
        self.inbound.drain(..used);
        let counter = self.recv_counter;
        let plaintext = (!payload.is_empty()).then(|| {
            self.recv_counter += 1;
            let mut p = payload.clone();
            self.field.decrypt(counter, &mut p);
            p
        });
        Some(match kind {
            ctrl::HEARTBEAT_REQ => {
                self.send(ctrl::HEARTBEAT_REP, &[]);
                Event::Heartbeat
            }
            ctrl::LOGIN_PROMPT => Event::LoginPrompt,
            ctrl::LOGIN if plaintext.as_ref().is_some_and(|p| !p.is_empty()) => Event::LoginResult {
                accepted: plaintext.as_ref().is_some_and(|p| p[0] == ctrl::LOGIN_ACCEPTED),
            },
            ctrl::SESSION_ID => {
                self.session_ready = true;
                Event::SessionReady
            }
            ctrl::STREAM_READY => Event::StreamReady,
            _ => Event::Frame { kind, payload, plaintext, counter },
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn field() -> ControlField {
        ControlField::new(&[3; 16], &[4; 16], 2, 1).unwrap()
    }

    /// The console's side: a frame whose payload is encrypted at its counter.
    fn console_frame(kind: u16, plaintext: &[u8], counter: u64) -> Vec<u8> {
        let mut p = plaintext.to_vec();
        field().encrypt(counter, &mut p);
        ctrl::build(kind, &p)
    }

    #[test]
    fn heartbeats_are_answered_and_spend_no_counter() {
        let mut s = ControlSession::new(field(), true, &ctrl::build(ctrl::HEARTBEAT_REQ, &[]));
        assert_eq!(s.poll_event(), Some(Event::Heartbeat));
        assert_eq!(s.poll_transmit(), Some(ctrl::build(ctrl::HEARTBEAT_REP, &[])));
        // The next payload the console sends is at counter 1.
        s.on_bytes(&console_frame(ctrl::LOGIN, &[ctrl::LOGIN_REJECTED], 1));
        assert_eq!(s.poll_event(), Some(Event::LoginResult { accepted: false }));
        s.on_bytes(&console_frame(ctrl::SESSION_ID, b"\x10InvalidSessionId", 2));
        assert_eq!(s.poll_event(), Some(Event::SessionReady));
        assert!(s.session_ready());
    }

    #[test]
    fn frames_wait_for_their_bytes() {
        let f = console_frame(0x0016, b"abcdef", 1);
        let mut s = ControlSession::new(field(), true, &f[..5]);
        assert_eq!(s.poll_event(), None);
        s.on_bytes(&f[5..]);
        assert_eq!(
            s.poll_event(),
            Some(Event::Frame {
                kind: 0x0016,
                payload: f[8..].to_vec(),
                plaintext: Some(b"abcdef".to_vec()),
                counter: 1
            })
        );
    }

    #[test]
    fn logins_take_a_fresh_counter_each_time() {
        let mut s = ControlSession::new(field(), false, &[]);
        assert_eq!(s.next_counter(), 4, "PS4's passcode is at 4");
        assert!(!s.submit_login("12a4"));
        assert_eq!(s.next_counter(), 4, "a refused passcode spends nothing");
        assert!(s.submit_login("1234"));
        assert!(s.submit_login("1234"));
        let (a, b) = (s.poll_transmit().unwrap(), s.poll_transmit().unwrap());
        assert_eq!(u16::from_be_bytes([a[4], a[5]]), ctrl::LOGIN_SUBMIT);
        assert_ne!(a, b, "a retry is encrypted at a new counter");
        let mut plain = a[8..].to_vec();
        field().decrypt(4, &mut plain);
        assert_eq!(plain, b"1234");
    }

    #[test]
    fn an_out_of_step_stream_is_resynchronised() {
        let mut junk = vec![0xff; 8];
        junk.extend(ctrl::build(ctrl::HEARTBEAT_REQ, &[]));
        let mut s = ControlSession::new(field(), true, &junk);
        assert_eq!(s.poll_event(), Some(Event::Heartbeat));
        assert_eq!((s.resyncs, s.resync_discarded), (1, 8));
    }
}
