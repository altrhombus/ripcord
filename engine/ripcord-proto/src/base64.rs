//! Base64 (RFC 4648, standard alphabet, padded), as strict as `libripcord/util/rc_base64.c`: the length is
//! a multiple of four, padding appears only in the last two places, and nothing outside the alphabet is
//! skipped, whitespace included. Strict on purpose: a malformed console value costs a retry, where a
//! lenient decoder could turn it into wrong key material.

const ALPHABET: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

pub fn encode(input: &[u8]) -> String {
    let mut out = String::with_capacity(input.len().div_ceil(3) * 4);
    for chunk in input.chunks(3) {
        let triple = (u32::from(chunk[0]) << 16)
            | (u32::from(*chunk.get(1).unwrap_or(&0)) << 8)
            | u32::from(*chunk.get(2).unwrap_or(&0));
        for i in 0..4 {
            if i <= chunk.len() {
                out.push(ALPHABET[(triple >> (18 - 6 * i) & 0x3f) as usize] as char);
            } else {
                out.push('=');
            }
        }
    }
    out
}

fn value(c: u8) -> Option<u32> {
    ALPHABET.iter().position(|&a| a == c).map(|p| p as u32)
}

/// `None` if the input is empty or malformed.
pub fn decode(input: &[u8]) -> Option<Vec<u8>> {
    let len = input.len();
    if len == 0 || !len.is_multiple_of(4) {
        return None;
    }
    let pad = usize::from(input[len - 1] == b'=') + usize::from(input[len - 2] == b'=');
    let mut out = Vec::with_capacity(len / 4 * 3);
    for (g, group) in input.chunks(4).enumerate() {
        let last = g == len / 4 - 1;
        let skip_third = last && pad >= 2;
        let skip_fourth = last && pad >= 1;
        let c0 = value(group[0])?;
        let c1 = value(group[1])?;
        let c2 = if skip_third { 0 } else { value(group[2])? };
        let c3 = if skip_fourth { 0 } else { value(group[3])? };
        let triple = (c0 << 18) | (c1 << 12) | (c2 << 6) | c3;
        out.push((triple >> 16) as u8);
        if !skip_third {
            out.push((triple >> 8) as u8);
        }
        if !skip_fourth {
            out.push(triple as u8);
        }
    }
    Some(out)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn round_trips_every_tail_length() {
        for n in 0..10u8 {
            let data: Vec<u8> = (0..n).collect();
            let text = encode(&data);
            if n > 0 {
                assert_eq!(decode(text.as_bytes()), Some(data));
            }
        }
        assert_eq!(encode(b"foobar"), "Zm9vYmFy");
        assert_eq!(encode(b"fo"), "Zm8=");
    }

    #[test]
    fn is_strict() {
        assert_eq!(decode(b""), None);
        assert_eq!(decode(b"Zm9"), None, "not a multiple of four");
        assert_eq!(decode(b"Zm 9"), None, "whitespace is not skipped");
        assert_eq!(decode(b"Z=9v"), None, "padding in the middle");
        assert_eq!(decode(b"Zm=v"), None, "padding third with a data character fourth");
    }
}
