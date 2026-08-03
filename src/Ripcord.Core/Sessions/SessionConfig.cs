namespace Ripcord.Core.Sessions;

public enum VideoCodec
{
    H264,
    Hevc,
}

/// <summary>
/// Dynamic range to request from the console.
///
/// <para>
/// HDR requires a 10-bit stream, which in practice means HEVC Main10 — an 8-bit AVC stream cannot carry it, so
/// requesting <see cref="Hdr"/> alongside <see cref="VideoCodec.H264"/> is not meaningful. Only "SDR" has ever
/// been observed on the wire; "HDR" is the presumed counterpart token.
/// </para>
/// </summary>
public enum DynamicRange
{
    Sdr,
    Hdr,
}

public enum LatencyMode
{
    Lowest,
    Balanced,
    Quality,
}

/// <param name="ReportConnectionQuality">
/// Send CONNECTION_QUALITY reports telling the console our measured RTT/loss and the bitrate we want.
/// <b>Defaults to false</b> because the message's targetBitrate units are not wire-confirmed: guessing wrong by
/// 1000x would make the console pick an absurd encode rate on a live session. Opt in to test it.
/// </param>
public sealed record SessionConfig(
    int Width,
    int Height,
    int TargetFps,
    int InitialBitrateKbps,
    VideoCodec CodecPreference,
    LatencyMode LatencyMode,
    bool ReportConnectionQuality = false,
    DynamicRange RequestedDynamicRange = DynamicRange.Sdr,
    /// <summary>Put the console into rest mode when the session ends, rather than leaving it awake. The
    /// vendor exposes this as a checkbox at disconnect; here it is a preference applied at teardown.</summary>
    bool RestConsoleOnDisconnect = false);
