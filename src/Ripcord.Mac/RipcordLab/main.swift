// ripcord-lab: drives libripcord from the Mac, against a real console. The Mac counterpart of
// tools/Ripcord.ProtocolLab, and the tool the engine spike (docs/macos-plan.md, step 2) grows in.

import CoreMedia
import Foundation
import RipcordKit
import Synchronization

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
      connect <host> [seconds] [out.h264] [--bitrate kbps] [--720] [--h264]
                            stream from a paired console for `seconds` (default 20), printing each
                            stage and a stats line, and writing every video frame, as the console
                            sent it, to out.h264 (Annex-B; ffplay or VLC plays it)

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

case "connect":
    var positional: [String] = []
    var options = ConsoleSession.Options()
    var rest = arguments.dropFirst()
    while let next = rest.popFirst() {
        switch next {
        case "--bitrate": options.bitrateKbps = rest.popFirst().flatMap(Int.init) ?? 0
        case "--720": options.width = 1280; options.height = 720
        case "--h264": options.allowHEVC = false
        default: positional.append(next)
        }
    }
    guard !positional.isEmpty else { fail(usage, code: 2) }
    let host = positional[0]
    let seconds = positional.count > 1 ? (Double(positional[1]) ?? 20) : 20
    let outPath = positional.count > 2 ? positional[2] : "ripcord-lab-capture.h264"
    guard let console = PairingStore.lab.load().first(where: { $0.host == host }) else {
        fail("connect: no paired console at \(host); run `ripcord-lab pair` first")
    }
    FileManager.default.createFile(atPath: outPath, contents: nil)
    guard let out = FileHandle(forWritingAtPath: outPath) else { fail("connect: cannot write \(outPath)") }
    let done = DispatchSemaphore(value: 0)
    let started = ContinuousClock.now
    @Sendable func stamp() -> String {
        let d = ContinuousClock.now - started
        return String(format: "%6.2fs", Double(d.components.seconds) + Double(d.components.attoseconds) / 1e18)
    }
    let lastStats = Mutex(ContinuousClock.now - .seconds(10))
    // The session is created after every handler is set: it copies them at init.
    let sessionBox = Mutex<ConsoleSession?>(nil)

    var handlers = SessionHandlers()
    handlers.log = { line in FileHandle.standardError.write(Data("  core: \(line)\n".utf8)) }
    handlers.stage = { stage in print("\(stamp())  stage \(stage)") }
    handlers.streamInfo = { info in print("\(stamp())  stream \(info.width)x\(info.height) \(info.codec)") }
    handlers.video = { sample, _ in out.write(annexB(sample)) }
    handlers.stats = { s in
        let due = lastStats.withLock { last -> Bool in
            guard ContinuousClock.now - last >= .seconds(1) else { return false }
            last = ContinuousClock.now
            return true
        }
        guard due else { return }
        print("\(stamp())  \(s.kbps) kb/s  video \(s.videoFrames) (key \(s.keyframes))  audio \(s.audioFrames)  lost \(s.packetsLost)/\(s.packetsReceived)  idr \(s.idrRequests)")
    }
    handlers.passcodeRequested = { retry in
        // Off the session thread: reading a terminal blocks, and the session must keep being serviced.
        DispatchQueue.global().async {
            print(retry == 0 ? "the console asks for its passcode: " : "refused; passcode again: ", terminator: "")
            fflush(stdout)
            let digits = readLine() ?? ""
            sessionBox.withLock { digits.isEmpty ? $0?.cancel() : $0?.supplyPasscode(digits) }
        }
    }
    handlers.ended = { outcome in
        print("\(stamp())  ended at \(outcome.stage), reason \(outcome.endReason), control error \(outcome.controlError)")
        done.signal()
    }
    let session = ConsoleSession(console: console, options: options, handlers: handlers)
    sessionBox.withLock { $0 = session }
    session.start()
    DispatchQueue.global().asyncAfter(deadline: .now() + seconds) { session.disconnect() }
    done.wait()
    try? out.close()
    print("video written to \(outPath)")

default:
    fail(usage, code: 2)
}

/// A sample as the console sent it: length prefixes back to start codes, and a keyframe's parameter
/// sets written in front, so the file is a plain Annex-B elementary stream.
func annexB(_ sample: CMSampleBuffer) -> Data {
    guard let block = CMSampleBufferGetDataBuffer(sample) else { return Data() }
    var length = 0
    var pointer: UnsafeMutablePointer<CChar>?
    guard CMBlockBufferGetDataPointer(block, atOffset: 0, lengthAtOffsetOut: nil, totalLengthOut: &length,
                                      dataPointerOut: &pointer) == noErr, let pointer else { return Data() }
    var data = Data()
    if let format = CMSampleBufferGetFormatDescription(sample), !sample.isNotSync { data.append(parameterSets(format)) }
    let raw = UnsafeRawBufferPointer(start: pointer, count: length)
    var offset = 0
    while offset + 4 <= length {
        let n = Int(raw.loadUnaligned(fromByteOffset: offset, as: UInt32.self).bigEndian)
        offset += 4
        guard offset + n <= length else { break }
        data.append(contentsOf: [0, 0, 0, 1])
        data.append(contentsOf: raw[offset..<(offset + n)])
        offset += n
    }
    return data
}

/// The format description's parameter sets as Annex-B, for a keyframe written to a raw stream file.
func parameterSets(_ format: CMFormatDescription) -> Data {
    var data = Data()
    let isHEVC = CMFormatDescriptionGetMediaSubType(format) == kCMVideoCodecType_HEVC
    var count = 0
    _ = isHEVC
        ? CMVideoFormatDescriptionGetHEVCParameterSetAtIndex(format, parameterSetIndex: 0, parameterSetPointerOut: nil, parameterSetSizeOut: nil, parameterSetCountOut: &count, nalUnitHeaderLengthOut: nil)
        : CMVideoFormatDescriptionGetH264ParameterSetAtIndex(format, parameterSetIndex: 0, parameterSetPointerOut: nil, parameterSetSizeOut: nil, parameterSetCountOut: &count, nalUnitHeaderLengthOut: nil)
    for i in 0..<count {
        var pointer: UnsafePointer<UInt8>?
        var size = 0
        _ = isHEVC
            ? CMVideoFormatDescriptionGetHEVCParameterSetAtIndex(format, parameterSetIndex: i, parameterSetPointerOut: &pointer, parameterSetSizeOut: &size, parameterSetCountOut: nil, nalUnitHeaderLengthOut: nil)
            : CMVideoFormatDescriptionGetH264ParameterSetAtIndex(format, parameterSetIndex: i, parameterSetPointerOut: &pointer, parameterSetSizeOut: &size, parameterSetCountOut: nil, nalUnitHeaderLengthOut: nil)
        guard let pointer else { continue }
        data.append(contentsOf: [0, 0, 0, 1])
        data.append(pointer, count: size)
    }
    return data
}

extension CMSampleBuffer {
    var isNotSync: Bool {
        let attachments = CMSampleBufferGetSampleAttachmentsArray(self, createIfNecessary: false) as? [[CFString: Any]]
        return (attachments?.first?[kCMSampleAttachmentKey_NotSync] as? Bool) ?? false
    }
}
