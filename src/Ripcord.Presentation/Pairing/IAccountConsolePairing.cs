using Ripcord.Presentation.Consoles;
using Ripcord.Presentation.Resources;

namespace Ripcord.Presentation.Pairing;

/// <summary>Whether this build can pair through the signed-in account at all, and if not, what to say.</summary>
/// <param name="Detail">
/// Shown to the user verbatim when unavailable. Names what is missing — the interop constants, or the account
/// credential this build was made without — because both are answerable by the person reading it.
/// </param>
public sealed record AccountPairingAvailability(bool Available, string Detail);

/// <summary>
/// What an account pairing needs: where the console is, who is asking, and which console the account service
/// should be told to talk to.
/// </summary>
/// <param name="Host">
/// The console's address on this network. The registration exchange itself is still a direct one — the account
/// route removes the code, not the need to reach the console.
/// </param>
/// <param name="AccountId">The signed-in account's id.</param>
/// <param name="CloudDeviceId">
/// The console's id in the account's own console list. Required, and the reason a hand-typed address cannot
/// take this route: the account service is asked to deliver something to *this* console, and only its cloud id
/// names it.
/// </param>
public sealed record AccountPairingRequest(
    string Host,
    string AccountId,
    string CloudDeviceId,
    ConsoleFamily Family);

/// <summary>
/// Pairing a console through the signed-in account, with no code read off the console's screen.
///
/// <para>
/// <b>A separate seam from <see cref="IConsoleRegistrar"/>, deliberately.</b> The two routes share a result
/// type and nothing else: this one needs the account tier — a token, the cloud, and a live push connection the
/// console publishes to — while the code route is a purely local exchange that must keep working in a build
/// with no account credential at all. Folding them into one interface would give the local route a cloud
/// dependency it does not have, which is the same argument that keeps the wake coordinator's two halves apart.
/// </para>
///
/// <para>
/// The result type is <see cref="ConsoleRegistrationResult"/> because what comes back is the same thing: a
/// pairing record, in plaintext, for the store to encrypt. Where it came from is this seam's business, not the
/// flow's.
/// </para>
/// </summary>
public interface IAccountConsolePairing
{
    /// <summary>
    /// Whether this <em>build and machine</em> could pair through an account — the constants are present and
    /// there is a credential to reach the account service with.
    ///
    /// <para>
    /// Deliberately not an answer about the current user: whether someone is signed in, and whether the console
    /// in front of them is in their account's list, both change while a flow is open, and the flow already
    /// knows both. Keeping them out means this answer can be asked once, synchronously, before the affordance
    /// is offered — it reads files, like its counterpart on <see cref="IConsoleRegistrar"/>.
    /// </para>
    /// </summary>
    AccountPairingAvailability CheckAvailability(ConsoleFamily family);

    /// <summary>
    /// Run the account pairing. Like the code route, an ordinary refusal — a console that never answers, a
    /// token that expired — is a result with <c>Succeeded: false</c> and a reason rather than an exception,
    /// though callers still guard, because a cloud call can always surprise them.
    /// </summary>
    Task<ConsoleRegistrationResult> PairAsync(AccountPairingRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The null object for a build with no account credential — or a front end that composed no account tier.
/// Mirrors <c>UnavailableAccountSession</c>: the graph says the capability is absent once, so no surface has to
/// null-check before asking, and "you cannot pair this way" is a state the UI renders rather than a branch it
/// takes.
/// </summary>
public sealed class UnavailableAccountPairing : IAccountConsolePairing
{
    /// <param name="detail">
    /// Why it is unavailable, in the user's terms. Defaulted to the reason that is true of a build made without
    /// the OAuth credential, which is the only way this type is reached in practice.
    /// </param>
    public UnavailableAccountPairing(string? detail = null)
        => Detail = detail ?? Strings.Pairing_NoSignInUseCode;

    public string Detail { get; }

    public AccountPairingAvailability CheckAvailability(ConsoleFamily family)
        => new(false, Detail);

    public Task<ConsoleRegistrationResult> PairAsync(
        AccountPairingRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new ConsoleRegistrationResult(false, Detail, null));
}
