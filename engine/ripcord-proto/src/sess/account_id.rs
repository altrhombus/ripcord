//! Making sense of a PSN account id somebody typed in. Ported from `libripcord/session/halyard_account_id.c`,
//! which has no .NET counterpart (the Windows client never takes one typed).
//!
//! The id is a 64-bit number written in more than one base by the tools people find it in, and getting
//! it wrong is silent: [`super::regist::encode_account_id`] sends an all-decimal id as a little-endian
//! u64 and anything else as its UTF-8, so a hex id typed into the box travels as ASCII and the console
//! refuses it without saying why. So the input is classified first: what reads as a 64-bit id comes back
//! as decimal, and what does not is refused with a reason.
//!
//! Base64 is recognised and deliberately refused: eight bytes have two orders and only the wire's is
//! confirmed, not the one tools print.

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Refusal {
    Empty,
    /// Recognised; its byte order is not ours to guess.
    Base64,
    Unreadable,
}

impl Refusal {
    /// In words a person in front of a television can act on.
    pub fn text(self) -> &'static str {
        match self {
            Refusal::Empty => "Nothing was entered",
            Refusal::Base64 => "That is the base64 form - enter the decimal or hex one instead",
            Refusal::Unreadable => "That is not an account id - it is 19 digits, or 16 hex characters",
        }
    }
}

fn nonzero(v: u64) -> Result<String, Refusal> {
    if v == 0 { Err(Refusal::Unreadable) } else { Ok(v.to_string()) }
}

/// The id as decimal. Accepted: decimal digits (which win a tie with sixteen hex characters); hex with a
/// `0x` prefix; and sixteen hex characters without one. [X] A bare hex id is read most significant digit
/// first, as hex means everywhere; a tool printing the bytes in wire order would produce the reverse.
/// Surrounding whitespace is ignored. Overflow and zero are refused, never wrapped.
pub fn normalise(input: &str) -> Result<String, Refusal> {
    let t = input.trim_matches([' ', '\t', '\r', '\n']);
    if t.is_empty() {
        return Err(Refusal::Empty);
    }
    if t.len() > 2 && (t.starts_with("0x") || t.starts_with("0X")) {
        let digits = &t[2..];
        if digits.len() > 16 || !digits.bytes().all(|b| b.is_ascii_hexdigit()) {
            return Err(Refusal::Unreadable);
        }
        return nonzero(u64::from_str_radix(digits, 16).map_err(|_| Refusal::Unreadable)?);
    }
    if t.bytes().all(|b| b.is_ascii_digit()) {
        if t.len() > 20 {
            return Err(Refusal::Unreadable);
        }
        return nonzero(t.parse::<u64>().map_err(|_| Refusal::Unreadable)?);
    }
    if t.len() == 16 && t.bytes().all(|b| b.is_ascii_hexdigit()) {
        return nonzero(u64::from_str_radix(t, 16).map_err(|_| Refusal::Unreadable)?);
    }
    let base64 = |b: u8| b.is_ascii_alphanumeric() || matches!(b, b'+' | b'/' | b'=');
    if (t.len() == 11 || t.len() == 12) && t.bytes().all(base64) {
        return Err(Refusal::Base64);
    }
    Err(Refusal::Unreadable)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_forms_people_paste() {
        assert_eq!(normalise(" 1234567890123456789\n").as_deref(), Ok("1234567890123456789"));
        assert_eq!(normalise("0x112210F47DE98115").as_deref(), Ok("1234567890123456789"));
        assert_eq!(normalise("112210f47de98115").as_deref(), Ok("1234567890123456789"));
        assert_eq!(normalise("1234567890123456").as_deref(), Ok("1234567890123456"), "decimal wins the tie");
        assert_eq!(normalise("FRGJ6fQQIhE="), Err(Refusal::Base64));
        assert_eq!(normalise("   "), Err(Refusal::Empty));
        assert_eq!(normalise("18446744073709551616"), Err(Refusal::Unreadable), "overflow is refused");
        assert_eq!(normalise("0"), Err(Refusal::Unreadable));
        assert_eq!(normalise("0x"), Err(Refusal::Unreadable));
        assert_eq!(normalise("psn-name"), Err(Refusal::Unreadable));
    }
}
