// ripcord-lab: drives libripcord from the Mac, against a real console. The Mac counterpart of
// tools/Ripcord.ProtocolLab, and the tool the engine spike (docs/macos-plan.md, step 2) grows in.

import Foundation
import RipcordKit

let usage = """
    usage: ripcord-lab <command>

      check                 the build's key-agreement backend and interop constants
      discover [host...]    SRCH for consoles and list every one that answers. With hosts, probe
                            each directly; without, broadcast. Wait with --wait <ms> (default 3000)
      bench                 time libripcord's per-packet stream crypto (GMAC verify + CTR decrypt)

    """

func fail(_ message: String, code: Int32 = 1) -> Never {
    FileHandle.standardError.write(Data((message + "\n").utf8))
    exit(code)
}

let arguments = Array(CommandLine.arguments.dropFirst())

switch arguments.first {
case "check", nil:
    print("key agreement:     \(Core.keyAgreementAvailable ? "CryptoKit" : "MISSING")")
    print("interop constants: \(Core.interopConstantsBundled ? "bundled" : "INCOMPLETE")")
    exit(Core.keyAgreementAvailable && Core.interopConstantsBundled ? 0 : 1)

case "discover":
    var hosts: [String] = []
    var milliseconds = 3000
    var rest = arguments.dropFirst()
    while let next = rest.popFirst() {
        if next == "--wait" {
            guard let value = rest.popFirst().flatMap(Int.init), value > 0 else { fail(usage, code: 2) }
            milliseconds = value
        } else {
            hosts.append(next)
        }
    }
    do {
        let consoles = try LANDiscovery.search(hosts: hosts, timeout: .milliseconds(milliseconds))
        if consoles.isEmpty {
            print("no console answered within \(milliseconds) ms")
            exit(1)
        }
        for c in consoles {
            let state = c.isAwake ? "awake" : "resting"
            print("\(c.hostType)  \(c.address.padding(toLength: 15, withPad: " ", startingAt: 0))  \(state.padding(toLength: 7, withPad: " ", startingAt: 0))  \(c.name)  (system \(c.systemVersion))")
        }
    } catch {
        fail("discover: \(error)")
    }

case "bench":
    _ = PacketCryptoBenchmark.run(packets: 2_000)   // warm caches and the branch predictor
    let r = PacketCryptoBenchmark.run()
    print(String(format: "%d packets of %d bytes: %.2f us/packet, %.0f packets/s, %.0f Mb/s on one core",
                 r.packets, r.packetBytes, r.microsecondsPerPacket, r.packetsPerSecond, r.megabitsPerSecond))

default:
    fail(usage, code: 2)
}
