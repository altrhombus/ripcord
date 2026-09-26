// How long the stream crypto takes per A/V packet on this machine: the question the plan's "crypto
// throughput" item asks (docs/macos-plan.md), answered by measurement rather than estimate. Phase 1
// measured both engines from this workload (C 7.71-8.11 us, Rust 0.53-0.57 us, engine/README.md); since
// Phase 3 the Mac links only the Rust engine, so only it is measured here.
//
// Each iteration does what the demuxer does to every packet: verify the
// 4-byte GMAC tag, then CTR-decrypt the payload. The key position advances by the payload length times
// 64, so every packet lands in its own GMAC rotation window and pays for a fresh key: the worst case. The
// keys are synthetic, so nothing here depends on a console.

internal import CRipcordEngine
import Foundation

public enum PacketCryptoBenchmark {
    public enum Engine: String, CaseIterable, Sendable {
        /// The Rust engine, through its generated C ABI.
        case rust
    }

    public struct Result: Sendable {
        public let engine: Engine
        public let packets: Int
        public let packetBytes: Int
        public let microsecondsPerPacket: Double
        /// Packets per second one core sustains, crypto only.
        public var packetsPerSecond: Double { 1_000_000 / microsecondsPerPacket }
        /// The stream bitrate that rate corresponds to.
        public var megabitsPerSecond: Double { packetsPerSecond * Double(packetBytes) * 8 / 1_000_000 }
    }

    /// One engine's verify-then-decrypt, behind the same three calls the loop makes.
    private struct Crypto {
        let seal: (UInt64, UnsafeMutableBufferPointer<UInt8>) -> Bool
        let verify: (UInt64, UnsafeBufferPointer<UInt8>) -> Bool
        let decrypt: (UInt64, UnsafeMutableBufferPointer<UInt8>) -> Void
        let release: () -> Void
    }

    private static let headerLength = 18   // STREAM_HEADER_LENGTH: the tag and key position live in it
    private static let tagOffset = 10      // the A/V tag offset, in both engines

    private static func makeRust(key: [UInt8], iv: [UInt8]) -> Crypto {
        precondition(RipcordEngineLayout.matches(), "the Rust engine's ABI does not match the header RipcordKit was built against")
        guard let ctx = ripcord_packet_crypto_new(key, iv) else { fatalError("ripcord_packet_crypto_new refused its keys") }
        return Crypto(
            seal: { ripcord_packet_crypto_seal(ctx, $0, $1.baseAddress, $1.count, tagOffset, false) == RIPCORD_STATUS_OK },
            verify: { ripcord_packet_crypto_verify(ctx, $0, $1.baseAddress, $1.count, tagOffset, false) == RIPCORD_STATUS_OK },
            decrypt: { _ = ripcord_packet_crypto_crypt_payload(ctx, $0, $1.baseAddress, $1.count) },
            release: { ripcord_packet_crypto_free(ctx) })
    }

    public static func run(engine: Engine = .rust, packets: Int = 20_000, packetBytes: Int = 1426) -> Result {
        let key = [UInt8](repeating: 0x5a, count: 16), iv = [UInt8](repeating: 0xa5, count: 16)
        let crypto = makeRust(key: key, iv: iv)
        defer { crypto.release() }

        var template = [UInt8](repeating: 0, count: packetBytes)
        for i in template.indices { template[i] = UInt8(truncatingIfNeeded: i &* 31) }

        // Seal one packet per distinct key position up front, so the timed loop does only receive work.
        let distinct = 256
        var sealed: [[UInt8]] = []
        var positions: [UInt64] = []
        var position: UInt64 = 0
        for _ in 0..<distinct {
            var packet = template
            let ok = packet.withUnsafeMutableBufferPointer { crypto.seal(position, $0) }
            precondition(ok, "sealing a benchmark packet failed")
            sealed.append(packet)
            positions.append(position)
            position += UInt64(packetBytes - headerLength) * 64   // spread across rotation windows
        }

        var working = sealed[0]
        var failures = 0
        let clock = ContinuousClock()
        let elapsed = clock.measure {
            for n in 0..<packets {
                let index = n % distinct
                working.withUnsafeMutableBufferPointer { buffer in
                    sealed[index].withUnsafeBufferPointer { source in
                        buffer.baseAddress!.update(from: source.baseAddress!, count: source.count)
                    }
                    if !crypto.verify(positions[index], UnsafeBufferPointer(buffer)) {
                        failures += 1
                    }
                    crypto.decrypt(positions[index], UnsafeMutableBufferPointer(rebasing: buffer[headerLength...]))
                }
            }
        }
        precondition(failures == 0, "a sealed packet failed to verify: the benchmark is measuring the wrong thing")
        let seconds = Double(elapsed.components.seconds) + Double(elapsed.components.attoseconds) / 1e18
        return Result(engine: engine, packets: packets, packetBytes: packetBytes,
                      microsecondsPerPacket: seconds * 1e6 / Double(packets))
    }
}
