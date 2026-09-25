// What a cloud call can fail with. The .NET side has one exception type for "the service said no"
// (HalyardCloudException) and lets everything else (a dropped connection, a timeout, a JSON parse error)
// escape as the framework's own exceptions. Swift's typed throws want one closed set, so here they are
// cases of one enum, and `isServiceError` recovers the line .NET draws: it is true exactly where .NET would
// have thrown HalyardCloudException, which is what decides, for instance, whether a failed restore clears
// the stored refresh token (a rejection does, an outage must not).

import Foundation
import Security

public enum CloudError: Error, Sendable, CustomStringConvertible {
    /// No OAuth client credential in this build. Every account surface degrades on this; LAN play needs none.
    case notConfigured
    /// A call that needs a session was made before one existed (the .NET side's InvalidOperationException).
    case notSignedIn
    /// A caller passed something unusable (the .NET side's ArgumentException).
    case invalidArgument(String)
    /// This Mac could not supply a 16-byte device id, so no client device id can be formed.
    case deviceIDUnavailable(length: Int)
    /// A cloud REST call answered with a failure status.
    case requestFailed(method: String, url: String, status: Int, body: String?)
    /// The token endpoint answered with a failure status.
    case tokenRequestFailed(status: Int, body: String?)
    /// A response arrived and could not be read.
    case invalidResponse(String, body: String?)
    /// The redirect the sign-in landed on carried no authorization code.
    case noAuthorizationCode
    /// The rendezvous gave up, for the reason given.
    case rendezvous(String)
    /// The request never completed: no route, a timeout, a TLS failure. Not the service's answer.
    case transport(URLError.Code, String)
    /// The push WebSocket failed to upgrade or dropped.
    case webSocket(String)
    /// The Keychain refused.
    case keychain(OSStatus)
    /// The calling task was cancelled.
    case cancelled

    /// True where the .NET reference would have thrown HalyardCloudException: the service answered, and the
    /// answer was no (or unreadable). False for anything that means "we never got an answer".
    public var isServiceError: Bool {
        switch self {
        case .notConfigured, .requestFailed, .tokenRequestFailed, .invalidResponse, .noAuthorizationCode, .rendezvous:
            true
        case .notSignedIn, .invalidArgument, .deviceIDUnavailable, .transport, .webSocket, .keychain, .cancelled:
            false
        }
    }

    /// The response body, for diagnostics. It may hold personal data: never log it to a shared sink.
    public var responseBody: String? {
        switch self {
        case .requestFailed(_, _, _, let body), .tokenRequestFailed(_, let body), .invalidResponse(_, let body): body
        default: nil
        }
    }

    public var description: String {
        switch self {
        case .notConfigured:
            "No OAuth client credential is configured, so account sign-in is unavailable in this build. "
                + "LAN play against an already-paired console does not require one."
        case .notSignedIn: "Not signed in - seed the token provider first."
        case .invalidArgument(let why): why
        case .deviceIDUnavailable(let length):
            "This machine did not supply a stable 16-byte device id (got \(length) bytes), so a client device "
                + "id cannot be formed."
        case let .requestFailed(method, url, status, _): "\(method) \(url) failed (\(status))."
        case .tokenRequestFailed(let status, _): "Token request failed (\(status))."
        case .invalidResponse(let what, _): what
        case .noAuthorizationCode: "That redirect carried no authorization code."
        case .rendezvous(let reason): reason
        case let .transport(code, text): "the request did not complete (\(code.rawValue): \(text))"
        case .webSocket(let why): "push connection: \(why)"
        case .keychain(let status):
            "the Keychain refused (\(status): \(SecCopyErrorMessageString(status, nil) as String? ?? "unknown"))"
        case .cancelled: "cancelled"
        }
    }

    /// Folds whatever URLSession or a task threw into this set.
    static func from(_ error: any Error) -> CloudError {
        if let cloud = error as? CloudError { return cloud }
        if error is CancellationError { return .cancelled }
        if let url = error as? URLError {
            return url.code == .cancelled ? .cancelled : .transport(url.code, url.localizedDescription)
        }
        return .transport(.unknown, String(describing: error))
    }
}
