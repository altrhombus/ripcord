// The REST surface and the token endpoint, against the URLProtocol stub. The expected bodies, URLs and
// headers are what the .NET HalyardCloudClient and HalyardAuthClient put on the wire for the same inputs,
// captured from a scratch program that ran them over an HttpMessageHandler (not recalled): the Mac must be
// byte-identical to the implementation that has been shown to work.

import Foundation
@testable import RipcordKit
import Testing

extension StubbedHTTP {
    @Suite("Cloud: REST bodies are .NET's, byte for byte")
    struct CloudClientTests {
        let session = StubURLProtocol.session()

        private func client(reply: String = "") async -> CloudClient {
            StubURLProtocol.install { _ in (200, reply) }
            return CloudClient(session: session, tokens: await seededTokens(session: session))
        }

        private var last: RecordedRequest { StubURLProtocol.requests.last! }

        @Test("every call carries the bearer token and the vendor user agent")
        func headers() async throws {
            let cloud = await client(reply: #"{"accountId":"1","onlineId":"o","region":"r"}"#)
            let info = try await cloud.accountInfo()
            #expect(info == AccountInfo(accountID: "1", onlineID: "o", region: "r"))
            #expect(last.method == "GET")
            #expect(last.url == CloudEndpoints.accountInfo)
            #expect(last.header("Authorization") == "Bearer access-1")
            #expect(last.header("User-Agent") == "RpNetHttpUtilImpl")
        }

        @Test("create session")
        func createSession() async throws {
            let cloud = await client(reply: #"{"remotePlaySessions":[{"sessionId":"s-1","members":[]}]}"#)
            let created = try await cloud.createSession(pushContextID: "0f0e0d0c-aaaa-bbbb-cccc-000000000001")
            #expect(created.sessionID == "s-1")
            #expect(last.method == "POST")
            #expect(last.url == CloudEndpoints.sessions)
            #expect(last.header("Content-Type") == "application/json; charset=utf-8")
            #expect(last.body == #"{"remotePlaySessions":[{"members":[{"accountId":"me","deviceUniqueId":"me","platform":"me","pushContexts":[{"pushContextId":"0f0e0d0c-aaaa-bbbb-cccc-000000000001"}]}]}]}"#)
        }

        @Test("a create that returns no session is an error, not a crash")
        func createSessionEmpty() async {
            let cloud = await client(reply: #"{"remotePlaySessions":[]}"#)
            await #expect(throws: CloudError.self) { try await cloud.createSession(pushContextID: "x") }
        }

        @Test("the connect command: a numeric accountId, the captured spacing, and .NET's escaping twice over")
        func connectCommand() async throws {
            let cloud = await client(reply: #"{"commandId":"c-1"}"#)
            let id = try await cloud.sendConnectCommand(
                consoleDUID: "CONSOLE-DUID", accountID: "1234567890123456789", sessionID: "s-1", clientType: "Windows",
                seeds: ConnectSeeds(data1: "ab+c/d==", data2: "x<y>&'z\u{e9}"))
            #expect(id == "c-1")
            #expect(last.url == CloudEndpoints.commands)
            #expect(last.body == #"{"commandDetail":{"platform":"PS5","duid":"CONSOLE-DUID","commandType":"remotePlay","parameters":{"initialParams":"{\u0022accountId\u0022:1234567890123456789, \u0022roomId\u0022:0, \u0022sessionId\u0022:\u0022s-1\u0022, \u0022clientType\u0022:\u0022Windows\u0022, \u0022data1\u0022:\u0022ab\\u002Bc/d==\u0022, \u0022data2\u0022:\u0022x\\u003Cy\\u003E\\u0026\\u0027z\\u00E9\u0022}"},"messageDestination":"SQS"}}"#)
        }

        @Test("a non-numeric accountId falls back to a quoted string")
        func connectCommandNonNumeric() async throws {
            let cloud = await client(reply: #"{"commandId":"c-1"}"#)
            try await cloud.sendConnectCommand(consoleDUID: "CONSOLE-DUID", accountID: "not-numeric", sessionID: "s-1",
                                               clientType: "Windows", seeds: ConnectSeeds(data1: "a", data2: "b"))
            #expect(last.body == #"{"commandDetail":{"platform":"PS5","duid":"CONSOLE-DUID","commandType":"remotePlay","parameters":{"initialParams":"{\u0022accountId\u0022:\u0022not-numeric\u0022, \u0022roomId\u0022:0, \u0022sessionId\u0022:\u0022s-1\u0022, \u0022clientType\u0022:\u0022Windows\u0022, \u0022data1\u0022:\u0022a\u0022, \u0022data2\u0022:\u0022b\u0022}"},"messageDestination":"SQS"}}"#)
        }

        @Test("an OFFER with candidates, a hashed id, and the second connection's sid and reqId")
        func offer() async throws {
            let cloud = await client()
            let hashed: [UInt8] = [250, 251, 252, 253, 254, 255] + [UInt8](repeating: 0, count: 14)
            try await cloud.sendOffer(sessionID: "s-1", accountID: "1234567890123456789", consoleDUID: "CONSOLE-DUID",
                                      candidates: [SignalingCandidate(type: "STUN", address: "198.51.100.9", port: 40000),
                                                   SignalingCandidate(type: "LOCAL", address: "192.168.1.9", port: 51000)],
                                      localHashedID: hashed, reqID: 3, sid: 2)
            #expect(last.url == CloudEndpoints.sessions + "/s-1/sessionMessage")
            #expect(last.body == #"{"channel":"remote_play:1","payload":"ver=1.0, type=text, body={\u0022action\u0022:\u0022OFFER\u0022,\u0022reqId\u0022:3,\u0022error\u0022:0,\u0022connRequest\u0022:{\u0022sid\u0022:2,\u0022peerSid\u0022:0,\u0022skey\u0022:\u0022AAAAAAAAAAAAAAAAAAAAAA==\u0022,\u0022natType\u0022:2,\u0022candidate\u0022:[{\u0022type\u0022:\u0022STUN\u0022,\u0022addr\u0022:\u0022198.51.100.9\u0022,\u0022mappedAddr\u0022:\u00220.0.0.0\u0022,\u0022port\u0022:40000,\u0022mappedPort\u0022:0},{\u0022type\u0022:\u0022LOCAL\u0022,\u0022addr\u0022:\u0022192.168.1.9\u0022,\u0022mappedAddr\u0022:\u00220.0.0.0\u0022,\u0022port\u0022:51000,\u0022mappedPort\u0022:0}],\u0022defaultRouteMacAddr\u0022:\u0022\u0022,\u0022localPeerAddr\u0022:{\u0022accountId\u0022:\u00221234567890123456789\u0022,\u0022platform\u0022:\u0022REMOTE_PLAY\u0022},\u0022localHashedId\u0022:\u0022\\u002Bvv8/f7/AAAAAAAAAAAAAAAAAAA=\u0022}}","to":[{"accountId":"1234567890123456789","deviceUniqueId":"CONSOLE-DUID","platform":"PS5"}]}"#)
        }

        @Test("an OFFER with nothing to offer and no hashed id, at the defaults")
        func emptyOffer() async throws {
            let cloud = await client()
            try await cloud.sendOffer(sessionID: "s-1", accountID: "42", consoleDUID: "CONSOLE-DUID", candidates: [])
            #expect(last.body == #"{"channel":"remote_play:1","payload":"ver=1.0, type=text, body={\u0022action\u0022:\u0022OFFER\u0022,\u0022reqId\u0022:1,\u0022error\u0022:0,\u0022connRequest\u0022:{\u0022sid\u0022:1,\u0022peerSid\u0022:0,\u0022skey\u0022:\u0022AAAAAAAAAAAAAAAAAAAAAA==\u0022,\u0022natType\u0022:2,\u0022candidate\u0022:[],\u0022defaultRouteMacAddr\u0022:\u0022\u0022,\u0022localPeerAddr\u0022:{\u0022accountId\u0022:\u002242\u0022,\u0022platform\u0022:\u0022REMOTE_PLAY\u0022},\u0022localHashedId\u0022:\u0022\u0022}}","to":[{"accountId":"42","deviceUniqueId":"CONSOLE-DUID","platform":"PS5"}]}"#)
        }

        @Test("a RESULT")
        func result() async throws {
            let cloud = await client()
            try await cloud.sendResult(sessionID: "s-1", accountID: "42", consoleDUID: "CONSOLE-DUID", reqID: 7)
            #expect(last.body == #"{"channel":"remote_play:1","payload":"ver=1.0, type=text, body={\u0022action\u0022:\u0022RESULT\u0022,\u0022reqId\u0022:7,\u0022error\u0022:0,\u0022connRequest\u0022:{}}","to":[{"accountId":"42","deviceUniqueId":"CONSOLE-DUID","platform":"PS5"}]}"#)
        }

        @Test("an ACCEPT, with the selected pair the right way round and the vendor's malformation kept")
        func accept() async throws {
            let cloud = await client()
            try await cloud.sendAccept(sessionID: "s-1", accountID: "42", consoleDUID: "CONSOLE-DUID", reqID: 2, sid: 1,
                                       peerSid: 24043,
                                       consoleCandidate: SignalingCandidate(type: "LOCAL", address: "192.168.1.50", port: 9303),
                                       localAddress: "192.168.1.9", localPort: 51000)
            #expect(last.body == #"{"channel":"remote_play:1","payload":"ver=1.0, type=text, body={\u0022action\u0022:\u0022ACCEPT\u0022,\u0022reqId\u0022:2,\u0022error\u0022:0,\u0022connRequest\u0022:{\u0022sid\u0022:1,\u0022peerSid\u0022:24043,\u0022skey\u0022:\u0022AAAAAAAAAAAAAAAAAAAAAA==\u0022,\u0022natType\u0022:0,\u0022candidate\u0022:[{\u0022type\u0022:\u0022LOCAL\u0022,\u0022addr\u0022:\u0022192.168.1.50\u0022,\u0022mappedAddr\u0022:\u0022192.168.1.9\u0022,\u0022port\u0022:9303,\u0022mappedPort\u0022:51000}],\u0022defaultRouteMacAddr\u0022:\u0022\u0022,\u0022localPeerAddr\u0022:,\u0022localHashedId\u0022:\u0022\u0022}}","to":[{"accountId":"42","deviceUniqueId":"CONSOLE-DUID","platform":"PS5"}]}"#)
        }

        @Test("leave, readback, console list and push lookup hit the .NET URLs and headers")
        func urls() async throws {
            let cloud = await client(reply: "")
            try await cloud.leaveSession(id: "s-1")
            #expect(last.method == "DELETE")
            #expect(last.url == CloudEndpoints.sessions + "/s-1/members/me")

            StubURLProtocol.install { _ in (200, #"{"remotePlaySessions":[{"sessionId":"s-1","members":[{"accountId":"42","platform":"REMOTE_PLAY","deviceUniqueId":"me"}]}]}"#) }
            let read = try await cloud.session(id: "s-1")
            #expect(last.header("X-PSN-SESSION-MANAGER-SESSION-IDS") == "s-1")
            #expect(read.first?.members?.first == SessionMember(accountID: "42", platform: "REMOTE_PLAY", deviceUniqueID: "me"))

            StubURLProtocol.install { _ in (200, #"{"clients":[{"duid":"D1","platform":"PS5","device":{"name":"Living room","enabledFeatures":["remotePlay"],"wakeupEnabledPowerModes":["standby"]}},{"duid":"D2","platform":"PS5","device":{"name":"Off","enabledFeatures":[]}}]}"#) }
            let consoles = try await cloud.listConsoles()
            #expect(last.url == CloudEndpoints.consoleList + "?platform=PS5&includeFields=device&limit=10&offset=0")
            #expect(consoles.map(\.duid) == ["D1", "D2"])
            #expect(consoles[0].remotePlayEnabled && consoles[0].canWake)
            #expect(!consoles[1].remotePlayEnabled && !consoles[1].canWake)
            #expect(try await CloudDiscovery.consoles(cloud) == [CloudDiscoveredConsole(duid: "D1", name: "Living room", canWake: true)])

            StubURLProtocol.install { _ in (200, #"{"fqdn":"abc-pushcl.np.communication.playstation.net","keepAliveStatus":{"clientKeepAliveInterval":10000,"clientKeepAliveTimeout":40000,"serverKeepAliveTimeout":30000,"serverPresenceTimeout":30000}}"#) }
            let push = try await cloud.pushServer()
            #expect(last.url == CloudEndpoints.pushServerAddress)
            #expect(push.clientKeepAlive == .seconds(10))
            #expect(push.pushURL?.absoluteString == "wss://abc-pushcl.np.communication.playstation.net/np/pushNotification")
        }

        @Test("a failure status names the method, URL and status, and keeps the body")
        func failureStatus() async {
            let cloud = await client()
            StubURLProtocol.install { _ in (404, "gone") }
            do {
                try await cloud.leaveSession(id: "s-1")
                Issue.record("expected a failure")
            } catch {
                guard case let .requestFailed(method, url, status, body) = error else { Issue.record("\(error)"); return }
                #expect(method == "DELETE" && status == 404 && body == "gone")
                #expect(url.hasSuffix("/s-1/members/me"))
                #expect(error.isServiceError)
            }
        }

        @Test("a request that gets no answer is a transport error, not the service's")
        func transportFailure() async {
            let cloud = await client()
            StubURLProtocol.install { _ in (-1, "") }
            do {
                _ = try await cloud.accountInfo()
                Issue.record("expected a failure")
            } catch {
                #expect(!error.isServiceError)
            }
        }

        @Test("a call before sign-in fails as not signed in")
        func unsigned() async {
            StubURLProtocol.install { _ in (200, "{}") }
            let cloud = CloudClient(session: session, tokens: TokenProvider(auth: AuthClient(session: session, config: testConfig)))
            await #expect(throws: CloudError.self) { try await cloud.listConsoles() }
            #expect(StubURLProtocol.requests.isEmpty)
        }
    }

    @Suite("Cloud: token endpoint")
    struct TokenTests {
        let session = StubURLProtocol.session()
        let tokenReply = #"{"access_token":"at","refresh_token":"rt","token_type":"bearer","expires_in":3600,"scope":"x"}"#

        @Test("the code exchange is .NET's form, Basic auth and all")
        func exchange() async throws {
            StubURLProtocol.install { [tokenReply] _ in (200, tokenReply) }
            let auth = AuthClient(session: session, config: testConfig)
            let duid = ClientDeviceID.prefix + syntheticDeviceIDHex
            let tokens = try await auth.exchange(code: "co de+/~*", clientDeviceID: duid)
            #expect(tokens.accessToken == "at" && tokens.refreshToken == "rt")
            #expect(tokens.expiresAt.timeIntervalSinceNow > 3500)

            let request = StubURLProtocol.requests.last!
            #expect(request.method == "POST")
            #expect(request.url == CloudEndpoints.token)
            #expect(request.header("Authorization") == "Basic dGVzdC1jbGllbnQ6dGVzdC1zZWNyZXQ=")
            #expect(request.header("Content-Type") == "application/x-www-form-urlencoded")
            // FormUrlEncodedContent: uppercase %XX, a space as +, and ~ left alone.
            #expect(request.body == "grant_type=authorization_code&code=co+de%2B%2F~%2A"
                + "&redirect_uri=https%3A%2F%2Fremoteplay.dl.playstation.net%2Fremoteplay%2Fredirect"
                + "&device_type=PC_APP&duid=" + duid)
        }

        @Test("the refresh grant is .NET's form")
        func refresh() async throws {
            StubURLProtocol.install { [tokenReply] _ in (200, tokenReply) }
            _ = try await AuthClient(session: session, config: testConfig).refresh(refreshToken: "r+t/=")
            #expect(StubURLProtocol.requests.last!.body == "grant_type=refresh_token&refresh_token=r%2Bt%2F%3D"
                + "&scope=psn%3Aclientapp+referenceDataService%3AcountryConfig.read"
                + "+pushNotification%3AwebSocket.desktop.connect+sessionManager%3AremotePlaySession.system.update"
                + "+sbahn%3Apc.telemetry.publish")
        }

        @Test("without a credential the token request explains itself rather than sending for a 401")
        func unconfigured() async {
            StubURLProtocol.install { _ in (200, "{}") }
            do {
                _ = try await AuthClient(session: session, config: .unconfigured).refresh(refreshToken: "some-token")
                Issue.record("expected a failure")
            } catch {
                #expect(String(describing: error).localizedCaseInsensitiveContains("no oauth client credential"))
            }
            #expect(StubURLProtocol.requests.isEmpty)
        }

        @Test("a rejected grant is a token failure with the status")
        func rejected() async {
            StubURLProtocol.install { _ in (400, #"{"error":"invalid_grant"}"#) }
            do {
                _ = try await AuthClient(session: session, config: testConfig).refresh(refreshToken: "dead")
                Issue.record("expected a failure")
            } catch {
                guard case .tokenRequestFailed(400, _) = error else { Issue.record("\(error)"); return }
            }
        }

        @Test("an expired access token is refreshed once, however many callers ask at once")
        func sharedRefresh() async throws {
            StubURLProtocol.install { [tokenReply] _ in (200, tokenReply) }
            let provider = TokenProvider(auth: AuthClient(session: session, config: testConfig))
            await provider.seed(AccountTokens(accessToken: "old", refreshToken: "r0", expiresAt: Date().addingTimeInterval(30)))
            async let a = provider.accessToken()
            async let b = provider.accessToken()
            #expect(try await [a, b] == ["at", "at"])
            #expect(StubURLProtocol.requests.count == 1)
            #expect(await provider.current?.refreshToken == "rt")
        }
    }

    @Suite("Cloud: the account gateway")
    struct GatewayTests {
        let session = StubURLProtocol.session()

        /// Answers the token endpoint with `refresh` (rotating to `rotated`) and the account lookup with `info`.
        private func install(tokenStatus: Int = 200, rotated: String = "rotated-token", infoStatus: Int = 200) {
            StubURLProtocol.install { request in
                if request.url == CloudEndpoints.token {
                    return (tokenStatus, #"{"access_token":"at","refresh_token":""# + rotated + #"","expires_in":3600}"#)
                }
                if request.url == CloudEndpoints.accountInfo {
                    return (infoStatus, #"{"accountId":"4200000000000000042","onlineId":"somebody","region":"gb"}"#)
                }
                return (404, "")
            }
        }

        private func gateway(_ store: any AccountTokenStore, config: ClientConfig = testConfig) throws -> AccountGateway {
            try AccountGateway(session: session, config: config, store: store, deviceID: syntheticDeviceID)
        }

        @Test("complete sign-in exchanges the code, identifies the account and keeps the refresh token")
        func completeSignIn() async throws {
            install()
            let store = InMemoryAccountTokenStore()
            let gateway = try gateway(store)
            let account = try await gateway.completeSignIn(redirect: URL(string: ClientConfig.defaultRedirectURI + "?code=abc")!)
            #expect(account == CloudAccount(accountID: "4200000000000000042", onlineID: "somebody", region: "gb"))
            #expect(store.load()?.refreshToken == "rotated-token")
            #expect(store.load()?.accountID == "4200000000000000042")
            #expect(await gateway.isSignedIn)
            #expect(StubURLProtocol.requests.first?.body.contains("code=abc") == true)
        }

        @Test("a redirect with no code is refused before any request")
        func noCode() async throws {
            install()
            let gateway = try gateway(InMemoryAccountTokenStore())
            await #expect(throws: CloudError.self) {
                try await gateway.completeSignIn(redirect: URL(string: ClientConfig.defaultRedirectURI)!)
            }
            #expect(StubURLProtocol.requests.isEmpty)
        }

        @Test("restore trades the stored token for a session and keeps the rotated one")
        func restore() async throws {
            install()
            let store = InMemoryAccountTokenStore(StoredAccountSession(refreshToken: "stored-token"))
            let account = try await gateway(store).restore()
            #expect(account?.accountID == "4200000000000000042")
            #expect(store.load()?.refreshToken == "rotated-token")
            #expect(StubURLProtocol.requests.first?.body.contains("refresh_token=stored-token") == true)
        }

        @Test("a rejected stored token clears the store")
        func restoreRejected() async throws {
            install(tokenStatus: 400)
            let store = InMemoryAccountTokenStore(StoredAccountSession(refreshToken: "dead-token"))
            #expect(try await gateway(store).restore() == nil)
            #expect(store.load() == nil)
        }

        @Test("an unreachable service keeps the stored token: an outage is not a sign-out")
        func restoreOffline() async throws {
            install(tokenStatus: -1)
            let store = InMemoryAccountTokenStore(StoredAccountSession(refreshToken: "good-token"))
            await #expect(throws: CloudError.self) { try await gateway(store).restore() }
            #expect(store.load()?.refreshToken == "good-token")
        }

        @Test("a refresh that works but an account lookup that fails keeps the cached identity")
        func restoreLookupFails() async throws {
            install(infoStatus: 503)
            let store = InMemoryAccountTokenStore(StoredAccountSession(refreshToken: "good-token",
                                                                       accountID: "4200000000000000042",
                                                                       displayName: "somebody"))
            let account = try await gateway(store).restore()
            #expect(account == CloudAccount(accountID: "4200000000000000042", onlineID: "somebody", region: ""))
        }

        @Test("ensureSignedIn restores once for callers arriving together")
        func ensureSignedInOnce() async throws {
            install()
            let gateway = try gateway(InMemoryAccountTokenStore(StoredAccountSession(refreshToken: "stored-token")))
            async let a = gateway.ensureSignedIn()
            async let b = gateway.ensureSignedIn()
            #expect(await [a, b] == [true, true])
            #expect(StubURLProtocol.requests.filter { $0.url == CloudEndpoints.token }.count == 1)
        }

        @Test("ensureSignedIn with nothing stored is false and touches nothing")
        func ensureSignedInNothing() async throws {
            install()
            #expect(try await !gateway(InMemoryAccountTokenStore()).ensureSignedIn())
            #expect(StubURLProtocol.requests.isEmpty)
        }

        @Test("a build without a credential cannot begin, and forms no device id")
        func unconfigured() throws {
            let gateway = try gateway(InMemoryAccountTokenStore(), config: .unconfigured)
            #expect(!gateway.canSignIn)
            #expect(gateway.clientDeviceID.isEmpty)
            #expect(throws: CloudError.self) { try gateway.beginSignIn() }
        }

        @Test("a machine without a device id fails at construction")
        func noDeviceID() {
            #expect(throws: CloudError.self) {
                try AccountGateway(session: session, config: testConfig, store: InMemoryAccountTokenStore(), deviceID: [])
            }
        }

        @Test("sign-out destroys the stored credential")
        func signOut() async throws {
            install()
            let store = InMemoryAccountTokenStore()
            let gateway = try gateway(store)
            _ = try await gateway.completeSignIn(redirect: URL(string: ClientConfig.defaultRedirectURI + "?code=abc")!)
            await gateway.signOut()
            #expect(store.load() == nil)
            #expect(await !gateway.isSignedIn)
        }
    }
}
