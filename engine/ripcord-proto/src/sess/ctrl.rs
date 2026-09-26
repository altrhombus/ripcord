//! The binary control channel that follows /sess/ctrl on the same connection: 8-byte headers
//! (payload length u32, type u16, zero u16, big-endian) and a payload. Ported from
//! `libripcord/session/halyard_ctrl_message.c` (`HalyardCtrlMessage.cs`).

use crate::halyard::control::ControlField;

pub const HEADER_SIZE: usize = 8;

pub const LOGIN_PROMPT: u16 = 0x0004;
pub const LOGIN_SUBMIT: u16 = 0x8004;
pub const LOGIN: u16 = 0x0005;
pub const LOGIN_ACCEPTED: u8 = 0x00;
pub const LOGIN_REJECTED: u8 = 0x01;
pub const SESSION_ID: u16 = 0x0033;
pub const PROBE_REPORT: u16 = 0x000d;
pub const PROBE_REPORT_LENGTH: usize = 16;
pub const PROBE_REPORT_ACK: u16 = 0x0010;
pub const STREAM_READY: u16 = 0x0034;
pub const REST_MODE: u16 = 0x0050;
pub const REST_MODE_ACK: u16 = 0x8050;
pub const HEARTBEAT_REQ: u16 = 0x00fe;
pub const HEARTBEAT_REP: u16 = 0x01fe;
/// From .NET's list; the C core does not name these.
pub const ECHO_PROBE: u16 = 0x0910;
pub const ECHO_PROBE_ACK: u16 = 0x8910;
/// Seen from the console, meaning unknown ([X]).
pub const UNKNOWN_0016: u16 = 0x0016;
pub const UNKNOWN_0017: u16 = 0x0017;
pub const UNKNOWN_0041: u16 = 0x0041;

/// The longest passcode the login field takes.
pub const LOGIN_PIN_MAX: usize = 32;

pub fn build(kind: u16, payload: &[u8]) -> Vec<u8> {
    let mut out = Vec::with_capacity(HEADER_SIZE + payload.len());
    out.extend_from_slice(&(payload.len() as u32).to_be_bytes());
    out.extend_from_slice(&kind.to_be_bytes());
    out.extend_from_slice(&[0, 0]);
    out.extend_from_slice(payload);
    out
}

/// One frame from the front of the stream: (type, payload, bytes used). `None` until the whole frame has
/// arrived.
pub fn parse(data: &[u8]) -> Option<(u16, &[u8], usize)> {
    let h = data.get(..HEADER_SIZE)?;
    let length = u32::from_be_bytes([h[0], h[1], h[2], h[3]]) as usize;
    let end = HEADER_SIZE.checked_add(length)?;
    let payload = data.get(HEADER_SIZE..end)?;
    Some((u16::from_be_bytes([h[4], h[5]]), payload, end))
}

/// The login passcode's payload: its ASCII digits, field-encrypted at `counter` (the connection's next;
/// 5 on PS5, 4 on PS4, see `fields::login_pin_counter`). `None` for an empty, overlong or non-digit PIN.
pub fn login_submit_payload(field: &ControlField, counter: u64, pin: &str) -> Option<Vec<u8>> {
    if pin.is_empty() || pin.len() > LOGIN_PIN_MAX || !pin.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    let mut payload = pin.as_bytes().to_vec();
    field.encrypt(counter, &mut payload);
    Some(payload)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn frames_round_trip_and_wait_for_the_rest() {
        let f = build(HEARTBEAT_REP, &[1, 2, 3]);
        assert_eq!(&f[..8], &[0, 0, 0, 3, 0x01, 0xfe, 0, 0]);
        assert_eq!(parse(&f), Some((HEARTBEAT_REP, &[1u8, 2, 3][..], 11)));
        assert_eq!(parse(&f[..10]), None);
        assert_eq!(parse(&[0xff; 8]), None, "a length past the data");
    }
}
