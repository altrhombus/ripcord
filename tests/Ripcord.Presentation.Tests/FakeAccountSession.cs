using Ripcord.Presentation.Accounts;

namespace Ripcord.Presentation.Tests;

/// <summary>
/// A controllable <see cref="IAccountSession"/>.
///
/// <para>
/// Shared between the account view-model's tests and the pairing flow's, because the interesting question in both
/// is the same one: what happens to a surface when the answer to "who is signed in" changes underneath it.
/// </para>
/// </summary>
internal sealed class FakeAccountSession : IAccountSession
{
    public bool CanSignIn { get; set; } = true;

    public AccountIdentity? Current { get; set; }

    public bool HasStoredSession { get; set; }

    /// <summary>What <see cref="RestoreAsync"/> resolves to.</summary>
    public AccountIdentity? RestoreResult { get; set; }

    /// <summary>What <see cref="CompleteSignInAsync"/> resolves to.</summary>
    public AccountIdentity CompleteResult { get; set; } = new("1", "player", "GB");

    public Exception? RestoreThrows { get; set; }

    public Exception? CompleteThrows { get; set; }

    public Exception? ListThrows { get; set; }

    public IReadOnlyList<CloudConsole> Consoles { get; set; } = [];

    public string AuthorizeUrl { get; set; } = "https://example.invalid/authorize";

    /// <summary>Held open so a test can control when a restore or sign-in resolves.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public int SignOutCalls { get; private set; }

    public int ListCalls { get; private set; }

    public async Task<AccountIdentity?> RestoreAsync(CancellationToken cancellationToken)
    {
        if (Gate is not null)
        {
            await Gate.Task.ConfigureAwait(false);
        }

        if (RestoreThrows is { } ex)
        {
            throw ex;
        }

        Current = RestoreResult;
        return RestoreResult;
    }

    public string BeginSignIn() => CanSignIn
        ? AuthorizeUrl
        : throw new InvalidOperationException("cannot sign in");

    public bool IsCompletionRedirect(Uri navigated)
        => navigated.AbsolutePath.Contains("redirect", StringComparison.Ordinal);

    public async Task<AccountIdentity> CompleteSignInAsync(Uri redirected, CancellationToken cancellationToken)
    {
        if (Gate is not null)
        {
            await Gate.Task.ConfigureAwait(false);
        }

        if (CompleteThrows is { } ex)
        {
            throw ex;
        }

        Current = CompleteResult;
        return CompleteResult;
    }

    public Task<IReadOnlyList<CloudConsole>> ListConsolesAsync(CancellationToken cancellationToken)
    {
        ListCalls++;
        return ListThrows is { } ex
            ? Task.FromException<IReadOnlyList<CloudConsole>>(ex)
            : Task.FromResult(Consoles);
    }

    public Exception? WakeThrows { get; set; }

    public List<string> WakeRequests { get; } = [];

    public Task WakeAsync(string cloudDeviceId, CancellationToken cancellationToken)
    {
        WakeRequests.Add(cloudDeviceId);
        return WakeThrows is { } ex ? Task.FromException(ex) : Task.CompletedTask;
    }

    public void SignOut()
    {
        SignOutCalls++;
        Current = null;
    }
}
