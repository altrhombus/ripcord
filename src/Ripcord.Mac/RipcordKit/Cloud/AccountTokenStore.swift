// What survives between runs after sign-in: the refresh token, and a cached identity beside it. Ported from
// Ripcord.Core/Accounts (IAccountTokenStore, AccountTokenStore, InMemoryAccountTokenStore).
//
// On Windows it is a JSON file with the token DPAPI-encrypted. On the Mac it is one Keychain item, a generic
// password under this app's service name, and never a file: the Keychain is the Mac's DPAPI and where a Mac
// user expects secrets to be (docs/macos-plan.md, "The split").
//
// THE REFRESH TOKEN IS THE WHOLE CREDENTIAL. Access tokens last about an hour and are never stored. The
// account id and display name are a cache so a surface can say who is signed in without a round trip; they
// are re-read from the service after every refresh.

import Foundation
import Security
import Synchronization

public struct StoredAccountSession: Sendable, Equatable, Codable {
    public var refreshToken: String
    public var accountID: String?
    public var displayName: String?
    public var savedAt: Date

    public init(refreshToken: String, accountID: String? = nil, displayName: String? = nil, savedAt: Date = Date()) {
        self.refreshToken = refreshToken
        self.accountID = accountID
        self.displayName = displayName
        self.savedAt = savedAt
    }
}

public protocol AccountTokenStore: Sendable {
    /// The stored session, or nil when nobody is signed in. Never throws: an unreadable item is "signed out".
    func load() -> StoredAccountSession?
    /// Replaces any stored session.
    func save(_ session: StoredAccountSession) throws(CloudError)
    /// Sign-out. Must leave nothing behind, and must not throw.
    func clear()
    /// How the token is protected at rest, for a settings surface to report honestly.
    var protectionDescription: String { get }
    var tokenEncrypted: Bool { get }
}

/// The Keychain store: one generic-password item holding the session as JSON.
///
/// **The login keychain, not the data-protection keychain, for now.** The data-protection keychain needs a
/// keychain-access-group entitlement, which needs a signing team, and every build here is ad hoc signed until
/// distribution is set up (docs/macos-plan.md, step 9). Pass `useDataProtectionKeychain: true` once it is; the
/// item's shape does not change.
public struct KeychainAccountTokenStore: AccountTokenStore {
    /// The app's service name: its bundle identifier, which the app target will carry.
    public static let defaultService = "io.github.altrhombus.Ripcord"
    public static let defaultAccount = "account-session"

    public let service: String
    public let account: String
    public let useDataProtectionKeychain: Bool

    public init(service: String = defaultService, account: String = defaultAccount,
                useDataProtectionKeychain: Bool = false) {
        self.service = service
        self.account = account
        self.useDataProtectionKeychain = useDataProtectionKeychain
    }

    public var protectionDescription: String { "stored in the Keychain" }
    public var tokenEncrypted: Bool { true }

    public func load() -> StoredAccountSession? {
        // A missing, unreadable or corrupt item all mean "signed out", as .NET's Load reads a bad file.
        guard let data = try? Keychain.read(service: service, account: account,
                                            dataProtection: useDataProtectionKeychain),
              let session = try? Self.decoder.decode(StoredAccountSession.self, from: data),
              !session.refreshToken.isEmpty else { return nil }
        return session
    }

    public func save(_ session: StoredAccountSession) throws(CloudError) {
        let data: Data
        do { data = try Self.encoder.encode(session) } catch { throw .invalidArgument("the session could not be encoded") }
        try Keychain.write(data, service: service, account: account, label: "Ripcord PlayStation Network sign-in",
                           dataProtection: useDataProtectionKeychain)
    }

    public func clear() {
        Keychain.delete(service: service, account: account, dataProtection: useDataProtectionKeychain)
    }

    private static let encoder: JSONEncoder = {
        let e = JSONEncoder()
        e.dateEncodingStrategy = .iso8601
        return e
    }()

    private static let decoder: JSONDecoder = {
        let d = JSONDecoder()
        d.dateDecodingStrategy = .iso8601
        return d
    }()
}

/// Holds the session in memory only: tests, and a host that declines to keep one.
public final class InMemoryAccountTokenStore: AccountTokenStore {
    private let session: Mutex<StoredAccountSession?>

    public init(_ initial: StoredAccountSession? = nil) { session = Mutex(initial) }

    public var protectionDescription: String { "held in memory only (not persisted)" }
    public var tokenEncrypted: Bool { false }
    public func load() -> StoredAccountSession? { session.withLock { $0 } }
    public func save(_ value: StoredAccountSession) throws(CloudError) { session.withLock { $0 = value } }
    public func clear() { session.withLock { $0 = nil } }
}

/// The three Keychain calls the cloud tier makes, on generic-password items.
enum Keychain {
    static func read(service: String, account: String, dataProtection: Bool = false) throws(CloudError) -> Data? {
        var query = base(service: service, account: account, dataProtection: dataProtection)
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        switch status {
        case errSecSuccess: return result as? Data
        case errSecItemNotFound: return nil
        default: throw .keychain(status)
        }
    }

    static func write(_ data: Data, service: String, account: String, label: String,
                      dataProtection: Bool = false) throws(CloudError) {
        let query = base(service: service, account: account, dataProtection: dataProtection)
        let update: [String: Any] = [kSecValueData as String: data, kSecAttrLabel as String: label]
        var status = SecItemUpdate(query as CFDictionary, update as CFDictionary)
        if status == errSecItemNotFound {
            var add = query
            add.merge(update) { $1 }
            add[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock
            status = SecItemAdd(add as CFDictionary, nil)
        }
        guard status == errSecSuccess else { throw .keychain(status) }
    }

    static func delete(service: String, account: String, dataProtection: Bool = false) {
        _ = SecItemDelete(base(service: service, account: account, dataProtection: dataProtection) as CFDictionary)
    }

    private static func base(service: String, account: String, dataProtection: Bool) -> [String: Any] {
        var query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
        if dataProtection { query[kSecUseDataProtectionKeychain as String] = true }
        return query
    }
}
