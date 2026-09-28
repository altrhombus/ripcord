// One streaming session, over the Rust engine's connect sequence (ripcord.h, `ripcord_client_*`, whose
// contract is libripcord/client/halyard_client.h's).
//
// The contract is one host-owned thread, and this is that thread: a dedicated Thread at user-interactive
// priority, not a task on the cooperative pool, because connect blocks. Everything
// else in the app talks to the session through three lock-protected slots (the latest pad state, a
// pending passcode, pending commands) that the engine pulls on this thread, and receives what the session
// produces through the handlers below, also called on this thread. A handler copies what it needs and
// returns: the PS3's shutdown lockups (b129-b142) came from work that did not.
//
// Video leaves as CMSampleBuffers ready for a display layer or a decoder; audio as 10 ms PCM buffers.
// The conversions (AnnexBSampleBuilder, OpusDecoder) run here, on the session thread, since both are
// cheap and both need state that belongs to one stream.

internal import CRipcordEngine
import AVFAudio
import CoreMedia
import Foundation
import Synchronization

public enum SessionStage: Int, Sendable, Comparable {
    case idle, controlOpen, signedIn, sessionReady, senkushaUp, takionUp, streamKeys, streamReady, streaming, ended

    init(_ c: RipcordClientStage) { self = SessionStage(rawValue: Int(c.rawValue)) ?? .idle }
    public static func < (a: Self, b: Self) -> Bool { a.rawValue < b.rawValue }
}

public enum SessionEndReason: Int, Sendable {
    case none, userDisconnect, hostCancel, consoleClosed, channelError, signInCancelled, signInRejected,
         signInNoSession, timeout, refused
    /// RENDEZVOUS: the media negotiation gave up, or no media peer answered in time (END_NO_MEDIA).
    case noMedia
    /// RENDEZVOUS, Swift's own: the cloud half failed or stopped before the connect began. Not a C value;
    /// `SessionOutcome.failure` says why.
    case rendezvousFailed = 100
}

public struct SessionStreamInfo: Sendable {
    public let width: Int
    public let height: Int
    public let codec: VideoCodec
}

public struct SessionStats: Sendable {
    public let packetsReceived, packetsLost, videoFrames, audioFrames, keyframes: UInt64
    public let kbps: UInt32
    public let msSinceConsoleActivity, msSinceVideoFrame: UInt32
    public let idrRequests: UInt32
    /// The stream channel's smoothed round trip, and where the adaptive ladder stands: the bitrate it would
    /// ask for and its rung's height (sent to the console only with `reportConnectionQuality`).
    public let rttMs: Double
    public let targetBitrateKbps: UInt32
    public let targetHeight: UInt32

    /// For a caller that stands in for the engine: the app's tests.
    public init(packetsReceived: UInt64 = 0, packetsLost: UInt64 = 0, videoFrames: UInt64 = 0, audioFrames: UInt64 = 0,
                keyframes: UInt64 = 0, kbps: UInt32 = 0, msSinceConsoleActivity: UInt32 = 0, msSinceVideoFrame: UInt32 = 0,
                idrRequests: UInt32 = 0, rttMs: Double = 0, targetBitrateKbps: UInt32 = 0, targetHeight: UInt32 = 0) {
        self.packetsReceived = packetsReceived
        self.packetsLost = packetsLost
        self.videoFrames = videoFrames
        self.audioFrames = audioFrames
        self.keyframes = keyframes
        self.kbps = kbps
        self.msSinceConsoleActivity = msSinceConsoleActivity
        self.msSinceVideoFrame = msSinceVideoFrame
        self.idrRequests = idrRequests
        self.rttMs = rttMs
        self.targetBitrateKbps = targetBitrateKbps
        self.targetHeight = targetHeight
    }
}

public struct SessionOutcome: Sendable {
    public let stage: SessionStage
    public let endReason: SessionEndReason
    /// Always 0 on the Rust engine, which has no separate control-session error code; kept so a caller
    /// written against the C core still compiles. The end reason says what happened.
    public let controlError: Int
    /// RIPCORD_CURVE_* of the stream's key agreement: 1 P-256, 2 P-521, 0 before it ran.
    public let curve: Int
    /// Why a rendezvous never reached the connect, in words; nil otherwise.
    public let failure: String?

    init(stage: SessionStage, endReason: SessionEndReason, controlError: Int, curve: Int, failure: String? = nil) {
        self.stage = stage
        self.endReason = endReason
        self.controlError = controlError
        self.curve = curve
        self.failure = failure
    }
}

/// What a session produces. Every handler runs on the session thread.
public struct SessionHandlers: Sendable {
    public var stage: @Sendable (SessionStage) -> Void = { _ in }
    public var streamInfo: @Sendable (SessionStreamInfo) -> Void = { _ in }
    /// A sample ready for AVSampleBufferDisplayLayer or VTDecompressionSession.
    public var video: @Sendable (CMSampleBuffer, _ isKeyframe: Bool) -> Void = { _, _ in }
    public var audio: @Sendable (AVAudioPCMBuffer) -> Void = { _ in }
    public var stats: @Sendable (SessionStats) -> Void = { _ in }
    public var log: @Sendable (String) -> Void = { _ in }
    /// The console wants its user's passcode. Answer with supplyPasscode(_:) when a person has typed it,
    /// or cancel(). `retry` counts refusals so far.
    public var passcodeRequested: @Sendable (_ retry: Int) -> Void = { _ in }
    public var ended: @Sendable (SessionOutcome) -> Void = { _ in }

    public init() {}
}

public final class ConsoleSession: Sendable {
    public struct Options: Sendable {
        /// 25 Mb/s because the console grants resolution by bitrate, not by request alone. Measured on a
        /// PS5, 2026-09-25: asked for 1920x1080 at the core's 10 Mb/s default, it streamed 1280x720; asked
        /// again at 25 Mb/s, it streamed 1920x1080. 23.2 Mb/s is the recorded 1080p60 figure (README).
        public var width = 1920, height = 1080, fps = 60, bitrateKbps = 25_000
        public var allowHEVC = true
        public var hdr = false
        /// Tell the console the adaptive ladder's target bitrate (CONNECTION_QUALITY). Off by default: its
        /// unit is unconfirmed, and a wrong guess by 1000x would have the console choose an absurd rate.
        public var reportConnectionQuality = false
        /// A diagnostic: the control channel's echo probe, which proves the control crypto is being read.
        public var controlEchoProbe = false
        /// How the console is reached. `.local` is the LAN (TCP 9295 and the console's UDP ports), and is
        /// exactly what this session did before the rendezvous route existed; `.rendezvous` is the account
        /// route over 9303 and a negotiated A/V leg, driven with the cloud tier (RendezvousLink.swift).
        public var route: Route = .local
        public init() {}
    }

    public enum Route: Sendable {
        case local
        case rendezvous(RendezvousRoute)

        var rendezvous: RendezvousRoute? {
            if case .rendezvous(let r) = self { return r }
            return nil
        }
    }

    private let state: Mutex<SharedState>
    private let console: PairedConsole
    private let options: Options
    private let handlers: SessionHandlers

    struct SharedState {
        var pad: RipcordInputState?
        var passcode: String?
        var passcodeCancelled = false
        var passcodeAsked = -1
        var commands: UInt32 = 0
        var started = false
        /// Latched by cancel() and disconnect(): unlike `commands`, the core's pull does not clear it, so the
        /// rendezvous wait before connect sees a stop the core already consumed inside a blocking 9303 stage.
        var stopRequested = false
    }

    public init(console: PairedConsole, options: Options = Options(), handlers: SessionHandlers) {
        self.console = console
        self.options = options
        self.handlers = handlers
        self.state = Mutex(SharedState())
    }

    /// Starts the session thread. Calling it twice does nothing.
    public func start() {
        let first = state.withLock { s -> Bool in
            defer { s.started = true }
            return !s.started
        }
        guard first else { return }
        let thread = Thread { [self] in run() }
        thread.name = "Ripcord session"
        thread.qualityOfService = .userInteractive
        thread.start()
    }

    public func update(pad: PadSnapshot?) { state.withLock { $0.pad = pad?.wireState } }
    public func supplyPasscode(_ digits: String) { state.withLock { $0.passcode = digits } }
    public func requestKeyframe() { state.withLock { $0.commands |= UInt32(RIPCORD_CMD_KEYFRAME) } }
    public func cancel() {
        state.withLock { $0.commands |= UInt32(RIPCORD_CMD_CANCEL); $0.passcodeCancelled = true; $0.stopRequested = true }
    }

    /// Goodbye: the console is asked to rest first only when `restConsole`, which should mean a person
    /// chose it.
    public func disconnect(restConsole: Bool = false) {
        state.withLock {
            $0.commands |= UInt32(RIPCORD_CMD_DISCONNECT) | (restConsole ? UInt32(RIPCORD_CMD_REST_CONSOLE) : 0)
            $0.stopRequested = true
        }
    }

    // MARK: - The session thread

    /// Everything the engine's callbacks need, reached through the `user` pointer. Owned by run().
    final class Context {
        let session: ConsoleSession
        var builder: AnnexBSampleBuilder?
        var opus: OpusDecoder?
        var frameIndex: Int64 = 0
        /// RENDEZVOUS: set before connect, read by poll_media.
        var link: RendezvousLink?
        var media: (@Sendable (RendezvousLeg) async throws -> MediaEndpoint?)?
        init(_ session: ConsoleSession) { self.session = session }

        /// poll_media: the first call starts the host's negotiation on a task of its own and says "not yet";
        /// later calls say "not yet" until it answers, then hand the console's endpoint to the engine.
        func pollMedia(_ leg: RipcordLeg, _ out: UnsafeMutablePointer<RipcordPeer>) -> Int32 {
            guard let link, let media else { return -1 }
            switch link.mediaState() {
            case .notAsked:
                link.markMediaPending()
                let info = RendezvousLeg(leg)
                let log = session.handlers.log
                log("rendezvous: A/V leg on local port \(info.localPort)\(info.reflexive.map { ", reflexive \($0)" } ?? ""); negotiating")
                Task {
                    var answer: MediaEndpoint?
                    do { answer = try await media(info) } catch { log("rendezvous: the media negotiation failed: \(error)") }
                    link.answerMedia(answer)
                }
                return 0
            case .pending:
                return 0
            case .answered(let endpoint):
                guard let endpoint, let peer = ConsoleSession.peer(address: endpoint.address, port: endpoint.port,
                                                                    hashedID: endpoint.consoleHashedID) else { return -1 }
                out.pointee = peer
                return 1
            }
        }
    }

    /// A RipcordPeer, or nil when the address is not a dotted quad or the id not 20 bytes.
    static func peer(address: String, port: Int, hashedID: [UInt8]) -> RipcordPeer? {
        guard let path = ConsolePath(address: address, port: port), hashedID.count == 20 else { return nil }
        return RipcordPeer.make(path: path, consoleHashedID: hashedID)
    }

    /// The engine's configuration for `options`, minus the pairing's pointers and the STUN list, which run()
    /// fills for the duration of ripcord_client_new - the engine copies them there. On `.local` it is the LAN
    /// configuration this session has always used; the rendezvous fields stay zero.
    static func makeConfig(_ options: Options) -> RipcordClientConfig {
        var config = RipcordClientConfig()
        config.route = UInt32(RIPCORD_ROUTE_LOCAL)
        config.width = Int32(options.width)
        config.height = Int32(options.height)
        config.fps = Int32(options.fps)
        config.bitrate_kbps = Int32(options.bitrateKbps)
        config.allow_hevc = options.allowHEVC
        config.hdr = options.hdr
        config.report_connection_quality = options.reportConnectionQuality
        config.control_echo_probe = options.controlEchoProbe
        config.require_session_ready = 0   // the default: wait for SESSION_ID on the LAN

        if let r = options.route.rendezvous {
            config.route = UInt32(RIPCORD_ROUTE_RENDEZVOUS)
            config.control_local_port = r.controlLocalPort
            config.media_local_port = r.mediaLocalPort
            config.media_offer_timeout_ms = r.mediaOfferTimeout.clampedMilliseconds
            config.dgram_stage_timeout_ms = r.dgramStageTimeout.clampedMilliseconds
            config.dgram_receive_timeout_ms = r.dgramReceiveTimeout.clampedMilliseconds
        }
        return config
    }

    private func run() {
        let rendezvous = options.route.rendezvous
        var config = Self.makeConfig(options)
        guard let host = ConsolePath.ipv4(console.host) else {
            handlers.ended(SessionOutcome(stage: .idle, endReason: .channelError, controlError: 0, curve: 0,
                                          failure: "\(console.host) is not an IPv4 address"))
            return
        }
        config.console = (host[0], host[1], host[2], host[3])
        config.is_ps5 = console.family == .ps5
        withUnsafeMutableBytes(of: &config.companion) { $0.copyBytes(from: console.companion.prefix(16)) }
        // Resolved here, on the session thread, so building Options touches no network. Copied at init.
        var stun = rendezvous.map { StunServer.resolve($0.stunServers) } ?? []
        if let address = rendezvous?.bindAddress {
            guard let parsed = ConsolePath.ipv4(address) else {
                handlers.ended(SessionOutcome(stage: .idle, endReason: .channelError, controlError: 0, curve: 0,
                                              failure: "\(address) is not an IPv4 address to bind"))
                return
            }
            config.bind_address = (parsed[0], parsed[1], parsed[2], parsed[3])
        }
        if let rendezvous, !rendezvous.stunServers.isEmpty {
            handlers.log("rendezvous: \(stun.count) of \(rendezvous.stunServers.count) STUN server(s) resolved")
        }

        let context = Context(self)
        context.opus = try? OpusDecoder()
        let user = Unmanaged.passRetained(context)
        defer { user.release() }

        let key = console.registrationKey, device = console.deviceID
        let pin = console.loginPIN
        let client: OpaquePointer? = key.withUnsafeBufferPointer { k in
            device.withUnsafeBufferPointer { d in
                stun.withUnsafeMutableBufferPointer { servers in
                    withOptionalCString(pin) { p in
                        config.registration_key = k.baseAddress
                        config.registration_key_length = k.count
                        config.device_id = d.baseAddress
                        config.device_id_length = d.count
                        config.login_pin = p
                        config.stun_servers = servers.isEmpty ? nil : UnsafePointer(servers.baseAddress)
                        config.stun_server_count = servers.count
                        var callbacks = Self.callbacks(user.toOpaque(), rendezvous: rendezvous != nil)
                        var random = EngineRandom.table
                        var ecdh = CryptoKitEngineECDH.backend
                        return ripcord_client_new(&config, &callbacks, &ecdh, &random)
                    }
                }
            }
        }
        guard let client else {
            handlers.ended(SessionOutcome(stage: .idle, endReason: .channelError, controlError: 0, curve: 0,
                                          failure: RipcordEngineLayout.matches() ? nil : "the engine's ABI does not match RipcordKit"))
            return
        }

        var plan = RendezvousPlan()
        if let rendezvous {
            plan = runRendezvous(client, rendezvous, context: context)
        }

        if rendezvous == nil || plan.proceed != nil {
            var stage = RIPCORD_CLIENT_STAGE_IDLE
            _ = ripcord_client_connect(client, &stage)
            if stage.rawValue >= RIPCORD_CLIENT_STAGE_STREAM_READY.rawValue {
                // The engine waits on its own sockets inside pump, for at most this long a call.
                var alive = true
                while alive, ripcord_client_pump(client, 10, &alive) == RIPCORD_STATUS_OK {}
            }
        }
        var r = RipcordClientResult()
        _ = ripcord_client_result(client, &r)
        var outcome = SessionOutcome(stage: SessionStage(r.stage),
                                     endReason: SessionEndReason(rawValue: Int(r.end_reason.rawValue)) ?? .none,
                                     controlError: 0, curve: Int(r.curve))
        if let failure = plan.failure {
            outcome = SessionOutcome(stage: outcome.stage, endReason: plan.cancelled ? .hostCancel : .rendezvousFailed,
                                     controlError: 0, curve: outcome.curve, failure: failure)
        }
        ripcord_client_free(client)

        // Step 8: the sockets are closed and the console told; only now is the cloud session left.
        plan.link?.close("the session has ended")
        if let proceed = plan.proceed {
            Self.waitFor(Self.teardownBound) { await proceed.onEnd() }
        } else if let driver = plan.driver {
            // Never proceeded: stop the cloud half, and let it leave its session before reporting the end.
            driver.task.cancel()
            _ = driver.done.wait(timeout: .now() + .milliseconds(Int(Self.teardownBound.clampedMilliseconds)))
        }
        handlers.ended(outcome)
    }

    // MARK: - The rendezvous, on the session thread

    private struct RendezvousPlan {
        var link: RendezvousLink?
        var driver: (task: Task<Void, Never>, done: DispatchSemaphore)?
        var proceed: RendezvousLink.Proceed?
        var failure: String?
        var cancelled = false
    }

    /// How long the end of a session waits for the cloud half to leave its session.
    static let teardownBound: Duration = .seconds(10)

    /// Steps 2 to 5a of the route: prepare the control leg, start the cloud half with a link
    /// to it, and run what it asks for until it says proceed (or fails, or the host stops the session).
    private func runRendezvous(_ client: OpaquePointer, _ route: RendezvousRoute, context: Context) -> RendezvousPlan {
        var plan = RendezvousPlan()
        var leg = RipcordLeg()
        guard ripcord_client_rendezvous_prepare(client, &leg) == RIPCORD_STATUS_OK else {
            plan.failure = "the control leg could not be bound"
            return plan
        }
        let link = RendezvousLink(controlLeg: RendezvousLeg(leg))
        plan.link = link
        handlers.log("rendezvous: control leg on local port \(link.controlLeg.localPort)"
                     + (link.controlLeg.reflexive.map { ", reflexive \($0)" } ?? ", no reflexive address"))

        let done = DispatchSemaphore(value: 0)
        let drive = route.drive
        let task = Task {
            defer { done.signal() }
            do {
                try await drive(link)
                link.abandon("the rendezvous finished without letting the session proceed")
            } catch {
                link.abandon("\(error)")
            }
        }
        plan.driver = (task, done)

        let deadline = ContinuousClock.now.advanced(by: route.driveTimeout)
        while plan.failure == nil {
            if state.withLock({ $0.stopRequested }) {
                plan.failure = "cancelled before the connect began"
                plan.cancelled = true
                break
            }
            if ContinuousClock.now >= deadline {
                plan.failure = "the cloud half did not finish within \(route.driveTimeout)"
                break
            }
            guard let job = link.takeJob() else {
                usleep(5_000)
                continue
            }
            switch job {
            case .begin(let request, let continuation):
                var peer = RipcordPeer.make(path: request.path, consoleHashedID: request.consoleHashedID)
                if ripcord_client_rendezvous_begin(client, request.localHashedID, &peer) == RIPCORD_STATUS_OK {
                    continuation.resume()
                } else {
                    continuation.resume(throwing: PairingError.associationFailed("our Init"))
                }

            case .register(let request, let continuation):
                do {
                    continuation.resume(returning: try register(request, client: client))
                } catch {
                    continuation.resume(throwing: error)
                }

            case .proceed(let proceed):
                plan.proceed = proceed
                context.link = link
                context.media = proceed.media
                link.close("the session has moved on to its connect")
                return plan

            case .abandon(let reason):
                plan.failure = reason
            }
        }
        link.close(plan.failure ?? "the rendezvous ended")
        return plan
    }

    /// Step 5a on the session thread: /sess/rgst over the session's own association. The record names the
    /// console this session was opened for.
    private func register(_ request: RendezvousLink.RegisterRequest, client: OpaquePointer) throws(PairingError) -> PairedConsole {
        guard let path = ConsolePath.control(request.context) else { throw .associationFailed("no console endpoint to register with") }
        let clientIP = try Pairing.localAddress(toward: path.text)
        return try AccountRegistration.run(client, family: request.family, accountID: request.accountID,
                                           clientIP: clientIP, seed: request.seed, host: console.host,
                                           name: console.name, consoleID: console.consoleID)
    }

    /// Runs async work from the session thread and waits for it, at most `bound`.
    private static func waitFor(_ bound: Duration, _ work: @escaping @Sendable () async -> Void) {
        let done = DispatchSemaphore(value: 0)
        Task {
            await work()
            done.signal()
        }
        _ = done.wait(timeout: .now() + .milliseconds(Int(bound.clampedMilliseconds)))
    }

    private static func callbacks(_ user: UnsafeMutableRawPointer, rendezvous: Bool) -> RipcordClientCallbacks {
        var c = RipcordClientCallbacks()
        c.user = user
        c.log = { user, _, line in
            guard let line else { return }
            sessionContext(user).session.handlers.log(String(cString: line))
        }
        c.stage = { user, stage in sessionContext(user).session.handlers.stage(SessionStage(stage)) }
        c.stream_info = { user, info in
            guard let info = info?.pointee else { return }
            let ctx = sessionContext(user)
            let codec: VideoCodec = info.is_hevc ? .hevc : .h264
            ctx.builder = AnnexBSampleBuilder(codec: codec)
            ctx.session.handlers.streamInfo(SessionStreamInfo(width: Int(info.width), height: Int(info.height), codec: codec))
        }
        c.video_frame = { user, data, length, isKeyframe in
            guard let data else { return }
            let ctx = sessionContext(user)
            guard var builder = ctx.builder else { return }
            defer { ctx.builder = builder }
            // A live stream has no timeline to honour: each frame is shown on arrival (the builder marks
            // it DisplayImmediately), so the timestamp only has to increase.
            let pts = CMTime(value: ctx.frameIndex, timescale: 60)
            ctx.frameIndex += 1
            if let sample = try? builder.sampleBuffer(fromAccessUnit: UnsafeRawBufferPointer(start: data, count: length),
                                                      isKeyframe: isKeyframe, presentationTime: pts) {
                ctx.session.handlers.video(sample, isKeyframe)
            }
        }
        c.audio_frame = { user, data, length in
            guard let data else { return }
            let ctx = sessionContext(user)
            if let pcm = try? ctx.opus?.decode(UnsafeRawBufferPointer(start: data, count: length)) {
                ctx.session.handlers.audio(pcm)
            }
        }
        c.poll_input = { user, out in
            guard let out, let pad = sessionContext(user).session.state.withLock({ $0.pad }) else { return false }
            out.pointee = pad
            return true
        }
        c.poll_passcode = { user, retry, out, outSize in
            let session = sessionContext(user).session
            let (digits, cancelled, firstAsk) = session.state.withLock { s -> (String?, Bool, Bool) in
                let first = s.passcodeAsked != Int(retry)
                s.passcodeAsked = Int(retry)
                defer { s.passcode = nil }
                return (s.passcode, s.passcodeCancelled, first)
            }
            if cancelled { return -1 }
            if firstAsk { session.handlers.passcodeRequested(Int(retry)) }
            guard let digits, let out, outSize > 0 else { return 0 }
            let bytes = Array(digits.utf8.prefix(outSize - 1)) + [0]
            bytes.withUnsafeBytes { UnsafeMutableRawPointer(out).copyMemory(from: $0.baseAddress!, byteCount: bytes.count) }
            return 1
        }
        c.poll_commands = { user in
            sessionContext(user).session.state.withLock { s in
                defer { s.commands = 0 }
                return s.commands
            }
        }
        c.stats = { user, stats in
            guard let s = stats?.pointee else { return }
            sessionContext(user).session.handlers.stats(SessionStats(
                packetsReceived: s.packets_received, packetsLost: s.packets_lost, videoFrames: s.video_frames,
                audioFrames: s.audio_frames, keyframes: s.keyframes, kbps: s.kbps,
                msSinceConsoleActivity: s.ms_since_console_activity, msSinceVideoFrame: s.ms_since_video_frame,
                idrRequests: s.idr_requests, rttMs: s.rtt_ms, targetBitrateKbps: s.target_bitrate_kbps,
                targetHeight: s.target_height))
        }
        // Only on the rendezvous route: a LAN session's callbacks are exactly what they were.
        if rendezvous {
            c.poll_media = { user, media, out in
                guard let media, let out else { return -1 }
                return sessionContext(user).pollMedia(media.pointee, out)
            }
        }
        return c
    }
}

/// The engine callbacks' way back to Swift. At file scope because a C function pointer cannot capture, and a
/// static method called from inside one counts as capturing its type.
private func sessionContext(_ user: UnsafeMutableRawPointer?) -> ConsoleSession.Context {
    Unmanaged<ConsoleSession.Context>.fromOpaque(user!).takeUnretainedValue()
}

/// A C string for an optional Swift string, valid for the closure; nil stays nil.
private func withOptionalCString<R>(_ text: String?, _ body: (UnsafePointer<CChar>?) -> R) -> R {
    guard let text else { return body(nil) }
    return text.withCString { body($0) }
}
