//! GMAC sealing of outgoing Takion packets, and verification of incoming control packets. Ported from
//! `libripcord/takion/takion_control_sealer.c`; the offsets and rules match
//! `HalyardV1SessionCrypto.cs`, which records the capture evidence for each.
//!
//! One key position is shared by control DATA, SACKs, congestion and input packets: a byte count, each
//! packet advancing it by its length rounded up to 16. A second counter would repeat a position, and a
//! repeated position is a repeated GMAC nonce under one key.
//!
//! | Packet | Key position | Tag | AAD zeroes |
//! |---|---|---|---|
//! | control | 9 | 5 | tag and key position [V] |
//! | congestion | 11 | 7 | tag and key position [V] |
//! | input | 4 | 8 | tag only, and the payload is CTR-encrypted first |

use crate::stream::packet_crypto::PacketCrypto;

pub const CONTROL_TAG_OFFSET: usize = 5;
pub const CONTROL_KEYPOS_OFFSET: usize = 9;
pub const CONGESTION_TAG_OFFSET: usize = 7;
pub const CONGESTION_KEYPOS_OFFSET: usize = 11;
pub const INPUT_KEYPOS_OFFSET: usize = 4;
pub const INPUT_TAG_OFFSET: usize = 8;
pub const CONGESTION_PACKET_SIZE: usize = 15;

/// The send direction's sealer.
pub struct Sealer {
    crypto: PacketCrypto,
    key_pos: u64,
}

impl Sealer {
    pub fn new(aes_key: &[u8; 16], base_iv: &[u8; 16]) -> Self {
        Self { crypto: PacketCrypto::new(aes_key, base_iv), key_pos: 0 }
    }

    pub fn key_position(&self) -> u64 {
        self.key_pos
    }

    /// Reserves this packet's position and writes it (low 32 bits, big-endian) at `keypos_offset`.
    fn reserve(&mut self, packet: &mut [u8], keypos_offset: usize) -> u64 {
        let key_pos = self.key_pos;
        self.key_pos += packet.len().next_multiple_of(16) as u64;
        packet[keypos_offset..keypos_offset + 4].copy_from_slice(&(key_pos as u32).to_be_bytes());
        key_pos
    }

    fn seal_at(&mut self, packet: &mut [u8], tag_offset: usize, keypos_offset: usize) {
        // Too short to hold the fields: left alone, and no position is spent.
        if packet.len() < keypos_offset + 4 {
            return;
        }
        let key_pos = self.reserve(packet, keypos_offset);
        self.crypto.seal(key_pos, packet, tag_offset, true);
    }

    /// A control packet (DATA or SACK).
    pub fn seal_control(&mut self, packet: &mut [u8]) {
        self.seal_at(packet, CONTROL_TAG_OFFSET, CONTROL_KEYPOS_OFFSET);
    }

    /// A congestion-feedback packet.
    pub fn seal_congestion(&mut self, packet: &mut [u8]) {
        self.seal_at(packet, CONGESTION_TAG_OFFSET, CONGESTION_KEYPOS_OFFSET);
    }

    /// A controller-input packet: encrypts `packet[payload_offset..]` with AES-CTR, then seals the
    /// ciphertext with the tag-only AAD rule. A plaintext payload is faithfully decrypted by the console
    /// into noise, having passed authentication.
    pub fn seal_input(&mut self, packet: &mut [u8], payload_offset: usize) {
        if packet.len() < INPUT_TAG_OFFSET + 4 || payload_offset > packet.len() {
            return;
        }
        let key_pos = self.reserve(packet, INPUT_KEYPOS_OFFSET);
        self.crypto.crypt_payload(key_pos, &mut packet[payload_offset..]);
        self.crypto.seal(key_pos, packet, INPUT_TAG_OFFSET, false);
    }
}

/// The receive direction's control verifier. The key position is read from the packet: the sender says
/// where it is, and the tag makes being told safe. Failures are counted, because the useful question on a
/// new platform is what proportion fails, not whether one did.
pub struct Verifier {
    crypto: PacketCrypto,
    pub checked: u64,
    pub failed: u64,
}

impl Verifier {
    pub fn new(aes_key: &[u8; 16], base_iv: &[u8; 16]) -> Self {
        Self { crypto: PacketCrypto::new(aes_key, base_iv), checked: 0, failed: 0 }
    }

    /// Whether the packet's tag is good. Too short to carry the fields is a failure, never a pass.
    pub fn check(&mut self, packet: &[u8]) -> bool {
        self.checked += 1;
        let ok = packet.len() >= CONTROL_KEYPOS_OFFSET + 4 && {
            let key_pos = u32::from_be_bytes(
                packet[CONTROL_KEYPOS_OFFSET..CONTROL_KEYPOS_OFFSET + 4].try_into().unwrap(),
            );
            self.crypto.verify(u64::from(key_pos), packet, CONTROL_TAG_OFFSET, true)
        };
        if !ok {
            self.failed += 1;
        }
        ok
    }
}

/// One congestion-feedback packet: base type 5, received at 3 and lost at 5 as big-endian u16s that
/// saturate rather than wrap. [V] layout; the lost field's meaning is [X]. Sealed by [`Sealer`].
pub fn congestion_packet(received: u64, lost: u64) -> [u8; CONGESTION_PACKET_SIZE] {
    let mut p = [0u8; CONGESTION_PACKET_SIZE];
    p[0] = 0x05;
    p[3..5].copy_from_slice(&(received.min(0xFFFF) as u16).to_be_bytes());
    p[5..7].copy_from_slice(&(lost.min(0xFFFF) as u16).to_be_bytes());
    p
}

#[cfg(test)]
mod tests {
    use super::*;

    const KEY: [u8; 16] =
        [0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff];
    const IV: [u8; 16] =
        [0x0f, 0x1e, 0x2d, 0x3c, 0x4b, 0x5a, 0x69, 0x78, 0x87, 0x96, 0xa5, 0xb4, 0xc3, 0xd2, 0xe1, 0xf0];

    /// The cases `stream_crypto_test.c`'s sealer, verifier and congestion checks pin.
    #[test]
    fn positions_advance_by_block_aligned_length_across_packet_kinds() {
        let mut s = Sealer::new(&KEY, &IV);
        let mut p = [0xa5u8; 40];
        s.seal_control(&mut p);
        assert_eq!(&p[9..13], &[0; 4], "the first packet seals at 0");
        assert_ne!(&p[5..9], &[0xa5; 4]);
        assert_eq!(s.key_position(), 48, "40 rounds up to 48");
        s.seal_control(&mut [0xa5; 32]);
        assert_eq!(s.key_position(), 80, "an exact multiple is not padded");
        let mut c = congestion_packet(351, 7);
        assert_eq!(&c[..7], &[0x05, 0, 0, 0x01, 0x5f, 0, 7]);
        s.seal_congestion(&mut c);
        assert_eq!(s.key_position(), 96, "congestion spends the same counter");
        let mut tiny = [0x3cu8; 8];
        s.seal_control(&mut tiny);
        assert_eq!((tiny, s.key_position()), ([0x3c; 8], 96), "too short: untouched, nothing spent");
        assert_eq!(&congestion_packet(70_000, 70_000)[3..7], &[0xff; 4], "saturates");
    }

    #[test]
    fn the_verifier_is_the_sealer_read_backwards() {
        let (mut s, mut v) = (Sealer::new(&KEY, &IV), Verifier::new(&KEY, &IV));
        let mut p = [0x5au8; 40];
        s.seal_control(&mut p);
        assert!(v.check(&p));
        p[39] ^= 1;
        assert!(!v.check(&p), "an altered byte");
        p[39] ^= 1;
        p[12] ^= 0x10;
        assert!(!v.check(&p), "a forged key position");
        assert!(!v.check(&[0x3c; 8]), "too short to authenticate");
        assert_eq!((v.checked, v.failed), (4, 3));
        let mut other = Verifier::new(&[0xff; 16], &IV);
        p[12] ^= 0x10;
        assert!(!other.check(&p), "the wrong direction's key");
    }
}
