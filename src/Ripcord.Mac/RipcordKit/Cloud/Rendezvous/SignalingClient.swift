// The cloud calls a rendezvous makes, as a seam, so the timelines can be tested without PSN. Ported from
// IHalyardSignalingClient.cs; CloudClient is the real implementation (the .NET HalyardCloudSignalingClient
// adapter is an extension here).

import Foundation

public protocol SignalingClient: Sendable {
    /// Creates the account-scoped session and returns its id.
    func createSessionID(pushContextID: String) async throws -> String
    /// Sends the wake/trigger command, which brings the console into the session. Returns the command id.
    @discardableResult
    func sendConnectCommand(consoleDUID: String, accountID: String, sessionID: String, clientType: String,
                            seeds: ConnectSeeds) async throws -> String
    /// POSTs our OFFER. `sid` names which of our connections is being offered.
    func sendOffer(sessionID: String, accountID: String, consoleDUID: String, candidates: [SignalingCandidate],
                   localHashedID: [UInt8], reqID: Int, sid: Int) async throws
    /// Reads one session back, to confirm ours exists and see who has joined.
    func session(id sessionID: String) async throws -> [CloudSession]
    /// Acknowledges a message the peer sent, by its reqId.
    func sendResult(sessionID: String, accountID: String, consoleDUID: String, reqID: Int) async throws
    /// Answers the console's OFFER, naming its stream id as our peerSid.
    func sendAccept(sessionID: String, accountID: String, consoleDUID: String, reqID: Int, sid: Int, peerSid: Int,
                    consoleCandidate: SignalingCandidate, localAddress: String, localPort: Int) async throws
    /// Leaves the session: the disconnect.
    func leaveSession(id sessionID: String) async throws
}

extension CloudClient: SignalingClient {
    public func createSessionID(pushContextID: String) async throws -> String {
        try await createSession(pushContextID: pushContextID).sessionID
    }
}

/// A fresh push context id, as .NET's `Guid.NewGuid().ToString()` writes one: lowercase, with dashes.
func newPushContextID() -> String { UUID().uuidString.lowercased() }
