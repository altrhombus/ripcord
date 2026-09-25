// Waking a resting console on the LAN, and waiting for it.
//
// The datagram is libripcord's (halyard_wake.h derives the credential from the pairing record's
// registration key and builds the payload); this is the socket, ported from the PS3's send_wakeup
// (rc_connect.c), which is hardware-verified. Two of its lessons are kept exactly:
//
//   - The source port. A sleeping console may only honour a wake that comes from the vendor's own
//     source port, so it is bound where possible and falls back to ephemeral rather than failing: a
//     wake from the wrong port is more likely to work than no wake at all.
//   - No acknowledgement. Nothing answers a WAKEUP, so the socket closes at once, and "awake" is learned
//     only by probing until the console reports it (discovery's 200 Ok instead of 620 Server Standby).
//     The PS3 measured 12.3 s from wake to answering (b36), so the default budget is its 30 s.

internal import CLibripcord
import Darwin

public enum WakeError: Error, Sendable, CustomStringConvertible {
    case noCredential
    case socket(operation: String, errno: Int32)
    case stillResting(after: Duration)

    public var description: String {
        switch self {
        case .noCredential: "the pairing record has no usable registration key to wake with"
        case let .socket(op, e): "\(op) failed: \(String(cString: strerror(e)))"
        case .stillResting(let d): "the console did not wake within \(d)"
        }
    }
}

public enum LANWake {
    /// Sends one WAKEUP. Returns whether it went out from the vendor's source port.
    @discardableResult
    public static func send(to console: PairedConsole) throws(WakeError) -> Bool {
        var record = console.record
        var credential = [CChar](repeating: 0, count: Int(HALYARD_WAKE_CREDENTIAL_MAX))
        guard withUnsafeBytes(of: &record.registkey, { key in
            halyard_wake_credential(key.baseAddress!.assumingMemoryBound(to: UInt8.self), record.registkey_length,
                                    &credential, credential.count)
        }) == 1 else { throw .noCredential }

        var profile = console.family == .ps5 ? halyard_discovery_profile_ps5 : halyard_discovery_profile_ps4
        var payload = [CChar](repeating: 0, count: 256)
        let length = halyard_wake_build_payload(&profile, credential, &payload, payload.count)
        guard length > 0 else { throw .noCredential }

        let sock = socket(PF_INET, SOCK_DGRAM, IPPROTO_UDP)
        guard sock >= 0 else { throw .socket(operation: "socket", errno: errno) }
        defer { close(sock) }

        var local = sockaddr_in()
        local.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        local.sin_family = sa_family_t(AF_INET)
        local.sin_port = profile.wake_source_port.bigEndian
        var boundSourcePort = bindSocket(sock, &local)
        if !boundSourcePort {
            local.sin_port = 0
            _ = bindSocket(sock, &local)
            boundSourcePort = false
        }

        guard var peer = sockaddr_in.ipv4(console.host, port: profile.port) else {
            throw .socket(operation: "inet_pton(\(console.host))", errno: EINVAL)
        }
        let sent = withUnsafePointer(to: &peer) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                sendto(sock, payload, length, 0, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        guard sent >= 0 else { throw .socket(operation: "sendto", errno: errno) }
        return boundSourcePort && profile.wake_source_port != 0
    }

    /// Wakes the console if it is resting, and waits until it reports awake. Returns at once if it
    /// already is. The WAKEUP is resent every few seconds, since nothing confirms one arrived.
    public static func wakeIfResting(_ console: PairedConsole, timeout: Duration = .seconds(30),
                                     progress: @Sendable (Duration) -> Void = { _ in }) throws(WakeError) {
        let started = ContinuousClock.now
        var lastWake = ContinuousClock.now - .seconds(10)
        while ContinuousClock.now - started < timeout {
            let answer = (try? LANDiscovery.search(families: [console.family], hosts: [console.host],
                                                   timeout: .milliseconds(800))) ?? []
            if answer.first?.isAwake == true { return }
            if ContinuousClock.now - lastWake >= .seconds(5) {
                try send(to: console)
                lastWake = ContinuousClock.now
            }
            progress(ContinuousClock.now - started)
        }
        throw .stillResting(after: timeout)
    }

    private static func bindSocket(_ sock: Int32, _ address: inout sockaddr_in) -> Bool {
        withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                bind(sock, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) == 0
            }
        }
    }
}
