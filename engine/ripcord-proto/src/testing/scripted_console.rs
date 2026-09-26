//! A console's side of the UDP 9303 association, scripted. Ported from
//! `libripcord/tests/fake_dgram_console.h`, behaviour for behaviour, so the two can be run side by side.
//!
//! It answers; it never originates, except through [`ScriptedConsole::push`] and
//! [`ScriptedConsole::close`], so a client that sends its steps out of order stalls rather than passing by
//! accident. /sess/init is answered with the configured reply and the connection closed; /sess/ctrl with
//! a 200 and the connection kept for the binary frames; every non-HTTP payload after that is recorded as
//! one control frame.
//!
//! Sans-IO like everything else here: [`ScriptedConsole::on_datagram`] returns what the console sends.

use crate::dgram::wire::{self, Prelude};

pub const MAX_REQUESTS: usize = 4;
pub const MAX_FRAMES: usize = 16;
const MAX_REQUEST_TEXT: usize = 1024;
const MAX_FRAME: usize = 300;

/// The console's 20-byte hashed id in the fixture: 101, 102, ...
pub fn console_id() -> [u8; 20] {
    std::array::from_fn(|i| 101 + i as u8)
}

/// The client's in the fixture: 1, 2, ...
pub fn client_id() -> [u8; 20] {
    std::array::from_fn(|i| 1 + i as u8)
}

#[derive(Clone, Debug)]
pub struct ScriptedConsole {
    sequence: u16,
    /// The whole HTTP reply to /sess/init. `None` answers with a plain 200.
    pub init_reply: Option<Vec<u8>>,
    /// The reply to anything else that is not /sess/ctrl, such as rgst. `None` answers 200.
    pub other_reply: Option<Vec<u8>>,
    /// For a POST that is not /sess/init: tear down instead of replying.
    pub close_before_answering: bool,
    /// Answer /sess/rgst as the account route's console would, under this seed: the field decrypted with
    /// the console's half of the derivation, and a pairing record for [`ACCOUNT_REGISTRATION_KEY`] back.
    pub account_seed: Option<[u8; 16]>,
    pub is_ps5: bool,
    pub rgst_requests: usize,
    /// Whether the last /sess/rgst field decrypted to what a client sends (`Client-Type: ...`).
    pub rgst_field_ok: bool,

    pub inits: usize,
    pub hellos: usize,
    pub closes_received: usize,
    /// How many HTTP requests arrived; the first [`MAX_REQUESTS`] are kept in `requests`.
    pub request_count: usize,
    pub requests: Vec<Vec<u8>>,
    /// Whether /sess/ctrl has been answered, after which payloads are frames.
    pub ctrl_open: bool,
    pub frames: Vec<Vec<u8>>,
}

impl Default for ScriptedConsole {
    fn default() -> Self {
        Self {
            sequence: 0xA65E,
            init_reply: None,
            other_reply: None,
            close_before_answering: false,
            account_seed: None,
            is_ps5: true,
            rgst_requests: 0,
            rgst_field_ok: false,
            inits: 0,
            hellos: 0,
            closes_received: 0,
            request_count: 0,
            requests: Vec::new(),
            ctrl_open: false,
            frames: Vec::new(),
        }
    }
}

/// The registration key the account-route console hands out: the ASCII hex a record carries, as the C
/// suites' console hands out.
pub const ACCOUNT_REGISTRATION_KEY: &[u8] = b"1a2b3c4d";

/// The account route's console side of /sess/rgst: the material unwrapped from the context, the field
/// derived under the seed, the client's field checked, and a pairing record sealed back. `None` for a
/// request too short to carry a context.
pub fn account_rgst_reply(is_ps5: bool, seed: &[u8; 16], request: &[u8]) -> Option<(Vec<u8>, bool)> {
    use crate::halyard::registration;
    let end = request.windows(4).position(|w| w == b"\r\n\r\n")? + 4;
    let body = &request[end..];
    let context = body.get(..registration::CONTEXT_LENGTH)?;
    let wrapped = registration::gather(context)?;
    let material = registration::unwrap_account_material(is_ps5, &wrapped, context)?;
    let field = registration::account_field(is_ps5, context, seed, &material)?;
    let mut plain = body[registration::CONTEXT_LENGTH..].to_vec();
    field.decrypt(registration::FIELD_COUNTER, &mut plain);
    let field_ok = plain.starts_with(b"Client-Type: ");
    let family = if is_ps5 { "PS5" } else { "PS4" };
    let hex = |b: &[u8]| b.iter().map(|x| format!("{x:02x}")).collect::<String>();
    let mut record = format!(
        "AP-Ssid: scripted\r\n{family}-RegistKey: {}\r\nRP-KeyType: 2\r\nRP-Key: {}\r\n",
        hex(ACCOUNT_REGISTRATION_KEY),
        hex(&[
            0xc0, 0xc1, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xcb, 0xcc, 0xcd, 0xce, 0xcf
        ])
    )
    .into_bytes();
    field.encrypt(registration::FIELD_COUNTER, &mut record);
    let mut reply = format!("HTTP/1.1 200 OK\r\nContent-Length: {}\r\n\r\n", record.len()).into_bytes();
    reply.extend(record);
    Some((reply, field_ok))
}

fn chunk(kind: u8, flags: u8, body: &[u8]) -> Vec<u8> {
    wire::write_chunk(kind, flags, body, wire::PAIRED_WORD_COUNT).expect("fixture chunks fit")
}

impl ScriptedConsole {
    pub fn new() -> Self {
        Self::default()
    }

    fn prelude(kind: u32, tag_pair: u32, token: u32) -> Vec<u8> {
        Prelude {
            kind,
            sender_id: console_id(),
            peer_id: client_id(),
            tag_pair,
            request_word: if kind == wire::PRELUDE_INIT { 0x19 } else { 0 },
            token,
            tail: [0; 8],
        }
        .write()
        .to_vec()
    }

    /// A console-originated payload on the open connection.
    pub fn push(&mut self, payload: &[u8]) -> Option<Vec<u8>> {
        if payload.len() + 2 > 1100 {
            return None;
        }
        self.sequence = self.sequence.wrapping_add(1);
        let mut body = self.sequence.to_be_bytes().to_vec();
        body.extend_from_slice(payload);
        Some(chunk(wire::CHUNK_DATA, 0x30, &body))
    }

    /// The console's teardown of the open connection.
    pub fn close() -> Vec<u8> {
        chunk(wire::CHUNK_CLOSE, 0x00, &[0; 8])
    }

    fn on_http(&mut self, text: &[u8], out: &mut Vec<Vec<u8>>) {
        const CTRL_REPLY: &[u8] = b"HTTP/1.1 200 OK\r\nRP-Version: 1.0\r\nContent-Length: 0\r\n\r\n";
        const PLAIN_REPLY: &[u8] = b"HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n";
        let contains = |needle: &[u8]| text.windows(needle.len()).any(|w| w == needle);
        let is_ctrl = contains(b"/sess/ctrl");
        let is_init = !is_ctrl && contains(b"/sess/init");

        if self.request_count < MAX_REQUESTS && text.len() < MAX_REQUEST_TEXT {
            self.requests.push(text.to_vec());
        }
        self.request_count += 1;

        if is_ctrl {
            out.extend(self.push(CTRL_REPLY));
            self.ctrl_open = true;
            return;
        }
        if !is_init && self.close_before_answering {
            out.push(Self::close());
            return;
        }
        if !is_init && contains(b"/sess/rgst") {
            self.rgst_requests += 1;
            if let Some(seed) = self.account_seed
                && let Some((reply, field_ok)) = account_rgst_reply(self.is_ps5, &seed, text)
            {
                self.rgst_field_ok = field_ok;
                out.extend(self.push(&reply));
                out.push(Self::close());
                return;
            }
        }
        let reply = match (is_init, &self.init_reply, &self.other_reply) {
            (true, Some(r), _) | (false, _, Some(r)) => r.clone(),
            _ => PLAIN_REPLY.to_vec(),
        };
        out.extend(self.push(&reply));
        // ...and tear the connection down, which is what makes the next request need a new one.
        out.push(Self::close());
    }

    /// Everything the console does is a reaction to a datagram from the client.
    pub fn on_datagram(&mut self, datagram: &[u8]) -> Vec<Vec<u8>> {
        let mut out = Vec::new();
        if let Some(p) = Prelude::parse(datagram) {
            if p.kind == wire::PRELUDE_INIT {
                self.inits += 1;
                // Answer, then echo: the shape a real console uses.
                out.push(Self::prelude(wire::PRELUDE_INIT, wire::swap_halves(p.tag_pair), 0x01B9_ACBE));
                out.push(Self::prelude(wire::PRELUDE_COOKIE_ECHO, p.tag_pair, p.token));
            }
            return out;
        }

        for c in wire::chunks(datagram) {
            match c.kind {
                wire::CHUNK_HELLO => {
                    self.hellos += 1;
                    let cookie: [u8; 42] = std::array::from_fn(|i| 0xE0u8.wrapping_add(i as u8)); // wraps past 0xFF, as the C fixture's uint8_t does
                    out.push(chunk(wire::CHUNK_COOKIE, 0x00, &cookie));
                }
                wire::CHUNK_HELLO_ECHO => {
                    // The C fixture reads two body bytes without checking; a short body is treated as
                    // zero here rather than read past.
                    let hello_seq = c.body.get(..2).map_or(0, |b| u16::from_be_bytes([b[0], b[1]]));
                    let mut accept = [0u8; 12];
                    accept[..2].copy_from_slice(&self.sequence.to_be_bytes());
                    accept[2..4].copy_from_slice(&hello_seq.wrapping_add(1).to_be_bytes());
                    self.ctrl_open = false;
                    out.push(chunk(wire::CHUNK_ACCEPT, 0x30, &accept));
                }
                wire::CHUNK_CLOSE => self.closes_received += 1,
                wire::CHUNK_DATA if c.body.len() > 2 => {
                    let payload = &c.body[2..];
                    let is_http = payload.starts_with(b"GET ") || payload.starts_with(b"POST ");
                    if is_http && !self.ctrl_open {
                        let text = &payload[..payload.len().min(1099)];
                        self.on_http(text, &mut out);
                    } else if self.frames.len() < MAX_FRAMES && payload.len() <= MAX_FRAME {
                        self.frames.push(payload.to_vec());
                    }
                }
                _ => {}
            }
        }
        out
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn data(seq: u16, payload: &[u8]) -> Vec<u8> {
        let mut body = seq.to_be_bytes().to_vec();
        body.extend_from_slice(payload);
        chunk(wire::CHUNK_DATA, 0x30, &body)
    }

    #[test]
    fn a_session_runs_init_then_ctrl_then_frames() {
        let mut console = ScriptedConsole {
            init_reply: Some(b"HTTP/1.1 200 OK\r\nRP-Nonce: abc\r\n\r\n".to_vec()),
            ..Default::default()
        };

        let opener =
            Prelude { kind: wire::PRELUDE_INIT, tag_pair: 0x0001_4321, token: 77, ..Default::default() };
        let answer = console.on_datagram(&opener.write());
        assert_eq!(answer.len(), 2);
        let (init, echo) = (Prelude::parse(&answer[0]).unwrap(), Prelude::parse(&answer[1]).unwrap());
        assert_eq!((init.kind, init.tag_pair, init.request_word), (wire::PRELUDE_INIT, 0x4321_0001, 0x19));
        assert_eq!((echo.kind, echo.tag_pair, echo.token), (wire::PRELUDE_COOKIE_ECHO, 0x0001_4321, 77));

        let cookie = console.on_datagram(&chunk(wire::CHUNK_HELLO, 0, &[0; 14]));
        assert_eq!(wire::read_chunk(&cookie[0]).unwrap().0.kind, wire::CHUNK_COOKIE);
        let accept = console.on_datagram(&chunk(wire::CHUNK_HELLO_ECHO, 0, &[0x12, 0x34]));
        assert_eq!(&wire::read_chunk(&accept[0]).unwrap().0.body[2..4], &[0x12, 0x35]);

        let reply = console.on_datagram(&data(1, b"GET /sie/ps5/rp/sess/init HTTP/1.1\r\n\r\n"));
        assert_eq!(reply.len(), 2, "the reply, then a close");
        assert!(wire::read_chunk(&reply[0]).unwrap().0.body.ends_with(b"RP-Nonce: abc\r\n\r\n"));
        assert_eq!(wire::read_chunk(&reply[1]).unwrap().0.kind, wire::CHUNK_CLOSE);

        let ctrl = console.on_datagram(&data(2, b"GET /sie/ps5/rp/sess/ctrl HTTP/1.1\r\n\r\n"));
        assert_eq!(ctrl.len(), 1, "ctrl keeps the connection");
        assert!(console.ctrl_open);
        assert!(console.on_datagram(&data(3, &[0xde, 0xad])).is_empty());
        assert_eq!(console.frames, [vec![0xde, 0xad]]);
        assert_eq!(console.request_count, 2);
    }

    #[test]
    fn close_before_answering_tears_down_a_post() {
        let mut console = ScriptedConsole { close_before_answering: true, ..Default::default() };
        let out = console.on_datagram(&data(1, b"POST /sie/ps5/rp/sess/rgst HTTP/1.1\r\n\r\n"));
        assert_eq!(out.len(), 1);
        assert_eq!(wire::read_chunk(&out[0]).unwrap().0.kind, wire::CHUNK_CLOSE);
    }
}
