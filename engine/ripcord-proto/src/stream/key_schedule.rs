//! The stream-plane key schedule (spec sec 5): an ECDH shared secret to a per-direction AES-128 key and
//! 16-byte base IV. Ported from `libripcord/stream/stream_key_schedule.c`.
//!
//! SP 800-108 counter mode, one HMAC-SHA256 block:
//!
//! ```text
//! info   = 0x01 || direction || 0x00 || handshakeKey(16) || 0x01 0x00    (0x0100 = 256 output bits)
//! block  = HMAC-SHA256(key = sharedSecret, msg = info)
//! aesKey = block[0:16], baseIv = block[16:32]
//! ```
//!
//! The shared secret is the HMAC key as it stands, unreduced, even at 66 bytes (P-521): HMAC hashes an
//! over-length key down itself.

use hmac::{Hmac, KeyInit, Mac};
use sha2::Sha256;

pub const DIRECTION_CLIENT_TO_SERVER: u8 = 2;
pub const DIRECTION_SERVER_TO_CLIENT: u8 = 3;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct DirectionKeys {
    pub aes_key: [u8; 16],
    pub base_iv: [u8; 16],
}

pub fn derive_direction(shared_secret: &[u8], handshake_key: &[u8; 16], direction: u8) -> DirectionKeys {
    let mut info = [0u8; 21];
    info[0] = 0x01;
    info[1] = direction;
    info[3..19].copy_from_slice(handshake_key);
    info[19] = 0x01;

    // HMAC accepts a key of any length, so this cannot fail.
    let mut mac =
        <Hmac<Sha256> as KeyInit>::new_from_slice(shared_secret).expect("HMAC takes any key length");
    mac.update(&info);
    let block = mac.finalize().into_bytes();

    let mut keys = DirectionKeys { aes_key: [0; 16], base_iv: [0; 16] };
    keys.aes_key.copy_from_slice(&block[..16]);
    keys.base_iv.copy_from_slice(&block[16..32]);
    keys
}
