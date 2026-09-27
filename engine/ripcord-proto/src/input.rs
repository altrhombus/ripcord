//! Controller input up the stream socket: the STATE and HISTORY packets. Ported from
//! `HalyardInputPacketWriter.cs`, which carries the provenance (spec 6.3, re-derived from cap48 by
//! correlating a scripted input sequence), and `libripcord/input/halyard_input.c` with the cadence from
//! `halyard_client_policy.c`. Where they differ this follows .NET, except where noted in `engine/README.md`.
//!
//! Both packets share a 12-byte header: type (6 state, 1 history), a per-type big-endian sequence, a
//! zero, then the key position and tag the sealer writes ([`crate::takion::sealer::Sealer::seal_input`]).
//!
//! HISTORY is cumulative, newest event first, and repeats the last [`HISTORY_RESEND`] events: the console
//! tracks button state from it, so one lost packet would otherwise lose a transition for good. Events
//! carry absolute state, so a repeat is harmless.

pub const HEADER_LENGTH: usize = 0x0c;
pub const STATE_PAYLOAD: usize = 0x1c;
pub const TYPE_HISTORY: u8 = 0x01;
pub const TYPE_STATE: u8 = 0x06;
/// How many earlier events every history packet repeats. Four is the .NET writer's.
pub const HISTORY_RESEND: usize = 4;
/// A still controller is re-reported this often: the console wants to keep hearing from it.
pub const STATE_KEEPALIVE_US: u64 = 200_000;

const STATE_LEAD_BYTE: u8 = 0xa0; // [0x00], every captured state packet
const STATE_TAIL_BYTE: u8 = 0xca; // [0x1b], 98% of them; meaning unknown, and not to be "corrected"
const SENSOR_REST: u16 = 0x7fff;
const RESTING_ORIENTATION: u32 = 0; // a packed quaternion cap48 could not settle; a fixed value is allowed
const PRESSED_CODE_BIAS: u8 = 0x20;
const L2_CODE: u8 = 0x86;
const R2_CODE: u8 = 0x87;

pub const CROSS: u32 = 1 << 0;
pub const CIRCLE: u32 = 1 << 1;
pub const SQUARE: u32 = 1 << 2;
pub const TRIANGLE: u32 = 1 << 3;
pub const DPAD_UP: u32 = 1 << 4;
pub const DPAD_DOWN: u32 = 1 << 5;
pub const DPAD_LEFT: u32 = 1 << 6;
pub const DPAD_RIGHT: u32 = 1 << 7;
pub const L1: u32 = 1 << 8;
pub const R1: u32 = 1 << 9;
/// With a zero trigger level, fully pressed: for a front end with digital shoulders.
pub const L2: u32 = 1 << 10;
pub const R2: u32 = 1 << 11;
pub const OPTIONS: u32 = 1 << 12;
pub const CREATE: u32 = 1 << 13;
pub const PS: u32 = 1 << 14;
pub const L3: u32 = 1 << 15;
pub const R3: u32 = 1 << 16;
/// .NET's touchpad click, code 0x91 `[X]`: our capture's scripted touchpad step produced no code, so the
/// value is provisional (.NET's ButtonMap says so, and spec §6.3 names the other candidate). The C core has
/// no bit for it.
pub const TOUCHPAD: u32 = 1 << 17;

/// (bit, code, press folded into the code). .NET's order, which decides the order of simultaneous
/// events. D-pad up and down were assigned by elimination in the derivation; the rest are timed.
const BUTTONS: [(u32, u8, bool); 16] = [
    (CROSS, 0x88, false),
    (CIRCLE, 0x89, false),
    (SQUARE, 0x8a, false),
    (TRIANGLE, 0x8b, false),
    (DPAD_UP, 0x80, false),
    (DPAD_DOWN, 0x81, false),
    (DPAD_LEFT, 0x82, false),
    (DPAD_RIGHT, 0x83, false),
    (L1, 0x84, false),
    (R1, 0x85, false),
    (L3, 0x8f, true),
    (R3, 0x90, true),
    (OPTIONS, 0x8c, true),
    (CREATE, 0x8d, true),
    (PS, 0x8e, true),
    (TOUCHPAD, 0x91, true), // [X], see TOUCHPAD
];

/// One controller snapshot, wire-facing. Sticks are s16 with left and up negative (cap48: full up drove
/// left-Y to -32767). Triggers are levels; the L2/R2 bits stand in for a zero level.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct State {
    pub buttons: u32,
    pub left_x: i16,
    pub left_y: i16,
    pub right_x: i16,
    pub right_y: i16,
    pub left_trigger: u8,
    pub right_trigger: u8,
    /// Gyro x, y, z then accelerometer x, y, z, as the wire's u16 with 0x7fff at rest. `None` sends the
    /// resting values, which the spec allows for a pad without a motion sensor.
    pub motion: Option<[u16; 6]>,
}

impl State {
    fn trigger_levels(&self) -> (u8, u8) {
        let level = |l: u8, bit: u32| {
            if l != 0 {
                l
            } else if self.buttons & bit != 0 {
                0xff
            } else {
                0
            }
        };
        (level(self.left_trigger, L2), level(self.right_trigger, R2))
    }

    fn sticks(&self) -> [i16; 4] {
        [self.left_x, self.left_y, self.right_x, self.right_y]
    }
}

/// The packets one poll owes, headers written and not sealed.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Packets {
    pub history: Option<Vec<u8>>,
    pub state: Option<Vec<u8>>,
}

#[derive(Default)]
pub struct Writer {
    state_seq: u16,
    history_seq: u16,
    previous: Option<State>,
    last_state_us: u64,
    history: Vec<Vec<u8>>,
}

fn header(kind: u8, sequence: u16, payload_length: usize) -> Vec<u8> {
    let mut p = vec![0u8; HEADER_LENGTH + payload_length];
    p[0] = kind;
    p[1..3].copy_from_slice(&sequence.to_be_bytes());
    p
}

impl Writer {
    pub fn new() -> Self {
        Self::default()
    }

    /// One poll: a HISTORY packet when a button or trigger level changed, and a STATE packet on the first
    /// poll, when a stick moved, or after [`STATE_KEEPALIVE_US`]. Call once per polled frame.
    pub fn step(&mut self, state: &State, now_us: u64) -> Packets {
        let mut out = Packets::default();
        if let Some(prev) = self.previous {
            let fresh = self.history.len();
            let changed = prev.buttons ^ state.buttons;
            for &(bit, code, in_code) in &BUTTONS {
                if changed & bit == 0 {
                    continue;
                }
                let pressed = state.buttons & bit != 0;
                self.history.insert(
                    0,
                    if in_code {
                        vec![0x80, if pressed { code + PRESSED_CODE_BIAS } else { code }]
                    } else {
                        vec![0x80, code, if pressed { 0xff } else { 0 }]
                    },
                );
            }
            let (l2, r2) = state.trigger_levels();
            let (was_l2, was_r2) = prev.trigger_levels();
            if l2 != was_l2 {
                self.history.insert(0, vec![0x80, L2_CODE, l2]);
            }
            if r2 != was_r2 {
                self.history.insert(0, vec![0x80, R2_CODE, r2]);
            }
            if self.history.len() != fresh {
                // Every event of this poll goes out, however many; only then is the list cut back.
                let payload: Vec<u8> = self.history.concat();
                let mut packet = header(TYPE_HISTORY, self.history_seq, payload.len());
                packet[HEADER_LENGTH..].copy_from_slice(&payload);
                self.history_seq = self.history_seq.wrapping_add(1);
                self.history.truncate(HISTORY_RESEND);
                out.history = Some(packet);
            }
        }
        let moved = self.previous.is_none_or(|p| p.sticks() != state.sticks());
        if moved || now_us.saturating_sub(self.last_state_us) >= STATE_KEEPALIVE_US {
            out.state = Some(self.state_packet(state));
            self.last_state_us = now_us;
        }
        self.previous = Some(*state);
        out
    }

    fn state_packet(&mut self, s: &State) -> Vec<u8> {
        let mut packet = header(TYPE_STATE, self.state_seq, STATE_PAYLOAD);
        self.state_seq = self.state_seq.wrapping_add(1);
        let p = &mut packet[HEADER_LENGTH..];
        p[0] = STATE_LEAD_BYTE;
        for (i, v) in s.motion.unwrap_or([SENSOR_REST; 6]).iter().enumerate() {
            p[1 + 2 * i..3 + 2 * i].copy_from_slice(&v.to_le_bytes());
        }
        p[0x0d..0x11].copy_from_slice(&RESTING_ORIENTATION.to_le_bytes());
        for (i, v) in s.sticks().iter().enumerate() {
            p[0x11 + 2 * i..0x13 + 2 * i].copy_from_slice(&v.to_be_bytes());
        }
        p[0x1b] = STATE_TAIL_BYTE;
        packet
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_first_poll_sends_state_only_and_a_still_pad_is_re_reported() {
        let mut w = Writer::new();
        let s = State { left_y: -32767, ..Default::default() };
        let p = w.step(&s, 0);
        assert!(p.history.is_none());
        let state = p.state.unwrap();
        assert_eq!(&state[..4], &[6, 0, 0, 0]);
        assert_eq!(state[12], 0xa0);
        assert_eq!(&state[12 + 1..12 + 3], &[0xff, 0x7f], "gyro at rest, little-endian");
        assert_eq!(&state[12 + 0x13..12 + 0x15], &(-32767i16).to_be_bytes());
        assert_eq!(state[12 + 0x1b], 0xca);
        assert_eq!(w.step(&s, 199_999), Packets::default());
        assert_eq!(w.step(&s, 200_000).state.unwrap()[2], 1, "the keep-alive takes the next sequence");
    }

    #[test]
    fn history_is_cumulative_newest_first_and_never_drops_a_simultaneous_event() {
        let mut w = Writer::new();
        w.step(&State::default(), 0);
        let p = w.step(&State { buttons: CROSS, ..Default::default() }, 1);
        assert_eq!(p.history.unwrap()[12..], [0x80, 0x88, 0xff]);
        assert!(p.state.is_none(), "a button alone owes no state packet");
        let p = w.step(&State { buttons: CROSS | OPTIONS, ..Default::default() }, 2);
        assert_eq!(p.history.unwrap()[12..], [0x80, 0xac, 0x80, 0x88, 0xff]);

        // Five buttons and a trigger at once: all six go out, then the list keeps four.
        let all = CIRCLE | SQUARE | TRIANGLE | DPAD_UP | DPAD_DOWN;
        let p = w.step(&State { buttons: all | CROSS | OPTIONS, right_trigger: 9, ..Default::default() }, 3);
        let h = p.history.unwrap();
        assert_eq!(h[1..3], [0, 2]);
        assert_eq!(h.len(), 12 + 6 * 3 + 2 + 3, "six new events, then the two before them");
        assert_eq!(h[12..15], [0x80, 0x87, 9], "the trigger last, so newest");
    }

    #[test]
    fn a_digital_shoulder_is_a_full_level() {
        let mut w = Writer::new();
        w.step(&State::default(), 0);
        let h = w.step(&State { buttons: L2, ..Default::default() }, 1).history.unwrap();
        assert_eq!(h[12..], [0x80, 0x86, 0xff]);
        let h = w.step(&State { buttons: L2, left_trigger: 0x40, ..Default::default() }, 2).history.unwrap();
        assert_eq!(h[12..15], [0x80, 0x86, 0x40], "the level wins over the bit");
    }
}
