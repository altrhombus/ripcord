// docs/engine-plan.md, C ABI rule 4: before the Rust engine is used, compare its view of every struct
// that crosses the boundary with the one Swift compiled against, and refuse to run on a mismatch. The
// engine is built from the same commit, so a mismatch means a stale library, not a supported case.

internal import CRipcordEngine

enum RipcordEngineLayout {
    static func matches() -> Bool {
        guard ripcord_api_version() == UInt32(RIPCORD_API_VERSION) else { return false }
        let structs: [(RipcordStructId, Int)] = [
            (RIPCORD_STRUCT_ID_STREAM_HEADER, MemoryLayout<RipcordStreamHeader>.size),
            (RIPCORD_STRUCT_ID_DEMUX_SINK, MemoryLayout<RipcordDemuxSink>.size),
            (RIPCORD_STRUCT_ID_DEMUX_COUNTERS, MemoryLayout<RipcordDemuxCounters>.size),
            (RIPCORD_STRUCT_ID_ECDH_BACKEND, MemoryLayout<RipcordEcdhBackend>.size),
            (RIPCORD_STRUCT_ID_KAT_RESULT, MemoryLayout<RipcordKatResult>.size),
            (RIPCORD_STRUCT_ID_SCRIPTED_CONSOLE_COUNTS, MemoryLayout<RipcordScriptedConsoleCounts>.size),
            (RIPCORD_STRUCT_ID_CLIENT_CONFIG, MemoryLayout<RipcordClientConfig>.size),
            (RIPCORD_STRUCT_ID_CLIENT_CALLBACKS, MemoryLayout<RipcordClientCallbacks>.size),
            (RIPCORD_STRUCT_ID_CLIENT_RESULT, MemoryLayout<RipcordClientResult>.size),
            (RIPCORD_STRUCT_ID_CLIENT_STATS, MemoryLayout<RipcordClientStats>.size),
            (RIPCORD_STRUCT_ID_INPUT_STATE, MemoryLayout<RipcordInputState>.size),
            (RIPCORD_STRUCT_ID_STREAM_INFO, MemoryLayout<RipcordStreamInfo>.size),
            (RIPCORD_STRUCT_ID_LEG, MemoryLayout<RipcordLeg>.size),
            (RIPCORD_STRUCT_ID_PEER, MemoryLayout<RipcordPeer>.size),
            (RIPCORD_STRUCT_ID_ENDPOINT, MemoryLayout<RipcordEndpoint>.size),
            (RIPCORD_STRUCT_ID_RANDOM, MemoryLayout<RipcordRandom>.size),
        ]
        return structs.allSatisfy { ripcord_struct_size($0.0.rawValue) == $0.1 }
    }
}
