//! The account-route registration seed, recovered from what the console publishes. Ported from
//! `libripcord/halyard/halyard_account_seed.c`; the provenance is in `HalyardAccountSeedDelivery.cs`.
//!
//! The client sends two ephemeral 16-byte values in the cloud `commands` call, data1 (the field-cipher
//! key) and data2 (the material). The console field-encrypts its seed under them, counter 0, and
//! publishes the ciphertext as customData1, double base64. The cloud traffic is the host's; everything
//! from the customData1 string to the seed is here. [C] against captured pairings and [V] live on a PS5;
//! the PS4 family is [X].

use super::control::ControlField;
use super::registration::{FIELD_COUNTER, seed_context_key};
use crate::base64;

pub const SEED_LENGTH: usize = 16;
/// Every observed ciphertext is 17 bytes; this is headroom against a console that pads differently, and a
/// bound because the input is console-supplied.
pub const MAX_CIPHERTEXT: usize = 64;

/// data1 is the key and data2 the material, as .NET has them; swapped, they yield noise nothing
/// downstream can tell from a wrong seed.
fn seed_field(is_ps5: bool, data1: &[u8; 16], data2: &[u8; 16]) -> ControlField {
    ControlField { key: *data1, material: *data2, context_key: seed_context_key(is_ps5) }
}

/// customData1's double base64 to the raw ciphertext. `None` if either layer is malformed or the result
/// exceeds [`MAX_CIPHERTEXT`].
pub fn decode_custom_data1(text: &[u8]) -> Option<Vec<u8>> {
    let inner = base64::decode(text)?;
    let raw = base64::decode(&inner)?;
    (raw.len() <= MAX_CIPHERTEXT).then_some(raw)
}

/// The seed from the decoded ciphertext; `None` if it is shorter than the seed. A wrong data1/data2 is
/// not detectable here (the field cipher has no tag), so callers count decrypts and failures separately.
pub fn recover(
    is_ps5: bool,
    data1: &[u8; 16],
    data2: &[u8; 16],
    ciphertext: &[u8],
) -> Option<[u8; SEED_LENGTH]> {
    // CFB's first 16 bytes depend on nothing after them, so decrypting only those gives the same seed
    // .NET gets by decrypting everything and truncating.
    let mut seed: [u8; SEED_LENGTH] = ciphertext.get(..SEED_LENGTH)?.try_into().ok()?;
    seed_field(is_ps5, data1, data2).decrypt(FIELD_COUNTER, &mut seed);
    Some(seed)
}

pub fn recover_custom_data1(
    is_ps5: bool,
    data1: &[u8; 16],
    data2: &[u8; 16],
    custom_data1: &[u8],
) -> Option<[u8; 16]> {
    recover(is_ps5, data1, data2, &decode_custom_data1(custom_data1)?)
}

/// The console's side, for tests and the scripted console.
pub fn seal(is_ps5: bool, data1: &[u8; 16], data2: &[u8; 16], seed: &[u8; 16]) -> [u8; 16] {
    let mut out = *seed;
    seed_field(is_ps5, data1, data2).encrypt(FIELD_COUNTER, &mut out);
    out
}

/// A ciphertext as the double-base64 wire value.
pub fn encode_custom_data1(ciphertext: &[u8]) -> String {
    base64::encode(base64::encode(ciphertext).as_bytes())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn seal_then_recover_through_the_wire_encoding() {
        let (d1, d2, seed) = ([1u8; 16], [2u8; 16], [0x5eu8; 16]);
        let wire = encode_custom_data1(&seal(true, &d1, &d2, &seed));
        assert_eq!(recover_custom_data1(true, &d1, &d2, wire.as_bytes()), Some(seed));
        assert_ne!(recover_custom_data1(true, &d2, &d1, wire.as_bytes()), Some(seed), "swapped roles");
        assert_eq!(recover(true, &d1, &d2, &[0; 15]), None);
        assert_eq!(decode_custom_data1(b"not base64!"), None);
    }
}
