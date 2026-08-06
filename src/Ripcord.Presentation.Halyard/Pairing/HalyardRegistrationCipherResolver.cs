using System.Text.Json;
using System.Text.Json.Serialization;
using Ripcord.Core.Platform;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;
using Ripcord.Protocol.Halyard.Session;

namespace Ripcord.Presentation.Halyard.Pairing;

/// <summary>
/// Builds the real registration cipher from the v1 interop constants (the key table, selector offset, material
/// wrap table, and context key).
///
/// <para>
/// Resolution is most-explicit-first: <c>RIPCORD_REGIST_FIXTURE</c> → the platform config directory → a walk up
/// to the dev-tree fixture → the constants bundled with this build
/// (<see cref="HalyardInteropConstants"/>). So a plain clone can pair, while a developer's own local fixture
/// still wins. Resolution is per <em>family</em>: a fixture that cannot serve the requested console family (a
/// PS5-only fixture asked for PS4) is skipped in favour of the bundle rather than failing the pairing. Returns
/// <see cref="UnavailableRegistrationCipher"/> only if every source fails — including a build made with
/// <c>-p:BundleInteropConstants=false</c> — so pairing fails cleanly with a clear message rather than crashing.
/// <c>source</c> always reports which path won, and why a located fixture lost.
/// </para>
///
/// <para>
/// Moved here from <c>Ripcord.App/Services/AppRegistrationCipher.cs</c> unchanged. The logic is deliberately
/// carried over verbatim: the resolution order and every <c>source</c> string are user-facing text on a pairing
/// failure, so "improving" them during a move would be an invisible behaviour change in the one place a user is
/// already having a bad time. The only difference is static → instance, so the pairing flow can be tested
/// against a fake.
/// </para>
/// </summary>
public sealed partial class HalyardRegistrationCipherResolver : IHalyardRegistrationCipherResolver
{
    private readonly IPlatformPaths _paths;
    private readonly string? _baseDirectory;

    /// <param name="baseDirectory">
    /// Where the dev-tree walk starts. Defaults to <see cref="AppContext.BaseDirectory"/>, which is the only
    /// value production ever uses. It is a parameter purely so the resolution *order* can be tested: with the
    /// walk hardcoded, a machine that happens to have a dirty-room fixture and a machine that does not would
    /// give different answers, and the test would assert whichever the author's box did.
    /// </param>
    public HalyardRegistrationCipherResolver(IPlatformPaths? paths = null, string? baseDirectory = null)
    {
        _paths = paths ?? new DefaultPlatformPaths();
        _baseDirectory = baseDirectory;
    }

    public IHalyardRegistrationCipher Resolve(HalyardConsolePlatform platform, out string source)
    {
        bool ps4 = platform == HalyardConsolePlatform.Ps4;

        // A fixture only wins if it can actually serve *this* family. A PS5-only fixture (every fixture
        // predating the PS4 registration work) must not mask a bundle that does carry the PS4 tables.
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

        // The constants bundled with this build. Absent when built with -p:BundleInteropConstants=false,
        // in which case pairing reports unavailable as before.
        var bundled = HalyardInteropConstants.Registration(platform);
        if (bundled is not null)
        {
            source = skipped is null ? "bundled interop constants" : $"bundled interop constants ({skipped})";
            return new HalyardRegistrationCipher(
                new HalyardRegistrationKdf(bundled.Value.Secrets, bundled.Value.VersionSelector),
                bundled.Value.ContextKey);
        }

        source = skipped ?? (ps4
            ? "PS4 registration constants not found (bundle omits the PS4 tables; set RIPCORD_REGIST_FIXTURE)"
            : "registration constants not found (set RIPCORD_REGIST_FIXTURE)");
        return new UnavailableRegistrationCipher();
    }

    /// <summary>
    /// Builds the cipher from <paramref name="path"/>, or returns null with <paramref name="reason"/> set when
    /// that fixture cannot serve the requested family — the caller then falls back to the bundle.
    /// </summary>
    private static IHalyardRegistrationCipher? TryLoadFixture(string path, bool ps4, out string? reason)
    {
        try
        {
            var fx = JsonSerializer.Deserialize(File.ReadAllText(path), FixtureContext.Default.Fixture);
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
                new HalyardRegistrationKdf(secrets, ps4 ? 0 : 1), Convert.FromHexString(contextKeyHex!));
        }
        catch (Exception ex)
        {
            // Names the file, like the other three reasons do. It previously did not, which made the one branch
            // reachable by arbitrary I/O and parse failures — the least self-explanatory of the four — also the
            // only one that did not say which file to go and look at.
            reason = $"fixture load failed ({path}): {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Explicit override, then the platform config directory (the location an installed build can use), then a
    /// walk up to the dev tree. The last of those was previously the only option, which is why a shipped app
    /// could never locate these constants.
    /// </summary>
    private string? Locate()
    {
        string? env = Environment.GetEnvironmentVariable("RIPCORD_REGIST_FIXTURE");
        if (!string.IsNullOrEmpty(env) && File.Exists(env))
            return env;

        string installed = Path.Combine(_paths.ConfigDirectory, "registration_crypto_vectors.json");
        if (File.Exists(installed))
            return installed;

        string? dir = _baseDirectory ?? AppContext.BaseDirectory;
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
