// Pairing through the account, with no PIN: the whole flow the lab's `account-pair` ran, moved here so the
// app and the lab run one copy of it. The counterpart of ProtocolLab's account pairing.
//
// The console's LAN address and id come from discovery when it is on this network, and otherwise from its
// OFFER. Nothing is stored here: the caller decides where the record goes.

import Foundation

public enum AccountPairing {
    /// The consoles on the account that can be paired with: remote play on.
    public static func pairableConsoles(_ gateway: AccountGateway) async throws -> [CloudConsole] {
        try await gateway.cloud.listConsoles().filter(\.remotePlayEnabled)
    }

    public static func family(ofPlatform platform: String) -> ConsoleFamily {
        platform.uppercased().contains("PS4") ? .ps4 : .ps5
    }

    /// Pairs with `console` through the signed-in account. `host`, when known, is where the console is on
    /// this network; without it, a broadcast looks for the console by name. Blocks in discovery for up to
    /// its timeout, so call it off the main actor.
    public static func pair(_ console: CloudConsole, gateway: AccountGateway, host knownHost: String? = nil,
                            log: @escaping @Sendable (String) -> Void = { _ in }) async throws -> PairedConsole {
        guard let account = await gateway.account else { throw CloudError.notSignedIn }
        let family = family(ofPlatform: console.platform)
        var host = knownHost ?? ""

        var consoleID = ""
        let found = (try? LANDiscovery.search(hosts: host.isEmpty ? [] : [host])) ?? []
        if let match = found.first(where: { host.isEmpty ? $0.name == console.device.name : $0.address == host }) {
            host = match.address
            consoleID = match.hostID
            log("found \(console.device.name) on this network at \(host) (\(match.isAwake ? "awake" : "resting"))")
        } else if host.isEmpty {
            log("\(console.device.name) is not on this network; its address will be taken from its OFFER")
        }

        guard let localAddress = (host.isEmpty ? nil : LocalNetwork.address(toward: host)) ?? LocalNetwork.primaryAddress() else {
            throw PairingError.noRouteToConsole(errno: ENETUNREACH)
        }
        let transport = try DatagramAccountTransport(family: family, accountID: account.accountID,
                                                     consoleName: console.device.name, consoleID: consoleID,
                                                     options: .init(log: log))
        let request = AccountRendezvousRequest(
            consoleID: host.isEmpty ? console.duid : host, consoleHost: host, consoleDUID: console.duid,
            accountID: account.accountID, clientDeviceID: [], family: family,
            localHashedID: try LocalHashedID.make(clientDeviceID: gateway.clientDeviceID),
            localEndpoint: UDPEndpoint(address: localAddress, port: transport.localPort))
        log("control association from \(localAddress):\(transport.localPort)")

        let token = try await gateway.accessToken()
        let pushServer = try await gateway.cloud.pushServer()
        let push = PushChannel(socket: URLSessionWebSocketChannel())
        let rendezvous = AccountRendezvous(signaling: gateway.cloud, transport: transport,
                                           options: AccountRendezvousOptions(log: log))
        do {
            let paired = try await rendezvous.pair(request, push: push, pushServer: pushServer, accessToken: token)
            await push.close()
            return paired
        } catch {
            await push.close()
            throw error
        }
    }
}
