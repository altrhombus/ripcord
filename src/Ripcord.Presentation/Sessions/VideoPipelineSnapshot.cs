namespace Ripcord.Presentation.Sessions;

/// <summary>
/// Everything the diagnostics readout needs to know about the decode pipeline, as plain data.
///
/// <para>
/// This mirrors <c>Ripcord.Media.PipelineStats</c> field for field, and the duplication is deliberate rather
/// than an oversight. <c>PipelineStats</c> is already nothing but numbers and strings — but it lives in
/// <c>Ripcord.Media</c>, which targets <c>net10.0-windows</c> and carries the WinRT projections for the native
/// D3D12 interop. Referencing it here would drag that whole graph into the portable layer and fail
/// <c>PresentationPortabilityTests</c> on the spot, for the sake of avoiding one mapping function.
/// </para>
///
/// <para>
/// The mapping lives on the front-end side, next to the pipeline it reads, so a second front end with a
/// different decoder implements <see cref="IVideoPipelineStats"/> against its own and nothing here changes.
/// </para>
/// </summary>
public readonly record struct VideoPipelineSnapshot(
    long DecodedFrames,
    long PresentedFrames,
    int QueueDepth,
    double PipelineLatencyMs,
    /// <summary>2 = zero-copy, 1 = readback, 0 = software.</summary>
    int DecodeMode,
    /// <summary>Resolution actually being decoded, which the console chooses and need not match the request.</summary>
    int DecodedWidth = 0,
    int DecodedHeight = 0,
    /// <summary>YCbCr matrix in use, and whether the stream signalled it.</summary>
    string ColorMatrix = "",
    /// <summary>Which decoder was selected, so "HEVC requested" and "HEVC decoding" stay distinguishable.</summary>
    string Decoder = "",
    /// <summary>Raw decode-loop counters. Only worth reading when frames are not appearing.</summary>
    string DecoderDiagnostic = "",
    /// <summary>
    /// The picture on screen is actually HDR — display accepts HDR10, stream carries it, and the swap chain took
    /// the colour space. The only one of the HDR facts that means what a viewer would mean by it.
    /// </summary>
    bool IsHdrOutput = false,
    /// <summary>10-bit decode. Independent of HDR — a stream can be 10-bit and SDR.</summary>
    bool IsTenBit = false,
    /// <summary>Pixel format in words, e.g. "10-bit P010".</summary>
    string VideoFormat = "",
    /// <summary>What the display is being given — "HDR10", "SDR", or which reason we are tone-mapping.</summary>
    string HdrOutput = "",
    long AudioFramesDecoded = 0,
    long AudioFramesSkipped = 0,
    /// <summary>Stream format and the device format it is converted to, or why audio is silent.</summary>
    string AudioFormat = "");
