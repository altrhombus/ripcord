namespace Ripcord.Core.Sessions;

/// <summary>
/// Still-encoded bitstream frame. Decode intentionally happens outside protocol backends (in
/// Ripcord.Media) so protocol backends stay headless-testable with no Media Foundation
/// or D3D12 dependency.
/// </summary>
public sealed record EncodedVideoFrame(ReadOnlyMemory<byte> Payload, long TimestampTicks, bool IsKeyFrame);

public sealed record EncodedAudioFrame(ReadOnlyMemory<byte> Payload, long TimestampTicks);
