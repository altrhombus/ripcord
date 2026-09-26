//! Takion framing: the 13-byte message header and the SCTP-shaped chunks it carries (INIT, INIT_ACK,
//! COOKIE_ECHO, COOKIE_ACK, DATA, SACK). Ported from `libripcord/takion/takion_{message,handshake,
//! data_chunk,sack_chunk}.c`, whose references are `TakionMessageHeader.cs`, `TakionHandshake.cs`,
//! `TakionDataChunk.cs` and `TakionSackChunk.cs`. Takion is SCTP (RFC 4960) carried in UDP with this header
//! in front (spec sec 8); all integers are big-endian.

pub const HEADER_SIZE: usize = 13;
/// SCTP handshake and DATA/SACK all ride base type 0.
pub const BASE_TYPE_CONTROL: u8 = 0x00;

pub const CHUNK_DATA: u8 = 0x00;
pub const CHUNK_INIT: u8 = 0x01;
pub const CHUNK_INIT_ACK: u8 = 0x02;
pub const CHUNK_SACK: u8 = 0x03;
pub const CHUNK_COOKIE_ECHO: u8 = 0x0a;
pub const CHUNK_COOKIE_ACK: u8 = 0x0b;

/// Wire-confirmed constants, every capture.
pub const INIT_A_RWND: u32 = 0x0001_9000;
pub const INIT_STREAMS: u16 = 0x0064;
pub const COOKIE_SIZE: usize = 32;

pub const DATA_FLAG_ENDING: u8 = 0x01;
/// Per-class channel ids, wire-confirmed. Server replies ride channel 0.
pub const CHANNEL_SERVER_REPLY: u16 = 0x0000;
pub const CHANNEL_SESSION: u16 = 0x0001;
pub const CHANNEL_BANDWIDTH: u16 = 0x0008;
pub const CHANNEL_STREAM_INFO: u16 = 0x0009;
pub const CHANNEL_PROTOCOL_VERSION: u16 = 0x0015;

const INIT_SIZE: usize = 20;
const INIT_ACK_SIZE: usize = 4 + 16 + COOKIE_SIZE;
const COOKIE_ECHO_SIZE: usize = 4 + COOKIE_SIZE;
const COOKIE_ACK_SIZE: usize = 4;
const FIRST_PREFIX: usize = 9; // seq(4) channel(2) reserved(3)
const CONTINUATION_PREFIX: usize = 8; // seq(4) channel(2) reserved(2)
const SACK_SIZE: usize = 16;

fn be16(b: &[u8]) -> u16 {
    u16::from_be_bytes([b[0], b[1]])
}

fn be32(b: &[u8]) -> u32 {
    u32::from_be_bytes([b[0], b[1], b[2], b[3]])
}

/// The 13-byte header: base type, the PEER's verification tag (zero before one is negotiated), then the
/// GMAC tag and key position (zero until stream keys exist).
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Header {
    pub base_type: u8,
    pub verification_tag: u32,
    pub gmac_tag: u32,
    pub key_position: u32,
}

impl Header {
    pub fn control(verification_tag: u32) -> Self {
        Self { base_type: BASE_TYPE_CONTROL, verification_tag, ..Default::default() }
    }

    /// The header followed by `chunk`.
    pub fn build(&self, chunk: &[u8]) -> Vec<u8> {
        let mut out = Vec::with_capacity(HEADER_SIZE + chunk.len());
        out.push(self.base_type);
        out.extend_from_slice(&self.verification_tag.to_be_bytes());
        out.extend_from_slice(&self.gmac_tag.to_be_bytes());
        out.extend_from_slice(&self.key_position.to_be_bytes());
        out.extend_from_slice(chunk);
        out
    }

    /// The header and the chunk bytes after it, or `None` if shorter than [`HEADER_SIZE`].
    pub fn parse(data: &[u8]) -> Option<(Self, &[u8])> {
        let h = data.get(..HEADER_SIZE)?;
        Some((
            Self {
                base_type: h[0],
                verification_tag: be32(&h[1..]),
                gmac_tag: be32(&h[5..]),
                key_position: be32(&h[9..]),
            },
            &data[HEADER_SIZE..],
        ))
    }
}

/// The chunk type byte, or `None` for an empty chunk area.
pub fn chunk_type(chunk: &[u8]) -> Option<u8> {
    chunk.first().copied()
}

fn header_ok(data: &[u8], kind: u8, size: usize) -> bool {
    data.len() >= size && data[0] == kind && usize::from(be16(&data[2..])) == size
}

fn chunk_header(kind: u8, flags: u8, length: usize) -> Vec<u8> {
    let mut out = Vec::with_capacity(length);
    out.extend_from_slice(&[kind, flags]);
    out.extend_from_slice(&(length as u16).to_be_bytes());
    out
}

/// The 20-byte INIT. The initial TSN equals the initiate tag, as on the vendor wire.
pub fn build_init(initiate_tag: u32) -> Vec<u8> {
    let mut c = chunk_header(CHUNK_INIT, 0, INIT_SIZE);
    c.extend_from_slice(&initiate_tag.to_be_bytes());
    c.extend_from_slice(&INIT_A_RWND.to_be_bytes());
    c.extend_from_slice(&INIT_STREAMS.to_be_bytes());
    c.extend_from_slice(&INIT_STREAMS.to_be_bytes());
    c.extend_from_slice(&initiate_tag.to_be_bytes());
    c
}

pub fn parse_init(data: &[u8]) -> Option<u32> {
    header_ok(data, CHUNK_INIT, INIT_SIZE).then(|| be32(&data[4..]))
}

/// INIT_ACK: server tag, window, streams, initial TSN, 32-byte cookie. Built only by test peers.
pub fn build_init_ack(server_tag: u32, initial_tsn: u32, cookie: &[u8; COOKIE_SIZE]) -> Vec<u8> {
    let mut c = chunk_header(CHUNK_INIT_ACK, 0, INIT_ACK_SIZE);
    c.extend_from_slice(&server_tag.to_be_bytes());
    c.extend_from_slice(&INIT_A_RWND.to_be_bytes());
    c.extend_from_slice(&INIT_STREAMS.to_be_bytes());
    c.extend_from_slice(&INIT_STREAMS.to_be_bytes());
    c.extend_from_slice(&initial_tsn.to_be_bytes());
    c.extend_from_slice(cookie);
    c
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct InitAck {
    pub server_tag: u32,
    pub initial_tsn: u32,
    pub cookie: [u8; COOKIE_SIZE],
}

pub fn parse_init_ack(data: &[u8]) -> Option<InitAck> {
    header_ok(data, CHUNK_INIT_ACK, INIT_ACK_SIZE).then(|| InitAck {
        server_tag: be32(&data[4..]),
        initial_tsn: be32(&data[16..]),
        cookie: data[20..52].try_into().unwrap(),
    })
}

pub fn build_cookie_echo(cookie: &[u8; COOKIE_SIZE]) -> Vec<u8> {
    let mut c = chunk_header(CHUNK_COOKIE_ECHO, 0, COOKIE_ECHO_SIZE);
    c.extend_from_slice(cookie);
    c
}

pub fn parse_cookie_echo(data: &[u8]) -> Option<[u8; COOKIE_SIZE]> {
    header_ok(data, CHUNK_COOKIE_ECHO, COOKIE_ECHO_SIZE).then(|| data[4..36].try_into().unwrap())
}

pub fn build_cookie_ack() -> Vec<u8> {
    chunk_header(CHUNK_COOKIE_ACK, 0, COOKIE_ACK_SIZE)
}

pub fn is_cookie_ack(data: &[u8]) -> bool {
    header_ok(data, CHUNK_COOKIE_ACK, COOKIE_ACK_SIZE)
}

/// A parsed DATA chunk. `payload` borrows from the datagram.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Data<'a> {
    pub tsn: u32,
    pub channel: u16,
    pub ending: bool,
    pub payload: &'a [u8],
}

fn build_data(prefix: usize, tsn: u32, channel: u16, ending: bool, payload: &[u8]) -> Option<Vec<u8>> {
    let total = 4 + prefix + payload.len();
    if total > 0xffff {
        return None;
    }
    let mut c = chunk_header(CHUNK_DATA, if ending { DATA_FLAG_ENDING } else { 0 }, total);
    c.extend_from_slice(&tsn.to_be_bytes());
    // The channel is in continuations too, at the same offset; only the reserved region shrinks. The C
    // port once zero-filled it there, which put every continuation on channel 0, the console's own.
    c.extend_from_slice(&channel.to_be_bytes());
    c.resize(4 + prefix, 0);
    c.extend_from_slice(payload);
    Some(c)
}

/// A first (or only) fragment: payload at value offset 9. `None` past a 16-bit chunk length.
pub fn build_data_first(tsn: u32, channel: u16, ending: bool, payload: &[u8]) -> Option<Vec<u8>> {
    build_data(FIRST_PREFIX, tsn, channel, ending, payload)
}

/// A continuation fragment: payload at value offset 8. `channel` must be the first fragment's.
pub fn build_data_continuation(tsn: u32, channel: u16, ending: bool, payload: &[u8]) -> Option<Vec<u8>> {
    build_data(CONTINUATION_PREFIX, tsn, channel, ending, payload)
}

fn parse_data(data: &[u8], prefix: usize) -> Option<Data<'_>> {
    if data.len() < 4 + prefix || data[0] != CHUNK_DATA {
        return None;
    }
    let length = usize::from(be16(&data[2..]));
    if length < 4 + prefix || length > data.len() {
        return None;
    }
    Some(Data {
        tsn: be32(&data[4..]),
        channel: be16(&data[8..]),
        ending: data[1] & DATA_FLAG_ENDING != 0,
        payload: &data[4 + prefix..length],
    })
}

/// Which fragment is first is not a wire bit: the receiver decides from its reassembly state, hence two
/// parsers.
pub fn parse_data_first(data: &[u8]) -> Option<Data<'_>> {
    parse_data(data, FIRST_PREFIX)
}

pub fn parse_data_continuation(data: &[u8]) -> Option<Data<'_>> {
    parse_data(data, CONTINUATION_PREFIX)
}

/// A cumulative-only SACK (no gap or duplicate blocks), the only form ever built.
pub fn build_sack(cumulative_tsn_ack: u32, a_rwnd: u32) -> Vec<u8> {
    let mut c = chunk_header(CHUNK_SACK, 0, SACK_SIZE);
    c.extend_from_slice(&cumulative_tsn_ack.to_be_bytes());
    c.extend_from_slice(&a_rwnd.to_be_bytes());
    c.extend_from_slice(&[0; 4]);
    c
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Sack {
    pub cumulative_tsn_ack: u32,
    pub a_rwnd: u32,
    pub gap_ack_blocks: u16,
    pub duplicate_tsns: u16,
}

/// Any SACK shape; gap and duplicate blocks are counted, not interpreted. The declared length must match
/// the block counts exactly.
pub fn parse_sack(data: &[u8]) -> Option<Sack> {
    if data.len() < SACK_SIZE || data[0] != CHUNK_SACK {
        return None;
    }
    let (gaps, dups) = (be16(&data[12..]), be16(&data[14..]));
    let expected = SACK_SIZE + usize::from(gaps) * 4 + usize::from(dups) * 4;
    let length = usize::from(be16(&data[2..]));
    if length != expected || length > data.len() {
        return None;
    }
    Some(Sack {
        cumulative_tsn_ack: be32(&data[4..]),
        a_rwnd: be32(&data[8..]),
        gap_ack_blocks: gaps,
        duplicate_tsns: dups,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn hex(s: &str) -> Vec<u8> {
        (0..s.len()).step_by(2).map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap()).collect()
    }

    /// TakionTests.cs's captured INIT, the vector `libripcord/tests/takion_test.c` also checks.
    #[test]
    fn init_matches_the_captured_vector() {
        let packet = Header::control(0).build(&build_init(0x0000_4823));
        assert_eq!(packet, hex("000000000000000000000000000100001400004823000190000064006400004823"));
        assert_eq!(parse_init(&packet[HEADER_SIZE..]), Some(0x0000_4823));
    }

    #[test]
    fn handshake_chunks_round_trip() {
        let cookie: [u8; 32] = std::array::from_fn(|i| i as u8);
        let ack = build_init_ack(0xb18c_cf00, 0x00b1_8ccf, &cookie);
        assert_eq!(
            parse_init_ack(&ack),
            Some(InitAck { server_tag: 0xb18c_cf00, initial_tsn: 0x00b1_8ccf, cookie })
        );
        assert_eq!(parse_cookie_echo(&build_cookie_echo(&cookie)), Some(cookie));
        assert!(is_cookie_ack(&build_cookie_ack()));
        assert!(!is_cookie_ack(&ack), "an INIT_ACK is not a COOKIE_ACK");
    }

    /// TakionDataChunkTests.cs's three captured DATA packets.
    #[test]
    fn data_matches_captured_packets() {
        for (packet, tsn, channel, payload) in [
            (
                "0000b18ccf000000000000000000010014000048230015000000081ffa01020809",
                0x4823,
                CHANNEL_PROTOCOL_VERSION,
                "081ffa01020809",
            ),
            (
                "000000482300000000000000000001001400b18ccf000000000008208202020809",
                0x00b1_8ccf,
                CHANNEL_SERVER_REPLY,
                "08208202020809",
            ),
            (
                "0000b18ccf000000000000000000010017000048250008000000080c7206080012020801",
                0x4825,
                CHANNEL_BANDWIDTH,
                "080c7206080012020801",
            ),
        ] {
            let bytes = hex(packet);
            let (header, chunk) = Header::parse(&bytes).unwrap();
            assert_eq!(header.base_type, BASE_TYPE_CONTROL);
            let d = parse_data_first(chunk).unwrap();
            assert_eq!((d.tsn, d.channel, d.ending, d.payload), (tsn, channel, true, &hex(payload)[..]));
        }
        let rebuilt = Header::control(0x00b1_8ccf).build(
            &build_data_first(0x4823, CHANNEL_PROTOCOL_VERSION, true, &hex("081ffa01020809")).unwrap(),
        );
        assert_eq!(rebuilt, hex("0000b18ccf000000000000000000010014000048230015000000081ffa01020809"));
    }

    #[test]
    fn continuation_carries_its_channel() {
        let c =
            build_data_continuation(0x4826, CHANNEL_SESSION, true, &[0x11, 0x22, 0x33, 0x44, 0x55]).unwrap();
        assert_eq!(&c[8..10], &[0x00, 0x01], "channel at value offset 4, as in a first fragment");
        let d = parse_data_continuation(&c).unwrap();
        assert_eq!(
            (d.tsn, d.channel, d.payload),
            (0x4826, CHANNEL_SESSION, &[0x11, 0x22, 0x33, 0x44, 0x55][..])
        );
    }

    /// TakionReliabilityTests.cs's captured SACK build and parse vectors.
    #[test]
    fn sack_matches_captured_packets() {
        assert_eq!(
            Header::control(0x4823).build(&build_sack(0x4823, INIT_A_RWND)),
            hex("0000004823000000000000000003000010000048230001900000000000")
        );
        let bytes = hex("0000b18ccf00000000000000000300001000b18cd00001900000000000");
        let s = parse_sack(Header::parse(&bytes).unwrap().1).unwrap();
        assert_eq!(
            s,
            Sack {
                cumulative_tsn_ack: 0x00b1_8cd0,
                a_rwnd: INIT_A_RWND,
                gap_ack_blocks: 0,
                duplicate_tsns: 0
            }
        );
    }
}
