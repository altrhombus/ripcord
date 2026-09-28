//! The controller-input writer against the C core's, on generated pad sequences.
//!
//! The STATE payload and both headers must match byte for byte. HISTORY differs by design in two respects,
//! both recorded in engine/README.md: the engine follows the dotnet client's writer, which repeats four earlier
//! events besides this poll's where C repeats four events in all, and orders a poll's simultaneous events
//! L3 and R3 before Options, Create and PS where C puts them after. So the test compares polls, not bytes:
//! newest first, each poll's events as a set, C's four-event window cut from the same sequence, and the two
//! agreeing on when a HISTORY packet is owed at all.

use ripcord_diff::*;
use ripcord_proto::input::{self, State, Writer};

/// Split a HISTORY payload into its events: `80 code level` for most codes, `80 code` for the ones whose
/// state is folded into the code.
fn events(payload: &[u8]) -> Vec<&[u8]> {
    let bias = input_pressed_bias();
    let folded = |code: u8| (0x8c..=0x91).contains(&code) || (0x8c..=0x91).contains(&code.wrapping_sub(bias));
    let mut out = Vec::new();
    let mut i = 0;
    while i < payload.len() {
        assert_eq!(payload[i], 0x80, "an event starts with 0x80");
        let two_byte = folded(payload[i + 1]);
        let length = if two_byte { 2 } else { 3 };
        out.push(&payload[i..i + length]);
        i += length;
    }
    out
}

/// The bias a press adds to a folded code, read off the engine rather than restated.
fn input_pressed_bias() -> u8 {
    let mut w = Writer::new();
    w.step(&State::default(), 0);
    let pressed = w.step(&State { buttons: input::OPTIONS, ..State::default() }, 1).history.unwrap();
    pressed[input::HEADER_LENGTH + 1] - 0x8c
}

fn random_state(rng: &mut Rng, previous: &State) -> State {
    // Mostly small changes, as a pad produces: a button or two flips, sometimes a stick moves, a trigger
    // travels. Now and then a burst of many, which is where the two writers' windows part.
    let mut s = *previous;
    let flips = if rng.below(10) == 0 { 1 + rng.below(8) } else { rng.below(3) };
    for _ in 0..flips {
        s.buttons ^= 1 << rng.below(17); // the 17 bits C knows; the touchpad bit is the engine's own
    }
    if rng.below(3) == 0 {
        let i = rng.below(4) as usize;
        let v = rng.next_u64() as i16;
        match i {
            0 => s.left_x = v,
            1 => s.left_y = v,
            2 => s.right_x = v,
            _ => s.right_y = v,
        }
    }
    if rng.below(4) == 0 {
        s.left_trigger = rng.bytes::<1>()[0];
    }
    if rng.below(4) == 0 {
        s.right_trigger = rng.bytes::<1>()[0];
    }
    s
}

/// C's window against the polls that made it, newest first. C takes a poll's events whole while four fit, and
/// four of a larger one, so each of its events must belong to the poll it falls in, and a poll it takes whole
/// must match exactly. The polls are the engine's fresh events, every one of them, since each writer keeps
/// its own four of a large poll for later packets and those need not be the same four.
fn same_polls(c: &[&[u8]], polls: &[Vec<Vec<u8>>]) {
    let mut k = 0;
    for poll in polls {
        if k == c.len() {
            break;
        }
        let take = poll.len().min(c.len() - k);
        let mut ours: Vec<&[u8]> = c[k..k + take].to_vec();
        ours.sort();
        if take == poll.len() {
            let mut whole: Vec<&[u8]> = poll.iter().map(Vec::as_slice).collect();
            whole.sort();
            assert_eq!(ours, whole, "a poll's events differ");
        } else {
            assert!(
                ours.iter().all(|x| poll.iter().any(|e| e.as_slice() == *x)),
                "C kept an event that poll did not produce"
            );
        }
        k += take;
    }
    assert_eq!(k, c.len(), "C's window reaches further back than the polls");
}

#[test]
fn state_payloads_and_headers_match() {
    let mut rng = Rng::new(0x5eed_0501);
    for _ in 0..2_000 {
        let s = random_state(&mut rng, &State::default());
        let sticks = [s.left_x, s.left_y, s.right_x, s.right_y];
        let mut w = Writer::new();
        let packet = w.step(&s, 0).state.expect("the first poll sends a STATE packet");
        assert_eq!(packet[..input::HEADER_LENGTH], c_input_header(input::TYPE_STATE, 0)[..], "state header");
        assert_eq!(
            packet[input::HEADER_LENGTH..],
            c_input_state(s.buttons, sticks, s.left_trigger, s.right_trigger)[..],
            "state payload for {s:?}"
        );
    }
    for seq in [0u16, 1, 0x7fff, 0xffff] {
        assert_eq!(
            c_input_header(input::TYPE_HISTORY, seq)[1..3],
            seq.to_be_bytes(),
            "history header sequence"
        );
    }
}

#[test]
fn history_is_c_s_four_events_and_the_same_decisions() {
    let mut rng = Rng::new(0x5eed_0502);
    let mut longer = 0;
    for _ in 0..200 {
        let mut rust = Writer::new();
        let mut c = CInput::new();
        let mut previous = State::default();
        let mut history_seq = 0u16;
        let mut polls: Vec<Vec<Vec<u8>>> = Vec::new(); // each poll's events, newest poll first
        let mut kept = 0usize;
        for poll in 0..200u64 {
            let s = if poll == 0 { State::default() } else { random_state(&mut rng, &previous) };
            let sticks = [s.left_x, s.left_y, s.right_x, s.right_y];
            let out = rust.step(&s, poll * 4_000);
            let c_payload = c.history(s.buttons, sticks, s.left_trigger, s.right_trigger);
            match out.history {
                None => assert!(c_payload.is_empty(), "C owed a HISTORY packet the engine did not, at {s:?}"),
                Some(packet) => {
                    assert_eq!(
                        packet[..input::HEADER_LENGTH],
                        c_input_header(input::TYPE_HISTORY, history_seq)[..]
                    );
                    history_seq = history_seq.wrapping_add(1);
                    let engine = events(&packet[input::HEADER_LENGTH..]);
                    let c_events = events(&c_payload);
                    // This poll's events, then the ones the engine kept from before (four at most).
                    let fresh = engine.len() - kept;
                    polls.insert(0, engine[..fresh].iter().map(|e| e.to_vec()).collect());
                    kept = engine.len().min(input::HISTORY_RESEND);
                    let total: usize = polls.iter().map(Vec::len).sum();
                    assert_eq!(c_events.len(), total.min(input::HISTORY_RESEND), "C's window is four events");
                    same_polls(&c_events, &polls);
                    if engine.len() > c_events.len() {
                        longer += 1;
                    }
                }
            }
            previous = s;
        }
    }
    assert!(longer > 0, "the generator never reached the case where the windows differ");
}
