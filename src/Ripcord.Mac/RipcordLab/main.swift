// ripcord-lab: drives libripcord from the Mac, against a real console. The Mac counterpart of
// tools/Ripcord.ProtocolLab, and the tool the engine spike (docs/macos-plan.md, step 2) grows in.

import Foundation
import RipcordKit

let usage = """
    usage: ripcord-lab <command>

      check                 the build's key-agreement backend and interop constants
      discover [ms]         broadcast SRCH and list every console that answers (default 1500 ms)

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
    let milliseconds = arguments.count > 1 ? Int(arguments[1]) : 1500
    guard let milliseconds, milliseconds > 0 else { fail(usage, code: 2) }
    do {
        let consoles = try LANDiscovery.search(timeout: .milliseconds(milliseconds))
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

default:
    fail(usage, code: 2)
}
