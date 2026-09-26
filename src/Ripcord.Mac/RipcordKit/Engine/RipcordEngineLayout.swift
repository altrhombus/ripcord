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
        ]
        return structs.allSatisfy { ripcord_struct_size($0.0.rawValue) == $0.1 }
    }
}
