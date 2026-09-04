using Ripcord.Core.Consoles;
using Ripcord.Presentation.Accounts;
using Ripcord.Presentation.Threading;
using Xunit;

namespace Ripcord.Presentation.Tests;

/// <summary>
/// Matching a stored console against the account service's view of it, and repairing the ones paired before
/// that id was ever recorded.
///
/// <para>
/// The backfill exists because remote wake would otherwise work only for consoles paired after the account
/// tier shipped — and would fail by simply never being attempted, with nothing on screen to explain why an
/// older console behaved differently from a newer one.
/// </para>
/// </summary>
public class CloudConsoleMatchTests
{
    private static readonly IReadOnlyList<CloudConsole> Cloud =
    [
        new("duid-living", "PS5-8A2F", true, true),
        new("duid-office", "PS5-1B3C", true, false),
    ];

    /// <summary>
    /// A stored console. The host is derived from <paramref name="id"/> rather than fixed, because
    /// <c>PairedConsoleStore.IsSameConsole</c> treats a shared host as the same physical box — so records
    /// sharing one would collapse into a single entry on upsert, which is correct behaviour and makes a
    /// multi-console fixture silently a one-console fixture.
    /// </summary>
    /// <summary>
    /// One host octet per distinct id, assigned in first-seen order.
    ///
    /// <para>
    /// This used to be <c>id.GetHashCode() &amp; 0x7f</c>, which flaked about one run in forty: .NET randomizes
    /// string hashing per process, and 0x7f leaves 128 buckets, so two of a test's ids would occasionally land
    /// on the same host. The store treats a shared host as the same physical console
    /// (<c>PairedConsoleStore.IsSameConsole</c> matches on <c>Id</c> <em>or</em> <c>Host</c>, so a re-pair after
    /// a DHCP change still finds its record), and an Upsert would then quietly replace a record the test still
    /// expected to be there. Deterministic and injective, so it cannot.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, int> HostOctets = new(StringComparer.Ordinal);

    private static int HostOctetFor(string id)
    {
        lock (HostOctets)
        {
            if (!HostOctets.TryGetValue(id, out int octet))
            {
                octet = HostOctets.Count + 1;
                HostOctets[id] = octet;
            }

            return octet;
        }
    }

    private static PairedConsole Stored(
        string id, string? reportedName, string? cloudDeviceId = null, string? nickname = null)
        => new(id, "PlayStation 5", $"10.0.0.{HostOctetFor(id)}", "PS5", "blob")
        {
            ReportedName = reportedName,
            CloudDeviceId = cloudDeviceId,
            Nickname = nickname,
        };

    // ---- matching -------------------------------------------------------------------------------

    [Fact]
    public void MatchesByName() => Assert.Equal("duid-living", CloudConsoleMatch.ResolveId(Cloud, "PS5-8A2F"));

    [Fact]
    public void MatchIsCaseInsensitive()
        => Assert.Equal("duid-office", CloudConsoleMatch.ResolveId(Cloud, "ps5-1b3c"));

    [Fact]
    public void AmbiguousNameYieldsNothingRatherThanAGuess()
    {
        // A wrong id here would send a wake to someone else's console.
        IReadOnlyList<CloudConsole> twins =
            [new("duid-a", "PS5-8A2F", true, true), new("duid-b", "PS5-8A2F", true, true)];

        Assert.Null(CloudConsoleMatch.ResolveId(twins, "PS5-8A2F"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("something else entirely")]
    public void UnmatchableNamesYieldNothing(string? name)
        => Assert.Null(CloudConsoleMatch.ResolveId(Cloud, name));

    [Fact]
    public void EmptyCloudListYieldsNothing()
        => Assert.Null(CloudConsoleMatch.ResolveId([], "PS5-8A2F"));

    // ---- backfill -------------------------------------------------------------------------------

    [Fact]
    public void Backfill_RepairsAConsolePairedBeforeTheCloudIdExisted()
    {
        var store = new InMemoryPairedConsoleStore();
        store.Upsert(Stored("a", "PS5-8A2F"));

        int repaired = CloudConsoleMatch.Backfill(store, Cloud);

        Assert.Equal(1, repaired);
        Assert.Equal("duid-living", store.Load().Single().CloudDeviceId);
    }

    [Fact]
    public void Backfill_MatchesOnTheConsolesOwnNameNotTheUsersNickname()
    {
        // The trap: DisplayName prefers the nickname, so matching on it would miss every console the user has
        // renamed — which is most of the ones they care about, since renaming is how you tell two apart.
        var store = new InMemoryPairedConsoleStore();
        store.Upsert(Stored("a", reportedName: "PS5-8A2F", nickname: "Living room"));

        CloudConsoleMatch.Backfill(store, Cloud);

        Assert.Equal("duid-living", store.Load().Single().CloudDeviceId);
    }

    [Fact]
    public void Backfill_NeverOverwritesAnIdAlreadyStored()
    {
        // A console that has been re-paired or hand-corrected keeps what it has.
        var store = new InMemoryPairedConsoleStore();
        store.Upsert(Stored("a", "PS5-8A2F", cloudDeviceId: "duid-set-by-pairing"));

        int repaired = CloudConsoleMatch.Backfill(store, Cloud);

        Assert.Equal(0, repaired);
        Assert.Equal("duid-set-by-pairing", store.Load().Single().CloudDeviceId);
    }

    [Fact]
    public void Backfill_IsIdempotent()
    {
        var store = new InMemoryPairedConsoleStore();
        store.Upsert(Stored("a", "PS5-8A2F"));

        Assert.Equal(1, CloudConsoleMatch.Backfill(store, Cloud));
        Assert.Equal(0, CloudConsoleMatch.Backfill(store, Cloud));
        Assert.Single(store.Load());
    }

    [Fact]
    public void Backfill_LeavesUnmatchedConsolesAlone()
    {
        // A console not on this account, or one whose record predates ReportedName being captured (those hold
        // the family label, which correctly matches nothing).
        var store = new InMemoryPairedConsoleStore();
        store.Upsert(Stored("a", reportedName: null));

        Assert.Equal(0, CloudConsoleMatch.Backfill(store, Cloud));
        Assert.Null(store.Load().Single().CloudDeviceId);
    }

    [Fact]
    public void Backfill_DoesNotDuplicateOrDropConsoles()
    {
        var store = new InMemoryPairedConsoleStore();
        store.Upsert(Stored("a", "PS5-8A2F"));
        store.Upsert(Stored("b", "PS5-1B3C"));
        store.Upsert(Stored("c", "PS5-NOPE"));

        CloudConsoleMatch.Backfill(store, Cloud);

        List<PairedConsole> after = store.Load();
        Assert.Equal(3, after.Count);
        Assert.Equal("duid-living", after.Single(c => c.Id == "a").CloudDeviceId);
        Assert.Equal("duid-office", after.Single(c => c.Id == "b").CloudDeviceId);
        Assert.Null(after.Single(c => c.Id == "c").CloudDeviceId);
    }

    [Fact]
    public void Backfill_PreservesEverythingElseOnTheRecord()
    {
        // Upsert rewrites the record, so a dropped field here would quietly lose a nickname or a credential.
        var store = new InMemoryPairedConsoleStore();
        store.Upsert(Stored("a", "PS5-8A2F", nickname: "Living room"));

        CloudConsoleMatch.Backfill(store, Cloud);

        PairedConsole after = store.Load().Single();
        Assert.Equal("Living room", after.Nickname);
        Assert.Equal("blob", after.CredentialBlob);
        Assert.Equal(Stored("a", "PS5-8A2F").Host, after.Host);
    }

    // ---- through the view-model ------------------------------------------------------------------

    [Fact]
    public async Task LoadingTheAccountsConsoles_RepairsStoredOnesOnTheWayPast()
    {
        var store = new InMemoryPairedConsoleStore();
        store.Upsert(Stored("a", "PS5-8A2F"));

        var session = new FakeAccountSession
        {
            HasStoredSession = true,
            RestoreResult = new AccountIdentity("1", "somebody", "GB"),
            Consoles = Cloud,
        };
        var vm = new AccountViewModel(session, new ImmediateUiDispatcher(), store);

        await vm.RestoreAsync();
        await vm.LoadConsolesAsync();

        Assert.Equal("duid-living", store.Load().Single().CloudDeviceId);
    }

    [Fact]
    public async Task AStoreThatCannotBeWritten_DoesNotFailTheConsoleList()
    {
        // The only thing lost is a remote wake, and the repair runs again next time.
        var session = new FakeAccountSession
        {
            HasStoredSession = true,
            RestoreResult = new AccountIdentity("1", "somebody", "GB"),
            Consoles = Cloud,
        };
        var vm = new AccountViewModel(session, new ImmediateUiDispatcher(), new ThrowingStore());

        await vm.RestoreAsync();
        await vm.LoadConsolesAsync();

        Assert.Equal(2, vm.State.Consoles.Count);
        Assert.Null(vm.State.Error);
    }

    private sealed class ThrowingStore : IPairedConsoleStore
    {
        public List<PairedConsole> Load() => [Stored("a", "PS5-8A2F")];

        public void Save(List<PairedConsole> consoles) => throw new IOException("disk full");

        public List<PairedConsole> Upsert(PairedConsole console) => throw new IOException("disk full");

        public List<PairedConsole> Remove(string id) => throw new IOException("disk full");

        public string EncodeBlob(byte[] pairingRecord) => string.Empty;

        public byte[]? DecodeBlob(string stored) => null;

        public string ProtectionDescription => "test";

        public bool CredentialsEncrypted => false;
    }
}
