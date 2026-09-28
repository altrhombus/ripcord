// The account web sign-in on the Mac: a web view that catches the authorization code out of the redirect.
// The counterpart of Ripcord.App's AccountSignInDialog (a WebView2 in a ContentDialog).
//
// WHY NOT ASWebAuthenticationSession. It is the Mac's own tool for this, and it cannot be used here. It
// completes by recognising a callback URL, and it accepts two kinds: a custom scheme the app registers, or
// (macOS 14.4 and later) an https host and path, which it matches only for a domain the app is associated
// with (an Associated Domains entitlement, confirmed by an apple-app-site-association file the domain itself
// serves). The flow's redirect is fixed by the client registration to
// https://remoteplay.dl.playstation.net/remoteplay/redirect: a custom scheme would be a different
// redirect_uri, which the authorize endpoint is expected to refuse for this client ([X]: not tried), and the
// https host is not ours to associate with. The system session would therefore sign the user in and then sit
// on Sony's redirect page with nothing to deliver the code to us.
//
// SO THE CLOSEST NATIVE EQUIVALENT: a WKWebView this process owns, watching each navigation for the
// redirect, exactly as the Windows dialog watches its WebView2. It is also better placed than that dialog in
// one respect: its website data store is non-persistent, so every sign-in starts with no cookies (the
// Windows one cannot get a throwaway profile per control and leans on `prompt=always` alone; this sends that
// too). The navigation to the redirect is cancelled once matched, so the user never sees the page it lands
// on and no request is made to it.
//
// UNVERIFIED against the live flow: that every hop reaches decidePolicyFor (server redirects included, which
// is why didReceiveServerRedirect is watched as well), and that the account pages work in a WKWebView
// with passkeys. The Windows flow's passkey and QR sign-in went through WebView2; WebKit's passkey support
// in an app's own web view needs the web-browser entitlement or an associated domain, so a passkey prompt
// may not appear here and the user may need the password or QR route instead. [X]

// macOS only for now: the window below is AppKit's. iPhone and iPad get the same web view in UIKit's
// presentation later (docs/ios-plan.md), and Apple TV has no WebKit at all.
#if os(macOS)
import AppKit
import WebKit

public enum WebSignInError: Error, Sendable, CustomStringConvertible {
    /// The user closed the window before finishing.
    case cancelled
    /// The first page never loaded.
    case couldNotLoad(String)

    public var description: String {
        switch self {
        case .cancelled: "sign-in was cancelled"
        case .couldNotLoad(let why): "couldn't open the sign-in page: \(why)"
        }
    }
}

/// Drives one sign-in in a web view. Embed `webView` anywhere (a sheet, a window), or call
/// `presentInWindow`, and await `run()` for the redirect URL to hand to `AccountGateway.completeSignIn`.
///
/// The gateway is not called from here, on purpose and as on Windows: a window that has already closed must
/// never be the thing awaiting a network call.
@MainActor
public final class WebSignInController: NSObject, WKNavigationDelegate, NSWindowDelegate {
    public let webView: WKWebView
    private let authorizeURL: URL
    private let matcher: RedirectMatcher
    private var continuation: CheckedContinuation<URL, any Error>?
    private var finished = false
    private var window: NSWindow?

    public init(authorizeURL: URL, matcher: RedirectMatcher) {
        let configuration = WKWebViewConfiguration()
        configuration.websiteDataStore = .nonPersistent()
        webView = WKWebView(frame: NSRect(x: 0, y: 0, width: 480, height: 640), configuration: configuration)
        self.authorizeURL = authorizeURL
        self.matcher = matcher
        super.init()
        webView.navigationDelegate = self
    }

    /// Loads the sign-in page and returns the redirect it ends on.
    public func run() async throws(WebSignInError) -> URL {
        do {
            return try await withCheckedThrowingContinuation { continuation in
                self.continuation = continuation
                webView.load(URLRequest(url: authorizeURL))
            }
        } catch let error as WebSignInError {
            throw error
        } catch {
            throw .cancelled
        }
    }

    /// Opens the web view in a small window of its own (a sheet on `parent` when given), runs the sign-in,
    /// and closes the window when it ends either way.
    public func presentInWindow(parent: NSWindow? = nil, title: String = "Sign in to PlayStation Network")
        async throws(WebSignInError) -> URL {
        let window = NSWindow(contentRect: webView.frame, styleMask: [.titled, .closable, .resizable],
                              backing: .buffered, defer: false)
        window.title = title
        window.contentView = webView
        window.delegate = self
        window.isReleasedWhenClosed = false
        self.window = window
        if let parent {
            parent.beginSheet(window, completionHandler: nil)
        } else {
            window.center()
            window.makeKeyAndOrderFront(nil)
        }
        defer { dismiss(parent: parent) }
        return try await run()
    }

    /// Ends the sign-in as cancelled.
    public func cancel() { finish(.failure(WebSignInError.cancelled)) }

    private func dismiss(parent: NSWindow?) {
        guard let window else { return }
        self.window = nil
        window.delegate = nil
        if let parent { parent.endSheet(window) } else { window.close() }
    }

    private func finish(_ result: Result<URL, any Error>) {
        guard !finished, let continuation else { return }
        finished = true
        self.continuation = nil
        webView.stopLoading()
        continuation.resume(with: result)
    }

    /// Whether `url` ends the flow; if so, finishes with it.
    private func check(_ url: URL?) -> Bool {
        guard !finished, let url, matcher.isCompletion(url) else { return false }
        finish(.success(url))
        return true
    }

    // MARK: WKNavigationDelegate

    public func webView(_ webView: WKWebView, decidePolicyFor navigationAction: WKNavigationAction,
                        decisionHandler: @escaping @MainActor @Sendable (WKNavigationActionPolicy) -> Void) {
        // Cancelled once matched: the redirect target is a page that has nothing to do with us.
        decisionHandler(check(navigationAction.request.url) ? .cancel : .allow)
    }

    public func webView(_ webView: WKWebView, didReceiveServerRedirectForProvisionalNavigation navigation: WKNavigation!) {
        if check(webView.url) { webView.stopLoading() }
    }

    public func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!,
                        withError error: any Error) {
        // A navigation we cancelled reports as a failure too; only a first page that never arrives is one.
        guard !finished, webView.url == nil || webView.url == authorizeURL else { return }
        let nsError = error as NSError
        if nsError.domain == NSURLErrorDomain, nsError.code == NSURLErrorCancelled { return }
        finish(.failure(WebSignInError.couldNotLoad(error.localizedDescription)))
    }

    // MARK: NSWindowDelegate

    public func windowWillClose(_ notification: Notification) { cancel() }
}
#endif
