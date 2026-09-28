//! The stream key agreement (spec sec 4.2, 5.1 to 5.3): SESSION_REQUEST out, SESSION_REPLY in, four
//! per-direction keys out. Ported from `libripcord/takion/takion_session_negotiator.c` and checked
//! against `TakionSessionNegotiator.cs`. Sans-IO: it builds and consumes bytes; the connection moves them.
//!
//! Where the two differ, this follows .NET except where C is strictly stronger (recorded in
//! `engine/README.md`): the curve for an unvalidated version is an error, as in .NET, not C's P-256
//! fallback; the reply's `versionAccepted`, required fields and signature length are checked, as in C.
//!
//! `handshake_key` is 16 fresh random bytes per session, carried to the console inside the encrypted
//! launch spec. Its only job is to bind both public keys to the session the console agreed to; a fixed
//! value removes the exchange's only defence against a man in the middle.

use super::control::{self, SessionRequest};
use crate::crypto::ecdh::{self, Curve, Ecdh};
use crate::stream::key_schedule::{
    self, DIRECTION_CLIENT_TO_SERVER, DIRECTION_SERVER_TO_CLIENT, DirectionKeys,
};

/// The version the shipped client sends; it selects P-521.
pub const CLIENT_VERSION: u32 = 17;
/// The versions offered in PROTOCOL_VERSION_REQUEST, as both references send them.
pub const OFFERED_VERSIONS: [u32; 8] = [9, 10, 11, 13, 14, 15, 16, 17];
/// The session-key string the vendor client sends on a direct LAN session: a literal, not a placeholder.
pub const DEFAULT_SESSION_KEY: &[u8] = b"InvalidSessionId";
/// The unused required `encryptedKey`: four zero bytes, which the console accepts on hardware. The vendor sends
/// it present and empty (`22 00`, cap53 frame 10086), so these are ours rather than the wire's; whether to
/// match the vendor is open in ROADMAP.
const ENCRYPTED_KEY: [u8; 4] = [0; 4];

/// The curve for a negotiated version: 0x0d to 0x11 are P-521, and nothing else has been validated.
pub fn curve_for_version(version: u32) -> Option<Curve> {
    (0x0d..=0x11).contains(&version).then_some(Curve::P521)
}

/// Why a SESSION_REPLY was refused. Diagnostic: the distinction that matters most is `Signature`, which
/// points at the launch spec's encryption (the console signed under a different handshake key).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Reject {
    Parse,
    Version,
    NoEcdh,
    SignatureLength,
    Signature,
    /// The peer key's length names a different curve than ours.
    Curve,
    /// The lengths agreed and the derivation still failed.
    Derive,
}

/// Both directions' stream keys.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SessionKeys {
    pub send: DirectionKeys,
    pub receive: DirectionKeys,
}

pub struct Negotiator {
    curve: Curve,
    handshake_key: [u8; 16],
    private_key: Vec<u8>,
    pub public_key: Vec<u8>,
}

impl Negotiator {
    /// The curve this negotiation's key pair is on.
    pub fn curve(&self) -> Curve {
        self.curve
    }

    /// Generates the ephemeral pair and builds SESSION_REQUEST. `launch_spec` is the already-encrypted,
    /// already-base64 spec carrying this same `handshake_key`. `None` for an unvalidated version or a
    /// failed key generation.
    pub fn begin(
        protocol_version: u32,
        handshake_key: &[u8; 16],
        session_key: &[u8],
        launch_spec: &[u8],
        ecdh: &dyn Ecdh,
        fill: &mut dyn FnMut(&mut [u8]),
    ) -> Option<(Self, Vec<u8>)> {
        let curve = curve_for_version(protocol_version)?;
        let (private_key, public_key) = ecdh.generate(curve, fill)?;
        let signature = ecdh::public_key_signature(handshake_key, &public_key);
        let request = SessionRequest {
            client_version: protocol_version,
            session_key,
            launch_spec,
            encrypted_key: &ENCRYPTED_KEY,
            ecdh_public_key: Some(&public_key),
            ecdh_signature: Some(&signature),
        }
        .build();
        Some((Self { curve, handshake_key: *handshake_key, private_key, public_key }, request))
    }

    /// Checks SESSION_REPLY and derives the keys. The signature check is the one that matters: without it
    /// anything able to inject a DATA chunk could substitute its own key and read the session.
    pub fn accept_reply(&self, reply: &[u8], ecdh: &dyn Ecdh) -> Result<SessionKeys, Reject> {
        let parsed = control::parse_session_reply(reply).ok_or(Reject::Parse)?;
        if !parsed.version_accepted {
            return Err(Reject::Version);
        }
        let (Some(peer), Some(signature)) = (parsed.ecdh_public_key, parsed.ecdh_signature) else {
            return Err(Reject::NoEcdh);
        };
        if signature.len() != 32 {
            return Err(Reject::SignatureLength);
        }
        let expected = ecdh::public_key_signature(&self.handshake_key, peer);
        if expected.iter().zip(signature).fold(0u8, |acc, (a, b)| acc | (a ^ b)) != 0 {
            return Err(Reject::Signature);
        }
        if Curve::for_public_key_length(peer.len()) != Some(self.curve) {
            return Err(Reject::Curve);
        }
        let shared = ecdh.shared_secret(self.curve, &self.private_key, peer).ok_or(Reject::Derive)?;
        Ok(SessionKeys {
            send: key_schedule::derive_direction(&shared, &self.handshake_key, DIRECTION_CLIENT_TO_SERVER),
            receive: key_schedule::derive_direction(&shared, &self.handshake_key, DIRECTION_SERVER_TO_CLIENT),
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::crypto::ecdh::RustCryptoEcdh;
    use crate::takion::control::SessionReply;

    fn rng(seed: u8) -> impl FnMut(&mut [u8]) {
        let mut n = seed;
        move |b: &mut [u8]| {
            for x in b {
                n = n.wrapping_mul(29).wrapping_add(7);
                *x = n;
            }
        }
    }

    /// The console's half, as a scripted peer runs it.
    fn console_reply(
        request: &[u8],
        handshake_key: &[u8; 16],
        tamper: impl Fn(&mut SessionReply<'_>),
    ) -> (Vec<u8>, SessionKeys) {
        let req = control::parse_session_request(request).unwrap();
        let (private, public) = RustCryptoEcdh.generate(Curve::P521, &mut rng(99)).unwrap();
        let shared =
            RustCryptoEcdh.shared_secret(Curve::P521, &private, req.ecdh_public_key.unwrap()).unwrap();
        let signature = ecdh::public_key_signature(handshake_key, &public);
        let mut reply = SessionReply {
            server_version: 17,
            token: 1,
            encrypted_key_accepted: true,
            version_accepted: true,
            session_key: b"",
            server_version_string: None,
            ecdh_public_key: Some(&public),
            ecdh_signature: Some(&signature),
        };
        tamper(&mut reply);
        // The console's send key is our receive key, and the reverse.
        let keys = SessionKeys {
            send: key_schedule::derive_direction(&shared, handshake_key, DIRECTION_CLIENT_TO_SERVER),
            receive: key_schedule::derive_direction(&shared, handshake_key, DIRECTION_SERVER_TO_CLIENT),
        };
        (reply.build(), keys)
    }

    #[test]
    fn both_sides_derive_the_same_keys() {
        let hk = [0x42; 16];
        let (n, request) = Negotiator::begin(
            CLIENT_VERSION,
            &hk,
            DEFAULT_SESSION_KEY,
            b"spec",
            &RustCryptoEcdh,
            &mut rng(1),
        )
        .unwrap();
        let parsed = control::parse_session_request(&request).unwrap();
        assert_eq!(
            (parsed.client_version, parsed.session_key, parsed.encrypted_key),
            (17, DEFAULT_SESSION_KEY, &[0u8; 4][..])
        );
        assert_eq!(parsed.ecdh_public_key.unwrap().len(), 133);
        let (reply, console_keys) = console_reply(&request, &hk, |_| {});
        assert_eq!(n.accept_reply(&reply, &RustCryptoEcdh), Ok(console_keys));
    }

    #[test]
    fn every_refusal_is_named() {
        let hk = [0x42; 16];
        let (n, request) = Negotiator::begin(
            CLIENT_VERSION,
            &hk,
            DEFAULT_SESSION_KEY,
            b"spec",
            &RustCryptoEcdh,
            &mut rng(1),
        )
        .unwrap();
        let reject = |t: &dyn Fn(&mut SessionReply<'_>)| {
            n.accept_reply(&console_reply(&request, &hk, t).0, &RustCryptoEcdh)
        };
        assert_eq!(reject(&|r| r.version_accepted = false), Err(Reject::Version));
        assert_eq!(reject(&|r| r.ecdh_signature = None), Err(Reject::NoEcdh));
        assert_eq!(reject(&|r| r.ecdh_signature = Some(&[0; 31])), Err(Reject::SignatureLength));
        assert_eq!(reject(&|r| r.ecdh_signature = Some(&[0; 32])), Err(Reject::Signature));
        assert_eq!(n.accept_reply(b"\x08\x01", &RustCryptoEcdh), Err(Reject::Parse));
        let (reply, _) = console_reply(&request, &[0x43; 16], |_| {});
        assert_eq!(
            n.accept_reply(&reply, &RustCryptoEcdh),
            Err(Reject::Signature),
            "signed under another handshake key"
        );
    }

    #[test]
    fn an_unvalidated_version_has_no_curve() {
        assert_eq!(curve_for_version(17), Some(Curve::P521));
        assert_eq!(curve_for_version(12), None);
        assert!(
            Negotiator::begin(12, &[0; 16], DEFAULT_SESSION_KEY, b"", &RustCryptoEcdh, &mut rng(1)).is_none()
        );
    }
}
