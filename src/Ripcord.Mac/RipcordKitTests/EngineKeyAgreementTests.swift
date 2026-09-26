// CryptoKit as the Rust engine's key agreement, checked against the .NET vectors on this platform
// (docs/engine-plan.md: every backend passes session-crypto.kat on its own platform before it is used
// there). The vectors are generated, never committed, so a checkout without them skips.

import Foundation
import Testing
@testable import RipcordKit

@Suite("Rust engine key agreement")
struct EngineKeyAgreementTests {
    static let vectors = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        .appending(path: "libripcord/tests/vectors/session-crypto.kat")

    @Test("CryptoKit passes session-crypto.kat through the engine, as RustCrypto does",
          .enabled(if: FileManager.default.fileExists(atPath: vectors.path),
                   "generate the vectors: dotnet run --project tools/Ripcord.ProtocolLab -- vectors"))
    func sessionVectors() throws {
        let text = try String(contentsOf: Self.vectors, encoding: .utf8)
        let cryptoKit = RipcordEngineKat.run(text, backend: .cryptoKit)
        let rustCrypto = RipcordEngineKat.run(text, backend: .rustCrypto)
        #expect(cryptoKit.ok, "CryptoKit failed \(cryptoKit.failed) checks (see stderr)")
        #expect(cryptoKit.failed == 0)
        #expect(cryptoKit.passed > 0)
        // Both backends check exactly the same lines.
        #expect(cryptoKit == rustCrypto)
    }

    @Test("a vector file with a wrong answer fails through CryptoKit too")
    func wrongAnswerFails() {
        // P-256's generator point is the public key of scalar 1; one flipped byte makes it wrong.
        let one = String(repeating: "0", count: 62) + "01"
        let wrong = "04" + String(repeating: "00", count: 64)
        let result = RipcordEngineKat.run("version 1\necdhpub p256 \(one) \(wrong)\n", backend: .cryptoKit)
        #expect(!result.ok)
        #expect(result.failed == 1)
    }
}
