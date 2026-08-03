using System.Text.Json;
using System.Text.Json.Serialization;
using Ripcord.Core.Platform;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;

namespace Ripcord.Protocol.Halyard.Session;

/// <summary>
/// Loads the v1 control-plane interop constants (the two KDF tables + the field context keys) into a
/// <see cref="HalyardControlSecrets"/>. The real session crypto (<see cref="HalyardV1SessionCrypto"/>) cannot
/// run without them.
///
/// <para>
/// Resolution runs most-explicit-first and ends at the constants bundled with the build
/// (<see cref="HalyardInteropConstants"/>), so a plain clone works while a developer's own local material
/// still takes precedence. Returns <c>null</c> only when every source fails — including a build made with
/// <c>-p:BundleInteropConstants=false</c> — letting callers fall back to the passthrough stub and fail
/// cleanly rather than crash. The <c>source</c> out-parameter always reports which path won; it surfaces in
/// the diagnostics overlay, so read it instead of assuming.
/// </para>
/// </summary>
public static partial class HalyardControlSecretsLoader
{
    public static HalyardControlSecrets? Load(out string source)
    {
        string? path = Locate();
        if (path is null)
        {
            // Last resort: the constants bundled with this build. Absent when built with
            // -p:BundleInteropConstants=false, in which case we degrade to the stub as before.
            var bundled = HalyardInteropConstants.Control();
            if (bundled is not null)
            {
                source = "bundled interop constants";
                return bundled;
            }

            source = "control constants not found (set RIPCORD_CONTROL_FIXTURE)";
            return null;
        }

        try
        {
            var fx = JsonSerializer.Deserialize(File.ReadAllText(path), FixtureContext.Default.Fixture);
            if (fx?.KdfTable1 is null || fx.KdfTable2 is null || fx.ContextKeys is null)
            {
                source = $"control fixture malformed ({path})";
                return null;
            }

            var ck = fx.ContextKeys;
            var contextKeys = new HalyardFieldContextKeys(
                Hex(ck.CodecInHigh), Hex(ck.SelectorOne), Hex(ck.SelectorZero), Hex(ck.FallbackZero));
            source = path;
            return new HalyardControlSecrets(Hex(fx.KdfTable1), Hex(fx.KdfTable2), contextKeys);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            source = $"control fixture load failed: {ex.Message}";
            return null;
        }
    }

    private static byte[] Hex(string? value)
        => string.IsNullOrEmpty(value) ? [] : Convert.FromHexString(value);

    /// <summary>
    /// Resolution order, most explicit first:
    /// <list type="number">
    ///   <item><description><c>RIPCORD_CONTROL_FIXTURE</c> — an explicit override.</description></item>
    ///   <item><description><c>control_crypto_vectors.json</c> in the platform config directory — the location
    ///   an <em>installed</em> app can actually use.</description></item>
    ///   <item><description>A walk up from the base directory to the dev-tree <c>docs/protocol/captures</c>
    ///   path — convenient when running from the repo.</description></item>
    ///   <item><description>Failing all of those, the caller falls back to the constants bundled with this
    ///   build (<see cref="HalyardInteropConstants"/>).</description></item>
    /// </list>
    /// </summary>
    private static string? Locate()
    {
        string? env = Environment.GetEnvironmentVariable("RIPCORD_CONTROL_FIXTURE");
        if (!string.IsNullOrEmpty(env) && File.Exists(env))
            return env;

        string installed = Path.Combine(new DefaultPlatformPaths().ConfigDirectory, "control_crypto_vectors.json");
        if (File.Exists(installed))
            return installed;

        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && dir is not null; i++)
        {
            string candidate = Path.Combine(dir, "docs", "protocol", "captures", "control_crypto_vectors.json");
            if (File.Exists(candidate))
                return candidate;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }

    // Source-generated: this loader sits on the same critical path as the bundle. Under PublishTrimmed the
    // reflection serializer throws, the dev-tree fixture would silently stop being found, and a developer's
    // build would fall back to the bundle - or to the stub - without saying why.
    [JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(Fixture))]
    private partial class FixtureContext : JsonSerializerContext;

    private sealed class Fixture
    {
        public string? KdfTable1 { get; set; }
        public string? KdfTable2 { get; set; }
        public ContextKeysDto? ContextKeys { get; set; }
    }

    private sealed class ContextKeysDto
    {
        public string? CodecInHigh { get; set; }
        public string? SelectorOne { get; set; }
        public string? SelectorZero { get; set; }
        public string? FallbackZero { get; set; }
    }
}
