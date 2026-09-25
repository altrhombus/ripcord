// The one place a cloud request meets URLSession.

import Foundation

extension URLSession {
    /// The session the cloud tier uses unless handed another: ephemeral (nothing cached or kept on disk, the
    /// nearest thing to a fresh HttpClient) with the 20-second timeout the .NET app composes its client with.
    /// Both URLSession timeouts are set because .NET's is a whole-request limit and URLSession's request
    /// timeout alone is an idle one.
    public static let ripcordCloud: URLSession = {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = CloudTiming.requestTimeout
        configuration.timeoutIntervalForResource = CloudTiming.requestTimeout
        configuration.httpCookieStorage = nil
        configuration.urlCache = nil
        return URLSession(configuration: configuration)
    }()
}

struct CloudHTTP: Sendable {
    let session: URLSession

    /// Sends `request` and returns the status and body. Only a request that got no answer throws; a failure
    /// status is the caller's to judge, since the token endpoint and the REST surface word it differently.
    func send(_ request: URLRequest) async throws(CloudError) -> (status: Int, body: Data) {
        do {
            let (data, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse else {
                throw CloudError.invalidResponse("not an HTTP response", body: nil)
            }
            return (http.statusCode, data)
        } catch {
            throw CloudError.from(error)
        }
    }
}
