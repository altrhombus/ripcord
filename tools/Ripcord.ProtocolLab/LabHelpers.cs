using System.Text.Json;
using Ripcord.Core.Discovery;
using Ripcord.Core.Sessions;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;

namespace Ripcord.ProtocolLab;

/// <summary>
/// Loads the real registration cipher for a console family — the KDF table + selector offset + material wrap
/// table + context key. Prefers the gitignored dirty-room fixture
/// <c>docs/protocol/captures/registration_crypto_vectors.json</c> (never committed), and falls back to the
/// constants bundled with the build. The fallback is per *family*: a PS5-only fixture asked for PS4 is skipped
/// in favour of the bundle rather than silently deriving PS5 keys for a PS4 pairing. Returns
/// <see cref="UnavailableRegistrationCipher"/> only when no source can serve the family.
/// </summary>
internal static class LabRegistrationCipher
{
    public static IHalyardRegistrationCipher Load(HalyardConsolePlatform platform, out string source)
    {
        bool ps4 = platform == HalyardConsolePlatform.Ps4;

        string? path = Locate();
        string? skipped = null;
        if (path is not null)
        {
            IHalyardRegistrationCipher? fromFixture = TryLoadFixture(path, ps4, out skipped);
            if (fromFixture is not null)
            {
                source = path;
                return fromFixture;
            }
        }

        var bundled = Ripcord.Protocol.Halyard.Session.HalyardInteropConstants.Registration(platform);
        if (bundled is not null)
        {
            source = skipped is null ? "bundled interop constants" : $"bundled interop constants ({skipped})";
            return new HalyardRegistrationCipher(
                new HalyardRegistrationKdf(bundled.Value.Secrets, bundled.Value.VersionSelector),
                bundled.Value.ContextKey);
        }

        source = skipped ?? "(no fixture, and this build bundles no interop constants)";
        return new UnavailableRegistrationCipher();
    }

    private static IHalyardRegistrationCipher? TryLoadFixture(string path, bool ps4, out string? reason)
    {
        try
        {
            var fx = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (fx?.RegistrationTable is null || fx.ContextKey is null)
            {
                reason = $"fixture malformed ({path})";
                return null;
            }

            string? ps4Table = ps4 ? fx.Ps4RegistrationTable : null;
            string? ps4WrapTable = ps4 ? fx.Ps4MaterialWrapTable : null;
            string? contextKeyHex = ps4 ? fx.Ps4ContextKey : fx.ContextKey;
            if (ps4 && (ps4Table is null || ps4WrapTable is null || contextKeyHex is null))
            {
                reason = $"fixture lacks PS4 registration constants ({path})";
                return null;
            }

            var secrets = new HalyardRegistrationSecrets(
                Convert.FromHexString(fx.RegistrationTable),
                fx.SelectorOffset,
                fx.MaterialWrapTable is null ? default : Convert.FromHexString(fx.MaterialWrapTable),
                ps4Table is null ? default : Convert.FromHexString(ps4Table),
                ps4WrapTable is null ? default : Convert.FromHexString(ps4WrapTable));
            reason = null;
            return new HalyardRegistrationCipher(
                // Non-null in both branches: the PS5 path is fx.ContextKey (checked above), the PS4 path is
                // Ps4ContextKey (checked in the guard) — flow analysis just can't see it through the ternary.
                new HalyardRegistrationKdf(secrets, ps4 ? 0 : 1), Convert.FromHexString(contextKeyHex!));
        }
        catch (Exception ex)
        {
            reason = $"fixture load failed: {ex.Message}";
            return null;
        }
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
        public string? Ps4RegistrationTable { get; set; }
        public string? Ps4MaterialWrapTable { get; set; }
        public int SelectorOffset { get; set; }
        public string? ContextKey { get; set; }
        public string? Ps4ContextKey { get; set; }
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
