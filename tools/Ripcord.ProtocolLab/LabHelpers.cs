using System.Text.Json;
using Ripcord.Core.Discovery;
using Ripcord.Core.Sessions;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;

namespace Ripcord.ProtocolLab;

/// <summary>
/// Loads the real (dirty-room) registration cipher from the gitignored fixture
/// <c>docs/protocol/captures/registration_crypto_vectors.json</c> — the extracted KDF table + selector
/// offset + context key. Never committed; the lab reads it so a live pairing can be driven without baking
/// interop constants into the tree. Returns <see cref="UnavailableRegistrationCipher"/> when absent.
/// </summary>
internal static class LabRegistrationCipher
{
    public static IHalyardRegistrationCipher Load(out string source)
    {
        string? path = Locate();
        if (path is null)
        {
            source = "(fixture not found)";
            return new UnavailableRegistrationCipher();
        }

        source = path;
        var fx = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("registration fixture failed to deserialize");

        var wrapTable = fx.MaterialWrapTable is null ? default : Convert.FromHexString(fx.MaterialWrapTable);
        var secrets = new HalyardRegistrationSecrets(Convert.FromHexString(fx.RegistrationTable!), fx.SelectorOffset, wrapTable);
        return new HalyardRegistrationCipher(new HalyardRegistrationKdf(secrets), Convert.FromHexString(fx.ContextKey!));
    }

    private static string? Locate()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && dir is not null; i++)
        {
            string candidate = Path.Combine(dir, "docs", "protocol", "captures", "registration_crypto_vectors.json");
            if (File.Exists(candidate))
                return candidate;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }

    private sealed class Fixture
    {
        public string? RegistrationTable { get; set; }
        public string? MaterialWrapTable { get; set; }
        public int SelectorOffset { get; set; }
        public string? ContextKey { get; set; }
    }
}

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
