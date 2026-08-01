namespace Ripcord.Protocol.Halyard.Common.Streaming;

/// <summary>
/// The channel/type discriminator carried in byte 0 of every packet on the multiplexed stream port.
/// Derived from our own capture analysis in docs/protocol/ps5-av-stream.md - the framing is in the
/// clear; only the trailing payload is encrypted.
/// </summary>
public enum HalyardStreamChannel : byte
{
    /// <summary>Control / keepalive / reliability feedback (a repeated idle heartbeat lives here).</summary>
    Control = 0x00,

    /// <summary>Video fragments (full-MTU fragments plus a short frame-tail fragment).</summary>
    Video = 0x02,

    /// <summary>Audio units (fixed-ish size, steady cadence).</summary>
    Audio = 0x03,

    /// <summary>
    /// Up-direction congestion/quality telemetry family, the dominant up traffic on a real WAN path
    /// (docs/protocol/ps5-wan-relay.md). Sparse on LAN.
    /// </summary>
    Congestion = 0x06,

    /// <summary>Controller input (66-byte UDP payload; see docs/protocol/ps5-controller-input-packet.md).</summary>
    Input = 0x0e,

    /// <summary>Low-rate down stats/report channel (tentative).</summary>
    Stats = 0x12,
}
