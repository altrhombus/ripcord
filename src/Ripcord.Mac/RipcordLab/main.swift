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
      pair <host> <PIN> <account-id>
                            PIN-pair with a console showing Settings > System > Remote Play > Pair
                            Device. The record is kept in ~/Library/Application Support/Ripcord/lab
      consoles              list the consoles the lab has paired with

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

case "pair":
    guard arguments.count == 4 else { fail(usage, code: 2) }
    let host = arguments[1]
    // Discovery first: it names the console, gives its stable id and family, and the registration
    // wants a search reply before it anyway.
    let found = (try? LANDiscovery.search(hosts: [host])) ?? []
    guard let console = found.first else { fail("pair: \(host) did not answer discovery") }
    guard console.isAwake else { fail("pair: \(console.name) is resting; turn it on and open its Pair Device screen") }
    do {
        let paired = try Pairing.register(host: host, family: console.family ?? .ps5, accountID: arguments[3],
                                          pin: arguments[2], name: console.name, consoleID: console.hostID)
        try PairingStore.lab.save(paired)
        print("paired with \(paired.name) (\(paired.family.rawValue), \(paired.host)); saved to \(PairingStore.lab.directory.path)")
    } catch {
        fail("pair: \(error)")
    }

case "consoles":
    let consoles = PairingStore.lab.load()
    if consoles.isEmpty { print("no paired consoles in \(PairingStore.lab.directory.path)") }
    for c in consoles { print("\(c.family.rawValue)  \(c.host)  \(c.name)  id \(c.consoleID)") }

default:
    fail(usage, code: 2)
}
