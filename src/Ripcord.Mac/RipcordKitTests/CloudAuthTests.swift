// The sign-in mechanics that need no network: the client configuration and where it comes from, the
// authorize URL, reading the code back out of the redirect, the device ids, and what survives a restart.
// Ported case for case from tests/Ripcord.Protocol.Halyard.Tests/HalyardAccountAuthTests.cs, with the
// byte-level expectations taken from the .NET code's own output for the same inputs.

import Foundation
@testable import RipcordKit
import Testing

@Suite("Cloud: client configuration")
struct ClientConfigTests {
    @Test("isConfigured needs all three parts", arguments: [
        ("", "secret", "https://x/y", false),
        ("id", "", "https://x/y", false),
        ("id", "secret", "", false),
        ("  ", "secret", "https://x/y", false),
        ("id", "secret", "https://x/y", true),
    ])
    func isConfigured(id: String, secret: String, redirect: String, expected: Bool) {
        #expect(ClientConfig(clientID: id, clientSecret: secret, redirectURI: redirect).isConfigured == expected)
    }

    @Test("the unconfigured value reports itself as unusable, with the standard redirect")
    func unconfigured() {
        #expect(!ClientConfig.unconfigured.isConfigured)
        #expect(ClientConfig.unconfigured.redirectURI == ClientConfig.defaultRedirectURI)
    }

    /// The one committed file, read where it lives. Shape, never value, as the .NET test says: asserting
    /// the literal would copy a secret into a second committed file.
    static let committedCredential = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        .appending(path: "Ripcord.Cloud.Halyard/Data/halyard-oauth-client.json")

    @Test("the committed credential is absent or well formed, never half populated")
    func bundledShape() {
        let bundled = ClientConfigLoader.readBundled(at: Self.committedCredential)
        guard bundled.isConfigured else { return }   // an emptied file is legitimate; half of one is not
        #expect(!bundled.clientID.isBlank)
        #expect(!bundled.clientSecret.isBlank)
        #expect(bundled.redirectURI == ClientConfig.defaultRedirectURI)
        #expect(bundled.scopes == ClientConfig.defaultScopes)
    }

    @Test("the committed credential carries no user or console material")
    func bundledCarriesNothingPersonal() throws {
        let raw = try String(contentsOf: Self.committedCredential, encoding: .utf8)
        for forbidden in ["refreshToken", "refresh_token", "accessToken", "access_token", "accountId", "account_id",
                          "npAccountId", "np_account_id", "duid", "deviceId", "device_id", "onlineId", "online_id",
                          "registkey", "regist_key", "RP-Key", "AP-Bssid", "AP-Name", "mac", "seed", "skey"] {
            #expect(raw.range(of: forbidden, options: .caseInsensitive) == nil, "found \(forbidden)")
        }
    }

    @Test("a missing bundle resolves to unconfigured, which is the omitted-credential build")
    func omittedBundle() throws {
        let empty = try temporaryDirectory()
        let (config, source) = ClientConfigLoader.load(environment: [:], configDirectory: empty, bundledCredential: nil)
        #expect(!config.isConfigured)
        #expect(source == .none)
    }

    @Test("the environment wins over client.json and the bundle")
    func environmentFirst() throws {
        let directory = try temporaryDirectory()
        try ClientConfigLoader.save(ClientConfig(clientID: "file-id", clientSecret: "file-secret"), configDirectory: directory)
        let (config, source) = ClientConfigLoader.load(
            environment: ["RIPCORD_CLIENT_ID": "env-id", "RIPCORD_CLIENT_SECRET": "env-secret"],
            configDirectory: directory, bundledCredential: Self.committedCredential)
        #expect(config.clientID == "env-id")
        #expect(config.clientSecret == "env-secret")
        #expect(source == .environment)
    }

    @Test("client.json wins over the bundle, and a half-set environment does not count")
    func fileSecond() throws {
        let directory = try temporaryDirectory()
        try ClientConfigLoader.save(ClientConfig(clientID: "file-id", clientSecret: "file-secret"), configDirectory: directory)
        let (config, source) = ClientConfigLoader.load(environment: ["RIPCORD_CLIENT_ID": "only-an-id"],
                                                       configDirectory: directory,
                                                       bundledCredential: Self.committedCredential)
        #expect(config.clientID == "file-id")
        #expect(source == .configFile(directory.appending(path: "client.json")))
    }

    @Test("a malformed client.json means unconfigured, not the bundle")
    func malformedFile() throws {
        let directory = try temporaryDirectory()
        try Data("not json".utf8).write(to: directory.appending(path: "client.json"))
        let (config, _) = ClientConfigLoader.load(environment: [:], configDirectory: directory,
                                                  bundledCredential: Self.committedCredential)
        #expect(!config.isConfigured)
    }

    @Test("with no override, the bundled file is used")
    func bundledLast() throws {
        let (config, source) = ClientConfigLoader.load(environment: [:], configDirectory: try temporaryDirectory(),
                                                       bundledCredential: Self.committedCredential)
        #expect(config == ClientConfigLoader.readBundled(at: Self.committedCredential))
        if config.isConfigured { #expect(source == .bundled(Self.committedCredential)) }
    }

    @Test("a written client.json is readable by its owner only")
    func savedFileMode() throws {
        let file = try ClientConfigLoader.save(testConfig, configDirectory: try temporaryDirectory())
        let mode = try FileManager.default.attributesOfItem(atPath: file.path)[.posixPermissions] as? Int
        #expect(mode == 0o600)
    }
}

@Suite("Cloud: authorize URL and redirect")
struct AuthorizeTests {
    let auth = AuthClient(session: StubURLProtocol.session(), config: testConfig)
    var duid: String { ClientDeviceID.prefix + syntheticDeviceIDHex }

    @Test("the authorize URL is .NET's, byte for byte")
    func authorizeURLBytes() throws {
        let url = try auth.authorizeURL(clientDeviceID: duid, locale: "en_US")
        // HttpUtility's encoding: lowercase %xx, a space as +. Captured from the .NET code for these inputs.
        let expected = CloudEndpoints.authorize + "?service_entity=urn%3aservice-entity%3apsn&response_type=code"
            + "&client_id=test-client"
            + "&redirect_uri=https%3a%2f%2fremoteplay.dl.playstation.net%2fremoteplay%2fredirect"
            + "&scope=psn%3aclientapp+referenceDataService%3acountryConfig.read"
            + "+pushNotification%3awebSocket.desktop.connect+sessionManager%3aremotePlaySession.system.update"
            + "+sbahn%3apc.telemetry.publish&device_type=PC_APP&duid=" + duid
            + "&smcid=remoteplay&ui=pr&prompt=always&request_locale=en_US"
        #expect(url.absoluteString == expected)
    }

    @Test("the authorize URL carries the device identity the token exchange will require")
    func authorizeCarriesDevice() throws {
        let items = try query(auth.authorizeURL(clientDeviceID: duid))
        #expect(items["duid"] == duid)
        #expect(items["device_type"] == ClientConfig.deviceType)
        #expect(items["response_type"] == "code")
        #expect(items["client_id"] == "test-client")
        #expect(items["redirect_uri"] == ClientConfig.defaultRedirectURI)
    }

    @Test("the authorize URL requests exactly the documented scopes")
    func scopes() throws {
        let raw = try #require(URLComponents(url: auth.authorizeURL(clientDeviceID: duid), resolvingAgainstBaseURL: false)?
            .percentEncodedQueryItems?.first { $0.name == "scope" }?.value)
        #expect(raw.replacingOccurrences(of: "+", with: " ").removingPercentEncoding == ClientConfig.defaultScopes.joined(separator: " "))
    }

    @Test("the request locale is the first preferred language, as language_REGION")
    func locale() {
        #expect(AuthClient.requestLocale(preferred: ["en-GB", "fr-FR"]) == "en_GB")
        #expect(AuthClient.requestLocale(preferred: []) == "en_US")
    }

    @Test("an authorize URL without a device id throws")
    func noDeviceID() {
        #expect(throws: CloudError.self) { try auth.authorizeURL(clientDeviceID: "  ") }
    }

    @Test("a redirect with a code is recognised")
    func redirectWithCode() {
        #expect(auth.redirectMatcher.authorizationCode(in: URL(string: ClientConfig.defaultRedirectURI + "?code=abc123&cid=some-uuid")!) == "abc123")
    }

    @Test("matching ignores extra query parameters")
    func extraParameters() {
        #expect(auth.redirectMatcher.authorizationCode(in: URL(string: ClientConfig.defaultRedirectURI + "?cid=x&code=abc&extra=1")!) != nil)
    }

    @Test("matching tolerates a trailing slash")
    func trailingSlash() {
        #expect(auth.redirectMatcher.authorizationCode(in: URL(string: ClientConfig.defaultRedirectURI + "/?code=abc")!) == "abc")
    }

    @Test("URLs that are not the completion are not mistaken for it", arguments: [
        "https://my.account.sony.com/signin?code=abc",           // a different host mid-flow
        "https://remoteplay.dl.playstation.net/other?code=abc",  // right host, wrong path
        ClientConfig.defaultRedirectURI,                         // the redirect, no code yet
        ClientConfig.defaultRedirectURI + "?code=",              // present but empty
    ])
    func notCompletion(_ url: String) {
        #expect(auth.redirectMatcher.authorizationCode(in: URL(string: url)!) == nil)
    }

    private func query(_ url: URL) -> [String: String] {
        var out: [String: String] = [:]
        let raw = url.query(percentEncoded: true) ?? ""
        for pair in raw.split(separator: "&") {
            let parts = pair.split(separator: "=", maxSplits: 1).map(String.init)
            let decode = { (s: String) in s.replacingOccurrences(of: "+", with: " ").removingPercentEncoding ?? s }
            out[decode(parts[0])] = parts.count > 1 ? decode(parts[1]) : ""
        }
        return out
    }
}

@Suite("Cloud: device ids")
struct DeviceIDTests {
    @Test("the client device id is the prefix followed by the machine id")
    func duid() throws {
        let duid = try ClientDeviceID.make(from: syntheticDeviceID)
        #expect(duid.count == ClientDeviceID.hexLength)
        #expect(duid.hasPrefix(ClientDeviceID.prefix))
        #expect(duid.hasSuffix(syntheticDeviceIDHex))
    }

    @Test("the client device id is stable across calls")
    func stable() throws {
        #expect(try ClientDeviceID.make(from: syntheticDeviceID) == ClientDeviceID.make(from: syntheticDeviceID))
    }

    @Test("without a machine id it throws rather than inventing one")
    func noMachineID() {
        #expect(throws: CloudError.self) { try ClientDeviceID.make(from: []) }
    }

    @Test("the local hashed id matches .NET's for the same device id")
    func hashedIDMatchesDotNet() throws {
        // HalyardLocalHashedId.For(HalyardClientDeviceId.For(bytes 0..15)), as the .NET code computed it.
        let hashed = try LocalHashedID.make(deviceID: syntheticDeviceID)
        #expect(hashed.count == LocalHashedID.length)
        #expect(Data(hashed).base64EncodedString() == "6lzJBIZSI0A2OrV+EUfOM+XBlws=")
    }

    @Test("an empty client device id is refused")
    func hashedIDNeedsInput() {
        #expect(throws: CloudError.self) { try LocalHashedID.make(clientDeviceID: "") }
    }

    @Test("a machine GUID parses with or without dashes, and nothing else does", arguments: [
        ("1a2b3c4d-dead-beef-0011-223344556677", true),
        ("10101010101010101010101010101010", true),
        ("1a2b3c4d-dead-beef-0011-22334455667", false),
        ("zz101010101010101010101010101010", false),
        ("", false),
    ])
    func parseGUID(text: String, valid: Bool) {
        #expect((DeviceIdentity.parse(text) != nil) == valid)
    }
}

@Suite("Cloud: what survives a restart")
struct TokenStoreTests {
    @Test("the in-memory store round-trips and clears")
    func inMemory() throws {
        let store = InMemoryAccountTokenStore()
        try store.save(StoredAccountSession(refreshToken: "refresh-abc", accountID: "4200000000000000042",
                                            displayName: "somebody"))
        #expect(store.load()?.refreshToken == "refresh-abc")
        #expect(store.load()?.accountID == "4200000000000000042")
        store.clear()
        #expect(store.load() == nil)
    }

    /// Against the real login keychain, under a service no one else uses, removed afterwards.
    @Test("the Keychain store round-trips the token and cached identity, and sign-out leaves nothing")
    func keychain() throws {
        let store = KeychainAccountTokenStore(service: "io.github.altrhombus.Ripcord.tests.\(UUID().uuidString)")
        defer { store.clear() }
        #expect(store.load() == nil)

        let saved = StoredAccountSession(refreshToken: "refresh-abc", accountID: "4200000000000000042",
                                         displayName: "somebody", savedAt: Date(timeIntervalSince1970: 1_000_000))
        try store.save(saved)
        #expect(store.load() == saved)

        try store.save(StoredAccountSession(refreshToken: "refresh-def"))   // replaces, not duplicates
        #expect(store.load()?.refreshToken == "refresh-def")

        store.clear()
        #expect(store.load() == nil)
        #expect(try Keychain.read(service: store.service, account: store.account) == nil)
    }
}

func temporaryDirectory() throws -> URL {
    let url = FileManager.default.temporaryDirectory.appending(path: "ripcord-tests-\(UUID().uuidString)", directoryHint: .isDirectory)
    try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    return url
}
