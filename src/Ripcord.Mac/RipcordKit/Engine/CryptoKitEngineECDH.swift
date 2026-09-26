// The Rust engine's key agreement on the Mac, in CryptoKit (docs/engine-plan.md, "Dependencies": the OS's
// implementation where it has one). The engine reaches it through the RipcordEcdhBackend table its C ABI
// defines; the contract is ripcord_proto::crypto::ecdh's, the same one CryptoKitECDH.swift meets for the C
// core:
//
//   - a public key is the uncompressed X9.63 point, 0x04 || X || Y;
//   - a shared secret is the X coordinate at the curve's full width;
//   - a peer point is validated on the curve before our scalar is touched, and a scalar must lie in
//     [1, n);
//   - failure writes nothing and returns false.
//
// A backend is used on a platform only once it has passed session-crypto.kat there. For this one that is
// EngineKeyAgreementTests, which runs the vectors through the engine with this table plugged in.

internal import CRipcordEngine
import CryptoKit
import Foundation

enum CryptoKitEngineECDH {
    /// The table to hand the engine. Stateless, so `user` is unused.
    static var backend: RipcordEcdhBackend {
        RipcordEcdhBackend(user: nil, public_key: publicKey, shared_secret: sharedSecret)
    }

    private static func width(_ curve: UInt32) -> Int? {
        switch curve {
        case UInt32(RIPCORD_CURVE_P256): 32
        case UInt32(RIPCORD_CURVE_P521): 66
        default: nil
        }
    }

    /// The scalar at the curve's width: leading zeros allowed, zero refused, and anything wider than the
    /// field refused. CryptoKit refuses a scalar at or above the order itself.
    private static func scalar(_ bytes: UnsafePointer<UInt8>?, _ length: Int, width: Int) -> Data? {
        guard let bytes, length > 0 else { return nil }
        let significant = UnsafeBufferPointer(start: bytes, count: length).drop(while: { $0 == 0 })
        guard !significant.isEmpty, significant.count <= width else { return nil }
        return Data(repeating: 0, count: width - significant.count) + Data(significant)
    }

    private static func write(_ data: Data, _ out: UnsafeMutablePointer<UInt8>?, _ capacity: Int,
                              _ length: UnsafeMutablePointer<Int>?) -> Bool {
        guard let out, let length, data.count <= capacity else { return false }
        data.copyBytes(to: out, count: data.count)
        length.pointee = data.count
        return true
    }

    private static let publicKey: @convention(c) (
        UnsafeMutableRawPointer?, UInt32, UnsafePointer<UInt8>?, Int, UnsafeMutablePointer<UInt8>?, Int,
        UnsafeMutablePointer<Int>?
    ) -> Bool = { _, curve, key, keyLength, out, capacity, length in
        guard let width = width(curve), let raw = scalar(key, keyLength, width: width) else { return false }
        let point: Data
        switch curve {
        case UInt32(RIPCORD_CURVE_P256):
            guard let k = try? P256.KeyAgreement.PrivateKey(rawRepresentation: raw) else { return false }
            point = k.publicKey.x963Representation
        default:
            guard let k = try? P521.KeyAgreement.PrivateKey(rawRepresentation: raw) else { return false }
            point = k.publicKey.x963Representation
        }
        return write(point, out, capacity, length)
    }

    private static let sharedSecret: @convention(c) (
        UnsafeMutableRawPointer?, UInt32, UnsafePointer<UInt8>?, Int, UnsafePointer<UInt8>?, Int,
        UnsafeMutablePointer<UInt8>?, Int, UnsafeMutablePointer<Int>?
    ) -> Bool = { _, curve, key, keyLength, peer, peerLength, out, capacity, length in
        guard let width = width(curve), let raw = scalar(key, keyLength, width: width), let peer,
              // Uncompressed only, at the curve's own length: 65 or 133 bytes.
              peerLength == 2 * width + 1, peer.pointee == 0x04
        else { return false }
        let point = Data(bytes: peer, count: peerLength)
        let secret: Data
        switch curve {
        case UInt32(RIPCORD_CURVE_P256):
            // The X9.63 initialiser refuses a point off the curve: the invalid-curve defence.
            guard let p = try? P256.KeyAgreement.PublicKey(x963Representation: point),
                  let k = try? P256.KeyAgreement.PrivateKey(rawRepresentation: raw),
                  let s = try? k.sharedSecretFromKeyAgreement(with: p) else { return false }
            secret = s.withUnsafeBytes { Data($0) }
        default:
            guard let p = try? P521.KeyAgreement.PublicKey(x963Representation: point),
                  let k = try? P521.KeyAgreement.PrivateKey(rawRepresentation: raw),
                  let s = try? k.sharedSecretFromKeyAgreement(with: p) else { return false }
            secret = s.withUnsafeBytes { Data($0) }
        }
        return write(secret, out, capacity, length)
    }
}
