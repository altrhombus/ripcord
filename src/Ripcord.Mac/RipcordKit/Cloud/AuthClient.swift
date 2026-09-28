// OAuth2 for account sign-in: the authorize URL, reading the code back out of the redirect, the
// authorization-code exchange, and the refresh grant. Ported from HalyardAuthClient.cs and
// HalyardTokenProvider.cs; structure from docs/protocol/ps5-cloud-session-api.md.

import Foundation

/// Decides whether a URL the sign-in web view is navigating to is the redirect carrying the code, and reads
/// the code out of it. A value type with no network, so a web view can ask it synchronously on every
/// navigation from the main thread.
public struct RedirectMatcher: Sendable {
    public let redirectURI: String

    public init(redirectURI: String) { self.redirectURI = redirectURI }

    /// The code, or nil when `url` is not the redirect we are waiting for.
    ///
    /// Matched on scheme, host and path only. The flow bounces through several URLs, and the final one
    /// carries parameters beyond `code`, so an equality test never fires and a substring test fires on the
    /// wrong page.
    public func authorizationCode(in url: URL) -> String? {
        guard let expected = URL(string: redirectURI), let expectedScheme = expected.scheme,
              let expectedHost = expected.host(),
              let scheme = url.scheme, let host = url.host(),
              scheme.caseInsensitiveCompare(expectedScheme) == .orderedSame,
              host.caseInsensitiveCompare(expectedHost) == .orderedSame,
              Self.trimmedPath(url).caseInsensitiveCompare(Self.trimmedPath(expected)) == .orderedSame
        else { return nil }

        let code = URLComponents(url: url, resolvingAgainstBaseURL: false)?
            .queryItems?.first { $0.name == "code" }?.value
        guard let code, !code.isBlank else { return nil }
        return code
    }

    public func isCompletion(_ url: URL) -> Bool { authorizationCode(in: url) != nil }

    private static func trimmedPath(_ url: URL) -> String {
        var path = url.path(percentEncoded: false)
        while path.hasSuffix("/") { path.removeLast() }
        return path
    }
}

public struct AuthClient: Sendable {
    public let config: ClientConfig
    let http: CloudHTTP

    public init(session: URLSession = .ripcordCloud, config: ClientConfig) {
        self.config = config
        self.http = CloudHTTP(session: session)
    }

    public var redirectMatcher: RedirectMatcher { RedirectMatcher(redirectURI: config.redirectURI) }

    /// The URL to open in the account web flow.
    ///
    /// **No `state` parameter**, as .NET: the observed flow sends none, sending an unknown parameter to
    /// someone else's authorize endpoint is a way to be rejected, and what `state` defends against (a code
    /// arriving from elsewhere) does not arise when the web view is this process's own, made for this one
    /// sign-in, and the redirect is matched before anything is read from it.
    ///
    /// - Parameter locale: the page's language, as `language_REGION`. Defaults to the user's first
    ///   preferred language, which is what .NET's CurrentUICulture is on Windows.
    public func authorizeURL(clientDeviceID: String, locale: String = AuthClient.requestLocale()) throws(CloudError) -> URL {
        guard !clientDeviceID.isBlank else { throw .invalidArgument("clientDeviceId must not be blank") }
        let query = WireURLEncoding.query([
            ("service_entity", "urn:service-entity:psn"),
            ("response_type", "code"),
            ("client_id", config.clientID),
            ("redirect_uri", config.redirectURI),
            ("scope", config.scopeString),
            // The same device identity the token exchange carries. Omitting these was the difference
            // between a code the token endpoint accepts and one it rejects as issued to another device.
            ("device_type", ClientConfig.deviceType),
            ("duid", clientDeviceID),
            // Page chrome: the compact remote-play styling rather than the full account portal, which in a
            // small web view is the difference between usable and scrolling sideways.
            ("smcid", "remoteplay"),
            ("ui", "pr"),
            ("prompt", "always"),
            ("request_locale", locale),
        ])
        guard let url = URL(string: "\(CloudEndpoints.authorize)?\(query)") else {
            throw .invalidArgument("the authorize URL could not be formed")
        }
        return url
    }

    /// `CultureInfo.CurrentUICulture.Name.Replace('-', '_')`, from the Mac's first preferred language.
    public static func requestLocale(preferred: [String] = Locale.preferredLanguages) -> String {
        (preferred.first ?? "en-US").replacingOccurrences(of: "-", with: "_")
    }

    public func exchange(code: String, clientDeviceID: String) async throws(CloudError) -> AccountTokens {
        try await requestToken([
            ("grant_type", "authorization_code"),
            ("code", code),
            ("redirect_uri", config.redirectURI),
            ("device_type", ClientConfig.deviceType),
            ("duid", clientDeviceID),
        ])
    }

    public func refresh(refreshToken: String) async throws(CloudError) -> AccountTokens {
        try await requestToken([
            ("grant_type", "refresh_token"),
            ("refresh_token", refreshToken),
            ("scope", config.scopeString),
        ])
    }

    private func requestToken(_ form: [(String, String)]) async throws(CloudError) -> AccountTokens {
        // Checked here, where every path into the token endpoint passes, so an unconfigured build fails with
        // a sentence rather than a 401 the user cannot act on.
        guard config.isConfigured else { throw .notConfigured }
        guard let url = URL(string: CloudEndpoints.token) else { throw .invalidArgument("bad token URL") }

        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.httpBody = Data(WireURLEncoding.form(form).utf8)
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        let basic = Data("\(config.clientID):\(config.clientSecret)".utf8).base64EncodedString()
        request.setValue("Basic \(basic)", forHTTPHeaderField: "Authorization")

        let (status, body) = try await http.send(request)
        guard (200..<300).contains(status) else {
            throw .tokenRequestFailed(status: status, body: String(decoding: body, as: UTF8.self))
        }
        guard let parsed = try? JSONDecoder().decode(TokenResponse.self, from: body) else {
            throw .invalidResponse("Token response could not be parsed.", body: String(decoding: body, as: UTF8.self))
        }
        return AccountTokens(accessToken: parsed.accessToken, refreshToken: parsed.refreshToken,
                             expiresAt: Date().addingTimeInterval(TimeInterval(parsed.expiresIn)))
    }
}

/// Holds the account tokens and yields a valid access token, refreshing when it has expired.
///
/// An actor, where .NET's is an unsynchronised class, and with one addition that follows from that: two
/// callers finding the token expired at once share one refresh. A refresh grant rotates the refresh token,
/// so two refreshes racing would have the second spend a token the first already used up.
public actor TokenProvider {
    private let auth: AuthClient
    private var tokens: AccountTokens?
    private var refreshing: Task<AccountTokens, any Error>?

    public init(auth: AuthClient) { self.auth = auth }

    public var current: AccountTokens? { tokens }

    public func seed(_ tokens: AccountTokens) { self.tokens = tokens }

    public func clear() { tokens = nil }

    /// Seeds from a persisted refresh token by refreshing immediately.
    public func seed(refreshToken: String) async throws(CloudError) {
        tokens = try await auth.refresh(refreshToken: refreshToken)
    }

    public func accessToken() async throws(CloudError) -> String {
        guard let tokens else { throw .notSignedIn }
        guard tokens.isExpired() else { return tokens.accessToken }

        let task: Task<AccountTokens, any Error>
        if let refreshing {
            task = refreshing
        } else {
            let auth = self.auth
            let refreshToken = tokens.refreshToken
            task = Task { try await auth.refresh(refreshToken: refreshToken) }
            refreshing = task
        }
        defer { refreshing = nil }
        do {
            let fresh = try await task.value
            self.tokens = fresh
            return fresh.accessToken
        } catch {
            throw CloudError.from(error)
        }
    }
}
