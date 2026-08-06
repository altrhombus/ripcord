using Ripcord.Media;
using Ripcord.Presentation.Sessions;

namespace Ripcord_App.Services;

/// <summary>
/// Presents the D3D12 decode pipeline through the portable stats seam.
///
/// <para>
/// The whole of the WinUI side of <see cref="IVideoPipelineStats"/>: one field mapping. The pipeline itself is
/// created, bound to a swap chain and torn down by <c>SessionPage</c>, which is where it belongs — this only
/// answers "what is it doing right now?" for the diagnostics readout.
/// </para>
///
/// <para>
/// The pipeline is handed over via <see cref="Attach"/> rather than the constructor because the view-model is
/// built before the device exists, and outlives it: the diagnostics panel runs from before the session starts
/// until after it ends, so "no pipeline yet" and "no pipeline any more" are both ordinary states.
/// </para>
/// </summary>
public sealed class D3D12VideoPipelineStats : IVideoPipelineStats
{
    private D3D12VideoDecodePipeline? _pipeline;

    public bool IsReady => _pipeline is not null;

    public string AdapterDescription => _pipeline?.ActiveAdapterDescription ?? string.Empty;

    public void Attach(D3D12VideoDecodePipeline? pipeline) => _pipeline = pipeline;

    public VideoPipelineSnapshot Read()
    {
        // The local, not the field: teardown nulls it from another path, so reading the field twice would be a
        // real race rather than merely a nullable warning.
        if (_pipeline is not { } pipeline)
        {
            return default;
        }

        PipelineStats s = pipeline.GetStats();

        return new VideoPipelineSnapshot(
            DecodedFrames: s.DecodedFrames,
            PresentedFrames: s.PresentedFrames,
            QueueDepth: s.QueueDepth,
            PipelineLatencyMs: s.PipelineLatencyMs,
            DecodeMode: s.DecodeMode,
            DecodedWidth: s.DecodedWidth,
            DecodedHeight: s.DecodedHeight,
            ColorMatrix: s.ColorMatrix,
            Decoder: s.Decoder,
            DecoderDiagnostic: s.DecoderDiagnostic,
            IsHdrOutput: s.IsHdrOutput,
            IsTenBit: s.IsTenBit,
            VideoFormat: s.VideoFormat,
            HdrOutput: s.HdrOutput,
            AudioFramesDecoded: s.AudioFramesDecoded,
            AudioFramesSkipped: s.AudioFramesSkipped,
            AudioFormat: s.AudioFormat);
    }
}
