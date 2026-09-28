//! The 9303 association (`HalyardControlAssociation`): the prelude handshake, then chunk-layer
//! connections carrying acknowledged data. Ported from `libripcord/session/halyard_dgram_assoc.c`, and
//! checked against .NET's own transcripts in `dgram-transport.kat`.
//!
//! Sans-IO, as the .NET type already is: every call queues the datagrams to send and the events it saw,
//! drained with [`Association::poll_transmit`] and [`Association::poll_event`]. Tags, tokens and sequences
//! come from the random source the host supplies (its CSPRNG; a counting source in the transcripts).

use std::collections::VecDeque;

use super::wire::{self, Prelude};

const CAPABILITY_BLOCK: [u8; 6] = [0x0B, 0x01, 0x01, 0x00, 0x01, 0x00];
const WINDOW_OR_MTU: u16 = 0x0582;
const DEFAULT_FLAGS: u8 = 0x30;
const COOKIE_HEADER_LENGTH: usize = 8;
const SEQUENCE_LENGTH: usize = 2;
const RETRANSMIT_HEADER_LENGTH: usize = 6;
const HELLO_BODY_LENGTH: usize = 14;
const HELLO_TAG_OFFSET: usize = 8;
const PEER_COOKIE_PREFIX: [u8; 18] = [
    0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x81, 0x20, 0x00, 0x96, 0x84, 0xD0, 0x00, 0x00, 0x00,
    0x00,
];
const PEER_COOKIE_RANDOM_LENGTH: usize = 24;

/// [X] The opener's request word. Not constant on the wire (real clients send a small growing counter,
/// and 0x40 is in no capture); it is 0x40 because .NET sends 0x40, and the two must change together.
pub const INITIATOR_REQUEST_WORD: u32 = 0x40;

/// How much received payload is held before the host drains it. .NET's is unbounded; past this a payload
/// is reported unhandled and not acknowledged, so the peer retransmits once room is made.
pub const INBOUND_MAX: usize = 16384;

/// The largest payload one send carries: one data chunk at the paired word count, minus its sequence.
pub const MAX_PAYLOAD: usize = wire::MAX_CHUNK_LENGTH - 2 * wire::PAIRED_WORD_COUNT - 2 - SEQUENCE_LENGTH;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Phase {
    Idle,
    Handshaking,
    Established,
    Connected,
    Closed,
}

/// How a chunk we originate addresses the peer: its header word count. PORT_PAIR is what every capture
/// carries; PEER_ONLY is an [X] experiment kept because .NET keeps it.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Addressing {
    PeerOnly = 1,
    SinglePort = 2,
    PortPair = 3,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Event {
    PreludeEstablished,
    ConnectionOpened {
        by_peer: bool,
    },
    /// Payload arrived: `data` is everything accumulated since the last clear, as .NET's event reports
    /// it, and `delta` the part this chunk added.
    DataReceived {
        data: Vec<u8>,
        delta: Vec<u8>,
    },
    PeerClosed,
    /// Reported, never silently dropped.
    Unhandled {
        reason: &'static str,
        datagram: Vec<u8>,
        chunk_type: u8,
    },
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SendError {
    NotConnected,
    TooLong,
}

pub struct Association {
    random: crate::RandomSource,
    local_id: [u8; 20],
    peer_id: [u8; 20],
    peer_address: [u8; 4],
    peer_port: u16,
    phase: Phase,
    tag_pair: u32,
    our_token: u32,
    we_sent_echo: bool,
    peer_echoed: bool,
    hello: Option<[u8; HELLO_BODY_LENGTH]>,
    hello_addressing: Addressing,
    sequence: u16,
    peer_sequence: u16,
    delivered: bool,
    inbound: Vec<u8>,
    outbox: VecDeque<Vec<u8>>,
    events: VecDeque<Event>,
}

fn be16(b: &[u8]) -> u16 {
    u16::from_be_bytes([b[0], b[1]])
}

impl Association {
    /// `peer_address` and `peer_port` are where we send, and what a CookieEcho reflects back.
    pub fn new(
        random: crate::RandomSource,
        local_id: [u8; 20],
        peer_id: [u8; 20],
        peer_address: [u8; 4],
        peer_port: u16,
    ) -> Self {
        Self {
            random,
            local_id,
            peer_id,
            peer_address,
            peer_port,
            phase: Phase::Idle,
            tag_pair: 0,
            our_token: 0,
            we_sent_echo: false,
            peer_echoed: false,
            hello: None,
            hello_addressing: Addressing::PortPair,
            sequence: 0,
            peer_sequence: 0,
            delivered: false,
            inbound: Vec::new(),
            outbox: VecDeque::new(),
            events: VecDeque::new(),
        }
    }

    pub fn phase(&self) -> Phase {
        self.phase
    }

    pub fn poll_transmit(&mut self) -> Option<Vec<u8>> {
        self.outbox.pop_front()
    }

    pub fn poll_event(&mut self) -> Option<Event> {
        self.events.pop_front()
    }

    /// Everything received since the last clear.
    pub fn inbound(&self) -> &[u8] {
        &self.inbound
    }

    pub fn clear_inbound(&mut self) {
        self.inbound.clear();
    }

    /// Drops the first `count` bytes, for a byte-pipe reader.
    pub fn consume_inbound(&mut self, count: usize) {
        self.inbound.drain(..count.min(self.inbound.len()));
    }

    fn draw<const N: usize>(&mut self) -> [u8; N] {
        let mut b = [0u8; N];
        (self.random)(&mut b);
        b
    }

    fn send_prelude(&mut self, kind: u32, request_word: u32, token: u32, tail: [u8; 8]) {
        let p = Prelude {
            kind,
            sender_id: self.local_id,
            peer_id: self.peer_id,
            tag_pair: self.tag_pair,
            request_word,
            token,
            tail,
        };
        self.outbox.push_back(p.write().to_vec());
    }

    fn send_chunk(&mut self, kind: u8, flags: u8, body: &[u8], words: usize) -> bool {
        match wire::write_chunk(kind, flags, body, words) {
            Some(c) => {
                self.outbox.push_back(c);
                true
            }
            None => false,
        }
    }

    fn unhandled(&mut self, reason: &'static str, datagram: &[u8], chunk_type: u8) {
        self.events.push_back(Event::Unhandled { reason, datagram: datagram.to_vec(), chunk_type });
    }

    fn pair(sequence: u16, ack: u16) -> [u8; 4] {
        let mut p = [0u8; 4];
        p[..2].copy_from_slice(&sequence.to_be_bytes());
        p[2..].copy_from_slice(&ack.to_be_bytes());
        p
    }

    /// Opens the prelude ourselves. A no-op once anything has happened.
    pub fn open(&mut self) {
        if self.phase != Phase::Idle {
            return;
        }
        let half: [u8; 2] = self.draw();
        let token: [u8; 4] = self.draw();
        self.tag_pair = 0x0001_0000 | u32::from(u16::from_be_bytes(half));
        self.our_token = u32::from_be_bytes(token);
        self.phase = Phase::Handshaking;
        self.send_prelude(wire::PRELUDE_INIT, INITIATOR_REQUEST_WORD, self.our_token, [0; 8]);
    }

    /// Re-sends our Init while the handshake waits and we have not yet echoed.
    pub fn retry(&mut self) {
        if self.phase == Phase::Handshaking && !self.we_sent_echo {
            self.send_prelude(wire::PRELUDE_INIT, INITIATOR_REQUEST_WORD, self.our_token, [0; 8]);
        }
    }

    fn settle_if_established(&mut self) {
        if matches!(self.phase, Phase::Established | Phase::Connected)
            || !self.we_sent_echo
            || !self.peer_echoed
        {
            return;
        }
        self.phase = Phase::Established;
        self.events.push_back(Event::PreludeEstablished);
    }

    fn on_prelude(&mut self, peer: &Prelude) {
        if peer.kind == wire::PRELUDE_COOKIE_ECHO {
            self.peer_echoed = true;
            self.settle_if_established();
            return;
        }
        if self.phase == Phase::Idle || peer.tag_pair != wire::swap_halves(self.tag_pair) {
            if self.our_token == 0 {
                self.our_token = u32::from_be_bytes(self.draw());
            }
            self.tag_pair = wire::swap_halves(peer.tag_pair);
            self.phase = Phase::Handshaking;
            self.send_prelude(wire::PRELUDE_INIT, 0, self.our_token, [0; 8]);
        }
        let tail = wire::reflect_peer_endpoint(self.peer_address, self.peer_port, self.tag_pair);
        self.send_prelude(wire::PRELUDE_COOKIE_ECHO, 0, peer.token, tail);
        self.we_sent_echo = true;
        self.settle_if_established();
    }

    /// Opens a chunk-layer connection: from Established, or from Connected or Closed, since the console
    /// closes each connection once it has answered. A no-op before the prelude is established.
    pub fn open_connection(&mut self, addressing: Addressing) {
        if !matches!(self.phase, Phase::Established | Phase::Connected | Phase::Closed) {
            return;
        }
        let seq: [u8; 2] = self.draw();
        let tag: [u8; 4] = self.draw();
        if self.phase != Phase::Established {
            self.phase = Phase::Established;
            self.peer_sequence = 0;
            self.delivered = false;
            self.hello = None;
            self.inbound.clear();
        }
        self.hello_addressing = addressing;
        self.sequence = u16::from_be_bytes(seq);
        let mut body = [0u8; HELLO_BODY_LENGTH];
        body[..2].copy_from_slice(&seq);
        body[2..8].copy_from_slice(&CAPABILITY_BLOCK);
        body[HELLO_TAG_OFFSET..HELLO_TAG_OFFSET + 4].copy_from_slice(&tag);
        body[12..].copy_from_slice(&WINDOW_OR_MTU.to_be_bytes());
        self.hello = Some(body);
        self.send_chunk(wire::CHUNK_HELLO, DEFAULT_FLAGS, &body, addressing as usize);
    }

    /// Re-sends the outstanding hello unchanged: a retransmission, not a second connection.
    pub fn reopen_connection(&mut self) {
        let Some(body) = self.hello else { return };
        if self.phase == Phase::Closed {
            self.phase = Phase::Established;
        }
        if self.phase == Phase::Established {
            self.send_chunk(wire::CHUNK_HELLO, DEFAULT_FLAGS, &body, self.hello_addressing as usize);
        }
    }

    /// Ends the open connection with a Close carrying our connection tag. On datagrams nothing says it
    /// implicitly, and a console never told refuses further cloud sessions until rebooted.
    pub fn close_connection(&mut self) {
        let Some(hello) = self.hello.filter(|_| self.phase == Phase::Connected) else { return };
        let mut body = [0u8; 8];
        body[4..].copy_from_slice(&hello[HELLO_TAG_OFFSET..HELLO_TAG_OFFSET + 4]);
        self.phase = Phase::Closed;
        self.send_chunk(wire::CHUNK_CLOSE, 0x00, &body, self.hello_addressing as usize);
    }

    /// Sends a payload on the open connection: an ack chunk then a data chunk, in one datagram.
    pub fn send(&mut self, payload: &[u8]) -> Result<(), SendError> {
        if self.phase != Phase::Connected {
            return Err(SendError::NotConnected);
        }
        if payload.len() > MAX_PAYLOAD {
            return Err(SendError::TooLong);
        }
        let mut datagram = wire::write_chunk(
            wire::CHUNK_ACK,
            DEFAULT_FLAGS,
            &Self::pair(self.sequence, self.peer_sequence.wrapping_add(1)),
            3,
        )
        .expect("an ack fits");
        let mut body = self.sequence.to_be_bytes().to_vec();
        body.extend_from_slice(payload);
        datagram.extend(
            wire::write_chunk(wire::CHUNK_DATA, DEFAULT_FLAGS, &body, 3).expect("bounded by MAX_PAYLOAD"),
        );
        self.sequence = self.sequence.wrapping_add(1);
        self.outbox.push_back(datagram);
        Ok(())
    }

    fn on_data(&mut self, chunk: &wire::Chunk<'_>) {
        let header = if chunk.kind == wire::CHUNK_DATA_RETRANSMIT {
            SEQUENCE_LENGTH + RETRANSMIT_HEADER_LENGTH
        } else {
            SEQUENCE_LENGTH
        };
        if chunk.body.len() <= header {
            return;
        }
        let sequence = be16(chunk.body);
        let payload = &chunk.body[header..];
        let words = usize::from(chunk.word_count);
        if self.delivered && (sequence.wrapping_sub(self.peer_sequence) as i16) <= 0 {
            // Already taken: acknowledge again what we have.
            self.send_chunk(
                wire::CHUNK_ACK,
                DEFAULT_FLAGS,
                &Self::pair(self.sequence, self.peer_sequence.wrapping_add(1)),
                words,
            );
            return;
        }
        if payload.len() > INBOUND_MAX - self.inbound.len() {
            self.unhandled(
                "received payload does not fit the inbound buffer; drain it",
                chunk.body,
                chunk.kind,
            );
            return;
        }
        self.peer_sequence = sequence;
        self.delivered = true;
        self.inbound.extend_from_slice(payload);
        self.send_chunk(
            wire::CHUNK_ACK,
            DEFAULT_FLAGS,
            &Self::pair(self.sequence, sequence.wrapping_add(1)),
            words,
        );
        self.events.push_back(Event::DataReceived { data: self.inbound.clone(), delta: payload.to_vec() });
    }

    fn on_chunk(&mut self, chunk: &wire::Chunk<'_>, datagram: &[u8]) {
        let words = usize::from(chunk.word_count);
        match chunk.kind {
            wire::CHUNK_COOKIE => {
                let Some(hello) = self.hello else {
                    return self.unhandled("a cookie arrived for a connection we never opened", datagram, 0);
                };
                if chunk.body.len() < COOKIE_HEADER_LENGTH {
                    return self.unhandled(
                        "a cookie too short to carry the 8-byte header the echo skips",
                        datagram,
                        0,
                    );
                }
                let mut body = hello.to_vec();
                body.extend_from_slice(&chunk.body[COOKIE_HEADER_LENGTH..]);
                if !self.send_chunk(
                    wire::CHUNK_HELLO_ECHO,
                    DEFAULT_FLAGS,
                    &body,
                    self.hello_addressing as usize,
                ) {
                    self.unhandled("a cookie too long to echo in one chunk", datagram, 0);
                }
            }
            wire::CHUNK_ACCEPT => {
                if chunk.body.len() < 4 {
                    return self.unhandled(
                        "an accept too short to carry a sequence and an acknowledgement",
                        datagram,
                        0,
                    );
                }
                self.peer_sequence = be16(chunk.body);
                self.delivered = false;
                self.sequence = be16(&chunk.body[2..]);
                self.phase = Phase::Connected;
                self.events.push_back(Event::ConnectionOpened { by_peer: false });
            }
            wire::CHUNK_HELLO => {
                let mut cookie = PEER_COOKIE_PREFIX.to_vec();
                let random: [u8; PEER_COOKIE_RANDOM_LENGTH] = self.draw();
                cookie.extend_from_slice(&random);
                self.peer_sequence = chunk.body.get(..2).map_or(0, be16);
                self.delivered = false;
                self.send_chunk(wire::CHUNK_COOKIE, 0x00, &cookie, words);
            }
            wire::CHUNK_HELLO_ECHO => {
                let seq: [u8; 2] = self.draw();
                let tag: [u8; 4] = self.draw();
                self.sequence = u16::from_be_bytes(seq);
                self.phase = Phase::Connected;
                let mut accept = Self::pair(self.sequence, self.peer_sequence.wrapping_add(1)).to_vec();
                accept.extend_from_slice(&CAPABILITY_BLOCK);
                accept.extend_from_slice(&tag);
                accept.extend_from_slice(&WINDOW_OR_MTU.to_be_bytes());
                self.send_chunk(wire::CHUNK_ACCEPT, DEFAULT_FLAGS, &accept, words);
                self.events.push_back(Event::ConnectionOpened { by_peer: true });
            }
            wire::CHUNK_DATA | wire::CHUNK_DATA_RETRANSMIT => self.on_data(chunk),
            wire::CHUNK_ACK | wire::CHUNK_ACK_EXTENDED => {}
            wire::CHUNK_CLOSE => {
                self.phase = Phase::Closed;
                self.events.push_back(Event::PeerClosed);
            }
            other => self.unhandled("unknown chunk type", datagram, other),
        }
    }

    /// Feeds one received datagram; everything the association does is a reaction to this.
    pub fn on_datagram(&mut self, data: &[u8]) {
        if let Some(p) = Prelude::parse(data) {
            return self.on_prelude(&p);
        }
        let mut any = false;
        for chunk in wire::chunks(data) {
            any = true;
            self.on_chunk(&chunk, data);
        }
        if !any {
            self.unhandled("neither a prelude nor a well-formed chunk", data, 0);
        }
    }
}
