using Ripcord.Core.Platform;
using Ripcord.Core.Security;
using Ripcord.Protocol.Halyard.Session;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The platform seams that keep the core host-neutral: config locations, credential protection, and device
/// identity. These run on any OS — which is the point, since the whole core is expected to build and test
/// away from Windows.
/// </summary>
public class PlatformSeamTests
{
    [Fact]
    public void PlatformPaths_AreAbsoluteAndExist()
    {
        var paths = new DefaultPlatformPaths("RipcordTests");

        foreach (string dir in (string[])[paths.ConfigDirectory, paths.DataDirectory, paths.StateDirectory])
        {
            Assert.True(Path.IsPathRooted(dir), $"{dir} should be absolute");
            Assert.True(Directory.Exists(dir), $"{dir} should be created eagerly");
        }
    }

    [Fact]
    public void PlatformPaths_IgnoreARelativeXdgOverride()
    {
        // The XDG spec says a relative value must be ignored. Resolving it against the working directory would
        // scatter user config wherever the app happened to be launched from.
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            return; // XDG is not consulted on these hosts
        }

        string? original = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", "relative/nope");
            var paths = new DefaultPlatformPaths("RipcordTests");

            Assert.True(Path.IsPathRooted(paths.ConfigDirectory));
            Assert.DoesNotContain("relative", paths.ConfigDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", original);
        }
    }

    [Fact]
    public void CredentialProtector_RoundTripsThroughWhicheverBackendThisHostHas()
    {
        ICredentialProtector protector = CredentialProtection.ForCurrentPlatform();
        byte[] secret = [0x6c, 0x9b, 0x1c, 0x31, 0xde, 0xad, 0xbe, 0xef];

        byte[] sealed_ = protector.Protect(secret);
        Assert.Equal(secret, protector.Unprotect(sealed_));

        // Real protection must not leave the plaintext sitting in the ciphertext.
        if (protector.IsRealProtection)
        {
            Assert.NotEqual(secret, sealed_);
        }
    }

    [Fact]
    public void CredentialProtector_ReportsHonestlyWhetherItEncrypts()
    {
        ICredentialProtector protector = CredentialProtection.ForCurrentPlatform();
        Assert.False(string.IsNullOrWhiteSpace(protector.Description));

        // The fallback must never claim protection it does not provide — the UI relies on this to warn.
        if (protector is PlaintextCredentialProtector)
        {
            Assert.False(protector.IsRealProtection);
        }
    }

    [Fact]
    public async Task CredentialStore_RoundTripsAndDoesNotStoreThePlaintextKey()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ripcord-cred-{Guid.NewGuid():N}.json");
        try
        {
            var store = new HalyardPairingCredentialStore(path);
            byte[] key = [0x6c, 0x9b, 0x1c, 0x31, 0x11, 0x22, 0x33, 0x44];

            await store.SaveAsync("console-1", key, CancellationToken.None);
            Assert.Equal(key, await store.LoadAsync("console-1", CancellationToken.None));

            // On a host with real protection the raw key must not appear in the file as hex.
            if (CredentialProtection.ForCurrentPlatform().IsRealProtection)
            {
                string onDisk = await File.ReadAllTextAsync(path, CancellationToken.None);
                Assert.DoesNotContain(Convert.ToHexString(key), onDisk, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CredentialStore_StillReadsAPrePreotectionPlaintextEntry()
    {
        // Upgrading must not silently drop an existing pairing and force the user to re-pair.
        string path = Path.Combine(Path.GetTempPath(), $"ripcord-cred-{Guid.NewGuid():N}.json");
        try
        {
            byte[] key = [0xAA, 0xBB, 0xCC, 0xDD];
            await File.WriteAllTextAsync(
                path,
                $"{{\"console-legacy\":\"{Convert.ToHexString(key)}\"}}",
                CancellationToken.None);

            var store = new HalyardPairingCredentialStore(path);
            Assert.Equal(key, await store.LoadAsync("console-legacy", CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CredentialStore_UndecryptableEntryReadsAsNotPairedRatherThanThrowing()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ripcord-cred-{Guid.NewGuid():N}.json");
        try
        {
            // "dpapi:" prefixed but not valid base64 — the shape a corrupt or foreign-user file takes.
            await File.WriteAllTextAsync(
                path, "{\"console-x\":\"dpapi:!!!not-base64!!!\"}", CancellationToken.None);

            var store = new HalyardPairingCredentialStore(path);
            Assert.Null(await store.LoadAsync("console-x", CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DeviceIdentity_IsEitherSixteenBytesOrEmpty()
    {
        // CI hosts and containers may have no machine id at all; empty is a valid, documented outcome, and the
        // one thing that must never happen is a partially-parsed id.
        ReadOnlyMemory<byte> id = new DefaultDeviceIdentity().StableDeviceId;
        Assert.True(id.Length is 0 or 16, $"expected 0 or 16 bytes, got {id.Length}");
    }

    [Theory]
    [InlineData("1a2b3c4d-dead-beef-0011-223344556677", true)]
    [InlineData("1a2b3c4ddeadbeef0011223344556677", true)]
    [InlineData("too-short", false)]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz", false)]
    public void DeviceIdentity_ParsesGuidShapesAndRejectsJunk(string input, bool expected)
    {
        Assert.Equal(expected, DefaultDeviceIdentity.TryParseMachineGuid(input, out byte[] bytes));
        if (expected)
        {
            Assert.Equal(16, bytes.Length);
        }
    }

    [Fact]
    public void HalyardDeviceIdentity_DelegatesToTheSeam()
    {
        // The protocol wrapper must expose whatever the injected identity says, so a non-Windows host can
        // supply its own rather than silently sending an empty RP-Did.
        byte[] injected = Convert.FromHexString("1a2b3c4ddeadbeef0011223344556677");
        Assert.Equal(injected, HalyardDeviceIdentity.From(new StaticDeviceIdentity(injected)).ToArray());
    }
}
