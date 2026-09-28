//! AES-128 keystream modes (NIST SP 800-38A) as the protocol uses them. Ported from
//! `libripcord/crypto/rc_modes.c`, which mirrors `Ripcord.Core.Net.Crypto.AesKeystreamModes`.
//!
//! Written over the block cipher rather than taken from mode crates, because the protocol needs exactly
//! these shapes: full-block CFB128 whose short final block ends the stream without feeding back, and
//! OFB. Both work in place.

use aes::Aes128;
use aes::cipher::{BlockCipherEncrypt, KeyInit};

const BLOCK: usize = 16;

/// AES-128-CFB128 over `data` in place. Only a full block feeds forward; a short last block ends it.
pub fn cfb128(key: &[u8; 16], iv: &[u8; 16], data: &mut [u8], encrypt: bool) {
    let aes = Aes128::new(&(*key).into());
    let mut feedback = aes::Block::from(*iv);
    for chunk in data.chunks_mut(BLOCK) {
        let mut keystream = feedback;
        aes.encrypt_block(&mut keystream);
        let full = chunk.len() == BLOCK;
        for (i, byte) in chunk.iter_mut().enumerate() {
            let input = *byte;
            *byte ^= keystream[i];
            if full {
                feedback[i] = if encrypt { *byte } else { input };
            }
        }
    }
}

/// AES-128-OFB over `data` in place. The same call encrypts and decrypts.
pub fn ofb(key: &[u8; 16], iv: &[u8; 16], data: &mut [u8]) {
    let aes = Aes128::new(&(*key).into());
    let mut feedback = aes::Block::from(*iv);
    for chunk in data.chunks_mut(BLOCK) {
        aes.encrypt_block(&mut feedback);
        for (byte, k) in chunk.iter_mut().zip(feedback.iter()) {
            *byte ^= k;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn cfb_round_trips_across_a_partial_block() {
        let (key, iv) = ([1u8; 16], [2u8; 16]);
        let original: Vec<u8> = (0..37u8).collect();
        let mut data = original.clone();
        cfb128(&key, &iv, &mut data, true);
        assert_ne!(data, original);
        cfb128(&key, &iv, &mut data, false);
        assert_eq!(data, original);
    }

    #[test]
    fn ofb_is_its_own_inverse() {
        let original: Vec<u8> = (0..50u8).collect();
        let mut data = original.clone();
        ofb(&[3; 16], &[4; 16], &mut data);
        ofb(&[3; 16], &[4; 16], &mut data);
        assert_eq!(data, original);
    }
}
