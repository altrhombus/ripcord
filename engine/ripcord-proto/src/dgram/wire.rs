//! The 9303 transport's two framings, the 88-byte prelude and the chunk layer, plus HTTP completeness.
//! Ported from `libripcord/session/halyard_dgram_wire.c` (`HalyardControlPrelude.cs`,
//! `HalyardControlChunk.cs`).

pub const PRELUDE_LENGTH: usize = 88;
pub const HASHED_ID_LENGTH: usize = 20;
pub const PRELUDE_TAIL_LENGTH: usize = 8;

/// Opens the exchange; sent by both sides.
pub const PRELUDE_INIT: u32 = 6;
/// The reply once a side holds the peer's token.
pub const PRELUDE_COOKIE_ECHO: u32 = 7;

const SENDER_ID_OFFSET: usize = 4;
const PEER_ID_OFFSET: usize = 36;
const TAG_PAIR_OFFSET: usize = 68;
const REQUEST_WORD_OFFSET: usize = 72;
const TOKEN_OFFSET: usize = 76;
const TAIL_OFFSET: usize = 80;

/// One prelude datagram. The type is the protocol's only little-endian field; every other one is
/// big-endian on the wire. `tag_pair`: the opener sends (1, X) and the answer carries the halves swapped,
/// [X] past that. `request_word`: nonzero only in the opener's Init, [X]. `token`: the sender's own in an
/// Init, the peer's echoed in a CookieEcho, [X] ours is a fixed random. `tail`: zero in an Init, the
/// reflected peer endpoint in a CookieEcho (see [`reflect_peer_endpoint`]).
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Prelude {
    pub kind: u32,
    pub sender_id: [u8; HASHED_ID_LENGTH],
    pub peer_id: [u8; HASHED_ID_LENGTH],
    pub tag_pair: u32,
    pub request_word: u32,
    pub token: u32,
    pub tail: [u8; PRELUDE_TAIL_LENGTH],
}

fn be32(b: &[u8]) -> u32 {
    u32::from_be_bytes([b[0], b[1], b[2], b[3]])
}

fn be16(b: &[u8]) -> u16 {
    u16::from_be_bytes([b[0], b[1]])
}

impl Prelude {
    pub fn write(&self) -> [u8; PRELUDE_LENGTH] {
        let mut out = [0u8; PRELUDE_LENGTH];
        // Little-endian: written big-endian it reads as 0x06000000 and the console sees an unknown type.
        out[..4].copy_from_slice(&self.kind.to_le_bytes());
        out[SENDER_ID_OFFSET..SENDER_ID_OFFSET + 20].copy_from_slice(&self.sender_id);
        out[PEER_ID_OFFSET..PEER_ID_OFFSET + 20].copy_from_slice(&self.peer_id);
        out[TAG_PAIR_OFFSET..TAG_PAIR_OFFSET + 4].copy_from_slice(&self.tag_pair.to_be_bytes());
        out[REQUEST_WORD_OFFSET..REQUEST_WORD_OFFSET + 4].copy_from_slice(&self.request_word.to_be_bytes());
        out[TOKEN_OFFSET..TOKEN_OFFSET + 4].copy_from_slice(&self.token.to_be_bytes());
        out[TAIL_OFFSET..].copy_from_slice(&self.tail);
        out
    }

    /// `Some` for an 88-byte datagram of type 6 or 7. A chunk can be 88 bytes too; a chunk always starts
    /// with nonzero high bits where this has a small little-endian type, which is how they are told apart.
    pub fn parse(data: &[u8]) -> Option<Self> {
        let data: &[u8; PRELUDE_LENGTH] = data.try_into().ok()?;
        let kind = u32::from_le_bytes([data[0], data[1], data[2], data[3]]);
        if kind != PRELUDE_INIT && kind != PRELUDE_COOKIE_ECHO {
            return None;
        }
        Some(Self {
            kind,
            sender_id: data[SENDER_ID_OFFSET..SENDER_ID_OFFSET + 20].try_into().unwrap(),
            peer_id: data[PEER_ID_OFFSET..PEER_ID_OFFSET + 20].try_into().unwrap(),
            tag_pair: be32(&data[TAG_PAIR_OFFSET..]),
            request_word: be32(&data[REQUEST_WORD_OFFSET..]),
            token: be32(&data[TOKEN_OFFSET..]),
            tail: data[TAIL_OFFSET..].try_into().unwrap(),
        })
    }
}

/// The tag pair with its halves exchanged: the form the peer answers with.
pub fn swap_halves(tag_pair: u32) -> u32 {
    tag_pair.rotate_left(16)
}

/// A CookieEcho's tail: the peer's IPv4 address XOR the tag pair, its port XOR the pair's high half, two
/// zero bytes. [C] against every echo in every capture the project holds.
pub fn reflect_peer_endpoint(
    peer_address: [u8; 4],
    peer_port: u16,
    tag_pair: u32,
) -> [u8; PRELUDE_TAIL_LENGTH] {
    let key = tag_pair.to_be_bytes();
    let mut tail = [0u8; PRELUDE_TAIL_LENGTH];
    for i in 0..4 {
        tail[i] = peer_address[i] ^ key[i];
    }
    tail[4..6].copy_from_slice(&(peer_port ^ (tag_pair >> 16) as u16).to_be_bytes());
    tail
}

// ---- the chunk layer ----

/// Observed chunk types. [X] These are combinations of a flags bitmap, not an enumeration.
pub const CHUNK_DATA: u8 = 0x02;
pub const CHUNK_DATA_RETRANSMIT: u8 = 0x12;
pub const CHUNK_ACK: u8 = 0x20;
pub const CHUNK_ACK_EXTENDED: u8 = 0x24;
pub const CHUNK_HELLO: u8 = 0x80;
pub const CHUNK_HELLO_ECHO: u8 = 0x90;
pub const CHUNK_ACCEPT: u8 = 0xA0;
pub const CHUNK_CLOSE: u8 = 0xC0;
pub const CHUNK_COOKIE: u8 = 0xD0;

/// The service port the port words carry: 9295, the control port. A required on-wire value.
pub const CONTROL_PORT: u16 = 0x244F;
/// The header plus a (source, destination) port pair: what every chunk in every capture carries.
pub const PAIRED_WORD_COUNT: usize = 3;
const TYPE_AND_FLAGS_LENGTH: usize = 2;
/// The length field is 11 bits, so a chunk is at most this long including its header.
pub const MAX_CHUNK_LENGTH: usize = 0x7FF;

/// One chunk as read. `body` borrows from the datagram. `word_count` (1 to 3) includes the header word;
/// the ports are the control port when the shape carries none, and both the one word for a count of 2.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Chunk<'a> {
    pub kind: u8,
    pub flags: u8,
    pub body: &'a [u8],
    pub source_port: u16,
    pub destination_port: u16,
    pub word_count: u8,
}

/// Bytes a chunk with `body_length` of body needs at `word_count`.
pub fn chunk_length(body_length: usize, word_count: usize) -> usize {
    word_count * 2 + TYPE_AND_FLAGS_LENGTH + body_length
}

/// One chunk, or `None` where .NET throws: a word count outside 1..=3 or a chunk past
/// [`MAX_CHUNK_LENGTH`]. Every port word written is the control port.
pub fn write_chunk(kind: u8, flags: u8, body: &[u8], word_count: usize) -> Option<Vec<u8>> {
    if !(1..=PAIRED_WORD_COUNT).contains(&word_count) || body.len() > MAX_CHUNK_LENGTH {
        return None;
    }
    let total = chunk_length(body.len(), word_count);
    if total > MAX_CHUNK_LENGTH {
        return None;
    }
    let mut out = Vec::with_capacity(total);
    out.extend_from_slice(&(((word_count as u16) << 14) | total as u16).to_be_bytes());
    for _ in 1..word_count {
        out.extend_from_slice(&CONTROL_PORT.to_be_bytes());
    }
    out.extend_from_slice(&[kind, flags]);
    out.extend_from_slice(body);
    Some(out)
}

/// The chunk at the start of `data` and its length, or `None` for anything the vendor's receive loop
/// abandons a datagram on: shorter than the header, a zero word count, a length that cannot hold its
/// prefix plus type and flags, or one that overruns the datagram. Bits 13..11 are masked, not checked.
pub fn read_chunk(data: &[u8]) -> Option<(Chunk<'_>, usize)> {
    let header = be16(data.get(..2)?);
    let words = usize::from(header >> 14);
    let total = usize::from(header & 0x07FF);
    if words == 0 || total > data.len() {
        return None;
    }
    let prefix = words * 2;
    if total < prefix + TYPE_AND_FLAGS_LENGTH {
        return None;
    }
    let (source_port, destination_port) = match words {
        3 => (be16(&data[2..]), be16(&data[4..])),
        2 => (be16(&data[2..]), be16(&data[2..])),
        _ => (CONTROL_PORT, CONTROL_PORT),
    };
    let chunk = Chunk {
        kind: data[prefix],
        flags: data[prefix + 1],
        body: &data[prefix + TYPE_AND_FLAGS_LENGTH..total],
        source_port,
        destination_port,
        word_count: words as u8,
    };
    Some((chunk, total))
}

/// Every chunk in a datagram, stopping at the end or at the first malformed chunk; whatever parsed
/// before it stands, as in the vendor's loop and in .NET.
pub fn chunks(data: &[u8]) -> impl Iterator<Item = Chunk<'_>> {
    let mut offset = 0;
    std::iter::from_fn(move || {
        let (chunk, used) = read_chunk(data.get(offset..).filter(|r| !r.is_empty())?)?;
        offset += used;
        Some(chunk)
    })
}

// ---- HTTP completeness ----

fn trim_space(c: u8) -> bool {
    c == b' ' || (0x09..=0x0D).contains(&c)
}

fn trim(s: &[u8]) -> &[u8] {
    let start = s.iter().position(|&c| !trim_space(c)).unwrap_or(s.len());
    let end = s.iter().rposition(|&c| !trim_space(c)).map_or(start, |e| e + 1);
    &s[start..end.max(start)]
}

/// `int.TryParse` with `NumberStyles.Integer` over trimmed text: an optional sign, then digits, within i32.
fn parse_int32(s: &[u8]) -> Option<i64> {
    let (negative, digits) = match s.first() {
        Some(b'+') => (false, &s[1..]),
        Some(b'-') => (true, &s[1..]),
        _ => (false, s),
    };
    if digits.is_empty() {
        return None;
    }
    let mut value: i64 = 0;
    for &d in digits {
        if !d.is_ascii_digit() {
            return None;
        }
        value = value * 10 + i64::from(d - b'0');
        if value > 2_147_483_648 {
            return None;
        }
    }
    if !negative && value > 2_147_483_647 {
        return None;
    }
    Some(if negative { -value } else { value })
}

/// Whether an HTTP message is complete, judged by Content-Length against what follows the blank line: a
/// datagram boundary says nothing about a message boundary. False until the header terminator arrives;
/// then true if no Content-Length parses as an integer, else whether the body has reached it. The first
/// parseable Content-Length wins, as in .NET.
pub fn http_complete(message: &[u8]) -> bool {
    let Some(header_end) = message.windows(4).position(|w| w == b"\r\n\r\n") else {
        return false;
    };
    let body_length = (message.len() - header_end - 4) as u64;
    let headers = &message[..header_end];
    let mut start = 0;
    while start <= header_end {
        let rest = &headers[start..];
        let line_len = rest.windows(2).position(|w| w == b"\r\n").unwrap_or(rest.len());
        let line = &rest[..line_len];
        if let Some(colon) = line.iter().position(|&c| c == b':').filter(|&c| c > 0) {
            let name = trim(&line[..colon]);
            if name.eq_ignore_ascii_case(b"Content-Length")
                && let Some(declared) = parse_int32(trim(&line[colon + 1..]))
            {
                return declared < 0 || body_length >= declared as u64;
            }
        }
        start += line_len + 2;
    }
    true
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn prelude_round_trips_and_is_told_from_a_chunk() {
        let p = Prelude {
            kind: PRELUDE_COOKIE_ECHO,
            sender_id: [7; 20],
            peer_id: [9; 20],
            tag_pair: 0x0001_1234,
            request_word: 0x19,
            token: 0xdead_beef,
            tail: reflect_peer_endpoint([192, 0, 2, 5], 9303, 0x0001_1234),
        };
        let wire = p.write();
        assert_eq!(&wire[..4], &[7, 0, 0, 0], "the type is little-endian");
        assert_eq!(Prelude::parse(&wire), Some(p));
        assert_eq!(Prelude::parse(&wire[..87]), None);
        let mut chunk_shaped = wire;
        chunk_shaped[0] = 0xC0;
        assert_eq!(Prelude::parse(&chunk_shaped), None);
        assert_eq!(swap_halves(0x0001_1234), 0x1234_0001);
    }

    #[test]
    fn chunks_round_trip_at_every_word_count() {
        for words in 1..=3 {
            let c = write_chunk(CHUNK_DATA, 0x30, b"hello", words).unwrap();
            let (chunk, used) = read_chunk(&c).unwrap();
            assert_eq!(used, c.len());
            assert_eq!(
                (chunk.kind, chunk.flags, chunk.body, chunk.word_count as usize),
                (CHUNK_DATA, 0x30, &b"hello"[..], words)
            );
        }
        assert_eq!(write_chunk(CHUNK_DATA, 0, b"", 0), None);
        assert_eq!(write_chunk(CHUNK_DATA, 0, &[0; 0x7FA], 3), None, "past the 11-bit length");
    }

    #[test]
    fn a_malformed_chunk_ends_the_datagram_but_keeps_what_came_before() {
        let mut d = write_chunk(CHUNK_ACK, 0, &[1, 2, 3, 4], 3).unwrap();
        d.extend(write_chunk(CHUNK_DATA, 0, b"x", 3).unwrap());
        d.extend([0x00, 0x09, 0xff]);
        let kinds: Vec<u8> = chunks(&d).map(|c| c.kind).collect();
        assert_eq!(kinds, [CHUNK_ACK, CHUNK_DATA]);
    }

    #[test]
    fn http_completeness() {
        assert!(!http_complete(b"HTTP/1.1 200 OK\r\nContent-Length: 4\r\n"));
        assert!(!http_complete(b"HTTP/1.1 200 OK\r\nContent-Length: 4\r\n\r\nab"));
        assert!(http_complete(b"HTTP/1.1 200 OK\r\ncontent-length :  4 \r\n\r\nabcd"));
        assert!(http_complete(b"HTTP/1.1 200 OK\r\nX: y\r\n\r\n"), "no Content-Length");
        assert!(!http_complete(b"HTTP/1.1 200 OK\r\nContent-Length: nope\r\nContent-Length: 3\r\n\r\n"));
    }
}
