// The sign-in lifecycle: begin (the URL a web view opens), complete (the redirect it lands on, exchanged,
// the account identified, the refresh token kept), restore (the kept token traded for a live session on a
// later launch), and sign out. Ported from HalyardAccountGateway.cs.
//
// An actor where .NET's is a dispatcher-confined class. The one piece of .NET's that takes a lock (one
// restore however many callers arrive together) is the actor's own job here.

import Foundation

public actor AccountGateway {
    public nonisolated let config: ClientConfig
    /// The `duid` this gateway signs in with. The signaling `localHashedId` must derive from the same one;
    /// a client announcing an id unrelated to the device it authenticated as would be two peers to the
    /// console. Empty when the build has no credential.
    public nonisolated let clientDeviceID: String
    /// The authenticated REST surface. Meaningful once signed in; before that its calls throw `.notSignedIn`,
    /// which is correct: asking for the console list unsigned is a bug at the call site.
    public nonisolated let cloud: CloudClient

    private let auth: AuthClient
    private let tokens: TokenProvider
    private let store: any AccountTokenStore
    private var signedIn: CloudAccount?
    private var restoring: Task<CloudAccount?, any Error>?

    /// - Parameter deviceID: this Mac's 16 bytes (`DeviceIdentity.stable()`). Resolved into a duid now, so a
    ///   machine that cannot supply one fails here with a sentence about the machine rather than midway
    ///   through a sign-in the user has started.
    public init(session: URLSession = .ripcordCloud, config: ClientConfig, store: any AccountTokenStore,
                deviceID: [UInt8]) throws(CloudError) {
        self.config = config
        self.store = store
        self.auth = AuthClient(session: session, config: config)
        self.tokens = TokenProvider(auth: auth)
        self.cloud = CloudClient(session: session, tokens: tokens)
        self.clientDeviceID = try config.isConfigured ? ClientDeviceID.make(from: deviceID) : ""
    }

    /// Whether this build has a credential at all. False means every method below declines.
    public nonisolated var canSignIn: Bool { config.isConfigured }

    /// Whether a stored session exists to restore, without touching the network.
    public nonisolated var hasStoredSession: Bool { store.load() != nil }

    /// Recognises the redirect that ends the web flow. Synchronous and actor-free, so the web view's
    /// navigation delegate can ask on the main thread.
    public nonisolated var redirectMatcher: RedirectMatcher { auth.redirectMatcher }

    /// The signed-in account, once a sign-in or restore has succeeded.
    public var account: CloudAccount? { signedIn }

    /// A session in memory. Not "is this user signed in": a stored token not yet restored reads false here.
    /// Anything deciding whether a feature is available wants `ensureSignedIn()`.
    public var isSignedIn: Bool { signedIn != nil }

    /// The URL the web flow opens.
    public nonisolated func beginSignIn() throws(CloudError) -> URL {
        guard canSignIn else { throw .notConfigured }
        return try auth.authorizeURL(clientDeviceID: clientDeviceID)
    }

    /// A valid access token, refreshed if need be. For the one caller that presents it outside the REST
    /// surface: the push WebSocket upgrade.
    public func accessToken() async throws(CloudError) -> String { try await tokens.accessToken() }

    /// Makes sure the session is loaded, restoring it from the stored token if not, and says whether there is
    /// one. Safe to call repeatedly; the restore happens at most once at a time.
    ///
    /// The .NET reason for it stands: nothing restored the session except the settings page, so a user who
    /// launched and connected straight away was told to sign in while holding a perfectly good token.
    /// Restoring lazily keeps launch free of a round trip a LAN connect never needs.
    public func ensureSignedIn() async -> Bool {
        if signedIn != nil { return true }
        guard canSignIn, hasStoredSession else { return false }

        // One restore however many callers arrive together: two would both spend the refresh token, and the
        // second would find it already rotated.
        let task: Task<CloudAccount?, any Error>
        if let restoring {
            task = restoring
        } else {
            task = Task { try await self.restore() }
            restoring = task
        }
        let restored = (try? await task.value) ?? nil
        // Cleared only on failure, so a later attempt can try again; a success short-circuits above.
        if signedIn == nil, restoring == task { restoring = nil }
        return restored != nil
    }

    /// Finishes a sign-in from the redirect the web view landed on.
    public func completeSignIn(redirect: URL) async throws(CloudError) -> CloudAccount {
        guard let code = auth.redirectMatcher.authorizationCode(in: redirect) else { throw .noAuthorizationCode }
        let fresh = try await auth.exchange(code: code, clientDeviceID: clientDeviceID)
        await tokens.seed(fresh)
        return try await identifyAndPersist(fresh)
    }

    /// Re-establishes a session from the stored refresh token. Nil when nothing is stored or the stored token
    /// is no longer good, both of which mean "show sign-in". Throws only when the service could not be
    /// reached, which must not be mistaken for a rejection.
    ///
    /// A *rejected* token clears the store, so a launch does not spend a round trip re-learning that it is
    /// dead. A token that refreshed but whose account lookup failed keeps the cached identity, so an install
    /// stays signed in through an outage.
    public func restore() async throws(CloudError) -> CloudAccount? {
        guard canSignIn, let stored = store.load() else { return nil }

        do {
            try await tokens.seed(refreshToken: stored.refreshToken)
        } catch where error.isServiceError {
            store.clear()
            return nil
        }

        guard let current = await tokens.current else {
            throw .invalidResponse("The refresh succeeded but produced no tokens.", body: nil)
        }

        do {
            return try await identifyAndPersist(current)
        } catch where error.isServiceError {
            guard let accountID = stored.accountID else { return nil }
            let cached = CloudAccount(accountID: accountID, onlineID: stored.displayName ?? "", region: "")
            signedIn = cached
            return cached
        }
    }

    /// Destroys the stored credential and forgets the session.
    public func signOut() async {
        store.clear()
        signedIn = nil
        await tokens.clear()
    }

    private func identifyAndPersist(_ tokensUsed: AccountTokens) async throws(CloudError) -> CloudAccount {
        let info = try await cloud.accountInfo()
        let account = CloudAccount(accountID: info.accountID, onlineID: info.onlineID, region: info.region)
        signedIn = account

        // The refresh token the provider holds NOW, not the one we started with: a refresh grant returns a
        // new one, and keeping the spent one signs an install out about an hour later for no visible reason.
        let latest = await tokens.current ?? tokensUsed
        try store.save(StoredAccountSession(refreshToken: latest.refreshToken, accountID: account.accountID,
                                            displayName: account.onlineID))
        return account
    }
}
