using Ripcord.Core.Consoles;
using Ripcord.Core.Sessions;

namespace Ripcord.Presentation.Sessions;

/// <summary>How a session reached the console.</summary>
public enum StreamingRoute
{
    /// <summary>Straight at the console's address on this network.</summary>
    Local,

    /// <summary>
    /// Through the signed-in account's rendezvous, which works from anywhere the console can be reached at
    /// all — including networks it is not on.
    /// </summary>
    Account,
}

/// <summary>
/// Which route a console would take, and why.
/// </summary>
/// <param name="Reason">
/// Shown to the user. Worth saying out loud: "connecting over your account because this console is not on
/// this network" is the difference between a slow connect that makes sense and one that looks broken.
/// </param>
public sealed record StreamingRouteChoice(StreamingRoute Route, string Reason);

/// <summary>
/// Opens a streaming session against a paired console, by whichever route can reach it.
///
/// <para>
/// <b>A seam because opening a session is a device, not a value.</b> It binds sockets, and on the account
/// route it also signs in, talks to a cloud service and holds a push connection open for the life of the
/// stream — none of which the portable layer can or should know about.
/// </para>
///
/// <para>
/// <b>Deciding and doing are separate on purpose.</b> A surface wants to say which route it is about to take
/// before it takes it, because the account route costs tens of seconds and silence for that long reads as a
/// hang. Folding the choice into <see cref="OpenAsync"/> would leave the UI unable to explain itself, and
/// would make the decision untestable without a console.
/// </para>
/// </summary>
/// <summary>Whether this build can stream at all, and if not, what to tell the user.</summary>
/// <param name="Detail">
/// Shown verbatim. Names what is missing and where to put it, because that is answerable by the person
/// reading it — the same contract <c>AccountPairingAvailability</c> keeps.
/// </param>
public sealed record StreamingAvailability(bool Available, string Detail);

public interface IStreamingSessionSource
{
    /// <summary>
    /// Whether a session can be opened by any route. False when the build is missing the protocol constants
    /// streaming needs — a condition worth stating before a connect rather than after it fails.
    /// </summary>
    StreamingAvailability Availability { get; }

    /// <summary>
    /// Which route this console would take right now. Never throws: an implementation that cannot tell says
    /// so in the reason and picks the one more likely to work.
    /// </summary>
    Task<StreamingRouteChoice> ChooseRouteAsync(PairedConsole console, CancellationToken cancellationToken);

    /// <summary>
    /// Open a session by <paramref name="route"/>.
    ///
    /// <para>
    /// The returned session <b>owns everything it needs</b>: disposing it releases whatever the route holds
    /// open, so a caller never has to know which route produced it. Throws on failure, with a message meant
    /// for the user.
    /// </para>
    /// </summary>
    /// <param name="loginPin">
    /// Asked for the console's login passcode when its user is locked. The bool says whether a previous
    /// attempt was rejected.
    /// </param>
    /// <param name="progress">Connect-stage lines for the UI; the account route has several worth showing.</param>
    Task<IStreamingSession> OpenAsync(
        PairedConsole console,
        StreamingRoute route,
        Func<bool, CancellationToken, Task<string?>>? loginPin,
        IProgress<string>? progress,
        CancellationToken cancellationToken);
}
