// LAN discovery: the SRCH broadcast and its replies (docs/protocol/ps5-local-discovery.md).
//
// The wire format is entirely the engine's (ripcord_discovery_probe builds it, ripcord_discovery_parse reads
// the reply);
// this file is only the socket. It follows the PS3 port's broadcast_find, which is hardware-verified:
// one UDP socket with SO_BROADCAST, the probe to the limited broadcast address on each family's port,
// and every reply fed to the core's parser. One socket serves both families, since a PS5 and a PS4
// answer on different ports and nothing else about the exchange differs.
//
// Synchronous and blocking for now, because its first caller is ripcord-lab. The session actor will
// wrap it rather than change it.

internal import CRipcordEngine
import Darwin

public enum ConsoleFamily: String, Sendable, CaseIterable {
    case ps5 = "PS5"
    case ps4 = "PS4"
}

public struct DiscoveredConsole: Sendable, Hashable {
    public let hostID: String
    /// Nil for a host-type this build does not know, which is reported rather than dropped.
    public let family: ConsoleFamily?
    public let hostType: String
    public let name: String
    public let systemVersion: String
    public let address: String
    /// False for "620 Server Standby": the console is in rest mode, and connecting will wake it.
    public let isAwake: Bool

    public init(hostID: String, family: ConsoleFamily?, hostType: String, name: String, systemVersion: String,
                address: String, isAwake: Bool) {
        self.hostID = hostID
        self.family = family
        self.hostType = hostType
        self.name = name
        self.systemVersion = systemVersion
        self.address = address
        self.isAwake = isAwake
    }
}

public enum DiscoveryError: Error, Sendable {
    case socket(operation: String, errno: Int32)
}

public enum LANDiscovery {
    /// Searches for consoles until `timeout` has passed, or until every one of `hosts` has answered.
    ///
    /// **Broadcast or direct.** With no `hosts` the probe is broadcast. With hosts, it goes to each of
    /// them directly, which is the only thing that works on a network that filters broadcast between segments
    /// (Wi-Fi to wired, commonly). Measured 2026-09-25 (journal, "The Mac streams"): a PS5 answered every
    /// unicast probe and never one broadcast, limited or subnet-directed. The dotnet client's known-address
    /// probe broadcasts and filters by address, so it would not find that console either.
    ///
    /// **The probe is repeated**, every `resendInterval`, for the whole window. A console that has been
    /// resting a while answered its first probe slowly (2.05 s) and then in about 120 ms, in one run, so the
    /// figures are [X]; and one three-second run with a single probe heard nothing at all. Repeating it
    /// covers a lost datagram and a slow wake alike, and costs 64 bytes a time. Replies are de-duplicated by
    /// host id.
    public static let defaultTimeout: Duration = .seconds(3)
    public static let resendInterval: Duration = .milliseconds(500)

    public static func search(families: [ConsoleFamily] = ConsoleFamily.allCases, hosts: [String] = [],
                              timeout: Duration = defaultTimeout) throws(DiscoveryError) -> [DiscoveredConsole] {
        let sock = socket(PF_INET, SOCK_DGRAM, IPPROTO_UDP)
        guard sock >= 0 else { throw .socket(operation: "socket", errno: errno) }
        defer { close(sock) }

        var on: Int32 = 1
        guard setsockopt(sock, SOL_SOCKET, SO_BROADCAST, &on, socklen_t(MemoryLayout<Int32>.size)) == 0 else {
            throw .socket(operation: "setsockopt(SO_BROADCAST)", errno: errno)
        }

        // Every (probe, destination) pair, built once.
        var datagrams: [(probe: [UInt8], length: Int, destination: sockaddr_in)] = []
        for family in families {
            var probe = [UInt8](repeating: 0, count: 128)
            var length = 0
            var port: UInt16 = 0
            guard ripcord_discovery_probe(family == .ps5, &probe, probe.count, &length, &port) == RIPCORD_STATUS_OK
            else { continue }
            if hosts.isEmpty {
                datagrams.append((probe, length, .ipv4(broadcastPort: port)))
            } else {
                for host in hosts {
                    guard let address = sockaddr_in.ipv4(host, port: port) else {
                        throw .socket(operation: "inet_pton(\(host))", errno: EINVAL)
                    }
                    datagrams.append((probe, length, address))
                }
            }
        }

        func sendAll() throws(DiscoveryError) {
            for var d in datagrams {
                let sent = withUnsafePointer(to: &d.destination) { address in
                    address.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                        sendto(sock, d.probe, d.length, 0, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
                    }
                }
                guard sent >= 0 else { throw .socket(operation: "sendto", errno: errno) }
            }
        }

        var found: [String: DiscoveredConsole] = [:]
        var answered = Set<String>()
        let deadline = ContinuousClock.now + timeout
        var nextSend = ContinuousClock.now
        var buffer = [UInt8](repeating: 0, count: 2048)

        while ContinuousClock.now < deadline {
            if !hosts.isEmpty && answered.isSuperset(of: hosts) { break }
            if ContinuousClock.now >= nextSend {
                try sendAll()
                nextSend = ContinuousClock.now + resendInterval
            }

            let wake = min(deadline, nextSend) - ContinuousClock.now
            var descriptor = pollfd(fd: sock, events: Int16(POLLIN), revents: 0)
            let ready = poll(&descriptor, 1, Int32(max(1, wake.milliseconds)))
            if ready < 0 {
                if errno == EINTR { continue }
                throw .socket(operation: "poll", errno: errno)
            }
            if ready == 0 { continue }

            var from = sockaddr_in()
            var fromLength = socklen_t(MemoryLayout<sockaddr_in>.size)
            let received = withUnsafeMutablePointer(to: &from) { address in
                address.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                    recvfrom(sock, &buffer, buffer.count - 1, 0, $0, &fromLength)
                }
            }
            guard received > 0 else { continue }

            var console = RipcordDiscoveredConsole()
            var sender = [CChar](repeating: 0, count: Int(INET_ADDRSTRLEN))
            inet_ntop(AF_INET, &from.sin_addr, &sender, socklen_t(sender.count))
            guard ripcord_discovery_parse(buffer, received, &console) == RIPCORD_STATUS_OK else { continue }

            let reply = DiscoveredConsole(console, address: String(decoding: sender.prefix(while: { $0 != 0 }).map { UInt8(bitPattern: $0) }, as: UTF8.self))
            found[reply.hostID] = reply
            answered.insert(reply.address)
        }
        return found.values.sorted { ($0.name, $0.address) < ($1.name, $1.address) }
    }
}

private extension DiscoveredConsole {
    init(_ c: RipcordDiscoveredConsole, address: String) {
        let hostType = cString(c.host_type)
        self.init(hostID: cString(c.host_id), family: ConsoleFamily(rawValue: hostType), hostType: hostType,
                  name: cString(c.host_name), systemVersion: cString(c.system_version),
                  address: address, isAwake: c.is_awake)
    }
}

/// A C `char name[N]` field, imported as a tuple, read as the NUL-terminated string it holds. The engine
/// always terminates these; the bound is the field's own size regardless.
func cString<T>(_ field: T) -> String {
    withUnsafeBytes(of: field) { raw in
        let bytes = raw.prefix(while: { $0 != 0 })
        return String(decoding: bytes, as: UTF8.self)
    }
}

extension sockaddr_in {
    static func ipv4(_ dottedQuad: String, port: UInt16) -> sockaddr_in? {
        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = port.bigEndian
        guard inet_pton(AF_INET, dottedQuad, &address.sin_addr) == 1 else { return nil }
        return address
    }

    static func ipv4(broadcastPort port: UInt16) -> sockaddr_in {
        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = port.bigEndian
        address.sin_addr.s_addr = 0xFFFF_FFFF  // 255.255.255.255, the same in either byte order
        return address
    }
}

private extension Duration {
    var milliseconds: Int64 {
        let (seconds, attoseconds) = components
        return seconds * 1000 + attoseconds / 1_000_000_000_000_000
    }
}
