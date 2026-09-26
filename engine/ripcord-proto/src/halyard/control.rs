//! The control-plane crypto: session KDF, context-key selection, per-field IV, and the field (CFB128)
//! and streaminfo (OFB) ciphers. Ported from `libripcord/halyard/halyard_control_crypto.c`.

use hmac::{Hmac, KeyInit, Mac};
use sha2::Sha256;

use super::{KEY_LENGTH, VERSION_SELECTOR_PS4, constants as c, entry};
use crate::crypto::modes;

/// The control AES key and the IV material, from the console's per-connect nonce (RP-Nonce) and the
/// stored per-pairing companion (RP-Key).
///
/// **The roles are not interchangeable.** `key` comes from the companion and is the AES-128 key;
/// `material` comes from the nonce and feeds [`field_iv`]. Ripcord shipped these swapped once: it passed
/// every primitive test and failed against a console.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ControlKeys {
    pub key: [u8; KEY_LENGTH],
    pub material: [u8; KEY_LENGTH],
}

/// `None` if the family's tables are not bundled. Anything but the PS4 selector takes the PS5 variant,
/// as the .NET dispatch does.
pub fn kdf(nonce: &[u8; 16], companion: &[u8; 16], version_selector: i32) -> Option<ControlKeys> {
    let mut keys = ControlKeys { key: [0; 16], material: [0; 16] };
    // Two different nonce bytes select the two tables: table 1 by nonce[7], table 2 by nonce[0].
    let (i1, i2) = (usize::from(nonce[7] >> 3), usize::from(nonce[0] >> 3));
    if version_selector == VERSION_SELECTOR_PS4 {
        let (t1, t2) = (c::PS4_KDF_TABLE1.as_ref()?, c::PS4_KDF_TABLE2.as_ref()?);
        let (e1, e2) = (entry(t1, i1), entry(t2, i2));
        for i in 0..16 {
            // PS4 XORs the table into the companion, then adds.
            keys.key[i] = (e1[i] ^ companion[i]).wrapping_add(0x21 + i as u8) ^ nonce[i];
            keys.material[i] = nonce[i].wrapping_add(0x36 + i as u8) ^ e2[i];
        }
    } else {
        let (e1, e2) = (entry(&c::KDF_TABLE1, i1), entry(&c::KDF_TABLE2, i2));
        for i in 0..16 {
            // PS5 adds to the companion, then XORs the table.
            keys.key[i] = companion[i].wrapping_add(0x18 + i as u8) ^ e1[i] ^ nonce[i];
            keys.material[i] = nonce[i].wrapping_sub(i as u8).wrapping_sub(0x2d) ^ e2[i];
        }
    }
    Some(keys)
}

/// The field context key from the two negotiated selectors. The high-band codec cases (8, 9) win
/// regardless of the version selector.
pub fn context_key(codec_selector: i32, version_selector: i32) -> &'static [u8; 16] {
    match (codec_selector, version_selector) {
        (8 | 9, _) => &c::CTX_CODEC_IN_HIGH,
        (_, 1) => &c::CTX_SELECTOR_ONE,
        (_, 0) => &c::CTX_SELECTOR_ZERO,
        _ => &c::CTX_FALLBACK_ZERO,
    }
}

/// `IV = HMAC-SHA256(context_key, material || big-endian u64 counter)[0:16]`. The counter is the field's
/// position in the connection's sequence. Big-endian and 64-bit: a little-endian or 32-bit write passes
/// every small-counter test and fails on a long session.
pub fn field_iv(context_key: &[u8; 16], material: &[u8; 16], counter: u64) -> [u8; 16] {
    let mut mac = <Hmac<Sha256> as KeyInit>::new_from_slice(context_key).expect("HMAC takes any key length");
    mac.update(material);
    mac.update(&counter.to_be_bytes());
    let full = mac.finalize().into_bytes();
    full[..16].try_into().expect("16 of 32 bytes")
}

/// The field crypto for one control session, or for a registration exchange (which fills the same three
/// inputs differently; see [`super::registration`]).
#[derive(Clone, Copy, Debug)]
pub struct ControlField {
    pub key: [u8; 16],
    pub material: [u8; 16],
    pub context_key: [u8; 16],
}

impl ControlField {
    /// A session's field crypto: the KDF, then the context key. `None` if the family's tables are absent.
    pub fn new(
        nonce: &[u8; 16],
        companion: &[u8; 16],
        codec_selector: i32,
        version_selector: i32,
    ) -> Option<Self> {
        let keys = kdf(nonce, companion, version_selector)?;
        Some(Self {
            key: keys.key,
            material: keys.material,
            context_key: *context_key(codec_selector, version_selector),
        })
    }

    fn iv(&self, counter: u64) -> [u8; 16] {
        field_iv(&self.context_key, &self.material, counter)
    }

    /// Encrypt one control header field in place (AES-128-CFB128).
    pub fn encrypt(&self, counter: u64, data: &mut [u8]) {
        modes::cfb128(&self.key, &self.iv(counter), data, true);
    }

    pub fn decrypt(&self, counter: u64, data: &mut [u8]) {
        modes::cfb128(&self.key, &self.iv(counter), data, false);
    }

    /// The streaminfo / launchSpec cipher (AES-128-OFB): the same IV derivation, a different mode, and one
    /// call for both directions.
    pub fn streaminfo_crypt(&self, counter: u64, data: &mut [u8]) {
        modes::ofb(&self.key, &self.iv(counter), data);
    }
}
