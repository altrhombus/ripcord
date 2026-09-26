// A whole LAN session through the Rust engine's client ABI, hosted the way the Mac will host it once it
// relinks (docs/engine-plan.md, Phase 3): @convention(c) callbacks with an Unmanaged box as the user
// pointer, CryptoKit as the key agreement, the system CSPRNG, connect, then pump until the session ends.
// The console is the engine's loopback one (test-support), so nothing leaves the machine.

internal import CRipcordEngine
import Foundation
import Security

enum RipcordEngineLoopback {
    struct Summary: Equatable {
        let reachedStreamReady: Bool
        let frames: Int
        let stats: Int
        let passcodesAsked: Int
        let endedByDisconnect: Bool
        let consoleHeardGoodbye: Bool
    }

    private final class Box {
        var frames = 0
        var stats = 0
        var passcodes = 0
        var ended = false
    }

    private static func box(_ user: UnsafeMutableRawPointer?) -> Box {
        Unmanaged<Box>.fromOpaque(user!).takeUnretainedValue()
    }

    static func run(frames wanted: Int = 15) -> Summary? {
        precondition(RipcordEngineLayout.matches(), "the Rust engine's ABI does not match the header RipcordKit was built against")
        var control: UInt16 = 0, senkusha: UInt16 = 0, stream: UInt16 = 0
        guard let console = ripcord_loopback_console_start("2468", &control, &senkusha, &stream) else { return nil }
        defer { ripcord_loopback_console_stop(console) }

        let state = Box()
        let user = Unmanaged.passRetained(state)
        defer { user.release() }

        var config = RipcordClientConfig()
        config.route = UInt32(RIPCORD_ROUTE_LOCAL)
        config.console = (127, 0, 0, 1)
        config.is_ps5 = true
        config.control_port = control
        config.senkusha_port = senkusha
        config.stream_port = stream
        config.no_arm_broadcast = true
        withUnsafeMutableBytes(of: &config.companion) { _ = ripcord_loopback_console_companion($0.baseAddress!.assumingMemoryBound(to: UInt8.self)) }

        var callbacks = RipcordClientCallbacks()
        callbacks.user = user.toOpaque()
        callbacks.video_frame = { user, _, _, _ in RipcordEngineLoopback.box(user).frames += 1 }
        callbacks.stats = { user, _ in RipcordEngineLoopback.box(user).stats += 1 }
        callbacks.stage = { user, stage in
            if stage == RIPCORD_CLIENT_STAGE_ENDED { RipcordEngineLoopback.box(user).ended = true }
        }
        callbacks.poll_passcode = { user, _, out, size in
            RipcordEngineLoopback.box(user).passcodes += 1
            let digits = Array("2468".utf8CString)
            guard let out, size >= digits.count else { return -1 }
            digits.withUnsafeBufferPointer { out.update(from: $0.baseAddress!, count: digits.count) }
            return 1
        }
        callbacks.poll_input = { _, state in
            state?.pointee.left_x = 1200
            return true
        }
        callbacks.poll_commands = { user in
            RipcordEngineLoopback.box(user).frames >= 15 ? UInt32(RIPCORD_CMD_DISCONNECT) : 0
        }
        var random = RipcordRandom(user: nil, fill: { _, out, length in
            guard let out else { return false }
            return SecRandomCopyBytes(kSecRandomDefault, length, out) == errSecSuccess
        })
        var ecdh = CryptoKitEngineECDH.backend

        let key = [UInt8](repeating: 0xab, count: 8)
        let device = [UInt8](repeating: 0x22, count: 32)
        let client: OpaquePointer? = key.withUnsafeBufferPointer { k in
            device.withUnsafeBufferPointer { d in
                config.registration_key = k.baseAddress
                config.registration_key_length = k.count
                config.device_id = d.baseAddress
                config.device_id_length = d.count
                return ripcord_client_new(&config, &callbacks, &ecdh, &random)
            }
        }
        guard let client else { return nil }
        defer { ripcord_client_free(client) }

        var stage = RIPCORD_CLIENT_STAGE_IDLE
        _ = ripcord_client_connect(client, &stage)
        var alive = true
        let start = Date()
        while alive && Date().timeIntervalSince(start) < 10 {
            _ = ripcord_client_pump(client, 2, &alive)
        }
        var result = RipcordClientResult()
        _ = ripcord_client_result(client, &result)

        let deadline = Date().addingTimeInterval(2)
        while !ripcord_loopback_console_saw_goodbye(console) && Date() < deadline {
            Thread.sleep(forTimeInterval: 0.005)
        }
        return Summary(
            reachedStreamReady: stage == RIPCORD_CLIENT_STAGE_STREAM_READY,
            frames: state.frames,
            stats: state.stats,
            passcodesAsked: state.passcodes,
            endedByDisconnect: result.end_reason == RIPCORD_CLIENT_END_USER_DISCONNECT && state.ended,
            consoleHeardGoodbye: ripcord_loopback_console_saw_goodbye(console))
    }
}
