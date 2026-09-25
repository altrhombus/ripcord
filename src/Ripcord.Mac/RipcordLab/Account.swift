// The lab's account-route commands: pairing through the account (no PIN), and the two rendezvous routes of
// `connect`. The counterparts of ProtocolLab's account pairing and wanconnect, over libripcord.
//
// Nothing here runs unless asked: every command needs a stored sign-in (`ripcord-lab signin`), talks to PSN
// and to the console, and is the owner's live verification pass rather than anything a test runs.

import Foundation
import RipcordKit
import Synchronization

let accountUsage = """
      account-pair <duid> [--host <address>]
                            pair with a console through the account, with no PIN. The console must be
                            on (or able to be woken over the internet) and signed in to PSN. Without
                            --host its address comes from a LAN broadcast by name, else from its OFFER.
                            The record is kept with the PIN-paired ones

    """

/// Progress lines from the rendezvous and the 9303 transport, beside the core's own.
let labLog: @Sendable (String) -> Void = { line in FileHandle.standardError.write(Data("  · \(line)\n".utf8)) }

/// A gateway with a live session, or the lab exits saying why.
func signedInGateway(_ command: String) -> (AccountGateway, CloudAccount) {
    let (config, _) = ClientConfigLoader.load(bundledCredential: labCredentialURL())
    guard config.isConfigured else { fail("\(command): \(CloudError.notConfigured)") }
    do {
        let gateway = try AccountGateway(config: config, store: KeychainAccountTokenStore(), deviceID: DeviceIdentity.stable())
        try blocking { try await requireSession(gateway) }
        guard let account = try blocking({ await gateway.account }) else { fail("\(command): \(CloudError.notSignedIn)") }
        return (gateway, account)
    } catch {
        fail("\(command): \(error)")
    }
}

func family(ofPlatform platform: String) -> ConsoleFamily {
    platform.uppercased().contains("PS4") ? .ps4 : .ps5
}

// MARK: - account-pair

func runAccountPair(_ arguments: [String]) -> Never {
    var rest = arguments.dropFirst()
    var duid: String?
    var host = ""
    while let next = rest.popFirst() {
        if next == "--host" {
            guard let value = rest.popFirst() else { fail(usage, code: 2) }
            host = value
        } else if duid == nil {
            duid = next
        } else {
            fail(usage, code: 2)
        }
    }
    guard let duid else { fail(usage, code: 2) }
    let (gateway, account) = signedInGateway("account-pair")

    do {
        let consoles = try blocking { try await gateway.cloud.listConsoles() }
        guard let target = consoles.first(where: { $0.duid == duid }) else {
            fail("account-pair: the account has no console with that duid (see `ripcord-lab cloud-consoles`)")
        }
        let family = family(ofPlatform: target.platform)
        print("pairing with \(target.device.name) (\(family.rawValue)) through the account")

        // The address the record will carry, and the console's own id, from the LAN when it is there.
        var consoleID = ""
        let found = (try? LANDiscovery.search(hosts: host.isEmpty ? [] : [host])) ?? []
        if let match = found.first(where: { host.isEmpty ? $0.name == target.device.name : $0.address == host }) {
            host = match.address
            consoleID = match.hostID
            print("found it on this network at \(host) (\(match.isAwake ? "awake" : "resting"))")
        } else if host.isEmpty {
            print("not found on this network; its address will be taken from its OFFER")
        }

        guard let localAddress = (host.isEmpty ? nil : LocalNetwork.address(toward: host)) ?? LocalNetwork.primaryAddress() else {
            fail("account-pair: this Mac has no IPv4 route")
        }
        let transport = try DatagramAccountTransport(family: family, accountID: account.accountID,
                                                     consoleName: target.device.name, consoleID: consoleID,
                                                     options: .init(log: labLog))
        let request = AccountRendezvousRequest(
            consoleID: host.isEmpty ? duid : host, consoleHost: host, consoleDUID: duid, accountID: account.accountID,
            clientDeviceID: [], family: family,
            localHashedID: try LocalHashedID.make(clientDeviceID: gateway.clientDeviceID),
            localEndpoint: UDPEndpoint(address: localAddress, port: transport.localPort))
        print("control association from \(localAddress):\(transport.localPort)")

        let paired = try blocking {
            let token = try await gateway.accessToken()
            let pushServer = try await gateway.cloud.pushServer()
            let push = PushChannel(socket: URLSessionWebSocketChannel())
            let rendezvous = AccountRendezvous(signaling: gateway.cloud, transport: transport,
                                               options: AccountRendezvousOptions(log: labLog))
            do {
                let paired = try await rendezvous.pair(request, push: push, pushServer: pushServer, accessToken: token)
                await push.close()
                return paired
            } catch {
                await push.close()
                throw error
            }
        }
        try PairingStore.lab.save(paired)
        print("paired with \(paired.name) (\(paired.family.rawValue), \(paired.host)); saved to \(PairingStore.lab.directory.path)")
    } catch {
        fail("account-pair: \(error)")
    }
    exit(0)
}

// MARK: - connect --route account | internet

enum LabRoute: String {
    case local, account, internet
}

/// What the WAN rendezvous found, handed from its task to the lab's top level.
final class CandidateBox: Sendable {
    let value = Mutex<[SignalingCandidate]>([])
}

/// The cloud console a paired record is, for a record that does not carry the duid: by name, or the only
/// remote-play console on the account.
func resolveDUID(for console: PairedConsole, gateway: AccountGateway) -> String {
    do {
        let consoles = try blocking { try await gateway.cloud.listConsoles() }.filter(\.remotePlayEnabled)
        if let match = consoles.first(where: { $0.device.name.caseInsensitiveCompare(console.name) == .orderedSame }) {
            return match.duid
        }
        if consoles.count == 1 { return consoles[0].duid }
        fail("connect: cannot tell which of the account's \(consoles.count) consoles \(console.name) is; pass --duid")
    } catch {
        fail("connect: \(error)")
    }
}

/// The rendezvous route for `connect`, with the cloud prepared now (sign-in, account, push server) so the
/// session thread only waits on signaling. `candidates` receives the console's candidates on `.internet`.
func labRendezvousRoute(_ route: LabRoute, console: PairedConsole, duid duidArgument: String?, registerFirst: Bool,
                        candidates: CandidateBox) -> RendezvousRoute {
    let (gateway, account) = signedInGateway("connect")
    let duid = duidArgument ?? resolveDUID(for: console, gateway: gateway)
    let pushServer: PushServerInfo
    do { pushServer = try blocking { try await gateway.cloud.pushServer() } } catch { fail("connect: \(error)") }
    print("route \(route.rawValue): account signed in, console duid resolved, push server \(pushServer.fqdn)")

    switch route {
    case .account:
        return RendezvousRoute { link in
            let token = try await gateway.accessToken()
            let localAddress = LocalNetwork.address(toward: console.host) ?? LocalNetwork.primaryAddress() ?? "0.0.0.0"
            let request = AccountRendezvousRequest(
                consoleID: console.host, consoleHost: console.host, consoleDUID: duid, accountID: account.accountID,
                clientDeviceID: [], family: console.family,
                localHashedID: try LocalHashedID.make(clientDeviceID: gateway.clientDeviceID),
                localEndpoint: UDPEndpoint(address: localAddress, port: link.controlLeg.localPort),
                reflexiveEndpoint: link.controlLeg.reflexive)
            let rendezvous = AccountRendezvous(signaling: gateway.cloud,
                                               transport: link.accountTransport(family: console.family, accountID: account.accountID),
                                               options: AccountRendezvousOptions(log: labLog))
            let push = PushChannel(socket: URLSessionWebSocketChannel())
            let connection: AccountConnection<PairedConsole>
            do {
                connection = try await rendezvous.connect(request, push: push, pushServer: pushServer, accessToken: token,
                                                          registerFirst: registerFirst)
            } catch {
                await push.close()
                throw error
            }
            if connection.registration != nil {
                labLog("registered on the session's association (the record is not stored, as .NET does not)")
            }
            link.proceed(media: { leg in
                try await connection.negotiateMedia(localPort: leg.localPort, reflexive: leg.reflexive)
            }, onEnd: {
                await connection.lifetime.close()
                await push.close()
                labLog("left the cloud session")
            })
        }

    case .internet, .local:
        return RendezvousRoute { link in
            let token = try await gateway.accessToken()
            let rendezvous = WanRendezvous(signaling: gateway.cloud, transport: link.wanTransport,
                                           options: WanRendezvousOptions(log: labLog))
            let push = PushChannel(socket: URLSessionWebSocketChannel())
            let connection = try await rendezvous.connect(WanRequest(consoleDUID: duid, accountID: account.accountID),
                                                          push: push, pushServer: pushServer, accessToken: token)
            candidates.value.withLock { $0 = connection.consoleCandidates }
            await connection.close()
            link.abandon("the WAN rendezvous ends at the console's candidates, as .NET's wanconnect does: its OFFER "
                         + "carries no localHashedId, so no 9303 association can be tied to it")
        }
    }
}
