using Ripcord.Core.Consoles;
using Ripcord.Core.Platform;
using Ripcord.Core.Security;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// Paired-console persistence. Deliberately mirrors <see cref="SettingsStoreTests"/> next door, because the two
/// stores are structural twins — same temp-file write, same fall-back-rather-than-throw read.
///
/// <para>
/// These tests exist because this store had none until it moved into Ripcord.Core, and every behaviour covered
/// here fails <em>silently and destructively</em>: the visible symptom of any of them regressing is "all my
/// consoles are gone, please pair again", with nothing in a log to say why.
/// </para>
/// </summary>
public class PairedConsoleStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly IPlatformPaths _paths;

    public PairedConsoleStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"ripcord-consoles-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _paths = new FixedPaths(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class FixedPaths(string dir) : IPlatformPaths
    {
        public string ConfigDirectory { get; } = dir;
        public string DataDirectory { get; } = dir;
        public string StateDirectory { get; } = dir;
    }

    private string ConsolesJson => Path.Combine(_dir, "consoles.json");

    private PairedConsoleStore NewStore(ICredentialProtector? protector = null)
        => new(_paths, protector ?? new PlaintextCredentialProtector());

    private static PairedConsole Console(string id, string host, string blob = "") =>
        new(Id: id, Name: "PlayStation 5", Host: host, Platform: "Ps5", CredentialBlob: blob);

    // ---- round trip and blob handling -------------------------------------------------------------

    [Fact]
    public void CredentialBlob_SurvivesAnEncodeStoreLoadDecodeRoundTrip()
    {
        var store = NewStore();
        byte[] record = [1, 2, 3, 4, 250, 251, 252, 253];

        store.Upsert(Console("host-id", "10.0.0.7", store.EncodeBlob(record)));

        PairedConsole loaded = Assert.Single(NewStore().Load());
        Assert.Equal(record, NewStore().DecodeBlob(loaded.StoredBlob));
    }

    [Fact]
    public void EncodeBlob_MarksTheValueAsProtected()
    {
        // The prefix is how a post-encryption blob is told apart from a pre-encryption hex one. If it ever stops
        // being written, the decode path reads ciphertext as hex and every pairing silently becomes unusable.
        Assert.StartsWith(PairedConsoleBlob.ProtectedPrefix, NewStore().EncodeBlob([9, 9, 9]));
    }

    [Fact]
    public void LegacyPlaintextHexBlob_StillDecodes()
    {
        // The upgrade path: a consoles.json written before credentials were encrypted stored bare hex under a
        // different property name. Both halves have to keep working or an upgrading user loses every pairing.
        File.WriteAllText(ConsolesJson, """
            [
              {
                "Id": "10.0.0.7",
                "Name": "PlayStation 5",
                "Host": "10.0.0.7",
                "Platform": "Ps5",
                "CredentialBlobHex": "0102030405"
              }
            ]
            """);

        PairedConsole loaded = Assert.Single(NewStore().Load());

        Assert.Equal("0102030405", loaded.StoredBlob);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, NewStore().DecodeBlob(loaded.StoredBlob));
    }

    [Fact]
    public void DecodeBlob_Garbage_ReturnsNullRatherThanThrowing()
    {
        // "Not decryptable" has to surface as "not paired", which the session reports cleanly, rather than as an
        // exception mid-connect.
        Assert.Null(NewStore().DecodeBlob("not-hex-and-not-base64-!!"));
    }

    [Fact]
    public void OptionalMetadata_SurvivesARoundTrip()
    {
        var store = NewStore();
        DateTimeOffset played = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

        store.Upsert(Console("host-id", "10.0.0.7") with
        {
            Nickname = "Front room",
            ReportedName = "PS5-8A2F",
            HostId = "host-id",
            SystemVersion = "10.01",
            LastConnectedUtc = played,
        });

        PairedConsole loaded = Assert.Single(NewStore().Load());
        Assert.Equal("Front room", loaded.Nickname);
        Assert.Equal("PS5-8A2F", loaded.ReportedName);
        Assert.Equal("10.01", loaded.SystemVersion);
        Assert.Equal(played, loaded.LastConnectedUtc);
        Assert.Equal("Front room", loaded.DisplayName);
    }

    [Fact]
    public void MissingOptionalMetadata_LoadsRatherThanFailing()
    {
        // The back-compat discipline the record's own comment insists on: every field added after the original
        // five is optional, so an old file deserializes with them simply absent. A required field here would make
        // every already-paired console vanish on upgrade.
        File.WriteAllText(ConsolesJson, """
            [{"Id":"10.0.0.7","Name":"PlayStation 5","Host":"10.0.0.7","Platform":"Ps5","CredentialBlob":"dpapi:AQID"}]
            """);

        PairedConsole loaded = Assert.Single(NewStore().Load());

        Assert.Null(loaded.Nickname);
        Assert.Null(loaded.LastConnectedUtc);
        // Falls back to Name, so an upgraded install looks exactly as it did.
        Assert.Equal("PlayStation 5", loaded.DisplayName);
    }

    // ---- identity ---------------------------------------------------------------------------------

    [Fact]
    public void Upsert_SameHost_ReplacesRatherThanDuplicating()
    {
        var store = NewStore();
        store.Upsert(Console("10.0.0.7", "10.0.0.7"));
        store.Upsert(Console("10.0.0.7", "10.0.0.7", "dpapi:AQID"));

        PairedConsole only = Assert.Single(store.Load());
        Assert.Equal("dpapi:AQID", only.CredentialBlob);
    }

    [Fact]
    public void Upsert_SameConsoleAtANewAddress_ReplacesRatherThanDuplicating()
    {
        // The DHCP case. Matching on host alone — which is what this did before host-ids were stored — silently
        // duplicated a console every time its lease moved.
        var store = NewStore();
        store.Upsert(Console("host-id-abc", "10.0.0.7"));
        store.Upsert(Console("host-id-abc", "10.0.0.9"));

        PairedConsole only = Assert.Single(store.Load());
        Assert.Equal("10.0.0.9", only.Host);
    }

    [Fact]
    public void Upsert_GenuinelyDifferentConsoles_BothKept()
    {
        var store = NewStore();
        store.Upsert(Console("host-id-abc", "10.0.0.7"));
        store.Upsert(Console("host-id-def", "10.0.0.8"));

        Assert.Equal(2, store.Load().Count);
    }

    [Fact]
    public void Remove_DropsOnlyTheNamedConsole()
    {
        var store = NewStore();
        store.Upsert(Console("host-id-abc", "10.0.0.7"));
        store.Upsert(Console("host-id-def", "10.0.0.8"));

        store.Remove("HOST-ID-ABC"); // case-insensitive on purpose

        PairedConsole only = Assert.Single(store.Load());
        Assert.Equal("host-id-def", only.Id);
    }

    // ---- failure modes ---------------------------------------------------------------------------

    [Fact]
    public void CorruptJson_LoadsAsEmptyRatherThanThrowing()
    {
        // A corrupt store must not brick the app. It reads as "no consoles" and the next save rewrites it.
        File.WriteAllText(ConsolesJson, "{ this is not json");
        Assert.Empty(NewStore().Load());
    }

    [Fact]
    public void MissingFile_LoadsAsEmpty()
        => Assert.Empty(NewStore().Load());

    [Fact]
    public void Save_IsAtomic_SoNoTempFileIsLeftBehind()
    {
        // Writes go to a temp file and are moved into place, so an interrupted write cannot leave a truncated
        // consoles.json that loses every pairing. If the move ever stopped happening, the real file would go
        // stale while a .tmp accumulated beside it.
        NewStore().Upsert(Console("host-id", "10.0.0.7"));

        Assert.True(File.Exists(ConsolesJson));
        Assert.False(File.Exists(ConsolesJson + ".tmp"));
    }

    // ---- the in-memory substitute ----------------------------------------------------------------

    [Fact]
    public void InMemoryStore_MatchesTheRealStoresIdentityAndRoundTripBehaviour()
    {
        // The substitute is only useful if it agrees with the real thing on the semantics tests rely on.
        var store = new InMemoryPairedConsoleStore();
        byte[] record = [7, 7, 7];

        store.Upsert(Console("host-id-abc", "10.0.0.7", store.EncodeBlob(record)));
        store.Upsert(Console("host-id-abc", "10.0.0.9"));       // same console, new lease

        PairedConsole only = Assert.Single(store.Load());
        Assert.Equal("10.0.0.9", only.Host);
        Assert.Equal(2, store.SaveCount);
    }

    [Fact]
    public void InMemoryStore_LoadReturnsACopy_SoCallersCannotMutateStoredStateWithoutSaving()
    {
        var store = new InMemoryPairedConsoleStore([Console("host-id", "10.0.0.7")]);

        store.Load().Clear();

        Assert.Single(store.Load());
    }
}
