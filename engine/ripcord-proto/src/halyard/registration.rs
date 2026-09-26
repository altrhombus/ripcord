//! v1 registration (PIN pairing), and the account (no-PIN) route beside it. Ported from
//! `libripcord/halyard/halyard_registration.c`; the provenance is in `HalyardRegistrationKdf.cs`.
//!
//! One 16-byte transport key protects the request field and the reply carrying the pairing record:
//!
//! ```text
//! K = registration_table[context[selector_offset] & 0x1f]
//! K[12..16] ^= big-endian u32(passcode)
//! ```
//!
//! The per-pairing material travels inside the request context, wrapped through a second table and
//! scattered to two offsets. The two families differ only in their tables and one bias.

use super::{constants as c, control::ControlField, entry};

pub const CONTEXT_LENGTH: usize = 0x1e0;
pub const FIELD_COUNTER: u64 = 0;
/// The console reads wrapped bytes 0..8 from one offset and 8..16 from another; the split is the
/// vendor's.
pub const WRAPPED_LOW: usize = 0x191;
pub const WRAPPED_HIGH: usize = 0x0c7;

const PS5_WRAP_BIAS: u8 = 0x2d; // subtracted
const PS4_WRAP_BIAS: u8 = 0x29; // added
const ACCOUNT_WRAP_BIAS: u8 = 0x2b;
/// context[0] >> 3 selects the wrap entry.
const MATERIAL_SELECTOR_OFFSET: usize = 0;

fn key_table(is_ps5: bool) -> Option<&'static [u8; 512]> {
    if is_ps5 { Some(&c::REGISTRATION_TABLE) } else { c::PS4_REGISTRATION_TABLE.as_ref() }
}

fn wrap_entry(is_ps5: bool, context: &[u8]) -> Option<&'static [u8; 16]> {
    let table = if is_ps5 { &c::MATERIAL_WRAP_TABLE } else { c::PS4_MATERIAL_WRAP_TABLE.as_ref()? };
    Some(entry(table, usize::from(*context.get(MATERIAL_SELECTOR_OFFSET)? >> 3)))
}

/// The transport key from the transmitted context and the 8-digit PIN. `None` if the family's tables are
/// absent or the context is too short to hold the selector.
pub fn derive_key(is_ps5: bool, context: &[u8], passcode: u32) -> Option<[u8; 16]> {
    let index = usize::from(*context.get(c::SELECTOR_OFFSET)? & 0x1f);
    let mut key = *entry(key_table(is_ps5)?, index);
    // Big-endian into the last four bytes: the other order is a key wrong in four bytes and
    // indistinguishable from a mistyped PIN.
    for (k, p) in key[12..].iter_mut().zip(passcode.to_be_bytes()) {
        *k ^= p;
    }
    Some(key)
}

/// `wrapped[i] = ((material[i] ^ table[i]) + bias + i)`, bias -0x2d on PS5 and +0x29 on PS4.
pub fn wrap_material(is_ps5: bool, material: &[u8; 16], context: &[u8]) -> Option<[u8; 16]> {
    let t = wrap_entry(is_ps5, context)?;
    Some(std::array::from_fn(|i| {
        let x = material[i] ^ t[i];
        let biased = if is_ps5 { x.wrapping_sub(PS5_WRAP_BIAS) } else { x.wrapping_add(PS4_WRAP_BIAS) };
        biased.wrapping_add(i as u8)
    }))
}

pub fn unwrap_material(is_ps5: bool, wrapped: &[u8; 16], context: &[u8]) -> Option<[u8; 16]> {
    let t = wrap_entry(is_ps5, context)?;
    Some(std::array::from_fn(|i| {
        let x = wrapped[i].wrapping_sub(i as u8);
        let unbiased = if is_ps5 { x.wrapping_add(PS5_WRAP_BIAS) } else { x.wrapping_sub(PS4_WRAP_BIAS) };
        unbiased ^ t[i]
    }))
}

/// Writes the wrapped bytes into the context at the two offsets. `false` if the context is too short.
pub fn scatter(wrapped: &[u8; 16], context: &mut [u8]) -> bool {
    if context.len() < WRAPPED_LOW + 8 {
        return false;
    }
    context[WRAPPED_LOW..WRAPPED_LOW + 8].copy_from_slice(&wrapped[..8]);
    context[WRAPPED_HIGH..WRAPPED_HIGH + 8].copy_from_slice(&wrapped[8..]);
    true
}

pub fn gather(context: &[u8]) -> Option<[u8; 16]> {
    if context.len() < WRAPPED_LOW + 8 {
        return None;
    }
    let mut out = [0u8; 16];
    out[..8].copy_from_slice(&context[WRAPPED_LOW..WRAPPED_LOW + 8]);
    out[8..].copy_from_slice(&context[WRAPPED_HIGH..WRAPPED_HIGH + 8]);
    Some(out)
}

/// Registration's field-cipher context key is not a fifth key: it is the control plane's selector-one
/// key for a PS5 and selector-zero for a PS4.
fn family_context_key(is_ps5: bool) -> [u8; 16] {
    if is_ps5 { c::CTX_SELECTOR_ONE } else { c::CTX_SELECTOR_ZERO }
}

/// The field crypto for a PIN registration exchange: the PIN-derived key, the caller's material (the
/// same bytes it wrapped into the context), and the family's context key.
pub fn field(is_ps5: bool, context: &[u8], passcode: u32, material: &[u8; 16]) -> Option<ControlField> {
    Some(ControlField {
        key: derive_key(is_ps5, context, passcode)?,
        material: *material,
        context_key: family_context_key(is_ps5),
    })
}

// ---- The account ("web"/no-PIN) route ----
//
// Not the PIN route with another bias: the arithmetic and the XOR are in the opposite order, and the
// constant is 0x2b for both families. The PIN transform here corrupts the first 16 bytes of the field and
// the console answers 403 / 80108b09. PS5 is [V]; PS4 is [X], never run against a console.

/// `wrapped[i] = ((material[i] - i) + 0x2b) ^ table[i]`, truncated to a byte before the XOR.
pub fn wrap_account_material(is_ps5: bool, material: &[u8; 16], context: &[u8]) -> Option<[u8; 16]> {
    let t = wrap_entry(is_ps5, context)?;
    Some(std::array::from_fn(|i| material[i].wrapping_sub(i as u8).wrapping_add(ACCOUNT_WRAP_BIAS) ^ t[i]))
}

pub fn unwrap_account_material(is_ps5: bool, wrapped: &[u8; 16], context: &[u8]) -> Option<[u8; 16]> {
    let t = wrap_entry(is_ps5, context)?;
    Some(std::array::from_fn(|i| (wrapped[i] ^ t[i]).wrapping_sub(ACCOUNT_WRAP_BIAS).wrapping_add(i as u8)))
}

/// The account route's transport key: the raw table entry (a zero PIN folds nothing) XOR the seed.
pub fn derive_account_key(is_ps5: bool, context: &[u8], seed: &[u8; 16]) -> Option<[u8; 16]> {
    let raw = derive_key(is_ps5, context, 0)?;
    Some(std::array::from_fn(|i| raw[i] ^ seed[i]))
}

pub fn account_field(
    is_ps5: bool,
    context: &[u8],
    seed: &[u8; 16],
    material: &[u8; 16],
) -> Option<ControlField> {
    Some(ControlField {
        key: derive_account_key(is_ps5, context, seed)?,
        material: *material,
        context_key: family_context_key(is_ps5),
    })
}

/// The context key the console seals the account seed under: the family's registration context key.
pub(crate) fn seed_context_key(is_ps5: bool) -> [u8; 16] {
    family_context_key(is_ps5)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn wraps_invert_and_scatter_round_trips() {
        let context: Vec<u8> = (0..CONTEXT_LENGTH).map(|i| (i * 7) as u8).collect();
        let material: [u8; 16] = std::array::from_fn(|i| (i * 13) as u8);
        for is_ps5 in [true, false] {
            if !is_ps5 && !super::super::has_ps4_registration() {
                continue;
            }
            let w = wrap_material(is_ps5, &material, &context).unwrap();
            assert_eq!(unwrap_material(is_ps5, &w, &context), Some(material));
            let a = wrap_account_material(is_ps5, &material, &context).unwrap();
            assert_ne!(a, w, "the account wrap is a different transform");
            assert_eq!(unwrap_account_material(is_ps5, &a, &context), Some(material));
            let mut scattered = context.clone();
            assert!(scatter(&w, &mut scattered));
            assert_eq!(gather(&scattered), Some(w));
        }
    }

    #[test]
    fn short_contexts_are_refused() {
        assert_eq!(derive_key(true, &[0; 10], 1234), None);
        assert_eq!(wrap_material(true, &[0; 16], &[]), None);
        assert!(!scatter(&[0; 16], &mut [0; 100]));
        assert_eq!(gather(&[0; 100]), None);
    }
}
