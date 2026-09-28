// The cloud side of connecting without the rendezvous, and cloud wake. Ported from
// HalyardSessionCoordinator.cs and HalyardCloudDiscoveryService.cs.

import Foundation

/// A live cloud session begun by `SessionCoordinator.begin`. `data1`/`data2` are the ephemeral values the
/// connect command carried, kept because the console encrypts the account seed with them.
public struct ConnectHandle: Sendable, Equatable {
    public var sessionID: String
    public var consoleDUID: String
    public var accountID: String
    public var data1: String
    public var data2: String
}

public struct SessionCoordinator: Sendable {
    public let cloud: CloudClient

    public init(cloud: CloudClient) { self.cloud = cloud }

    /// Creates the session, sends the command, and offers `localCandidates`. The simplified path: it does not
    /// run the push channel, so it never hears the console's answer; the rendezvous types do that.
    public func begin(console: CloudConsole, localCandidates: [SignalingCandidate]) async throws(CloudError) -> ConnectHandle {
        let account = try await cloud.accountInfo()
        let session = try await cloud.createSession(pushContextID: newPushContextID())
        let seeds = ConnectSeeds.random()
        try await cloud.sendConnectCommand(consoleDUID: console.duid, accountID: account.accountID,
                                           sessionID: session.sessionID, clientType: "Windows",
                                           seeds: ConnectSeeds(data1: seeds.data1, data2: seeds.data2, data3: ""))
        try await cloud.sendOffer(sessionID: session.sessionID, accountID: account.accountID,
                                  consoleDUID: console.duid, candidates: localCandidates)
        return ConnectHandle(sessionID: session.sessionID, consoleDUID: console.duid, accountID: account.accountID,
                             data1: seeds.data1, data2: seeds.data2)
    }

    public func end(_ handle: ConnectHandle) async throws(CloudError) {
        try await cloud.leaveSession(id: handle.sessionID)
    }

    /// Wakes a console without going on to connect: create a session and send the command, then stop.
    ///
    /// The command reaches the console through PSN's own fan-out, not any path we hold open, so this works
    /// without the push channel, and it is the only way to wake a console that is not on this network (the
    /// LAN wake is a broadcast that will not leave the subnet). It returns once PSN has accepted the command,
    /// which is not the console being awake: that is answered by probing it, as on the LAN.
    ///
    /// The session is left behind, as .NET leaves it: [X] whether leaving at once would cancel the wake.
    public func wake(consoleDUID: String) async throws(CloudError) {
        guard !consoleDUID.isBlank else { throw .invalidArgument("consoleDuid must not be blank") }
        let account = try await cloud.accountInfo()
        let session = try await cloud.createSession(pushContextID: newPushContextID())
        try await cloud.sendConnectCommand(consoleDUID: consoleDUID, accountID: account.accountID,
                                           sessionID: session.sessionID, clientType: "Windows", seeds: .random())
    }
}

/// A console on the account, as cloud discovery reports it. Reachable off the LAN and wake-capable, but with
/// no address: that comes from LAN discovery, merged by name.
public struct CloudDiscoveredConsole: Sendable, Equatable {
    public var duid: String
    public var name: String
    /// Whether the account service says it can be woken. Its actual state is confirmed on connect.
    public var canWake: Bool
}

public enum CloudDiscovery {
    /// The account's consoles that have remote play enabled.
    public static func consoles(_ cloud: CloudClient) async throws(CloudError) -> [CloudDiscoveredConsole] {
        try await cloud.listConsoles()
            .filter(\.remotePlayEnabled)
            .map { CloudDiscoveredConsole(duid: $0.duid, name: $0.device.name, canWake: $0.canWake) }
    }
}
