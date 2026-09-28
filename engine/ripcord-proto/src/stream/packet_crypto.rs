//! The v1 per-packet stream crypto, one channel direction. Ported from
//! `libripcord/stream/stream_packet_crypto.c`.
//!
//! AES-128-CTR payload encryption plus a 4-byte GMAC, both keyed off the packet's 64-bit key position.
//! Encrypt-then-MAC: a receiver verifies the tag over the packet as it stands on the wire, then decrypts.
//!
//! Nonces are 16 bytes, formed by little-endian addition into the base IV modulo 2^128:
//!
//! ```text
//! GMAC nonce = baseIv + keyPos/16
//! CTR nonce  = baseIv + (keyPos + 16)/16         one block above the GMAC's
//! ```
//!
//! The GMAC key rotates every 45,000 key-position units; the payload key does not:
//!
//! ```text
//! window 0:    gmacKey = fold(aesKey, baseIv)
//! window n>=1: gmacKey = fold(gmacKey_0, baseIv + n * 0xAF6E)          n = keyPos / 45000
//! fold(a, b) = SHA-256(a || b)[0:16] XOR SHA-256(a || b)[16:32]
//! ```
//!
//! Which bytes the AAD treats as zero depends on the packet class. A/V (tag at 10) and feedback (tag
//! at 8) zero the 4-byte tag only; control (tag at 5) and congestion (tag at 7) also zero the key
//! position that follows it. Callers say which with `zero_key_pos` rather than this module guessing
//! from the offset.

use aes::Aes128;
use aes_gcm::aead::consts::U16;
use aes_gcm::{AeadInOut, AesGcm, KeyInit};
use ctr::CtrCore;
use ctr::cipher::{InnerIvInit, StreamCipher};
use sha2::{Digest, Sha256};

pub const TAG_LENGTH: usize = 4;
pub const AV_TAG_OFFSET: usize = 10;
pub const FEEDBACK_TAG_OFFSET: usize = 8;

/// The largest packet a tag is computed over. Tag computation fails closed past it, as the C core's
/// does, so the two engines accept exactly the same packets.
pub const MAX_PACKET: usize = 2048;

const ROTATION_INTERVAL: u64 = 45_000;
const ROTATION_FACTOR: u128 = 0xAF6E;

/// GMAC with a 16-byte IV: GCM's GHASH-derived J0, over AAD only.
type Gmac = AesGcm<Aes128, U16>;
type Ctr128Le<'a> = ctr::Ctr128LE<&'a Aes128>;

pub struct PacketCrypto {
    /// Expanded once; CTR borrows it, so no packet pays for a key schedule.
    aes: Aes128,
    base_iv: u128,
    base_iv_bytes: [u8; 16],
    gmac_base_key: [u8; 16],
    /// The prepared GMAC for the rotation window last used. A window spans hundreds of packets on the
    /// wire, so the key is derived and the authenticator built once per window, not once per packet.
    prepared: Option<(u64, Gmac)>,
}

fn fold(a: &[u8; 16], b: &[u8; 16]) -> [u8; 16] {
    let digest = Sha256::new().chain_update(a).chain_update(b).finalize();
    std::array::from_fn(|i| digest[i] ^ digest[i + 16])
}

impl PacketCrypto {
    pub fn new(aes_key: &[u8; 16], base_iv: &[u8; 16]) -> Self {
        Self {
            aes: Aes128::new(&(*aes_key).into()),
            base_iv: u128::from_le_bytes(*base_iv),
            base_iv_bytes: *base_iv,
            gmac_base_key: fold(aes_key, base_iv),
            prepared: None,
        }
    }

    pub fn gmac_nonce(&self, key_pos: u64) -> [u8; 16] {
        self.base_iv.wrapping_add(u128::from(key_pos >> 4)).to_le_bytes()
    }

    pub fn ctr_nonce(&self, key_pos: u64) -> [u8; 16] {
        // The +16 wraps in 64 bits, as it does in the C core and in the .NET reference.
        self.base_iv.wrapping_add(u128::from(key_pos.wrapping_add(16) >> 4)).to_le_bytes()
    }

    pub fn gmac_key(&self, key_pos: u64) -> [u8; 16] {
        self.gmac_key_for_window(key_pos / ROTATION_INTERVAL)
    }

    fn gmac_key_for_window(&self, window: u64) -> [u8; 16] {
        if window == 0 {
            return self.gmac_base_key;
        }
        // window * factor exceeds 64 bits for large windows; the addition is modulo 2^128.
        let rotated = self.base_iv.wrapping_add(u128::from(window) * ROTATION_FACTOR);
        fold(&self.gmac_base_key, &rotated.to_le_bytes())
    }

    /// AES-128-CTR over `payload` in place, with a 128-bit little-endian counter. Symmetric: the same
    /// call encrypts and decrypts.
    pub fn crypt_payload(&self, key_pos: u64, payload: &mut [u8]) {
        let core = CtrCore::inner_iv_init(&self.aes, &self.ctr_nonce(key_pos).into());
        Ctr128Le::from_core(core).apply_keystream(payload);
    }

    /// The 4-byte tag over `packet` as it stands on the wire, with the AAD zeroing described above
    /// applied to a copy. `None` if the packet exceeds [`MAX_PACKET`] or the tag offset lies past its end.
    pub fn compute_tag(
        &mut self,
        key_pos: u64,
        packet: &[u8],
        tag_offset: usize,
        zero_key_pos: bool,
    ) -> Option<[u8; TAG_LENGTH]> {
        if packet.len() > MAX_PACKET || tag_offset > packet.len() {
            return None;
        }
        let mut scratch = [0u8; MAX_PACKET];
        let aad = &mut scratch[..packet.len()];
        aad.copy_from_slice(packet);
        let clear = (TAG_LENGTH + if zero_key_pos { 4 } else { 0 }).min(packet.len() - tag_offset);
        aad[tag_offset..tag_offset + clear].fill(0);

        let window = key_pos / ROTATION_INTERVAL;
        if !matches!(self.prepared, Some((w, _)) if w == window) {
            let key = self.gmac_key_for_window(window);
            self.prepared = Some((window, Gmac::new(&key.into())));
        }
        let (_, gmac) = self.prepared.as_ref()?;
        let nonce = self.gmac_nonce(key_pos).into();
        // An empty message cannot exceed GCM's length limits, and the AAD is capped at MAX_PACKET.
        let tag = gmac.encrypt_inout_detached(&nonce, aad, (&mut [][..]).into()).ok()?;
        let mut out = [0u8; TAG_LENGTH];
        out.copy_from_slice(&tag[..TAG_LENGTH]);
        Some(out)
    }

    /// Writes the tag into `packet[tag_offset..tag_offset + 4]` (sender side). `false` on the same
    /// conditions as [`compute_tag`](Self::compute_tag), or if the tag does not fit.
    pub fn seal(&mut self, key_pos: u64, packet: &mut [u8], tag_offset: usize, zero_key_pos: bool) -> bool {
        if tag_offset.checked_add(TAG_LENGTH).is_none_or(|end| end > packet.len()) {
            return false;
        }
        match self.compute_tag(key_pos, packet, tag_offset, zero_key_pos) {
            Some(tag) => {
                packet[tag_offset..tag_offset + TAG_LENGTH].copy_from_slice(&tag);
                true
            }
            None => false,
        }
    }

    /// Whether the packet's on-wire tag is correct (receiver side). A packet too short to carry a tag
    /// fails; "cannot be authenticated" is never "fine".
    pub fn verify(&mut self, key_pos: u64, packet: &[u8], tag_offset: usize, zero_key_pos: bool) -> bool {
        let Some(on_wire) = packet.get(tag_offset..tag_offset.saturating_add(TAG_LENGTH)) else {
            return false;
        };
        let Some(expected) = self.compute_tag(key_pos, packet, tag_offset, zero_key_pos) else {
            return false;
        };
        expected.iter().zip(on_wire).fold(0u8, |acc, (a, b)| acc | (a ^ b)) == 0
    }

    pub fn base_iv(&self) -> &[u8; 16] {
        &self.base_iv_bytes
    }
}

/// GMAC over `aad` with an arbitrary-length IV (never 12 bytes: that length takes GCM's other J0 path,
/// which this protocol does not use). The full 16-byte tag, for the `gmac` known-answer lines.
pub fn gmac(key: &[u8; 16], iv: &[u8; 16], aad: &[u8]) -> [u8; 16] {
    let tag = Gmac::new(&(*key).into())
        .encrypt_inout_detached(&(*iv).into(), aad, (&mut [][..]).into())
        .expect("AAD within GCM's limits");
    tag.into()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn seal_then_verify_round_trips_and_detects_tampering() {
        let mut c = PacketCrypto::new(&[0x5a; 16], &[0xa5; 16]);
        let mut packet: Vec<u8> = (0..64u8).collect();
        assert!(c.seal(90_001, &mut packet, AV_TAG_OFFSET, false));
        assert!(c.verify(90_001, &packet, AV_TAG_OFFSET, false));
        assert!(!c.verify(90_017, &packet, AV_TAG_OFFSET, false), "a different key position must not verify");
        packet[40] ^= 1;
        assert!(!c.verify(90_001, &packet, AV_TAG_OFFSET, false));
    }

    #[test]
    fn crypt_payload_is_symmetric() {
        let c = PacketCrypto::new(&[1; 16], &[2; 16]);
        let original: Vec<u8> = (0..=255u8).cycle().take(1_411).collect();
        let mut buf = original.clone();
        c.crypt_payload(123_456, &mut buf);
        assert_ne!(buf, original);
        c.crypt_payload(123_456, &mut buf);
        assert_eq!(buf, original);
    }

    #[test]
    fn fails_closed_on_bad_lengths() {
        let mut c = PacketCrypto::new(&[0; 16], &[0; 16]);
        assert_eq!(c.compute_tag(0, &[0; MAX_PACKET + 1], AV_TAG_OFFSET, false), None);
        assert_eq!(c.compute_tag(0, &[0; 8], 9, false), None);
        assert!(!c.verify(0, &[0; 12], AV_TAG_OFFSET, false), "too short to hold the tag");
        assert!(!c.seal(0, &mut [0; 12], AV_TAG_OFFSET, false));
    }
}
