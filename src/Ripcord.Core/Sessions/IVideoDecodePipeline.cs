namespace Ripcord.Core.Sessions;

/// <summary>Upscale path selection; chosen jointly with bitrate under a shared power/thermal signal.</summary>
public enum UpscaleMode
{
    None,
    AutoSrOs,
    DirectMlApp,
    FsrFallback,
}

/// <summary>
/// Consumes still-encoded frames from a session and decodes/renders them. The concrete
/// implementation (Media Foundation + D3D12, bound to a SwapChainPanel) lives in Ripcord.Media and
/// the app; this Core seam stays headless so protocol backends and tests never depend on the
/// rendering stack. The render target (SwapChainPanel) is a concern of the concrete implementation,
/// not this contract.
/// </summary>
public interface IVideoDecodePipeline : IAsyncDisposable
{
    Task StartAsync(SessionConfig config, CancellationToken cancellationToken);

    void SubmitEncodedVideo(EncodedVideoFrame frame);

    void SubmitEncodedAudio(EncodedAudioFrame frame);

    UpscaleMode UpscaleMode { get; set; }

    /// <summary>
    /// Raised when the pipeline has fallen too far behind to catch up and wants to resynchronise on a fresh key
    /// frame. The owner routes this to <see cref="IStreamingSession.RequestKeyFrame"/>. Part of the seam because
    /// the decision ("I cannot catch up") belongs to the decoder while the remedy ("ask the console") belongs to
    /// the protocol, and neither should know about the other.
    /// </summary>
    event Action? KeyFrameRequested;
}
