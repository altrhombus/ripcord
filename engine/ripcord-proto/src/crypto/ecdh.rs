//! The key-agreement seam (spec sec 5.2). Ported from `libripcord/crypto/rc_ecdh.h`'s contract.
//!
//! Curve arithmetic is the one primitive this project declines to own, and the engine plan prefers the
//! operating system's implementation where there is one. So this is a trait: the host supplies CryptoKit
//! on Apple platforms and CNG on Windows, and [`RustCryptoEcdh`] serves Linux, Android and the test suite.
//! Every backend has to pass the same .NET vectors on its own platform before it is used there.
//!
//! The contract, for every backend:
//! - Public keys are uncompressed SEC1 (`0x04 || X || Y`): 65 bytes on P-256, 133 on P-521.
//! - A shared secret is the X coordinate at the curve's full width, leading zeros kept: 32 or 66 bytes.
//! - A peer point is validated as on the curve, and not the identity, before it is used. The console is
//!   not a trusted input because it is on the LAN.
//! - A private scalar of zero or at least the group order is refused.
//! - Failure is `None`, never anything key-shaped.

use hmac::{Hmac, KeyInit, Mac};
use sha2::Sha256;

use crate::stream::key_schedule::{self, DirectionKeys};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Curve {
    P256,
    P521,
}

impl Curve {
    pub fn public_key_length(self) -> usize {
        match self {
            Curve::P256 => 65,
            Curve::P521 => 133,
        }
    }

    pub fn secret_length(self) -> usize {
        match self {
            Curve::P256 => 32,
            Curve::P521 => 66,
        }
    }

    /// The width of a private scalar, which is also the width of the field.
    pub fn private_key_length(self) -> usize {
        self.secret_length()
    }

    /// How a received peer key is classified: the wire carries no curve id, and the .NET side also
    /// decides from the length alone.
    pub fn for_public_key_length(length: usize) -> Option<Curve> {
        match length {
            65 => Some(Curve::P256),
            133 => Some(Curve::P521),
            _ => None,
        }
    }
}

pub trait Ecdh {
    /// The uncompressed public point for a private scalar, or `None` if the scalar is out of range.
    fn public_key(&self, curve: Curve, private_key: &[u8]) -> Option<Vec<u8>>;

    /// The shared secret between a local private scalar and a peer's public point, or `None` if either is
    /// invalid, the peer point is not uncompressed on `curve`, or the result is degenerate.
    fn shared_secret(&self, curve: Curve, private_key: &[u8], peer_public_key: &[u8]) -> Option<Vec<u8>>;

    /// A fresh key pair `(private, public)`. The randomness is the host's (this crate reads no entropy
    /// source of its own): `fill` must fill its buffer from a CSPRNG. Out-of-range draws are redrawn.
    fn generate(&self, curve: Curve, fill: &mut dyn FnMut(&mut [u8])) -> Option<(Vec<u8>, Vec<u8>)> {
        let mut private = vec![0u8; curve.private_key_length()];
        // A valid scalar is found on the first draw with overwhelming probability; the bound only
        // stops a broken RNG (one that returns zeros, say) from looping forever.
        for _ in 0..64 {
            fill(&mut private);
            if curve == Curve::P521 {
                private[0] &= 0x01; // 521 bits in 66 bytes
            }
            if let Some(public) = self.public_key(curve, &private) {
                return Some((private, public));
            }
        }
        None
    }
}

/// The public-key signature the Takion handshake carries: HMAC-SHA256 keyed by the handshake key, over the
/// uncompressed public point.
pub fn public_key_signature(handshake_key: &[u8; 16], public_key: &[u8]) -> [u8; 32] {
    let mut mac =
        <Hmac<Sha256> as KeyInit>::new_from_slice(handshake_key).expect("HMAC takes any key length");
    mac.update(public_key);
    mac.finalize().into_bytes().into()
}

/// Key agreement straight through to one direction's stream keys: ECDH, then the stream key schedule.
pub fn stream_keys(
    ecdh: &dyn Ecdh,
    curve: Curve,
    private_key: &[u8],
    peer_public_key: &[u8],
    handshake_key: &[u8; 16],
    direction: u8,
) -> Option<DirectionKeys> {
    let secret = ecdh.shared_secret(curve, private_key, peer_public_key)?;
    Some(key_schedule::derive_direction(&secret, handshake_key, direction))
}

/// RustCrypto's `p256` and `p521`: the backend for Linux, Android and the test suite.
pub struct RustCryptoEcdh;

mod rustcrypto {
    use elliptic_curve::sec1::{FromSec1Point, ModulusSize, ToSec1Point};
    use elliptic_curve::{AffinePoint, CurveArithmetic, FieldBytesSize, PublicKey, SecretKey};

    pub(super) fn public_key<C>(private_key: &[u8], length: usize) -> Option<Vec<u8>>
    where
        C: CurveArithmetic,
        AffinePoint<C>: FromSec1Point<C> + ToSec1Point<C>,
        FieldBytesSize<C>: ModulusSize,
    {
        if private_key.len() != length {
            return None;
        }
        let secret = SecretKey::<C>::from_slice(private_key).ok()?;
        Some(secret.public_key().as_affine().to_sec1_point(false).as_bytes().to_vec())
    }

    pub(super) fn shared_secret<C>(
        private_key: &[u8],
        length: usize,
        peer: &[u8],
        peer_length: usize,
    ) -> Option<Vec<u8>>
    where
        C: CurveArithmetic,
        AffinePoint<C>: FromSec1Point<C> + ToSec1Point<C>,
        FieldBytesSize<C>: ModulusSize,
    {
        // Uncompressed only, at the curve's own length: a compressed or other-curve point is refused
        // rather than interpreted.
        if private_key.len() != length || peer.len() != peer_length || peer.first() != Some(&0x04) {
            return None;
        }
        let secret = SecretKey::<C>::from_slice(private_key).ok()?;
        // from_sec1_bytes checks the point is on the curve and is not the identity.
        let peer = PublicKey::<C>::from_sec1_bytes(peer).ok()?;
        let shared = elliptic_curve::ecdh::diffie_hellman(secret.to_nonzero_scalar(), peer.as_affine());
        Some(shared.raw_secret_bytes().to_vec())
    }
}

impl Ecdh for RustCryptoEcdh {
    fn public_key(&self, curve: Curve, private_key: &[u8]) -> Option<Vec<u8>> {
        let n = curve.private_key_length();
        match curve {
            Curve::P256 => rustcrypto::public_key::<p256::NistP256>(private_key, n),
            Curve::P521 => rustcrypto::public_key::<p521::NistP521>(private_key, n),
        }
    }

    fn shared_secret(&self, curve: Curve, private_key: &[u8], peer_public_key: &[u8]) -> Option<Vec<u8>> {
        let (n, p) = (curve.private_key_length(), curve.public_key_length());
        match curve {
            Curve::P256 => rustcrypto::shared_secret::<p256::NistP256>(private_key, n, peer_public_key, p),
            Curve::P521 => rustcrypto::shared_secret::<p521::NistP521>(private_key, n, peer_public_key, p),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn counter_rng() -> impl FnMut(&mut [u8]) {
        let mut n = 0u8;
        move |buf: &mut [u8]| {
            for b in buf {
                n = n.wrapping_add(37);
                *b = n;
            }
        }
    }

    #[test]
    fn both_sides_agree_on_both_curves() {
        for curve in [Curve::P256, Curve::P521] {
            let mut rng = counter_rng();
            let (a_priv, a_pub) = RustCryptoEcdh.generate(curve, &mut rng).unwrap();
            let (b_priv, b_pub) = RustCryptoEcdh.generate(curve, &mut rng).unwrap();
            assert_eq!(a_pub.len(), curve.public_key_length());
            let ab = RustCryptoEcdh.shared_secret(curve, &a_priv, &b_pub).unwrap();
            let ba = RustCryptoEcdh.shared_secret(curve, &b_priv, &a_pub).unwrap();
            assert_eq!(ab, ba);
            assert_eq!(ab.len(), curve.secret_length());
        }
    }

    #[test]
    fn refuses_what_it_must() {
        let mut rng = counter_rng();
        let (private, public) = RustCryptoEcdh.generate(Curve::P256, &mut rng).unwrap();
        assert_eq!(RustCryptoEcdh.public_key(Curve::P256, &[0; 32]), None, "zero scalar");
        assert_eq!(RustCryptoEcdh.public_key(Curve::P256, &[0xff; 32]), None, "scalar above the order");
        let mut off_curve = public.clone();
        off_curve[64] ^= 1;
        assert_eq!(RustCryptoEcdh.shared_secret(Curve::P256, &private, &off_curve), None, "off-curve point");
        assert_eq!(RustCryptoEcdh.shared_secret(Curve::P521, &private, &public), None, "curve mismatch");
        let mut compressed = public[..33].to_vec();
        compressed[0] = 0x02 | (public[64] & 1);
        assert_eq!(
            RustCryptoEcdh.shared_secret(Curve::P256, &private, &compressed),
            None,
            "compressed point"
        );
        assert_eq!(RustCryptoEcdh.generate(Curve::P256, &mut |b: &mut [u8]| b.fill(0)), None, "a zero RNG");
    }
}
