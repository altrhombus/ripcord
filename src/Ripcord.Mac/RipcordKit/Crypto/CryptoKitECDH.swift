// libripcord's ECDH backend on the Mac, in CryptoKit.
//
// libripcord/crypto/rc_ecdh.h defines the seam and its contract. The Libripcord target is built with
// RC_ECDH_EXTERNAL_BACKEND, which compiles the backend-neutral half of rc_ecdh.c (lengths,
// fingerprints, diagnostics) and leaves these five entry points to this file. They are exported under
// the C names the core calls, so the core cannot tell which backend it is talking to. That is what lets
// libripcord/tests/ecdh_test.c, unmodified, check this backend against the .NET vectors (the
// libripcord-ecdh-kat target).
//
// Every check the Mbed TLS branch makes is made here too, in the same order where order matters: the
// curve comes from the peer key's length, the peer point is validated before our scalar is touched,
// the scalar must lie in [1, n), and a failure leaves nothing key-shaped behind.

internal import CLibripcord
import CryptoKit
import Foundation

private enum Curve {
    case p256, p521

    init?(_ id: CUnsignedInt) {
        switch id {
        case RC_ECDH_CURVE_P256: self = .p256
        case RC_ECDH_CURVE_P521: self = .p521
        default: return nil
        }
    }

    /// The scalar's width, which is also the shared secret's: 32 or 66 bytes.
    var width: Int {
        switch self {
        case .p256: Int(RC_ECDH_P256_SECRET_LENGTH)
        case .p521: Int(RC_ECDH_P521_SECRET_LENGTH)
        }
    }

    /// Public point and private scalar for a validated raw scalar, or nil if CryptoKit refuses it.
    func keyPair(scalar: Data) -> (publicKey: Data, privateKey: Data)? {
        switch self {
        case .p256:
            guard let key = try? P256.KeyAgreement.PrivateKey(rawRepresentation: scalar) else { return nil }
            return (key.publicKey.x963Representation, key.rawRepresentation)
        case .p521:
            guard let key = try? P521.KeyAgreement.PrivateKey(rawRepresentation: scalar) else { return nil }
            return (key.publicKey.x963Representation, key.rawRepresentation)
        }
    }

    /// Validates a peer point: CryptoKit's X9.63 initialiser refuses a point that is not on the curve,
    /// which is the invalid-curve defence rc_ecdh.h requires.
    func checkPeer(_ point: Data) throws {
        switch self {
        case .p256: _ = try P256.KeyAgreement.PublicKey(x963Representation: point)
        case .p521: _ = try P521.KeyAgreement.PublicKey(x963Representation: point)
        }
    }

    func sharedSecret(scalar: Data, peer: Data) throws(ECDHFailure) -> Data {
        switch self {
        case .p256:
            let peerKey: P256.KeyAgreement.PublicKey
            do { peerKey = try .init(x963Representation: peer) } catch { throw .peerCheck(error) }
            let key: P256.KeyAgreement.PrivateKey
            do { key = try .init(rawRepresentation: scalar) } catch { throw .privateKey(error) }
            do { return try key.sharedSecretFromKeyAgreement(with: peerKey).withUnsafeBytes { Data($0) } }
            catch { throw .multiply(error) }
        case .p521:
            let peerKey: P521.KeyAgreement.PublicKey
            do { peerKey = try .init(x963Representation: peer) } catch { throw .peerCheck(error) }
            let key: P521.KeyAgreement.PrivateKey
            do { key = try .init(rawRepresentation: scalar) } catch { throw .privateKey(error) }
            do { return try key.sharedSecretFromKeyAgreement(with: peerKey).withUnsafeBytes { Data($0) } }
            catch { throw .multiply(error) }
        }
    }
}

private enum ECDHFailure: Error {
    case peerCheck(any Error)
    case privateKey(any Error)
    case multiply(any Error)

    var step: Int32 {
        switch self {
        case .peerCheck: RC_ECDH_STEP_PEER_CHECK
        case .privateKey: RC_ECDH_STEP_PRIVATE_KEY
        case .multiply: RC_ECDH_STEP_MULTIPLY
        }
    }

    /// The backend's own error value, verbatim, as rc_ecdh.h asks. CryptoKit's errors carry no number of
    /// their own, so this is the bridged NSError code.
    var code: Int32 {
        switch self {
        case .peerCheck(let e), .privateKey(let e), .multiply(let e): Int32(truncatingIfNeeded: (e as NSError).code)
        }
    }
}

/// A scalar as the caller gave it, normalised to the curve's width. mbedtls reads a big-endian integer
/// of any length up to RC_ECDH_PRIVATE_MAX, so leading zeros are allowed and a shorter scalar is
/// allowed; CryptoKit wants exactly `width` bytes. Returns nil for zero, or for a value too wide to be
/// less than the group order. CryptoKit then refuses anything >= n itself.
private func normalisedScalar(_ bytes: UnsafePointer<UInt8>, _ length: Int, width: Int) -> Data? {
    let raw = UnsafeBufferPointer(start: bytes, count: length)
    let significant = raw.drop(while: { $0 == 0 })
    guard !significant.isEmpty, significant.count <= width else { return nil }
    return Data(repeating: 0, count: width - significant.count) + Data(significant)
}

private func store(_ pair: (publicKey: Data, privateKey: Data), curve: CUnsignedInt,
                   into out: UnsafeMutablePointer<rc_ecdh_keypair>) {
    out.pointee = rc_ecdh_keypair()
    out.pointee.curve = curve
    withUnsafeMutableBytes(of: &out.pointee.private_key) { $0.copyBytes(from: pair.privateKey) }
    out.pointee.private_key_length = pair.privateKey.count
    withUnsafeMutableBytes(of: &out.pointee.public_key) { $0.copyBytes(from: pair.publicKey) }
    out.pointee.public_key_length = pair.publicKey.count
}

@_cdecl("rc_ecdh_available")
func rcECDHAvailable() -> Int32 { 1 }

@_cdecl("rc_ecdh_keypair_from_private")
func rcECDHKeypairFromPrivate(_ curveId: CUnsignedInt, _ privateKey: UnsafePointer<UInt8>?, _ length: Int,
                              _ rng: rc_rng_fn?, _ rngContext: UnsafeMutableRawPointer?,
                              _ out: UnsafeMutablePointer<rc_ecdh_keypair>?) -> Int32 {
    guard let out else { return 0 }
    out.pointee = rc_ecdh_keypair()
    // rng is required by the contract even though CryptoKit needs no blinding source, so a caller written
    // against this backend still works against Mbed TLS.
    guard let curve = Curve(curveId), let privateKey, rng != nil,
          length > 0, length <= Int(RC_ECDH_PRIVATE_MAX),
          let scalar = normalisedScalar(privateKey, length, width: curve.width),
          let pair = curve.keyPair(scalar: scalar)
    else { return 0 }
    store(pair, curve: curveId, into: out)
    return 1
}

@_cdecl("rc_ecdh_generate")
func rcECDHGenerate(_ curveId: CUnsignedInt, _ rng: rc_rng_fn?, _ rngContext: UnsafeMutableRawPointer?,
                    _ out: UnsafeMutablePointer<rc_ecdh_keypair>?) -> Int32 {
    guard let out else { return 0 }
    out.pointee = rc_ecdh_keypair()
    guard let curve = Curve(curveId), let rng else { return 0 }

    // The scalar comes from the caller's RNG, never CryptoKit's own, because the seam says the RNG is
    // the caller's. Rejection sampling, as Mbed TLS does: draw `width` bytes, clear the bits above the
    // order's length (P-521's top byte holds a single bit), and retry anything out of range. Each draw
    // is rejected with probability well under one half, so 64 attempts failing means the RNG is broken.
    var candidate = Data(count: curve.width)
    defer { candidate.resetBytes(in: 0..<candidate.count) }
    for _ in 0..<64 {
        let drawn = candidate.withUnsafeMutableBytes { buffer in
            rng(rngContext, buffer.baseAddress!.assumingMemoryBound(to: UInt8.self), buffer.count)
        }
        guard drawn == 0 else { return 0 }
        if curve == .p521 { candidate[0] &= 0x01 }
        guard candidate.contains(where: { $0 != 0 }), let pair = curve.keyPair(scalar: candidate) else { continue }
        store(pair, curve: curveId, into: out)
        return 1
    }
    return 0
}

@_cdecl("rc_ecdh_check_peer_point")
func rcECDHCheckPeerPoint(_ curveId: CUnsignedInt, _ point: UnsafePointer<UInt8>?, _ length: Int) -> Int32 {
    guard let point else {
        rc_ecdh_backend_record_failure(RC_ECDH_STEP_ARGUMENTS, 0)
        return 0
    }
    guard rc_ecdh_curve_for_public_key_length(length) == curveId, let curve = Curve(curveId) else {
        rc_ecdh_backend_record_failure(RC_ECDH_STEP_CURVE, 0)
        return 0
    }
    do {
        try curve.checkPeer(Data(bytes: point, count: length))
    } catch {
        rc_ecdh_backend_record_failure(RC_ECDH_STEP_PEER_CHECK, Int32(truncatingIfNeeded: (error as NSError).code))
        return 0
    }
    rc_ecdh_backend_record_failure(RC_ECDH_STEP_NONE, 0)
    return 1
}

@_cdecl("rc_ecdh_derive_shared")
func rcECDHDeriveShared(_ pair: UnsafePointer<rc_ecdh_keypair>?,
                        _ peer: UnsafePointer<UInt8>?, _ peerLength: Int,
                        _ rng: rc_rng_fn?, _ rngContext: UnsafeMutableRawPointer?,
                        _ outSecret: UnsafeMutablePointer<UInt8>?, _ outSecretSize: Int,
                        _ outSecretLength: UnsafeMutablePointer<Int>?) -> Int32 {
    guard let pair, let peer, let outSecret, rng != nil else {
        rc_ecdh_backend_record_failure(RC_ECDH_STEP_ARGUMENTS, 0)
        return 0
    }
    // Recorded from the arguments as received, before anything else touches them; see rc_ecdh.h.
    rc_ecdh_backend_record_derivation(pair.pointee.curve, pair.pointee.private_key_length, peer, peerLength)

    // The wire carries no curve id, so the peer key's length names its curve, and it must be the curve
    // our scalar is on. A mismatch is a disagreement about the negotiated version, not something to coerce.
    guard rc_ecdh_curve_for_public_key_length(peerLength) == pair.pointee.curve,
          let curve = Curve(pair.pointee.curve) else {
        rc_ecdh_backend_record_failure(RC_ECDH_STEP_CURVE, 0)
        return 0
    }
    let privateLength = pair.pointee.private_key_length
    guard outSecretSize >= curve.width, privateLength > 0, privateLength <= Int(RC_ECDH_PRIVATE_MAX) else {
        rc_ecdh_backend_record_failure(RC_ECDH_STEP_SIZES, 0)
        return 0
    }
    let scalar = withUnsafeBytes(of: pair.pointee.private_key) { raw in
        normalisedScalar(raw.baseAddress!.assumingMemoryBound(to: UInt8.self), privateLength, width: curve.width)
    }
    guard let scalar else {
        rc_ecdh_backend_record_failure(RC_ECDH_STEP_PRIVATE_KEY, 0)
        return 0
    }

    // Peer first, our scalar second, inside sharedSecret(): validate what arrived from the wire before
    // reaching for our own key material.
    var secret: Data
    do {
        secret = try curve.sharedSecret(scalar: scalar, peer: Data(bytes: peer, count: peerLength))
    } catch {
        rc_ecdh_backend_record_failure(error.step, error.code)
        return 0
    }
    defer { secret.resetBytes(in: 0..<secret.count) }
    // CryptoKit's secret is the X coordinate at the curve's full width. Anything else would be a change
    // in CryptoKit, and a key derived from it would silently disagree with the console.
    guard secret.count == curve.width else {
        rc_ecdh_backend_record_failure(RC_ECDH_STEP_WRITE, 0)
        return 0
    }
    secret.copyBytes(to: outSecret, count: secret.count)
    outSecretLength?.pointee = secret.count
    return 1
}
