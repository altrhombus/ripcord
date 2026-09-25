// The CryptoKit ECDH backend, through the C entry points the core calls. The known-answer comparison
// against the .NET vectors is libripcord-ecdh-kat, which runs the core's own ecdh_test.c; these check
// the contract's edges, which vectors do not reach.

import CLibripcord
import Testing

@Suite("CryptoKit key agreement")
struct KeyAgreementTests {
    private func generate(_ curve: CUnsignedInt) throws -> rc_ecdh_keypair {
        var pair = rc_ecdh_keypair()
        try #require(rc_ecdh_generate(curve, rc_random_rng_callback, nil, &pair) == 1)
        return pair
    }

    private func publicKey(_ pair: rc_ecdh_keypair) -> [UInt8] {
        withUnsafeBytes(of: pair.public_key) { Array($0.prefix(pair.public_key_length)) }
    }

    private func derive(_ pair: rc_ecdh_keypair, _ peer: [UInt8]) -> [UInt8]? {
        var local = pair
        var secret = [UInt8](repeating: 0, count: Int(RC_ECDH_SECRET_MAX))
        var length = 0
        let ok = rc_ecdh_derive_shared(&local, peer, peer.count, rc_random_rng_callback, nil,
                                       &secret, secret.count, &length)
        return ok == 1 ? Array(secret.prefix(length)) : nil
    }

    @Test("both sides agree", arguments: [RC_ECDH_CURVE_P256, RC_ECDH_CURVE_P521])
    func agreement(curve: CUnsignedInt) throws {
        let a = try generate(curve), b = try generate(curve)
        #expect(a.public_key_length == rc_ecdh_public_key_length(curve))
        let ab = try #require(derive(a, publicKey(b)))
        let ba = try #require(derive(b, publicKey(a)))
        #expect(ab == ba)
        #expect(ab.count == rc_ecdh_secret_length(curve))
    }

    @Test("a peer point off the curve is refused before it is used")
    func invalidCurvePoint() throws {
        let a = try generate(RC_ECDH_CURVE_P521)
        var peer = publicKey(try generate(RC_ECDH_CURVE_P521))
        peer[peer.count - 1] ^= 0x01   // Y no longer satisfies the curve equation
        #expect(derive(a, peer) == nil)
        #expect(rc_ecdh_last_error_step() == RC_ECDH_STEP_PEER_CHECK)
        #expect(rc_ecdh_check_peer_point(RC_ECDH_CURVE_P521, peer, peer.count) == 0)
    }

    @Test("a peer on the other curve is a curve mismatch, not a coercion")
    func curveMismatch() throws {
        let a = try generate(RC_ECDH_CURVE_P521)
        #expect(derive(a, publicKey(try generate(RC_ECDH_CURVE_P256))) == nil)
        #expect(rc_ecdh_last_error_step() == RC_ECDH_STEP_CURVE)
    }

    @Test("a zero scalar is refused, and leaves nothing key-shaped behind")
    func zeroScalar() {
        var pair = rc_ecdh_keypair()
        pair.public_key_length = 99
        let zero = [UInt8](repeating: 0, count: 66)
        #expect(rc_ecdh_keypair_from_private(RC_ECDH_CURVE_P521, zero, zero.count, rc_random_rng_callback, nil, &pair) == 0)
        #expect(pair.public_key_length == 0 && pair.private_key_length == 0)
    }

    @Test("a leading-zero scalar means the same key as its trimmed form, as Mbed TLS reads it")
    func paddedScalar() throws {
        let generated = try generate(RC_ECDH_CURVE_P256)
        let scalar = withUnsafeBytes(of: generated.private_key) { Array($0.prefix(generated.private_key_length)) }
        var padded = rc_ecdh_keypair()
        let wide = [UInt8](repeating: 0, count: 34) + scalar
        try #require(rc_ecdh_keypair_from_private(RC_ECDH_CURVE_P256, wide, wide.count, rc_random_rng_callback, nil, &padded) == 1)
        #expect(publicKey(padded) == publicKey(generated))
    }

    @Test("the core reports the CryptoKit backend")
    func available() {
        #expect(rc_ecdh_available() == 1)
    }
}
