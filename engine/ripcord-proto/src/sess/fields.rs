//! The encrypted /sess/ctrl header fields: their plaintexts and their counters. Ported from
//! `libripcord/session/halyard_sess_fields.c` (`HalyardSessCtrlFields.cs`).
//!
//! One counter per connection: RP-Auth 0, RP-Did 1, RP-OSType 2, RP-StartBitrate 3, and on PS5 only
//! RP-StreamingType 4. Anything encrypted later on the same connection, the login passcode first, continues
//! from there. Restarting it reuses an IV.

pub const COUNTER_AUTH: u64 = 0;
pub const COUNTER_DID: u64 = 1;
pub const COUNTER_OS_TYPE: u64 = 2;
pub const COUNTER_START_BITRATE: u64 = 3;
pub const COUNTER_STREAMING_TYPE: u64 = 4;
/// The console's own frames on the channel start at 1.
pub const COUNTER_CONSOLE_START: u64 = 1;

/// Where the login passcode lands: after the headers actually sent. PS5 sends five (0 to 4), so 5; PS4
/// sends four, so 4. [C] from a hook on our own vendor client's field cipher.
pub fn login_pin_counter(is_ps5: bool) -> u64 {
    if is_ps5 { 5 } else { COUNTER_STREAMING_TYPE }
}

/// RP-Auth: the registration key, zero-padded or truncated to 16 bytes.
pub fn auth_plaintext(registration_key: &[u8]) -> [u8; 16] {
    let mut out = [0u8; 16];
    let n = registration_key.len().min(16);
    out[..n].copy_from_slice(&registration_key[..n]);
    out
}

const DID_PREFIX: [u8; 10] = [0x00, 0x18, 0x00, 0x00, 0x00, 0x07, 0x00, 0x40, 0x00, 0x80];

/// RP-Did: a fixed 10-byte prefix, up to 16 bytes of device id, zero padding to 32.
pub fn did_plaintext(device_id: &[u8]) -> [u8; 32] {
    let mut out = [0u8; 32];
    out[..10].copy_from_slice(&DID_PREFIX);
    let n = device_id.len().min(16);
    out[10..10 + n].copy_from_slice(&device_id[..n]);
    out
}

/// RP-OSType: `Win<major>.<minor>` and its terminating NUL, which is part of the field.
pub fn os_type_plaintext(major: i32, minor: i32) -> Vec<u8> {
    let mut v = format!("Win{major}.{minor}").into_bytes();
    v.push(0);
    v
}

/// RP-StartBitrate and RP-StreamingType: a little-endian i32.
pub fn int32le_plaintext(value: i32) -> [u8; 4] {
    value.to_le_bytes()
}
