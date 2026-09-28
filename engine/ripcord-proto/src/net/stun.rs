//! STUN (RFC 5389) Binding, for the reflexive address an internet connect offers. Ported from
//! `libripcord/net/rc_stun*.c`, following `StunMessage.cs` and `StunClient.cs`.
//!
//! The codec reads XOR-MAPPED-ADDRESS (0x0020, and the pre-standard 0x8020 the vendor's own relay sends)
//! in preference to MAPPED-ADDRESS, on IPv4 and IPv6. The gatherer is sans-IO: it says where to send what,
//! and the host feeds it every datagram and the time.

use std::collections::VecDeque;

pub const MAGIC_COOKIE: u32 = 0x2112_A442;
pub const BINDING_REQUEST: u16 = 0x0001;
pub const BINDING_SUCCESS: u16 = 0x0101;
pub const BINDING_ERROR: u16 = 0x0111;
const HEADER_LENGTH: usize = 20;
const ATTR_MAPPED_ADDRESS: u16 = 0x0001;
const ATTR_XOR_MAPPED_ADDRESS: u16 = 0x0020;
const ATTR_XOR_MAPPED_ADDRESS_LEGACY: u16 = 0x8020;

/// .NET's defaults: three attempts per server, 500 ms each.
pub const DEFAULT_ATTEMPTS: u32 = 3;
pub const DEFAULT_TIMEOUT_US: u64 = 500_000;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum Address {
    V4([u8; 4], u16),
    V6([u8; 16], u16),
}

impl Address {
    pub fn port(&self) -> u16 {
        match *self {
            Address::V4(_, p) | Address::V6(_, p) => p,
        }
    }

    /// The mapping as a candidate can carry it. An IPv6 mapping is decoded but never offered, as the C
    /// leg discards it; .NET's production path (`StunReflexiveAddress`) never decodes one at all.
    pub fn ipv4(&self) -> Option<([u8; 4], u16)> {
        match *self {
            Address::V4(a, p) => Some((a, p)),
            Address::V6(..) => None,
        }
    }
}

/// A Binding Request with no attributes.
pub fn binding_request(transaction_id: &[u8; 12]) -> [u8; HEADER_LENGTH] {
    let mut out = [0u8; HEADER_LENGTH];
    out[..2].copy_from_slice(&BINDING_REQUEST.to_be_bytes());
    out[4..8].copy_from_slice(&MAGIC_COOKIE.to_be_bytes());
    out[8..].copy_from_slice(transaction_id);
    out
}

/// A parsed STUN message: its type, transaction id, and the reflexive address if it carries one.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Message {
    pub kind: u16,
    pub transaction_id: [u8; 12],
    pub mapped: Option<Address>,
}

fn read_address(value: &[u8], xor: bool, transaction_id: &[u8; 12]) -> Option<Address> {
    if value.len() < 4 {
        return None;
    }
    let length = match value[1] {
        0x01 => 4,
        0x02 => 16,
        _ => return None,
    };
    let bytes = value.get(4..4 + length)?;
    let mut port = u16::from_be_bytes([value[2], value[3]]);
    let mut mask = [0u8; 16];
    mask[..4].copy_from_slice(&MAGIC_COOKIE.to_be_bytes());
    mask[4..].copy_from_slice(transaction_id);
    let mut address = [0u8; 16];
    for i in 0..length {
        address[i] = if xor { bytes[i] ^ mask[i] } else { bytes[i] };
    }
    if xor {
        port ^= (MAGIC_COOKIE >> 16) as u16;
    }
    Some(if length == 4 {
        Address::V4(address[..4].try_into().unwrap(), port)
    } else {
        Address::V6(address, port)
    })
}

/// `None` unless it looks like STUN: the top two type bits clear, the magic cookie, and attributes that fit.
pub fn parse(data: &[u8]) -> Option<Message> {
    let h = data.get(..HEADER_LENGTH)?;
    let kind = u16::from_be_bytes([h[0], h[1]]);
    let length = usize::from(u16::from_be_bytes([h[2], h[3]]));
    if kind & 0xC000 != 0 || u32::from_be_bytes([h[4], h[5], h[6], h[7]]) != MAGIC_COOKIE {
        return None;
    }
    let attributes = data.get(HEADER_LENGTH..HEADER_LENGTH + length)?;
    let transaction_id: [u8; 12] = h[8..].try_into().unwrap();
    let (mut offset, mut plain) = (0usize, None);
    let mut mapped = None;
    while offset + 4 <= attributes.len() {
        let kind = u16::from_be_bytes([attributes[offset], attributes[offset + 1]]);
        let len = usize::from(u16::from_be_bytes([attributes[offset + 2], attributes[offset + 3]]));
        let Some(value) = attributes.get(offset + 4..offset + 4 + len) else { break };
        match kind {
            ATTR_XOR_MAPPED_ADDRESS | ATTR_XOR_MAPPED_ADDRESS_LEGACY => {
                if let Some(a) = read_address(value, true, &transaction_id) {
                    mapped = Some(a);
                    break;
                }
            }
            ATTR_MAPPED_ADDRESS if plain.is_none() => plain = read_address(value, false, &transaction_id),
            _ => {}
        }
        offset += 4 + len + ((4 - (len & 3)) & 3);
    }
    Some(Message { kind, transaction_id, mapped: mapped.or(plain) })
}

/// The result of a gather.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Outcome {
    /// Each answering server's index and the reflexive address it saw, in order.
    Answered(Vec<(usize, Address)>),
    /// Every attempt on every server ran out: a normal outcome, the connect proceeds without it.
    NoAnswer,
}

/// Asks servers in turn, `attempts` times each with `timeout_us` per attempt and a fresh transaction id
/// per attempt, until `wanted` servers have answered (1 for a reflexive address, 2 for the NAT-mapping
/// check) or the list runs out. A reply is matched on its transaction id, so a stale answer to an earlier
/// attempt, or anything else on the socket, is ignored.
pub struct Gatherer<A> {
    servers: Vec<A>,
    attempts: u32,
    timeout_us: u64,
    wanted: usize,
    random: crate::RandomSource,
    server: usize,
    attempt: u32,
    transaction_id: [u8; 12],
    deadline_us: u64,
    answers: Vec<(usize, Address)>,
    outbox: VecDeque<(A, Vec<u8>)>,
    outcome: Option<Outcome>,
}

impl<A: Clone> Gatherer<A> {
    pub fn new(
        servers: Vec<A>,
        attempts: u32,
        timeout_us: u64,
        wanted: usize,
        random: crate::RandomSource,
    ) -> Self {
        Self {
            servers,
            attempts: attempts.max(1),
            timeout_us,
            wanted: wanted.max(1),
            random,
            server: 0,
            attempt: 0,
            transaction_id: [0; 12],
            deadline_us: 0,
            answers: Vec::new(),
            outbox: VecDeque::new(),
            outcome: None,
        }
    }

    pub fn start(&mut self, now_us: u64) {
        self.send_attempt(now_us);
    }

    fn finish(&mut self) {
        let answers = std::mem::take(&mut self.answers);
        self.outcome = Some(if answers.is_empty() { Outcome::NoAnswer } else { Outcome::Answered(answers) });
    }

    fn send_attempt(&mut self, now_us: u64) {
        let Some(server) = self.servers.get(self.server).cloned() else { return self.finish() };
        (self.random)(&mut self.transaction_id);
        self.attempt += 1;
        self.deadline_us = now_us + self.timeout_us;
        self.outbox.push_back((server, binding_request(&self.transaction_id).to_vec()));
    }

    fn next_server(&mut self, now_us: u64) {
        self.server += 1;
        self.attempt = 0;
        self.send_attempt(now_us);
    }

    pub fn poll_transmit(&mut self) -> Option<(A, Vec<u8>)> {
        self.outbox.pop_front()
    }

    pub fn next_timeout(&self) -> Option<u64> {
        self.outcome.is_none().then_some(self.deadline_us)
    }

    pub fn handle_timeout(&mut self, now_us: u64) {
        if self.outcome.is_some() || now_us < self.deadline_us {
            return;
        }
        if self.attempt < self.attempts { self.send_attempt(now_us) } else { self.next_server(now_us) }
    }

    /// Any datagram that arrived on the gathering socket.
    pub fn on_datagram(&mut self, now_us: u64, data: &[u8]) {
        if self.outcome.is_some() {
            return;
        }
        let Some(m) = parse(data) else { return };
        let Some(address) =
            m.mapped.filter(|_| m.kind == BINDING_SUCCESS && m.transaction_id == self.transaction_id)
        else {
            return;
        };
        self.answers.push((self.server, address));
        if self.answers.len() >= self.wanted { self.finish() } else { self.next_server(now_us) }
    }

    pub fn outcome(&self) -> Option<&Outcome> {
        self.outcome.as_ref()
    }
}

/// Whether the NAT mapping is endpoint-independent: the same reflexive address seen by two servers.
/// `None` with fewer than two answers.
pub fn endpoint_independent(answers: &[(usize, Address)]) -> Option<bool> {
    match answers {
        [(_, a), (_, b), ..] => Some(a == b),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn success(txid: &[u8; 12], attrs: &[(u16, Vec<u8>)]) -> Vec<u8> {
        let mut body = Vec::new();
        for (kind, value) in attrs {
            body.extend(kind.to_be_bytes());
            body.extend((value.len() as u16).to_be_bytes());
            body.extend(value);
            body.resize(body.len().next_multiple_of(4), 0);
        }
        let mut m = BINDING_SUCCESS.to_be_bytes().to_vec();
        m.extend((body.len() as u16).to_be_bytes());
        m.extend(MAGIC_COOKIE.to_be_bytes());
        m.extend(txid);
        m.extend(body);
        m
    }

    fn xor_v4(a: [u8; 4], port: u16) -> Vec<u8> {
        let c = MAGIC_COOKIE.to_be_bytes();
        let mut v = vec![0, 1];
        v.extend((port ^ 0x2112).to_be_bytes());
        v.extend((0..4).map(|i| a[i] ^ c[i]));
        v
    }

    #[test]
    fn the_xor_form_wins_over_the_plain_one() {
        let txid = [7u8; 12];
        let mut plain = vec![0, 1];
        plain.extend(9999u16.to_be_bytes());
        plain.extend([10, 0, 0, 1]);
        let m = parse(&success(
            &txid,
            &[
                (ATTR_MAPPED_ADDRESS, plain.clone()),
                (ATTR_XOR_MAPPED_ADDRESS_LEGACY, xor_v4([203, 0, 113, 9], 61000)),
            ],
        ))
        .unwrap();
        assert_eq!(m.mapped, Some(Address::V4([203, 0, 113, 9], 61000)));
        let only_plain = parse(&success(&txid, &[(ATTR_MAPPED_ADDRESS, plain)])).unwrap();
        assert_eq!(only_plain.mapped, Some(Address::V4([10, 0, 0, 1], 9999)));
        assert_eq!(parse(&binding_request(&txid)).unwrap().kind, BINDING_REQUEST);
        assert_eq!(parse(&[0xC0; 20]), None);
    }

    #[test]
    fn the_gatherer_retries_moves_on_and_matches_transactions() {
        let mut n = 0u8;
        let random = Box::new(move |b: &mut [u8]| {
            b.iter_mut().for_each(|x| {
                n = n.wrapping_add(1);
                *x = n
            })
        });
        let mut g = Gatherer::new(vec!["a", "b"], 2, 100, 1, random);
        g.start(0);
        let (to, first) = g.poll_transmit().unwrap();
        assert_eq!(to, "a");
        g.handle_timeout(100);
        let (_, second) = g.poll_transmit().unwrap();
        assert_ne!(first[8..], second[8..], "a fresh transaction id per attempt");
        // A reply to the first attempt is stale now.
        let stale: [u8; 12] = first[8..].try_into().unwrap();
        g.on_datagram(150, &success(&stale, &[(ATTR_XOR_MAPPED_ADDRESS, xor_v4([1, 2, 3, 4], 5))]));
        assert!(g.outcome().is_none());
        g.handle_timeout(200);
        let (to, third) = g.poll_transmit().unwrap();
        assert_eq!(to, "b", "two attempts, then the next server");
        let txid: [u8; 12] = third[8..].try_into().unwrap();
        g.on_datagram(250, &success(&txid, &[(ATTR_XOR_MAPPED_ADDRESS, xor_v4([1, 2, 3, 4], 5))]));
        assert_eq!(g.outcome(), Some(&Outcome::Answered(vec![(1, Address::V4([1, 2, 3, 4], 5))])));
    }

    #[test]
    fn no_answer_is_an_outcome() {
        let mut g = Gatherer::new(vec![0u8], 1, 10, 1, Box::new(|b: &mut [u8]| b.fill(1)));
        g.start(0);
        g.handle_timeout(10);
        assert_eq!(g.outcome(), Some(&Outcome::NoAnswer));
        assert_eq!(
            endpoint_independent(&[(0, Address::V4([1; 4], 1)), (1, Address::V4([1; 4], 1))]),
            Some(true)
        );
        assert_eq!(endpoint_independent(&[(0, Address::V4([1; 4], 1))]), None);
    }
}
