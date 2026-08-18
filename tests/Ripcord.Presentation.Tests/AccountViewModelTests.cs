using Ripcord.Presentation.Accounts;
using Ripcord.Presentation.Consoles;
using Ripcord.Presentation.Threading;
using Xunit;

namespace Ripcord.Presentation.Tests;

/// <summary>
/// The account surface's state machine.
///
/// <para>
/// The cases worth pinning here are the ones that are invisible until they go wrong in front of a user: a build
/// with no credential must say so rather than offering a button that throws, a failed restore must read as
/// "signed out" rather than as an error the user is expected to act on, and — the one this layer has got wrong
/// twice before — a result from an abandoned operation must not land on top of a newer one.
/// </para>
/// </summary>
public class AccountViewModelTests
{
    private static AccountViewModel Build(FakeAccountSession session)
        => new(session, new ImmediateUiDispatcher());

    [Fact]
    public void WithoutCredential_ReportsUnavailableRatherThanSignedOut()
    {
        // The distinction matters: "not signed in" invites the user to sign in, and in a build with no
        // credential that invitation leads to a button that cannot work. They are different states and the
        // surface must be able to tell them apart.
        var session = new FakeAccountSession { CanSignIn = false };

        AccountViewState state = Build(session).State;

        Assert.Equal(AccountStep.Unavailable, state.Step);
        Assert.False(state.CanSignIn);
        Assert.False(state.CanSignOut);
        Assert.Contains("isn't available", state.Heading, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WithoutCredential_BeginSignInReturnsNullRatherThanThrowing()
    {
        // The front end calls this from a click handler. Throwing there goes to the message loop.
        var session = new FakeAccountSession { CanSignIn = false };

        Assert.Null(Build(session).BeginSignIn());
    }

    [Fact]
    public void SignedOut_OffersSignIn()
    {
        AccountViewState state = Build(new FakeAccountSession()).State;

        Assert.Equal(AccountStep.SignedOut, state.Step);
        Assert.True(state.CanSignIn);
        Assert.False(state.HasAccountId);
    }

    [Fact]
    public async Task Restore_WithStoredSession_SignsIn()
    {
        var session = new FakeAccountSession
        {
            HasStoredSession = true,
            RestoreResult = new AccountIdentity("4200000000000000042", "somebody", "GB"),
        };
        AccountViewModel vm = Build(session);

        await vm.RestoreAsync();

        Assert.Equal(AccountStep.SignedIn, vm.State.Step);
        Assert.Equal("4200000000000000042", vm.AccountId);
        Assert.True(vm.State.HasAccountId);
        Assert.Equal(StatusTone.Positive, vm.State.Tone);
        Assert.False(vm.State.IsBusy);
    }

    [Fact]
    public async Task Restore_WithNothingStored_DoesNotTouchTheNetwork()
    {
        // Nothing stored is the common case on a fresh install, and it must not cost a request.
        var session = new FakeAccountSession { HasStoredSession = false, Gate = new TaskCompletionSource() };
        AccountViewModel vm = Build(session);

        // Would hang on the gate if it had actually called through.
        await vm.RestoreAsync();

        Assert.Equal(AccountStep.SignedOut, vm.State.Step);
    }

    [Fact]
    public async Task Restore_ThatFails_ReadsAsSignedOutNotAsAnError()
    {
        var session = new FakeAccountSession
        {
            HasStoredSession = true,
            RestoreThrows = new InvalidOperationException("network down"),
        };
        AccountViewModel vm = Build(session);

        await vm.RestoreAsync();

        Assert.Equal(AccountStep.SignedOut, vm.State.Step);
        Assert.False(vm.State.IsBusy);
        Assert.NotNull(vm.State.Error);
    }

    [Fact]
    public async Task SignOut_DuringRestore_IsNotUndoneWhenTheRestoreLands()
    {
        // The generation check exists for exactly this. Without it the restore's continuation signs the user
        // back in a moment after they signed out, which looks like the button did nothing.
        var gate = new TaskCompletionSource();
        var session = new FakeAccountSession
        {
            HasStoredSession = true,
            RestoreResult = new AccountIdentity("1", "somebody", "GB"),
            Gate = gate,
        };
        AccountViewModel vm = Build(session);

        Task restore = vm.RestoreAsync();
        vm.SignOut();
        gate.SetResult();
        await restore;

        Assert.Equal(AccountStep.SignedOut, vm.State.Step);
        Assert.Equal(string.Empty, vm.AccountId);
    }

    [Fact]
    public async Task CompleteSignIn_ThatFails_ReportsThroughStateRatherThanThrowing()
    {
        // Called from a web-view navigation handler, where an escaping exception has nowhere to go.
        var session = new FakeAccountSession { CompleteThrows = new InvalidOperationException("bad code") };
        AccountViewModel vm = Build(session);

        await vm.CompleteSignInAsync(new Uri("https://example.invalid/redirect?code=x"));

        Assert.Equal(AccountStep.SignedOut, vm.State.Step);
        Assert.Contains("bad code", vm.State.Error);
        Assert.False(vm.State.IsBusy);
    }

    [Fact]
    public async Task IdentityChanged_FiresOnSignInAndSignOut()
    {
        var session = new FakeAccountSession
        {
            HasStoredSession = true,
            RestoreResult = new AccountIdentity("77", "somebody", "GB"),
        };
        AccountViewModel vm = Build(session);
        List<AccountIdentity?> seen = [];
        vm.IdentityChanged += seen.Add;

        await vm.RestoreAsync();
        vm.SignOut();

        Assert.Equal(2, seen.Count);
        Assert.Equal("77", seen[0]?.AccountId);
        Assert.Null(seen[1]);
    }

    [Fact]
    public async Task SignOut_ClearsTheConsoleList()
    {
        // Showing the previous account's consoles to the next person to sign in would be a real leak.
        var session = new FakeAccountSession
        {
            HasStoredSession = true,
            RestoreResult = new AccountIdentity("1", "somebody", "GB"),
            Consoles = [new CloudConsole("duid-1", "Living room", true, true)],
        };
        AccountViewModel vm = Build(session);

        await vm.RestoreAsync();
        await vm.LoadConsolesAsync();
        Assert.Single(vm.State.Consoles);

        vm.SignOut();

        Assert.Empty(vm.State.Consoles);
        Assert.False(vm.State.ConsolesLoaded);
    }

    [Fact]
    public async Task LoadConsoles_WhenSignedOut_DoesNotCallThrough()
    {
        var session = new FakeAccountSession();
        AccountViewModel vm = Build(session);

        await vm.LoadConsolesAsync();

        Assert.Equal(0, session.ListCalls);
    }

    [Fact]
    public async Task LoadConsoles_ThatFails_KeepsWhatWasAlreadyThere()
    {
        // A transient cloud error must not empty a list the user is looking at.
        var session = new FakeAccountSession
        {
            HasStoredSession = true,
            RestoreResult = new AccountIdentity("1", "somebody", "GB"),
            Consoles = [new CloudConsole("duid-1", "Living room", true, true)],
        };
        AccountViewModel vm = Build(session);
        await vm.RestoreAsync();
        await vm.LoadConsolesAsync();

        session.ListThrows = new InvalidOperationException("cloud down");
        await vm.LoadConsolesAsync();

        Assert.Single(vm.State.Consoles);
        Assert.Contains("cloud down", vm.State.Error);
    }

    [Fact]
    public void UnavailableAccountSession_DegradesRatherThanThrowing_ExceptWhereItIsAWiringBug()
    {
        var session = new UnavailableAccountSession();

        Assert.False(session.CanSignIn);
        Assert.Null(session.Current);
        Assert.False(session.HasStoredSession);
        Assert.False(session.IsCompletionRedirect(new Uri("https://example.invalid/redirect?code=x")));
        session.SignOut(); // must not throw

        // These two are only reachable by ignoring CanSignIn, which is a bug worth a stack trace.
        Assert.Throws<InvalidOperationException>(session.BeginSignIn);
    }
}
