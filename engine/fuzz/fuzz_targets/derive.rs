//! The crypto and Halyard derivations layer: the one layer the other targets reach only with well-formed
//! values. What arrives from outside here is a peer's public point (the console's, on the wire, before
//! anything authenticates it), a registration context of whatever length a caller hands over, and selectors
//! and counters. Each is fed arbitrary bytes. The inverse pairs are also checked to be inverses, so a broken
//! round trip fails the run rather than passing as "did not panic".
#![no_main]
use libfuzzer_sys::fuzz_target;
use ripcord_fuzz::Records;
use ripcord_proto::crypto::ecdh::{self, Curve, Ecdh, RustCryptoEcdh};
use ripcord_proto::crypto::modes;
use ripcord_proto::halyard::{control, registration};

/// `N` bytes from `record`, zero-padded: fixed-size inputs from a record of any length.
fn fixed<const N: usize>(record: &[u8]) -> [u8; N] {
    let mut out = [0u8; N];
    let n = record.len().min(N);
    out[..n].copy_from_slice(&record[..n]);
    out
}

/// One key pair per curve, made once: generating two per input spent most of each run on P-521 scalar
/// multiplication, and the surface being fuzzed is the peer's point, not our own key.
fn keys() -> &'static [(Curve, Vec<u8>, Vec<u8>)] {
    static KEYS: std::sync::OnceLock<Vec<(Curve, Vec<u8>, Vec<u8>)>> = std::sync::OnceLock::new();
    KEYS.get_or_init(|| {
        let mut n = 1u8;
        let mut fill = |buf: &mut [u8]| buf.iter_mut().for_each(|x| { *x = n; n = n.wrapping_mul(29).wrapping_add(7); });
        [Curve::P256, Curve::P521]
            .into_iter()
            .map(|curve| {
                let (private, public) = RustCryptoEcdh.generate(curve, &mut fill).expect("a key pair");
                (curve, private, public)
            })
            .collect()
    })
}

fuzz_target!(|data: &[u8]| {
    let records: Vec<&[u8]> = Records::new(data).collect();
    let [head, a, b, context, point, payload] = [0, 1, 2, 3, 4, 5].map(|i| records.get(i).copied().unwrap_or(&[]));
    let flags = head.first().copied().unwrap_or(0);
    let is_ps5 = flags & 1 != 0;
    let selector = i32::from(flags >> 1 & 3) - 1;   // -1..=2: out of range as well as in
    let counter = u64::from_be_bytes(fixed(a));

    // The control KDF and field ciphers: CFB and OFB must undo themselves.
    if let Some(field) = control::ControlField::new(&fixed(a), &fixed(b), selector, selector) {
        let mut text = payload.to_vec();
        field.encrypt(counter, &mut text);
        field.decrypt(counter, &mut text);
        assert_eq!(text, payload, "a control field did not decrypt to itself");
        let mut info = payload.to_vec();
        field.streaminfo_crypt(counter, &mut info);
        field.streaminfo_crypt(counter, &mut info);
        assert_eq!(info, payload, "the streaminfo cipher did not undo itself");
    }
    let mut cfb = payload.to_vec();
    modes::cfb128(&fixed(a), &fixed(b), &mut cfb, true);
    modes::cfb128(&fixed(a), &fixed(b), &mut cfb, false);
    assert_eq!(cfb, payload);

    // Registration, over a context of any length: the wraps must be inverses, and so must scatter/gather.
    let _ = registration::derive_key(is_ps5, context, counter as u32);
    let _ = registration::derive_account_key(is_ps5, context, &fixed(b));
    let material: [u8; 16] = fixed(a);
    if let Some(wrapped) = registration::wrap_material(is_ps5, &material, context) {
        assert_eq!(registration::unwrap_material(is_ps5, &wrapped, context), Some(material));
    }
    if let Some(wrapped) = registration::wrap_account_material(is_ps5, &material, context) {
        assert_eq!(registration::unwrap_account_material(is_ps5, &wrapped, context), Some(material));
    }
    let mut scattered = context.to_vec();
    if registration::scatter(&material, &mut scattered) {
        assert_eq!(registration::gather(&scattered), Some(material));
    }
    let _ = registration::field(is_ps5, context, counter as u32, &material);

    // ECDH against an arbitrary peer point, on both curves and at the point's own length. A point off the
    // curve, not uncompressed, or of the wrong length must be refused, never multiplied.
    let signature = ecdh::public_key_signature(&fixed(b), point);
    assert_eq!(signature, ecdh::public_key_signature(&fixed(b), point));
    for (curve, private, public) in keys() {
        if let Some(secret) = RustCryptoEcdh.shared_secret(*curve, private, point) {
            assert_eq!(secret.len(), curve.secret_length());
            assert_eq!(point.len(), curve.public_key_length(), "a secret from a point of the wrong length");
        }
        let _ = ecdh::stream_keys(&RustCryptoEcdh, *curve, private, point, &fixed(b), flags);
        // An arbitrary private scalar with a valid public key, the other half of the same surface.
        let _ = RustCryptoEcdh.shared_secret(*curve, point, public);
    }
    let _ = Curve::for_public_key_length(point.len());
});
