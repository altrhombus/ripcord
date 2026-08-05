using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;

namespace Ripcord_App.Services;

/// <summary>
/// Builds the real registration cipher from the v1 interop constants (the key table, selector offset, material
/// wrap table, and context key).
///
/// <para>
/// Resolution is most-explicit-first: <c>RIPCORD_REGIST_FIXTURE</c> → the platform config directory → a walk up
/// to the dev-tree fixture → the constants bundled with this build
/// (<see cref="Ripcord.Protocol.Halyard.Session.HalyardInteropConstants"/>). So a plain clone can pair, while a
/// developer's own local fixture still wins. Returns <see cref="UnavailableRegistrationCipher"/> only if every
/// source fails — including a build made with <c>-p:BundleInteropConstants=false</c> — so pairing fails cleanly
/// with a clear message rather than crashing. <paramref name="source"/> always reports which path won.
/// </para>
/// </summary>
internal static partial class AppRegistrationCipher
{
    public static IHalyardRegistrationCipher Load(out string source)
        => Load(HalyardConsolePlatform.Ps5, out source);

    public static IHalyardRegistrationCipher Load(HalyardConsolePlatform platform, out string source)
    {
        bool ps4 = platform == HalyardConsolePlatform.Ps4;
        string? path = Locate();
        if (path is null)
        {
            // Last resort: the constants bundled with this build. Absent when built with
            // -p:BundleInteropConstants=false, in which case pairing reports unavailable as before.
            var bundled = Ripcord.Protocol.Halyard.Session.HalyardInteropConstants.Registration(platform);
            if (bundled is not null)
            {
                source = "bundled interop constants";
                return new HalyardRegistrationCipher(
                    new HalyardRegistrationKdf(bundled.Value.Secrets, bundled.Value.VersionSelector),
                    bundled.Value.ContextKey);
            }

            source = ps4
                ? "PS4 registration constants not found (bundle omits the PS4 tables; set RIPCORD_REGIST_FIXTURE)"
                : "registration constants not found (set RIPCORD_REGIST_FIXTURE)";
            return new UnavailableRegistrationCipher();
        }

        try
        {
            var fx = JsonSerializer.Deserialize(File.ReadAllText(path), FixtureContext.Default.Fixture);
            if (fx?.RegistrationTable is null || fx.ContextKey is null)
            {
                source = $"fixture malformed ({path})";
                return new UnavailableRegistrationCipher();
            }

            string? ps4Table = ps4 ? fx.Ps4RegistrationTable : null;
            string? ps4WrapTable = ps4 ? fx.Ps4MaterialWrapTable : null;
            string? contextKeyHex = ps4 ? fx.Ps4ContextKey : fx.ContextKey;
            if (ps4 && (ps4Table is null || ps4WrapTable is null || contextKeyHex is null))
            {
                source = $"fixture lacks PS4 registration constants ({path})";
                return new UnavailableRegistrationCipher();
            }

            var secrets = new HalyardRegistrationSecrets(
                Convert.FromHexString(fx.RegistrationTable),
                fx.SelectorOffset,
                fx.MaterialWrapTable is null ? default : Convert.FromHexString(fx.MaterialWrapTable),
                ps4Table is null ? default : Convert.FromHexString(ps4Table),
                ps4WrapTable is null ? default : Convert.FromHexString(ps4WrapTable));
            source = path;
            return new HalyardRegistrationCipher(
                new HalyardRegistrationKdf(secrets, ps4 ? 0 : 1), Convert.FromHexString(contextKeyHex!));
        }
        catch (Exception ex)
        {
            source = $"fixture load failed: {ex.Message}";
            return new UnavailableRegistrationCipher();
        }
    }

    /// <summary>
    /// Explicit override, then the platform config directory (the location an installed build can use), then a
    /// walk up to the dev tree. The last of those was previously the only option, which is why a shipped app
    /// could never locate these constants.
    /// </summary>
    private static string? Locate()
    {
        string? env = Environment.GetEnvironmentVariable("RIPCORD_REGIST_FIXTURE");
        if (!string.IsNullOrEmpty(env) && File.Exists(env))
            return env;

        string installed = Path.Combine(
            new Ripcord.Core.Platform.DefaultPlatformPaths().ConfigDirectory, "registration_crypto_vectors.json");
        if (File.Exists(installed))
            return installed;

        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && dir is not null; i++)
        {
            string candidate = Path.Combine(dir, "docs", "protocol", "captures", "registration_crypto_vectors.json");
            if (File.Exists(candidate))
                return candidate;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }

    [JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(Fixture))]
    private partial class FixtureContext : JsonSerializerContext;

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
