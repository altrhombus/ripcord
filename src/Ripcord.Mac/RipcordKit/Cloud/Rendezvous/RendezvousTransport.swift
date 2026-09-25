// THE SEAM BETWEEN THE CLOUD TIMELINE AND THE UDP ONE.
//
// Both rendezvous interleave cloud signaling with steps on the client's own UDP sockets, and the order is
// unforgiving: on the account route, our datagram Init must go out after our OFFER POST and before our
// ACCEPT POST, and both neighbouring orderings were falsified against hardware (HalyardAccountPairing.cs,
// RunAsync). The cloud half is Swift, here. The UDP half is libripcord's, and its API is not settled yet, so
// the Swift side calls it through these two protocols and the timelines are tested against fakes of them.
//
// WHAT THE C SIDE MUST PROVIDE is exactly the requirements below; each says which libripcord function (or
// .NET type, where the C one is still being written) it stands for. Nothing else in the rendezvous touches
// a socket or a key.

import Darwin
import Foundation

public struct UDPEndpoint: Sendable, Equatable, Hashable, CustomStringConvertible {
    public var address: String
    public var port: Int

    public init(address: String, port: Int) {
        self.address = address
        self.port = port
    }

    public var description: String { "\(address):\(port)" }
}

// MARK: - The WAN rendezvous (HalyardWanRendezvous)

/// The media socket's side of `WanRendezvous`.
public protocol WanRendezvousTransport: Sendable {
    /// The local UDP port of the socket the stream will run on. Advertised as our `LOCAL` candidate's port.
    var localPort: Int { get }

    /// A STUN binding **on that same socket**, or nil when no server answered. On the socket the stream runs
    /// on, because a NAT binding is per source port: a reflexive address found on any other socket points at
    /// a port the media never uses. (.NET: IReflexiveGatherer.GatherAsync, StunClient with a 500 ms wait per
    /// server. C: net/rc_stun.h.) Must not throw: a failed gather is an offer without that candidate.
    func gatherReflexive() async -> UDPEndpoint?

    /// This host's LAN address, advertised as the `LOCAL` candidate. The default asks the routing table.
    func lanAddress() -> String?
}

extension WanRendezvousTransport {
    public func lanAddress() -> String? { LocalNetwork.primaryAddress() }
}

// MARK: - The account route (HalyardAccountPairing)

/// The two ephemeral 16-byte values the connect command carries: the seed-delivery field key (`data1`) and
/// its material (`data2`). The console encrypts the pairing seed with them and publishes it as customData1.
public struct SeedKeyMaterial: Sendable, Equatable {
    public var data1: [UInt8]
    public var data2: [UInt8]

    public init(data1: [UInt8], data2: [UInt8]) {
        self.data1 = data1
        self.data2 = data2
    }

    var seeds: ConnectSeeds {
        ConnectSeeds(data1: Data(data1).base64EncodedString(), data2: Data(data2).base64EncodedString(), data3: "")
    }
}

/// What the account route's transport needs, known only once the console has offered.
public struct AccountTransportContext: Sendable, Equatable {
    /// The console's OFFER, in full: its candidates, its stream id, the id it names itself by.
    public var consoleOffer: SignalingMessage
    /// The address the caller already had for the console (from LAN discovery, or a WAN candidate).
    public var consoleHost: String
    /// Our own 20-byte signaling id, as our OFFER announces it. The 9303 prelude must name us by this.
    public var localHashedID: [UInt8]
    /// The console's, from its OFFER. The prelude names the console by this.
    public var consoleHashedID: [UInt8]
    /// The candidate our ACCEPT names back to the console, which the transport must also be the one to
    /// speak to: telling the console we chose one path while talking to another is the failure this exists
    /// to rule out. Nil when the console offered none we could parse.
    public var selectedCandidate: SignalingCandidate?

    public init(consoleOffer: SignalingMessage, consoleHost: String, localHashedID: [UInt8], consoleHashedID: [UInt8],
                selectedCandidate: SignalingCandidate?) {
        self.consoleOffer = consoleOffer
        self.consoleHost = consoleHost
        self.localHashedID = localHashedID
        self.consoleHashedID = consoleHashedID
        self.selectedCandidate = selectedCandidate
    }
}

/// The control association's side of `AccountRendezvous`.
public protocol AccountRouteTransport: Sendable {
    /// What a successful `/sess/rgst` produces (a pairing record, on the C side).
    associatedtype Registration: Sendable

    /// Fresh data1 and data2 for one connect command.
    /// C: `halyard_account_regist_generate_key_material` (session/halyard_account_regist_flow.h).
    func makeSeedKeyMaterial() throws -> SeedKeyMaterial

    /// The 16-byte seed from a customData1 value, or nil when it does not open under this key material (a
    /// stray frame, a foreign session, or our own mistake: the rendezvous counts these, because "arrived and
    /// would not open" and "never arrived" send the next person to opposite ends of the stack).
    /// C: `halyard_account_seed_recover_custom_data1` (halyard/halyard_account_seed.h).
    func recoverSeed(customData1: String, keyMaterial: SeedKeyMaterial, family: ConsoleFamily) -> [UInt8]?

    /// OUR DATAGRAM INIT: open the 9303 control association toward `context.selectedCandidate` (or
    /// `context.consoleHost`), with a prelude naming us by `context.localHashedID` and the console by
    /// `context.consoleHashedID`, from the local endpoint the request advertised.
    ///
    /// Called exactly once per rendezvous, at one exact point: after our OFFER POST has returned and before
    /// our ACCEPT POST is sent. It should return once the Init has gone out and the association is being
    /// serviced (the .NET PrepareAsync), not wait for the whole handshake: the ACCEPT is due within about a
    /// second, and the console gives up on us after that. A throw is logged and the rendezvous carries on
    /// (as .NET's does); the finish step then reports that no association was opened.
    /// .NET: HalyardDatagramRegistrationTransport.PrepareAsync. C: being written.
    func openAssociation(_ context: AccountTransportContext) async throws

    /// `/sess/rgst` over the association `openAssociation` opened, with the seed.
    /// .NET: IHalyardRegistration.RegisterAsync. C: `halyard_account_regist_run`, with the association as its
    /// exchange callback.
    func register(context: AccountTransportContext, seed: [UInt8]) async throws -> Registration

    /// Whether `address` is on one of this host's own subnets: how the ACCEPT picks the console's LAN
    /// candidate when there is one. The default reads the interfaces, as
    /// HalyardDatagramRegistrationTransport.SharesSubnetWithLocalInterface does.
    func sharesSubnetWithLocalInterface(_ address: String) -> Bool
}

extension AccountRouteTransport {
    public func sharesSubnetWithLocalInterface(_ address: String) -> Bool {
        LocalNetwork.sharesSubnetWithLocalInterface(address)
    }
}

// MARK: - The host's own addresses

public enum LocalNetwork {
    /// The address the default route would carry a packet from: a connected UDP socket makes the kernel
    /// choose, and sends nothing. The target is RFC 5737 TEST-NET-1, as .NET's, so this depends on no one's
    /// server being reachable.
    public static func primaryAddress() -> String? {
        try? Pairing.localAddress(toward: "192.0.2.1")
    }

    /// This host's address on the interface that routes to `host` (IPv4), the same way. Nil when there is
    /// no route. Sends nothing.
    public static func address(toward host: String) -> String? {
        try? Pairing.localAddress(toward: host)
    }

    /// Whether `address` (IPv4) falls inside the subnet of any interface that is up.
    public static func sharesSubnetWithLocalInterface(_ address: String) -> Bool {
        var target = in_addr()
        guard inet_pton(AF_INET, address, &target) == 1 else { return false }
        var list: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&list) == 0, let first = list else { return false }
        defer { freeifaddrs(list) }

        for entry in sequence(first: first, next: { $0.pointee.ifa_next }) {
            let ifa = entry.pointee
            guard ifa.ifa_flags & UInt32(IFF_UP) != 0, let addr = ifa.ifa_addr, let mask = ifa.ifa_netmask,
                  addr.pointee.sa_family == sa_family_t(AF_INET) else { continue }
            let a = addr.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { $0.pointee.sin_addr.s_addr }
            let m = mask.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { $0.pointee.sin_addr.s_addr }
            if m != 0, a & m == target.s_addr & m { return true }
        }
        return false
    }
}
