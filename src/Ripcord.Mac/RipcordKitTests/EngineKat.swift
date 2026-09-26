// The known-answer vector runner through the Rust engine (test-support): the check each platform's key
// agreement has to pass on its own platform before it is used there.

import CRipcordEngine
@testable import RipcordKit

/// Runs a known-answer vector file through the Rust engine: the check each platform's backend has to
/// pass on its own platform before it is used there.
enum RipcordEngineKat {
    enum Backend {
        /// CryptoKit, through the engine's backend table.
        case cryptoKit
        /// The engine's own RustCrypto backend, for comparison.
        case rustCrypto
    }

    struct Result: Equatable {
        let passed: UInt64, failed: UInt64, deferred: UInt64
        let ok: Bool
    }

    static func run(_ text: String, backend: Backend) -> Result {
        precondition(RipcordEngineLayout.matches(), "the Rust engine's ABI does not match the header RipcordKit was built against")
        var result = RipcordKatResult()
        var utf8 = Array(text.utf8)
        let status: RipcordStatus
        switch backend {
        case .cryptoKit:
            var table = CryptoKitEngineECDH.backend
            status = ripcord_kat_run(&utf8, utf8.count, &table, &result)
        case .rustCrypto:
            status = ripcord_kat_run(&utf8, utf8.count, nil, &result)
        }
        return Result(passed: result.passed, failed: result.failed, deferred: result.deferred, ok: status == RIPCORD_STATUS_OK)
    }
}
