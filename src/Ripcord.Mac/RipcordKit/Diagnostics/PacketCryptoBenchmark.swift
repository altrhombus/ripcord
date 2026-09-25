// How long libripcord's stream crypto takes per A/V packet on this machine: the question the plan's
// "crypto throughput" item asks (docs/macos-plan.md), answered by measurement rather than estimate.
//
// Each iteration does what the demuxer does to every packet (stream_demux's open_packet): verify the
// 4-byte GMAC tag, then CTR-decrypt the payload. The key position advances by the payload length, as it
// does on the wire, so the run crosses into the rotated-key windows a long session spends nearly all its
// time in. The keys are synthetic, so nothing here depends on a console.

internal import CLibripcord
import Foundation

public enum PacketCryptoBenchmark {
    public struct Result: Sendable {
        public let packets: Int
        public let packetBytes: Int
        public let microsecondsPerPacket: Double
        /// Packets per second one core sustains, crypto only.
        public var packetsPerSecond: Double { 1_000_000 / microsecondsPerPacket }
        /// The stream bitrate that rate corresponds to.
        public var megabitsPerSecond: Double { packetsPerSecond * Double(packetBytes) * 8 / 1_000_000 }
    }

    public static func run(packets: Int = 20_000, packetBytes: Int = 1426) -> Result {
        var ctx = stream_packet_crypto()
        let key = [UInt8](repeating: 0x5a, count: 16), iv = [UInt8](repeating: 0xa5, count: 16)
        stream_packet_crypto_init(&ctx, key, iv)

        let headerLength = 18   // STREAM_HEADER_LENGTH: the tag and key position live in it
        let tagOffset = Int32(STREAM_PACKET_CRYPTO_AV_TAG_OFFSET)
        var template = [UInt8](repeating: 0, count: packetBytes)
        for i in template.indices { template[i] = UInt8(truncatingIfNeeded: i &* 31) }

        // Seal one packet per distinct key position up front, so the timed loop does only receive work.
        let distinct = 256
        var sealed: [[UInt8]] = []
        var positions: [UInt64] = []
        var position: UInt64 = 0
        for _ in 0..<distinct {
            var packet = template
            _ = stream_packet_crypto_seal(&ctx, position, &packet, packet.count, tagOffset, 0)
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
                    if stream_packet_crypto_verify(&ctx, positions[index], buffer.baseAddress, buffer.count, tagOffset, 0) != 1 {
                        failures += 1
                    }
                    stream_packet_crypto_crypt_payload(&ctx, positions[index],
                                                       buffer.baseAddress! + headerLength, buffer.count - headerLength)
                }
            }
        }
        precondition(failures == 0, "a sealed packet failed to verify: the benchmark is measuring the wrong thing")
        let seconds = Double(elapsed.components.seconds) + Double(elapsed.components.attoseconds) / 1e18
        return Result(packets: packets, packetBytes: packetBytes, microsecondsPerPacket: seconds * 1e6 / Double(packets))
    }
}
