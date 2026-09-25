// One streaming session, over libripcord's connect sequence (libripcord/client/halyard_client.h).
//
// The contract there is one host-owned thread, and this is that thread: a dedicated Thread at
// user-interactive priority, not a task on the cooperative pool, because connect() blocks. Everything
// else in the app talks to the session through three lock-protected slots (the latest pad state, a
// pending passcode, pending commands) that the core pulls on this thread, and receives what the session
// produces through the handlers below, also called on this thread. A handler copies what it needs and
// returns: the PS3's shutdown lockups (b129-b142) came from work that did not.
//
// Video leaves as CMSampleBuffers ready for a display layer or a decoder; audio as 10 ms PCM buffers.
// The conversions (AnnexBSampleBuilder, OpusDecoder) run here, on the session thread, since both are
// cheap and both need state that belongs to one stream.

internal import CLibripcord
import AVFAudio
import CoreMedia
import Foundation
import Synchronization

public enum SessionStage: Int, Sendable, Comparable {
    case idle, controlOpen, signedIn, sessionReady, senkushaUp, takionUp, streamKeys, streamReady, streaming, ended

    init(_ c: halyard_client_stage) { self = SessionStage(rawValue: Int(c.rawValue)) ?? .idle }
    public static func < (a: Self, b: Self) -> Bool { a.rawValue < b.rawValue }
}

public enum SessionEndReason: Int, Sendable {
    case none, userDisconnect, hostCancel, consoleClosed, channelError, signInCancelled, signInRejected,
         signInNoSession, timeout, refused
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
}

public struct SessionOutcome: Sendable {
    public let stage: SessionStage
    public let endReason: SessionEndReason
    public let controlError: Int
    public let curve: Int
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
        public var width = 1920, height = 1080, fps = 60, bitrateKbps = 0
        public var allowHEVC = true
        public var hdr = false
        public init() {}
    }

    private let state: Mutex<SharedState>
    private let console: PairedConsole
    private let options: Options
    private let handlers: SessionHandlers

    struct SharedState {
        var pad: halyard_input_state?
        var passcode: String?
        var passcodeCancelled = false
        var passcodeAsked = -1
        var commands: UInt32 = 0
        var started = false
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
    public func requestKeyframe() { state.withLock { $0.commands |= HALYARD_CLIENT_CMD_KEYFRAME } }
    public func cancel() { state.withLock { $0.commands |= HALYARD_CLIENT_CMD_CANCEL; $0.passcodeCancelled = true } }

    /// Goodbye: the console is asked to rest first only when `restConsole`, which should mean a person
    /// chose it (halyard_client.h).
    public func disconnect(restConsole: Bool = false) {
        state.withLock { $0.commands |= HALYARD_CLIENT_CMD_DISCONNECT | (restConsole ? HALYARD_CLIENT_CMD_REST_CONSOLE : 0) }
    }

    // MARK: - The session thread

    /// Everything the C callbacks need, reached through the `user` pointer. Owned by run().
    final class Context {
        let session: ConsoleSession
        var builder: AnnexBSampleBuilder?
        var opus: OpusDecoder?
        var frameIndex: Int64 = 0
        init(_ session: ConsoleSession) { self.session = session }
    }

    private func run() {
        let size = halyard_client_struct_size()
        let storage = UnsafeMutableRawPointer.allocate(byteCount: size, alignment: 16)
        defer { storage.deallocate() }

        var record = console.record
        var config = halyard_client_config()
        config.route = HALYARD_ROUTE_LOCAL
        config.width = Int32(options.width)
        config.height = Int32(options.height)
        config.fps = Int32(options.fps)
        config.bitrate_kbps = Int32(options.bitrateKbps)
        config.allow_hevc = options.allowHEVC ? 1 : 0
        config.hdr = options.hdr ? 1 : 0
        config.require_session_ready = 1

        let context = Context(self)
        context.opus = try? OpusDecoder()
        let user = Unmanaged.passRetained(context)
        defer { user.release() }

        let outcome: SessionOutcome = withUnsafePointer(to: &record) { recordPointer in
            config.record = recordPointer
            var callbacks = Self.callbacks(user.toOpaque())
            guard let client = halyard_client_init(storage, size, &config, &callbacks) else {
                return SessionOutcome(stage: .idle, endReason: .channelError, controlError: 0, curve: 0)
            }
            defer { halyard_client_destroy(client) }

            if halyard_client_connect(client).rawValue >= HALYARD_CLIENT_STAGE_STREAM_READY.rawValue {
                var next: UInt32 = 0
                while halyard_client_pump(client, &next) == 1 {
                    Self.wait(on: client, milliseconds: next)
                }
            }
            let r = halyard_client_result_get(client)!.pointee
            return SessionOutcome(stage: SessionStage(r.stage), endReason: SessionEndReason(rawValue: Int(r.end_reason.rawValue)) ?? .none,
                                  controlError: Int(r.control_error), curve: Int(r.curve))
        }
        handlers.ended(outcome)
    }

    /// Sleeps on the session's sockets until data arrives or the core's next deadline, whichever is first.
    private static func wait(on client: OpaquePointer, milliseconds: UInt32) {
        guard milliseconds > 0 else { return }
        var fds = [Int32](repeating: -1, count: 8)
        let n = Int(halyard_client_fds(client, &fds, Int32(fds.count)))
        var descriptors = fds.prefix(n).map { pollfd(fd: $0, events: Int16(POLLIN), revents: 0) }
        _ = poll(&descriptors, nfds_t(descriptors.count), Int32(min(milliseconds, 100)))
    }

    private static func callbacks(_ user: UnsafeMutableRawPointer) -> halyard_client_callbacks {
        var c = halyard_client_callbacks()
        c.user = user
        c.log = { user, _, line in
            guard let line else { return }
            sessionContext(user).session.handlers.log(String(cString: line))
        }
        c.stage = { user, stage in sessionContext(user).session.handlers.stage(SessionStage(stage)) }
        c.stream_info = { user, info in
            guard let info = info?.pointee else { return }
            let ctx = sessionContext(user)
            let codec: VideoCodec = info.is_hevc != 0 ? .hevc : .h264
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
                                                      isKeyframe: isKeyframe != 0, presentationTime: pts) {
                ctx.session.handlers.video(sample, isKeyframe != 0)
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
            guard let out, let pad = sessionContext(user).session.state.withLock({ $0.pad }) else { return 0 }
            out.pointee = pad
            return 1
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
                idrRequests: s.idr_requests))
        }
        return c
    }
}

/// The C callbacks' way back to Swift. At file scope because a C function pointer cannot capture, and a
/// static method called from inside one counts as capturing its type.
private func sessionContext(_ user: UnsafeMutableRawPointer?) -> ConsoleSession.Context {
    Unmanaged<ConsoleSession.Context>.fromOpaque(user!).takeUnretainedValue()
}
