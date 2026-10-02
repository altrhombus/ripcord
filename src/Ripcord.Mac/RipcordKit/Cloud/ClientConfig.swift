// The OAuth client identity Ripcord authenticates as, and where it comes from. Ported from
// HalyardClientConfig.cs, HalyardClientConfigFile.cs and HalyardBundledClient.cs.
//
// WHAT THE BUNDLED CREDENTIAL IS. It is the vendor desktop client's own client id and secret, recovered from
// this project's own capture of its own traffic (CLAUDE.md, "Bounded exception 2", and NOTICE, which argues
// it on its own grounds). It is an access credential, not a protocol value: a LAN session works without it.
// It is generic, tied to no account or console.
//
// ONE FILE. The Mac reads the same committed file the .NET build embeds,
// src/Ripcord.Cloud.Halyard/Data/halyard-oauth-client.json, copied into the app bundle's resources at build
// time. There is no second copy in the tree, and nothing in Swift source holds its value: it is only ever
// read from that file. A build that omits it (the Mac's equivalent of -p:BundleOAuthClient=false) simply has
// no resource, and every account surface then says sign-in is unavailable.
//
// PRECEDENCE, as .NET: the environment, then client.json in the configuration directory, then the bundled
// file. The bundle is last on purpose, so anyone with their own registered client can override what shipped
// without rebuilding.

import Foundation

public struct ClientConfig: Sendable, Equatable {
    public var clientID: String
    public var clientSecret: String
    public var redirectURI: String
    public var scopes: [String]

    public init(clientID: String, clientSecret: String, redirectURI: String = ClientConfig.defaultRedirectURI,
                scopes: [String] = ClientConfig.defaultScopes) {
        self.clientID = clientID
        self.clientSecret = clientSecret
        self.redirectURI = redirectURI
        self.scopes = scopes
    }

    /// The redirect the account web flow sends the code to. The credential is registered against this exact
    /// URL, so it is not ours to choose; it is recorded in docs/protocol/ps5-cloud-session-api.md.
    public static let defaultRedirectURI = "https://remoteplay.dl.playstation.net/remoteplay/redirect"

    /// Identifies the client as a desktop app in the authorize and token calls. A required wire value.
    public static let deviceType = "PC_APP"

    /// The scopes observed on the official flow. Required on-wire tokens.
    public static let defaultScopes = [
        "psn:clientapp",
        "referenceDataService:countryConfig.read",
        "pushNotification:webSocket.desktop.connect",
        "sessionManager:remotePlaySession.system.update",
        "sbahn:pc.telemetry.publish",
    ]

    /// No credential, the standard redirect and scopes: what a build without the bundle resolves to.
    public static let unconfigured = ClientConfig(clientID: "", clientSecret: "")

    /// Whether sign-in is possible at all. Asked before any surface offers it, so the failure is a sentence
    /// up front rather than a 401 after the user has typed a password.
    public var isConfigured: Bool {
        !clientID.isBlank && !clientSecret.isBlank && !redirectURI.isBlank
    }

    public var scopeString: String { scopes.joined(separator: " ") }

    /// `RIPCORD_CLIENT_ID`, `RIPCORD_CLIENT_SECRET` and, optionally, `RIPCORD_REDIRECT_URI`.
    public static func fromEnvironment(_ environment: [String: String] = ProcessInfo.processInfo.environment) -> ClientConfig {
        ClientConfig(clientID: environment["RIPCORD_CLIENT_ID"] ?? "",
                     clientSecret: environment["RIPCORD_CLIENT_SECRET"] ?? "",
                     redirectURI: environment["RIPCORD_REDIRECT_URI"] ?? defaultRedirectURI)
    }
}

/// Where a loaded credential came from, so a settings surface can say so honestly.
public enum ClientCredentialSource: Sendable, Equatable {
    case environment
    case configFile(URL)
    case bundled(URL)
    case none
}

public enum ClientConfigLoader {
    public static let fileName = "client.json"

    /// The bundled file's name in the app's resources. It is the committed file's own name, because it IS
    /// the committed file, copied there by a build phase.
    public static let bundledResourceName = "halyard-oauth-client"
    public static let bundledResourceExtension = "json"

    /// `~/Library/Application Support/Ripcord`, which is where the .NET DefaultPlatformPaths puts the
    /// configuration directory on macOS, so a `client.json` placed for one is found by the other.
    public static var defaultConfigDirectory: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appending(path: "Ripcord", directoryHint: .isDirectory)
    }

    /// The bundled credential in `bundle`'s resources, or nil when this build omitted it.
    ///
    /// `RIPCORD_OMIT_OAUTH_CLIENT` (a Swift compilation condition) makes this nil whatever the bundle holds.
    /// The build phase is the real switch; this is a second one, because an incremental build does not
    /// delete a resource an earlier build copied, and the edition that must not carry the credential is the
    /// one where a stale copy would matter.
    public static func bundledCredentialURL(in bundle: Bundle = .main) -> URL? {
        #if RIPCORD_OMIT_OAUTH_CLIENT
        return nil
        #else
        return bundle.url(forResource: bundledResourceName, withExtension: bundledResourceExtension)
        #endif
    }

    /// Environment, then `client.json`, then the bundled file, then nothing.
    ///
    /// - Parameter bundledCredential: the bundled file. Defaults to the app bundle's copy. A command-line
    ///   tool, which has no resources, passes the committed file's path in the checkout instead: the same
    ///   one file, read where it lives.
    public static func load(environment: [String: String] = ProcessInfo.processInfo.environment,
                            configDirectory: URL = defaultConfigDirectory,
                            bundledCredential: URL? = bundledCredentialURL()) -> (config: ClientConfig, source: ClientCredentialSource) {
        let fromEnvironment = ClientConfig.fromEnvironment(environment)
        if fromEnvironment.isConfigured { return (fromEnvironment, .environment) }

        let file = configDirectory.appending(path: fileName)
        guard FileManager.default.fileExists(atPath: file.path) else {
            guard let bundledCredential else { return (.unconfigured, .none) }
            let bundled = readBundled(at: bundledCredential)
            return bundled.isConfigured ? (bundled, .bundled(bundledCredential)) : (.unconfigured, .none)
        }

        // As .NET: a client.json that exists is authoritative, even when it is malformed or empty. It does
        // not fall through to the bundle, because a user who wrote one meant to replace what shipped.
        guard let data = try? Data(contentsOf: file),
              let persisted = try? JSONDecoder().decode(PersistedClientConfig.self, from: data) else {
            return (.unconfigured, .configFile(file))
        }
        let redirect = persisted.redirectUri.flatMap { $0.isBlank ? nil : $0 } ?? ClientConfig.defaultRedirectURI
        return (ClientConfig(clientID: persisted.clientId ?? "", clientSecret: persisted.clientSecret ?? "",
                             redirectURI: redirect), .configFile(file))
    }

    /// Reads a bundled credential file. Anything short of both values present is "unconfigured", never half
    /// a credential: an id with no secret would pass a nil check and fail at the token endpoint with a 401
    /// that says nothing about the cause.
    public static func readBundled(at url: URL) -> ClientConfig {
        guard let data = try? Data(contentsOf: url),
              let parsed = try? JSONDecoder().decode(PersistedClientConfig.self, from: data),
              let id = parsed.clientId, !id.isBlank,
              let secret = parsed.clientSecret, !secret.isBlank else {
            return .unconfigured
        }
        return ClientConfig(clientID: id, clientSecret: secret)
    }

    /// Writes a credential for a settings surface that lets the user supply one, readable by its owner only.
    /// Returns where it went.
    @discardableResult
    public static func save(_ config: ClientConfig, configDirectory: URL = defaultConfigDirectory) throws -> URL {
        try FileManager.default.createDirectory(at: configDirectory, withIntermediateDirectories: true)
        let file = configDirectory.appending(path: fileName)
        let persisted = PersistedClientConfig(clientId: config.clientID, clientSecret: config.clientSecret,
                                              redirectUri: config.redirectURI)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        try encoder.encode(persisted).write(to: file, options: [.atomic])
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: file.path)
        return file
    }

    /// The shape of both files: `client.json` and the bundled one. Keys are the .NET JsonPropertyName values.
    private struct PersistedClientConfig: Codable {
        var clientId: String?
        var clientSecret: String?
        var redirectUri: String?
    }
}

extension String {
    /// `string.IsNullOrWhiteSpace`, for a non-optional.
    var isBlank: Bool { allSatisfy(\.isWhitespace) }
}
