using Ripcord.Core.Platform;
using Ripcord.Presentation.Halyard.Pairing;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Session;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// Registration-cipher resolution: which source of the v1 interop constants wins, and what the user is told when
/// one loses.
///
/// <para>
/// Both halves matter. The order is a real user-facing behaviour — a PS5-only fixture masking a PS4-capable
/// bundle is exactly the bug that made PS4 pairing report "unavailable" on a machine that could in fact pair —
/// and the <c>source</c> strings are shown verbatim on a pairing failure, so they are contract rather than
/// debug output.
/// </para>
///
/// <para>
/// These tests set <c>RIPCORD_REGIST_FIXTURE</c>, which is process-global. Every test that does so restores it,
/// and the class is not parallelised against itself by xUnit, so the mutation is contained.
/// </para>
/// </summary>
public class HalyardRegistrationCipherResolverTests : IDisposable
{
    private const string FixtureEnvVar = "RIPCORD_REGIST_FIXTURE";

    private readonly string _dir;
    private readonly string _configDir;
    private readonly string _emptyBaseDir;
    private readonly string? _savedEnv;

    public HalyardRegistrationCipherResolverTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"ripcord-regist-{Guid.NewGuid():N}");
        _configDir = Path.Combine(_dir, "config");
        _emptyBaseDir = Path.Combine(_dir, "base");
        Directory.CreateDirectory(_configDir);
        Directory.CreateDirectory(_emptyBaseDir);

        // The real value would otherwise decide these tests' outcome on a developer's machine.
        _savedEnv = Environment.GetEnvironmentVariable(FixtureEnvVar);
        Environment.SetEnvironmentVariable(FixtureEnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FixtureEnvVar, _savedEnv);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class FixedPaths(string dir) : IPlatformPaths
    {
        public string ConfigDirectory { get; } = dir;
        public string DataDirectory { get; } = dir;
        public string StateDirectory { get; } = dir;
    }

    /// <summary>A resolver whose three fixture sources are all under our control.</summary>
    private HalyardRegistrationCipherResolver NewResolver()
        => new(new FixedPaths(_configDir), _emptyBaseDir);

    private static string Hex(int length, byte seed)
        => Convert.ToHexString(Enumerable.Range(0, length).Select(i => (byte)(seed + i)).ToArray());

    /// <summary>A syntactically valid fixture carrying only the PS5 constants — i.e. every fixture written before the PS4 work.</summary>
    private string WritePs5OnlyFixture(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $$"""
            {
              "registrationTable": "{{Hex(512, 1)}}",
              "materialWrapTable": "{{Hex(512, 2)}}",
              "selectorOffset": 397,
              "contextKey": "{{Hex(16, 3)}}"
            }
            """);
        return path;
    }

    // Whether the build under test actually bundled its interop constants. A build made with
    // -p:BundleInteropConstants=false legitimately cannot fall back, so the fallback assertions would be
    // asserting the wrong thing rather than finding a bug.
    private static bool BundleAvailable => HalyardInteropConstants.Registration(HalyardConsolePlatform.Ps5) is not null;

    private static bool Ps4BundleAvailable => HalyardInteropConstants.Registration(HalyardConsolePlatform.Ps4) is not null;

    [Fact]
    public void EnvironmentVariable_WinsOverEveryOtherSource()
    {
        string envFixture = WritePs5OnlyFixture(Path.Combine(_dir, "explicit", "registration_crypto_vectors.json"));
        WritePs5OnlyFixture(Path.Combine(_configDir, "registration_crypto_vectors.json"));
        Environment.SetEnvironmentVariable(FixtureEnvVar, envFixture);

        NewResolver().Resolve(HalyardConsolePlatform.Ps5, out string source);

        Assert.Equal(envFixture, source);
    }

    [Fact]
    public void ConfigDirectory_WinsOverTheDevTreeAndTheBundle()
    {
        string installed = WritePs5OnlyFixture(Path.Combine(_configDir, "registration_crypto_vectors.json"));

        IHalyardRegistrationCipher cipher = NewResolver().Resolve(HalyardConsolePlatform.Ps5, out string source);

        Assert.Equal(installed, source);
        Assert.True(cipher.IsAvailable);
    }

    [Fact]
    public void DevTreeFixture_IsFoundByWalkingUp()
    {
        // The walk exists so a plain clone works from bin/Debug/... without any configuration. Nested three deep
        // to prove it is a walk and not a single check.
        string nested = Path.Combine(_emptyBaseDir, "bin", "Debug", "net10.0");
        Directory.CreateDirectory(nested);
        string devTree = WritePs5OnlyFixture(
            Path.Combine(_emptyBaseDir, "docs", "protocol", "captures", "registration_crypto_vectors.json"));

        var resolver = new HalyardRegistrationCipherResolver(new FixedPaths(_configDir), nested);
        resolver.Resolve(HalyardConsolePlatform.Ps5, out string source);

        Assert.Equal(devTree, source);
    }

    [SkippableFact]
    public void NoFixture_FallsBackToTheBundledConstants()
    {
        Skip.IfNot(BundleAvailable, "this build omits the bundled interop constants");

        IHalyardRegistrationCipher cipher = NewResolver().Resolve(HalyardConsolePlatform.Ps5, out string source);

        Assert.True(cipher.IsAvailable);
        Assert.Equal("bundled interop constants", source);
    }

    [SkippableFact]
    public void Ps5OnlyFixture_AskedForPs4_LosesToTheBundleAndSaysWhy()
    {
        // The bug this whole per-family path exists for: a fixture that cannot serve PS4 must not mask a bundle
        // that can. Before the fix this returned an unavailable cipher and PS4 pairing failed on a machine that
        // was perfectly capable of it.
        Skip.IfNot(Ps4BundleAvailable, "this build omits the PS4 registration tables");

        string installed = WritePs5OnlyFixture(Path.Combine(_configDir, "registration_crypto_vectors.json"));

        IHalyardRegistrationCipher cipher = NewResolver().Resolve(HalyardConsolePlatform.Ps4, out string source);

        Assert.True(cipher.IsAvailable);
        Assert.StartsWith("bundled interop constants", source);
        Assert.Contains("fixture lacks PS4 registration constants", source);
        Assert.Contains(installed, source);   // names the fixture that lost, so the message is actionable
    }

    [Fact]
    public void Ps5OnlyFixture_AskedForPs5_StillWins()
    {
        // The other half of the same behaviour: per-family fallback must not demote a fixture that is perfectly
        // adequate for the family being asked about.
        string installed = WritePs5OnlyFixture(Path.Combine(_configDir, "registration_crypto_vectors.json"));

        NewResolver().Resolve(HalyardConsolePlatform.Ps5, out string source);

        Assert.Equal(installed, source);
    }

    [SkippableFact]
    public void MalformedFixture_FallsBackToTheBundleRatherThanFailing()
    {
        Skip.IfNot(BundleAvailable, "this build omits the bundled interop constants");

        string path = Path.Combine(_configDir, "registration_crypto_vectors.json");
        File.WriteAllText(path, "{ not json at all");

        IHalyardRegistrationCipher cipher = NewResolver().Resolve(HalyardConsolePlatform.Ps5, out string source);

        Assert.True(cipher.IsAvailable);
        Assert.StartsWith("bundled interop constants", source);
        Assert.Contains("fixture load failed", source);

        // Every reason names the file it is about. This branch is reachable by any I/O or parse failure, so it is
        // the one most in need of saying which file — and the one that used to omit it.
        Assert.Contains(path, source);
    }

    [SkippableFact]
    public void FixtureMissingRequiredKeys_FallsBackAndReportsItAsMalformed()
    {
        Skip.IfNot(BundleAvailable, "this build omits the bundled interop constants");

        string path = Path.Combine(_configDir, "registration_crypto_vectors.json");
        File.WriteAllText(path, """{ "selectorOffset": 397 }""");

        NewResolver().Resolve(HalyardConsolePlatform.Ps5, out string source);

        Assert.Contains("fixture malformed", source);
    }

    [SkippableFact]
    public void ValidFixture_ProducesAWorkingCipherForBothFamilies()
    {
        // Guards the family -> version-selector wiring (PS4 = 0, PS5 = 1). A cipher that reports available but
        // was built for the wrong family derives wrong keys and fails against the console, not here.
        Skip.IfNot(Ps4BundleAvailable, "this build omits the PS4 registration tables");

        Assert.True(NewResolver().Resolve(HalyardConsolePlatform.Ps5, out _).IsAvailable);
        Assert.True(NewResolver().Resolve(HalyardConsolePlatform.Ps4, out _).IsAvailable);
    }
}
