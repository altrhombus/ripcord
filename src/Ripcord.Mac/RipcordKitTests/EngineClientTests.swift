// The Rust engine's client ABI from Swift: a whole LAN session against the engine's loopback console,
// with CryptoKit doing the key agreement. Loopback only; nothing leaves the machine.

import Testing
@testable import RipcordKit

@Suite("Rust engine client")
struct EngineClientTests {
    @Test("a LAN session runs through the client ABI to video and the goodbye, on CryptoKit")
    func loopbackSession() throws {
        let summary = try #require(RipcordEngineLoopback.run())
        #expect(summary.reachedStreamReady)
        #expect(summary.frames >= 15)
        #expect(summary.stats >= 1)
        #expect(summary.passcodesAsked == 1)
        #expect(summary.endedByDisconnect)
        #expect(summary.consoleHeardGoodbye)
    }
}
