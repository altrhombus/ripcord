//! The senkusha RTT and MTU echo packet. Ported from `libripcord/takion/senkusha_echo.c`, whose reference
//! is `SenkushaEchoProbe.cs` (captured pings returned byte-identically by the console, twice). Bytes 22..27
//! are a send timestamp the console never reads, so any monotonic value serves.

/// 548 payload, 576 as an IP datagram: RFC 791's minimum reassembly buffer.
pub const ECHO_PAYLOAD: usize = 548;
/// IPv4 20 + UDP 8: an mtuReq denotes the whole IP datagram, not the payload.
pub const IP_UDP_OVERHEAD: usize = 28;
pub const PING_COUNT: u8 = 10;

const BASE_TYPE: u8 = 0x03;
const SEQUENCE: usize = 5;
const MARKER1: usize = 6;
const MARKER2: usize = 9;
const TIMESTAMP: usize = 22;
const PADDING: usize = 27;
const MARKER: u8 = 0xFF;

/// One ping of `payload_length` bytes, or `None` below the timestamp's end. `padding` fills from offset
/// 27: zero for the RTT ping, 0x47 for the MTU test, so a compressing link cannot pass an MTU it lacks.
pub fn build(sequence: u8, microseconds: u64, payload_length: usize, padding: u8) -> Option<Vec<u8>> {
    if payload_length < TIMESTAMP + 5 {
        return None;
    }
    let mut buf = vec![0u8; payload_length];
    if padding != 0 && payload_length > PADDING {
        buf[PADDING..].fill(padding);
    }
    buf[0] = BASE_TYPE;
    buf[SEQUENCE] = sequence;
    buf[MARKER1] = MARKER;
    buf[MARKER2] = MARKER;
    // Five bytes: a 40-bit microsecond counter.
    buf[TIMESTAMP..TIMESTAMP + 5].copy_from_slice(&(microseconds & 0xFF_FFFF_FFFF).to_be_bytes()[3..]);
    Some(buf)
}

/// The sequence of an echo of one of our pings, matched on the markers rather than the whole packet.
pub fn echo_sequence(datagram: &[u8]) -> Option<u8> {
    (datagram.len() >= TIMESTAMP + 5
        && datagram[0] == BASE_TYPE
        && datagram[MARKER1] == MARKER
        && datagram[MARKER2] == MARKER)
        .then(|| datagram[SEQUENCE])
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_ping_is_recognised_as_its_own_echo() {
        let p = build(7, 0x12_3456_789A, ECHO_PAYLOAD, 0x47).unwrap();
        assert_eq!(&p[22..27], &[0x12, 0x34, 0x56, 0x78, 0x9A]);
        assert!(p[27..].iter().all(|&b| b == 0x47));
        assert_eq!(echo_sequence(&p), Some(7));
        assert_eq!(build(0, 0, 26, 0), None);
    }
}
