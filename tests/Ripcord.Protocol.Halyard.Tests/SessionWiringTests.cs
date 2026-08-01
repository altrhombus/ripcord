using Ripcord.Core.Discovery;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;
using Ripcord.Protocol.Halyard.Session;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// Covers the connect-path integration seams built in Session 10: the file credential store
/// (<see cref="HalyardPairingCredentialStore"/>), the control-secrets loader, and the session factory.
/// These are what let a live <see cref="HalyardStreamingSession"/> actually be constructed.
/// </summary>
public class SessionWiringTests
{
    private static string TempPath()
        => Path.Combine(Path.GetTempPath(), "ripcord-tests", Guid.NewGuid().ToString("N"), "credentials.json");

    [Fact]
    public async Task CredentialStore_RoundTripsAPairingRecordBlob()
    {
        var store = new HalyardPairingCredentialStore(TempPath());
        // Synthetic credentials. These must never be real: a registration key and companion together are the
        // pairing credential for a specific console.
        var record = new HalyardPairingRecord(
            RegistrationKey: "1a2b3c4d"u8.ToArray(),
            Companion: Convert.FromHexString("000102030405060708090a0b0c0d0e0f"),
            KeyType: 2);

        await store.SaveAsync("console-A", record.Serialize(), CancellationToken.None);
        byte[]? blob = await store.LoadAsync("console-A", CancellationToken.None);

        Assert.NotNull(blob);
        Assert.True(HalyardPairingRecord.TryDeserialize(blob, out var back));
        Assert.Equal(record.RegistrationKey, back!.RegistrationKey);
        Assert.Equal(record.Companion, back.Companion);
        Assert.Equal(record.KeyType, back.KeyType);
    }

    [Fact]
    public async Task CredentialStore_MissingIdReturnsNull_AndRemoveWorks()
    {
        var store = new HalyardPairingCredentialStore(TempPath());
        Assert.Null(await store.LoadAsync("nope", CancellationToken.None));

        await store.SaveAsync("c1", [1, 2, 3], CancellationToken.None);
        await store.SaveAsync("c2", [4, 5, 6], CancellationToken.None);   // second entry persists alongside
        Assert.Equal(new byte[] { 1, 2, 3 }, await store.LoadAsync("c1", CancellationToken.None));

        await store.RemoveAsync("c1", CancellationToken.None);
        Assert.Null(await store.LoadAsync("c1", CancellationToken.None));
        Assert.Equal(new byte[] { 4, 5, 6 }, await store.LoadAsync("c2", CancellationToken.None)); // c2 untouched
    }

    [Fact]
    public async Task CredentialStore_OverwritesSameId()
    {
        var store = new HalyardPairingCredentialStore(TempPath());
        await store.SaveAsync("c", [0xAA], CancellationToken.None);
        await store.SaveAsync("c", [0xBB, 0xCC], CancellationToken.None);
        Assert.Equal(new byte[] { 0xBB, 0xCC }, await store.LoadAsync("c", CancellationToken.None));
    }

    [Fact]
    public async Task CredentialStore_ToleratesCorruptFile()
    {
        string path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{ this is not valid json ");
        var store = new HalyardPairingCredentialStore(path);

        Assert.Null(await store.LoadAsync("c", CancellationToken.None));   // does not throw
        await store.SaveAsync("c", [7], CancellationToken.None);           // recovers by rewriting
        Assert.Equal(new byte[] { 7 }, await store.LoadAsync("c", CancellationToken.None));
    }

    // SkippableFact, not Fact: this calls Skip.If, and under a plain [Fact] the SkipException surfaces as a
    // FAILURE instead of a skip. It never showed up because the gitignored fixture was always discoverable by
    // walking up from the test binary — so a fresh clone without it, or any build whose output lands outside
    // the repo tree, would have reported a spurious red test.
    [SkippableFact]
    public void ControlSecretsLoader_LoadsFixtureWhenPresent()
    {
        // The dirty-room fixture ships in the dev tree; skip cleanly if a checkout lacks it.
        HalyardControlSecretsLoader.Load(out string source);
        Skip.If(source.Contains("not found"), $"Control fixture absent ({source}).");

        var secrets = HalyardControlSecretsLoader.Load(out _);
        Assert.NotNull(secrets);
        // The KDF tables are fixed-size; the loader validates length, so a non-null result is well-formed.
        Assert.Equal(HalyardControlSecrets.KdfTableLength, secrets!.KdfTable1.Length);
        Assert.Equal(HalyardControlSecrets.KdfTableLength, secrets.KdfTable2.Length);
    }

    [Fact]
    public void Factory_UsesRealCryptoWhenSecretsPresent_ElsePassthrough()
    {
        var creds = new HalyardPairingCredentialStore(TempPath());

        var withReal = new HalyardSessionFactory(HalyardControlSecretsLoader.Load(out string source), creds);
        Assert.Equal(!source.Contains("not found"), withReal.HasRealCrypto);

        var passthrough = new HalyardSessionFactory(null, creds);
        Assert.False(passthrough.HasRealCrypto);
        // Create must produce a session without touching the network (crypto is chosen eagerly).
        Assert.NotNull(passthrough.Create("c", System.Net.IPAddress.Loopback));
    }
}
