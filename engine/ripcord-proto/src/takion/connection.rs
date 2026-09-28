//! The Takion association as a sans-IO state machine: the SCTP 4-way handshake, then reliable DATA with
//! cumulative SACKs, fragmentation, reassembly and retransmission. Ported from
//! `libripcord/takion/takion_reliable_channel.c` and checked against `TakionConnection.cs` and
//! `TakionReliableChannel.cs`; the choices where the two differ are listed in `engine/README.md`.
//!
//! The host owns the socket and the clock. It calls [`Connection::connect`], feeds every control
//! datagram to [`Connection::on_datagram`], calls [`Connection::handle_timeout`] at or after
//! [`Connection::next_timeout`], and sends whatever [`Connection::poll_transmit`] returns. Time is in
//! microseconds from any fixed origin. Routing by base type and filtering by peer address are the host's:
//! [`is_control`] says which datagrams belong here.

use std::collections::{HashMap, VecDeque};

use super::chunks::{self, Header};
use super::sealer::{Sealer, Verifier};

/// Both references use 1000.
pub const MAX_PAYLOAD_PER_CHUNK: usize = 1000;
/// Both references use 300 ms. Each chunk is resent once it is this old, as in C; .NET resends every
/// unacknowledged chunk on each tick, including one sent a millisecond before it.
pub const RETRANSMIT_INTERVAL_US: u64 = 300_000;
/// A bound on one reassembled message against hostile growth. .NET has none; C's 2048 is a console-port
/// memory limit.
pub const MAX_MESSAGE: usize = 64 * 1024;

/// Whether a datagram is Takion control traffic (base type 0), as opposed to A/V, feedback or congestion.
pub fn is_control(datagram: &[u8]) -> bool {
    datagram.first().is_some_and(|b| b & 0x0f == chunks::BASE_TYPE_CONTROL)
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum State {
    Idle,
    AwaitingInitAck,
    AwaitingCookieAck,
    Established,
    /// The handshake ran out of attempts.
    Failed,
}

#[derive(Clone, Copy, Debug)]
pub struct Config {
    /// Sends per handshake phase (INIT, then COOKIE_ECHO), each phase with its own budget, as in both.
    pub max_attempts: u32,
    pub attempt_timeout_us: u64,
}

/// A complete reliable message.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Message {
    pub channel: u16,
    pub payload: Vec<u8>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SendError {
    NotEstablished,
    /// A chunk larger than a DATA chunk's 16-bit length can describe; cannot happen at 1000 per chunk.
    TooLarge,
}

struct Unacked {
    tsn: u32,
    packet: Vec<u8>,
    first_sent_us: u64,
    last_sent_us: u64,
    retransmitted: bool,
}

/// RFC 1982 serial comparison, so a SACK clears across a TSN wrap. .NET's sorted-key scan does not.
fn tsn_le(a: u32, b: u32) -> bool {
    (a.wrapping_sub(b) as i32) <= 0
}

pub struct Connection {
    config: Config,
    state: State,
    local_tag: u32,
    peer_tag: u32,
    cookie: [u8; chunks::COOKIE_SIZE],
    attempts: u32,
    deadline_us: u64,

    next_send_tsn: u32,
    expected_recv_tsn: u32,
    have_received_any: bool,
    unacked: Vec<Unacked>,
    /// Per-channel reassembly, as .NET keys it: a continuation carries its channel, so messages on
    /// different channels can be in flight together.
    partial: HashMap<u16, Vec<u8>>,
    outbox: VecDeque<Vec<u8>>,

    sealer: Option<Sealer>,
    verifier: Option<Verifier>,
    verify_enforces: bool,
    verify_dropped: u64,
    smoothed_rtt_us: f64,
    rtt_samples: u64,
}

impl Connection {
    /// `local_tag` must come from the host's CSPRNG, as .NET's does; zero is replaced by one.
    pub fn new(local_tag: u32, config: Config) -> Self {
        Self {
            config,
            state: State::Idle,
            local_tag: local_tag.max(1),
            peer_tag: 0,
            cookie: [0; chunks::COOKIE_SIZE],
            attempts: 0,
            deadline_us: 0,
            next_send_tsn: 0,
            expected_recv_tsn: 0,
            have_received_any: false,
            unacked: Vec::new(),
            partial: HashMap::new(),
            outbox: VecDeque::new(),
            sealer: None,
            verifier: None,
            verify_enforces: false,
            verify_dropped: 0,
            smoothed_rtt_us: 0.0,
            rtt_samples: 0,
        }
    }

    pub fn state(&self) -> State {
        self.state
    }

    pub fn local_tag(&self) -> u32 {
        self.local_tag
    }

    /// The smoothed round trip in microseconds and how many samples fed it (zero means no estimate).
    /// Seeded by the first sample, then an EWMA with alpha 0.125, retransmitted chunks excluded (Karn):
    /// .NET's estimator, which the stats need.
    pub fn round_trip(&self) -> (f64, u64) {
        (self.smoothed_rtt_us, self.rtt_samples)
    }

    /// Incoming packets that failed GMAC verification (dropped only if enforcing).
    pub fn verify_dropped(&self) -> u64 {
        self.verify_dropped
    }

    pub fn unacked_count(&self) -> usize {
        self.unacked.len()
    }

    /// The next datagram to send, if any.
    pub fn poll_transmit(&mut self) -> Option<Vec<u8>> {
        self.outbox.pop_front()
    }

    /// When [`handle_timeout`](Self::handle_timeout) next has work: a handshake retry or a retransmission.
    pub fn next_timeout(&self) -> Option<u64> {
        match self.state {
            State::AwaitingInitAck | State::AwaitingCookieAck => Some(self.deadline_us),
            State::Established => self.unacked.iter().map(|u| u.last_sent_us + RETRANSMIT_INTERVAL_US).min(),
            _ => None,
        }
    }

    /// Starts the handshake: queues INIT. A no-op unless idle.
    pub fn connect(&mut self, now_us: u64) {
        if self.state == State::Idle {
            self.state = State::AwaitingInitAck;
            self.attempts = 0;
            self.send_handshake(now_us);
        }
    }

    fn send_handshake(&mut self, now_us: u64) {
        let packet = match self.state {
            State::AwaitingInitAck => Header::control(0).build(&chunks::build_init(self.local_tag)),
            State::AwaitingCookieAck => {
                Header::control(self.peer_tag).build(&chunks::build_cookie_echo(&self.cookie))
            }
            _ => return,
        };
        self.attempts += 1;
        self.deadline_us = now_us + self.config.attempt_timeout_us;
        self.outbox.push_back(packet);
    }

    /// Handshake retries, and the retransmission of every chunk at least 300 ms old.
    pub fn handle_timeout(&mut self, now_us: u64) {
        match self.state {
            State::AwaitingInitAck | State::AwaitingCookieAck if now_us >= self.deadline_us => {
                if self.attempts < self.config.max_attempts {
                    self.send_handshake(now_us);
                } else {
                    self.state = State::Failed;
                }
            }
            State::Established => {
                for u in self.unacked.iter_mut() {
                    if now_us.saturating_sub(u.last_sent_us) >= RETRANSMIT_INTERVAL_US {
                        // The stored bytes, sealed once: the key position reserved for them stays theirs.
                        self.outbox.push_back(u.packet.clone());
                        u.last_sent_us = now_us;
                        u.retransmitted = true;
                    }
                }
            }
            _ => {}
        }
    }

    /// Seals everything sent from now on, SACKs included; forgetting SACKs makes the console drop the
    /// session over an unacknowledged STREAM_INFO. Call right after key agreement, before sending.
    pub fn enable_sealing(&mut self, aes_key: &[u8; 16], base_iv: &[u8; 16]) {
        self.sealer = Some(Sealer::new(aes_key, base_iv));
    }

    /// Authenticates incoming control packets, which .NET does not do. `enforce` false counts failures and
    /// delivers anyway: start there on a new platform, so a wrong key schedule shows as a number.
    pub fn enable_verification(&mut self, aes_key: &[u8; 16], base_iv: &[u8; 16], enforce: bool) {
        self.verifier = Some(Verifier::new(aes_key, base_iv));
        self.verify_enforces = enforce;
        self.verify_dropped = 0;
    }

    /// The sealer, for congestion and input packets, which must spend the same key-position counter.
    pub fn sealer_mut(&mut self) -> Option<&mut Sealer> {
        self.sealer.as_mut()
    }

    fn seal_and_queue(&mut self, mut packet: Vec<u8>) -> Vec<u8> {
        if let Some(s) = self.sealer.as_mut() {
            s.seal_control(&mut packet);
        }
        self.outbox.push_back(packet.clone());
        packet
    }

    /// Sends `payload` reliably on `channel`, fragmenting at [`MAX_PAYLOAD_PER_CHUNK`]. Every fragment is
    /// queued at once, so a message is never half sent.
    pub fn send(&mut self, now_us: u64, channel: u16, payload: &[u8]) -> Result<(), SendError> {
        if self.state != State::Established {
            return Err(SendError::NotEstablished);
        }
        let pieces: Vec<&[u8]> =
            if payload.is_empty() { vec![&[][..]] } else { payload.chunks(MAX_PAYLOAD_PER_CHUNK).collect() };
        let last = pieces.len() - 1;
        for (i, piece) in pieces.into_iter().enumerate() {
            let tsn = self.next_send_tsn;
            let chunk = if i == 0 {
                chunks::build_data_first(tsn, channel, i == last, piece)
            } else {
                chunks::build_data_continuation(tsn, channel, i == last, piece)
            }
            .ok_or(SendError::TooLarge)?;
            let packet = self.seal_and_queue(Header::control(self.peer_tag).build(&chunk));
            self.unacked.push(Unacked {
                tsn,
                packet,
                first_sent_us: now_us,
                last_sent_us: now_us,
                retransmitted: false,
            });
            self.next_send_tsn = tsn.wrapping_add(1);
        }
        Ok(())
    }

    fn send_sack(&mut self, cumulative: u32) {
        let packet =
            Header::control(self.peer_tag).build(&chunks::build_sack(cumulative, chunks::INIT_A_RWND));
        self.seal_and_queue(packet);
    }

    /// Feeds one datagram. Returns a message when one completes. Non-control datagrams are ignored; use
    /// [`is_control`] to route before calling. Only the first chunk after the header is examined, as in
    /// both references.
    pub fn on_datagram(&mut self, now_us: u64, datagram: &[u8]) -> Option<Message> {
        if !is_control(datagram) {
            return None;
        }
        let (header, chunk) = Header::parse(datagram)?;
        match self.state {
            State::AwaitingInitAck => {
                // .NET's checks: the INIT_ACK must carry our tag, and at least the INIT_ACK's length.
                let ack = parse_init_ack(chunk).filter(|_| header.verification_tag == self.local_tag)?;
                self.peer_tag = ack.server_tag;
                self.next_send_tsn = self.local_tag;
                self.cookie = ack.cookie;
                self.state = State::AwaitingCookieAck;
                self.attempts = 0;
                self.send_handshake(now_us);
                None
            }
            State::AwaitingCookieAck => {
                if header.verification_tag != self.local_tag {
                    return None;
                }
                match chunks::chunk_type(chunk) {
                    Some(chunks::CHUNK_COOKIE_ACK) => {
                        self.state = State::Established;
                        None
                    }
                    // DATA is proof of establishment too. C keeps it and processes it; .NET discards it and
                    // waits for the retransmission. Nothing on the wire depends on the discard.
                    Some(chunks::CHUNK_DATA) => {
                        self.state = State::Established;
                        self.on_established(now_us, datagram, chunk)
                    }
                    _ => None,
                }
            }
            State::Established => {
                // C's checks, which .NET lacks: our association, then (if armed) the GMAC.
                if header.verification_tag != self.local_tag {
                    return None;
                }
                self.on_established(now_us, datagram, chunk)
            }
            State::Idle | State::Failed => None,
        }
    }

    fn on_established(&mut self, now_us: u64, datagram: &[u8], chunk: &[u8]) -> Option<Message> {
        if let Some(v) = self.verifier.as_mut()
            && !v.check(datagram)
        {
            self.verify_dropped += 1;
            if self.verify_enforces {
                return None;
            }
        }
        match chunks::chunk_type(chunk)? {
            chunks::CHUNK_SACK => {
                if let Some(sack) = parse_sack_at_least(chunk) {
                    self.on_sack(now_us, sack);
                }
                None
            }
            chunks::CHUNK_DATA => self.on_data(chunk),
            _ => None, // an unrecognised chunk on this association: ignored rather than guessed at
        }
    }

    fn on_sack(&mut self, now_us: u64, cumulative: u32) {
        let (mut rtt, mut samples) = (self.smoothed_rtt_us, self.rtt_samples);
        self.unacked.retain(|u| {
            if !tsn_le(u.tsn, cumulative) {
                return true;
            }
            if !u.retransmitted {
                let sample = now_us.saturating_sub(u.first_sent_us) as f64;
                rtt = if samples == 0 { sample } else { 0.875 * rtt + 0.125 * sample };
                samples += 1;
            }
            false
        });
        (self.smoothed_rtt_us, self.rtt_samples) = (rtt, samples);
    }

    fn on_data(&mut self, chunk: &[u8]) -> Option<Message> {
        // The channel is at the same offset in both fragment shapes, so it decides which shape to parse.
        let channel = u16::from_be_bytes([*chunk.get(8)?, *chunk.get(9)?]);
        let continuing = self.partial.contains_key(&channel);
        let data = if continuing {
            chunks::parse_data_continuation(chunk)
        } else {
            chunks::parse_data_first(chunk)
        }?;

        if self.have_received_any && data.tsn != self.expected_recv_tsn {
            // Out of order or a duplicate: re-acknowledge what we have, so the sender retransmits from there.
            self.send_sack(self.expected_recv_tsn.wrapping_sub(1));
            return None;
        }
        self.expected_recv_tsn = data.tsn.wrapping_add(1);
        self.have_received_any = true;
        self.send_sack(data.tsn);

        let buffer = self.partial.entry(channel).or_default();
        if buffer.len() + data.payload.len() > MAX_MESSAGE {
            self.partial.remove(&channel);
            return None;
        }
        buffer.extend_from_slice(data.payload);
        if !data.ending {
            return None;
        }
        let payload = self.partial.remove(&channel).unwrap_or_default();
        Some(Message { channel, payload })
    }
}

/// INIT_ACK with .NET's length rule: at least 52 bytes declared and present.
fn parse_init_ack(chunk: &[u8]) -> Option<chunks::InitAck> {
    let declared = usize::from(u16::from_be_bytes([*chunk.get(2)?, *chunk.get(3)?]));
    if chunk.first() != Some(&chunks::CHUNK_INIT_ACK) || declared < 52 || chunk.len() < declared {
        return None;
    }
    // Re-read the fixed part through the exact-length parser.
    let mut fixed = chunk[..52].to_vec();
    fixed[2..4].copy_from_slice(&52u16.to_be_bytes());
    chunks::parse_init_ack(&fixed)
}

/// A SACK's cumulative TSN with .NET's length rule: at least 16 bytes declared and present.
fn parse_sack_at_least(chunk: &[u8]) -> Option<u32> {
    let declared = usize::from(u16::from_be_bytes([*chunk.get(2)?, *chunk.get(3)?]));
    (chunk.first() == Some(&chunks::CHUNK_SACK) && declared >= 16 && chunk.len() >= declared)
        .then(|| u32::from_be_bytes(chunk[4..8].try_into().unwrap()))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::takion::negotiator::{self, Negotiator};

    const CONFIG: Config = Config { max_attempts: 3, attempt_timeout_us: 100_000 };

    /// A console's side of a Takion association, scripted: answers the handshake, SACKs DATA and hands
    /// back what it received.
    struct Peer {
        tag: u32,
        client_tag: u32,
        next_tsn: u32,
        received: Vec<Vec<u8>>,
        send_cookie_ack: bool,
        mid_message: bool,
    }

    impl Peer {
        fn new() -> Self {
            Self {
                tag: 0xb18c_cf00,
                client_tag: 0,
                next_tsn: 0x00b1_8ccf,
                received: Vec::new(),
                send_cookie_ack: true,
                mid_message: false,
            }
        }

        fn on(&mut self, d: &[u8]) -> Vec<Vec<u8>> {
            let (_, chunk) = Header::parse(d).unwrap();
            match chunk[0] {
                chunks::CHUNK_INIT => {
                    self.client_tag = chunks::parse_init(chunk).unwrap();
                    vec![Header::control(self.client_tag).build(&chunks::build_init_ack(
                        self.tag,
                        self.next_tsn,
                        &[7; 32],
                    ))]
                }
                chunks::CHUNK_COOKIE_ECHO if self.send_cookie_ack => {
                    vec![Header::control(self.client_tag).build(&chunks::build_cookie_ack())]
                }
                chunks::CHUNK_DATA => {
                    // First or continuation is the receiver's own state, never a guess from the bytes.
                    let data = if self.mid_message {
                        chunks::parse_data_continuation(chunk)
                    } else {
                        chunks::parse_data_first(chunk)
                    }
                    .unwrap();
                    self.mid_message = !data.ending;
                    self.received.push(data.payload.to_vec());
                    vec![
                        Header::control(self.client_tag)
                            .build(&chunks::build_sack(data.tsn, chunks::INIT_A_RWND)),
                    ]
                }
                _ => vec![],
            }
        }

        fn data(&mut self, channel: u16, payload: &[u8], ending: bool, first: bool) -> Vec<u8> {
            let tsn = self.next_tsn;
            self.next_tsn += 1;
            let chunk = if first {
                chunks::build_data_first(tsn, channel, ending, payload)
            } else {
                chunks::build_data_continuation(tsn, channel, ending, payload)
            };
            Header::control(self.client_tag).build(&chunk.unwrap())
        }
    }

    fn pump(c: &mut Connection, p: &mut Peer, now: u64) -> Vec<Message> {
        let mut messages = Vec::new();
        while let Some(d) = c.poll_transmit() {
            for reply in p.on(&d) {
                messages.extend(c.on_datagram(now, &reply));
            }
        }
        messages
    }

    fn established() -> (Connection, Peer) {
        let (mut c, mut p) = (Connection::new(0x4823, CONFIG), Peer::new());
        c.connect(0);
        pump(&mut c, &mut p, 0);
        assert_eq!(c.state(), State::Established);
        (c, p)
    }

    #[test]
    fn the_handshake_sends_the_captured_init_and_establishes() {
        let mut c = Connection::new(0x4823, CONFIG);
        c.connect(0);
        let init = c.poll_transmit().unwrap();
        assert_eq!(init[13..], chunks::build_init(0x4823)[..]);
        let mut p = Peer::new();
        for reply in p.on(&init) {
            c.on_datagram(0, &reply);
        }
        assert_eq!(c.state(), State::AwaitingCookieAck);
        pump(&mut c, &mut p, 0);
        assert_eq!(c.state(), State::Established);
    }

    #[test]
    fn an_init_ack_for_another_association_is_ignored() {
        let mut c = Connection::new(0x4823, CONFIG);
        c.connect(0);
        c.poll_transmit();
        let wrong = Header::control(0x9999).build(&chunks::build_init_ack(1, 1, &[0; 32]));
        c.on_datagram(0, &wrong);
        assert_eq!(c.state(), State::AwaitingInitAck, ".NET checks the INIT_ACK's tag");
    }

    #[test]
    fn handshake_retries_then_fails() {
        let mut c = Connection::new(1, CONFIG);
        c.connect(0);
        for t in [100_000, 200_000] {
            c.handle_timeout(t);
            assert!(c.poll_transmit().is_some());
        }
        c.poll_transmit();
        assert_eq!(c.state(), State::AwaitingInitAck);
        c.handle_timeout(300_000);
        assert_eq!(c.state(), State::Failed, "three sends, then give up");
    }

    #[test]
    fn data_instead_of_cookie_ack_establishes_and_is_delivered() {
        let (mut c, mut p) = (Connection::new(0x4823, CONFIG), Peer::new());
        p.send_cookie_ack = false;
        c.connect(0);
        pump(&mut c, &mut p, 0);
        assert_eq!(c.state(), State::AwaitingCookieAck);
        let m = c.on_datagram(0, &p.data(0, b"hello", true, true));
        assert_eq!(c.state(), State::Established);
        assert_eq!(m, Some(Message { channel: 0, payload: b"hello".to_vec() }));
    }

    #[test]
    fn large_messages_fragment_and_reassemble_per_channel() {
        let (mut c, mut p) = established();
        let big: Vec<u8> = (0..2_500u32).map(|i| i as u8).collect();
        c.send(0, 1, &big).unwrap();
        pump(&mut c, &mut p, 1_000);
        assert_eq!(p.received.concat(), big);
        assert_eq!(c.unacked_count(), 0, "every fragment was SACKed");
        let (rtt, samples) = c.round_trip();
        assert_eq!((rtt, samples), (1_000.0, 3));

        // Two channels interleaved, which C's single slot cannot reassemble.
        let a1 = p.data(0, b"ab", false, true);
        let b1 = p.data(9, b"XY", false, true);
        let a2 = p.data(0, b"cd", true, false);
        let b2 = p.data(9, b"Z", true, false);
        let got: Vec<Message> = [a1, b1, a2, b2].iter().filter_map(|d| c.on_datagram(0, d)).collect();
        assert_eq!(
            got,
            [
                Message { channel: 0, payload: b"abcd".to_vec() },
                Message { channel: 9, payload: b"XYZ".to_vec() }
            ]
        );
    }

    #[test]
    fn out_of_order_data_is_dropped_and_re_acknowledged() {
        let (mut c, mut p) = established();
        let first = p.data(0, b"1", true, true);
        let _lost = p.data(0, b"2", true, true);
        let third = p.data(0, b"3", true, true);
        assert!(c.on_datagram(0, &first).is_some());
        while c.poll_transmit().is_some() {}
        assert_eq!(c.on_datagram(0, &third), None);
        let sack = c.poll_transmit().unwrap();
        assert_eq!(
            chunks::parse_sack(&sack[13..]).unwrap().cumulative_tsn_ack,
            0x00b1_8ccf,
            "re-acks the last in-order TSN"
        );
    }

    #[test]
    fn unacknowledged_chunks_are_resent_verbatim_after_300ms_and_skip_rtt() {
        let (mut c, _) = established();
        c.send(0, 1, b"x").unwrap();
        let original = c.poll_transmit().unwrap();
        c.handle_timeout(299_999);
        assert!(c.poll_transmit().is_none());
        assert_eq!(c.next_timeout(), Some(300_000));
        c.handle_timeout(300_000);
        assert_eq!(c.poll_transmit(), Some(original));
        c.on_sack(310_000, c.next_send_tsn.wrapping_sub(1));
        assert_eq!(c.round_trip().1, 0, "Karn: a retransmitted chunk is no sample");
    }

    #[test]
    fn a_sack_clears_across_the_tsn_wrap() {
        let mut c = Connection::new(u32::MAX, CONFIG);
        c.state = State::Established;
        c.next_send_tsn = u32::MAX;
        c.send(0, 1, b"a").unwrap();
        c.send(0, 1, b"b").unwrap();
        assert_eq!(c.unacked.iter().map(|u| u.tsn).collect::<Vec<_>>(), [u32::MAX, 0]);
        c.on_sack(1, u32::MAX);
        assert_eq!(
            c.unacked.iter().map(|u| u.tsn).collect::<Vec<_>>(),
            [0],
            "0xFFFFFFFF cleared, 0 still pending"
        );
    }

    #[test]
    fn a_sealed_session_round_trip_with_key_agreement() {
        use crate::crypto::ecdh::{Curve, Ecdh, RustCryptoEcdh, public_key_signature};
        use crate::stream::key_schedule;
        use crate::takion::control;

        let (mut c, mut p) = established();
        let hk = [0x24; 16];
        let mut n = 1u8;
        let mut fill = |b: &mut [u8]| {
            b.iter_mut().for_each(|x| {
                n = n.wrapping_mul(13).wrapping_add(5);
                *x = n
            })
        };
        let (neg, request) =
            Negotiator::begin(17, &hk, negotiator::DEFAULT_SESSION_KEY, b"spec", &RustCryptoEcdh, &mut fill)
                .unwrap();
        c.send(0, chunks::CHANNEL_SESSION, &request).unwrap();
        pump(&mut c, &mut p, 0);

        // The console answers with its own key over the reliable channel.
        let request_bytes = p.received.last().unwrap().clone();
        let req = control::parse_session_request(&request_bytes).unwrap();
        let (cp, cpub) = RustCryptoEcdh.generate(Curve::P521, &mut fill).unwrap();
        let sig = public_key_signature(&hk, &cpub);
        let reply = control::SessionReply {
            server_version: 17,
            token: 1,
            encrypted_key_accepted: true,
            version_accepted: true,
            session_key: b"",
            server_version_string: None,
            ecdh_public_key: Some(&cpub),
            ecdh_signature: Some(&sig),
        }
        .build();
        let m = c.on_datagram(0, &p.data(chunks::CHANNEL_SERVER_REPLY, &reply, true, true)).unwrap();
        let keys = neg.accept_reply(&m.payload, &RustCryptoEcdh).unwrap();
        // The SACK for the reply went out before the keys existed, and rightly unsealed.
        while c.poll_transmit().is_some() {}

        let shared = RustCryptoEcdh.shared_secret(Curve::P521, &cp, req.ecdh_public_key.unwrap()).unwrap();
        let console_send =
            key_schedule::derive_direction(&shared, &hk, key_schedule::DIRECTION_SERVER_TO_CLIENT);
        c.enable_sealing(&keys.send.aes_key, &keys.send.base_iv);
        c.enable_verification(&keys.receive.aes_key, &keys.receive.base_iv, true);

        // Something the console seals with its send key verifies; something unsealed does not.
        let mut console_sealer = Sealer::new(&console_send.aes_key, &console_send.base_iv);
        let mut sealed = p.data(0, b"streaminfo", true, true);
        console_sealer.seal_control(&mut sealed);
        assert_eq!(c.on_datagram(0, &sealed).map(|m| m.payload), Some(b"streaminfo".to_vec()));
        assert_eq!(c.on_datagram(0, &p.data(0, b"forged", true, true)), None);
        assert_eq!(c.verify_dropped(), 1);

        // And the SACK we sent for it carries a tag the console can check with our send key.
        while let Some(out) = c.poll_transmit() {
            let mut console_verifier = Verifier::new(&keys.send.aes_key, &keys.send.base_iv);
            assert!(console_verifier.check(&out), "outgoing packets are sealed, SACKs included");
        }
    }
}
