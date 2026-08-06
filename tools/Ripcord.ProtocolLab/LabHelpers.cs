using System.Text.Json;
using Ripcord.Core.Discovery;
using Ripcord.Core.Sessions;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;

namespace Ripcord.ProtocolLab;

/// <summary>Minimal IObserver adapter for collecting observable emissions in the lab.</summary>
internal sealed class Observer<T>(Action<T> onNext, Action? onCompleted = null) : IObserver<T>
{
    public void OnNext(T value) => onNext(value);
    public void OnError(Exception error) => Console.Error.WriteLine($"observer error: {error.Message}");
    public void OnCompleted() => onCompleted?.Invoke();
}

/// <summary>No-op credential store for lab runs (no registration key available yet - Stage 5).</summary>
internal sealed class NullCredentialStore : IConsoleCredentialStore
{
    public Task SaveAsync(string consoleId, byte[] registrationKey, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<byte[]?> LoadAsync(string consoleId, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);
    public Task RemoveAsync(string consoleId, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Stand-in decode pipeline for the lab: counts/prints frames instead of decoding. Stands in for the
/// real Media Foundation + D3D12 pipeline (not yet built) so the session-to-decode wiring seam can be
/// exercised end to end.
/// </summary>
internal sealed class CountingDecodePipeline : IVideoDecodePipeline
{
    public int VideoCount { get; private set; }
    public int AudioCount { get; private set; }
    public UpscaleMode UpscaleMode { get; set; }

    /// <summary>Never raised: this stand-in never falls behind, so it never needs to resynchronise.</summary>
    public event Action? KeyFrameRequested
    {
        add { }
        remove { }
    }

    public Task StartAsync(SessionConfig config, CancellationToken cancellationToken)
    {
        Console.WriteLine($"decode pipeline started: {config.Width}x{config.Height}@{config.TargetFps}, codec={config.CodecPreference}");
        return Task.CompletedTask;
    }

    public void SubmitEncodedVideo(EncodedVideoFrame frame)
    {
        VideoCount++;
        Console.WriteLine($"  -> decode video: {frame.Payload.Length} bytes, key={frame.IsKeyFrame}");
    }

    public void SubmitEncodedAudio(EncodedAudioFrame frame)
    {
        AudioCount++;
        Console.WriteLine($"  -> decode audio: {frame.Payload.Length} bytes");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
