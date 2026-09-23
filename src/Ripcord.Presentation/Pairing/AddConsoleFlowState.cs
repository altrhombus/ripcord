using Ripcord.Presentation.Consoles;

namespace Ripcord.Presentation.Pairing;

/// <summary>The steps of the add-a-console flow, in order.</summary>
public enum AddConsoleStep
{
    Family,
    Find,
    Link,
    Pairing,
    Done,
}

/// <summary>
/// How to prove to the console that this PC may connect.
///
/// <para>
/// Two genuinely different acts, which is why the user picks rather than the app guessing: one is walking to
/// the console and reading a code off it, the other is the console confirming this PC through the signed-in
/// account with nobody getting up. Both remain available wherever both can work — the account route is not
/// always possible (a console the account has never seen, a build with no credential), and someone may simply
/// prefer the code.
/// </para>
/// </summary>
public enum PairingRoute
{
    /// <summary>The console confirms this PC through the signed-in account. No code.</summary>
    Account,

    /// <summary>An 8-digit code read off the console's own screen.</summary>
    Code,
}

/// <summary>
/// Everything the add-console flow currently shows, as one immutable value.
/// </summary>
public sealed record AddConsoleFlowState(
    AddConsoleStep Step,
    int ReachedDash,
    ConsoleFamily Family,
    string? FamilyNote,
    string FindHeading,
    string FindSubheading,
    bool IsScanning,
    bool ManualEntryOpen,
    string LinkHeading,
    string ConsoleStepsText,
    bool CanPair,
    bool AccountPairingOffered,
    bool CanPairWithAccount,
    string AccountPairingNote,

    /// <summary>
    /// Which route the link step is currently set up for. Everything below follows from it: the step used to
    /// show both routes at once — "no code needed", then how to find a code, then a box asking for one — which
    /// contradicted itself and left two commit buttons with nothing to say which belonged to what.
    /// </summary>
    PairingRoute Route,

    /// <summary>
    /// Whether to offer the choice at all. False when only one route can work, in which case the step shows
    /// that one without asking a question that has one answer.
    /// </summary>
    bool RouteChoiceOffered,

    /// <summary>
    /// What the quiet link to the other route says, named for what it gets the player rather than for
    /// the mechanism behind it. Empty when there is only one route.
    /// </summary>
    string SwitchRouteLabel,

    /// <summary>Show the console's own instructions and the code box.</summary>
    /// <summary>
    /// What signing in would save this user, or empty when it would save nothing — because they already have,
    /// or because this build ships no account credential and cannot.
    ///
    /// <para>
    /// An invitation rather than a requirement. The code route works without it and is offered regardless;
    /// this only says what the other route would spare them, which for a first-time user is finding their
    /// account id by hand.
    /// </para>
    /// </summary>
    string SignInInvitation,

    /// <summary>The invitation's own button label. Empty when there is no invitation.</summary>
    string SignInActionLabel,

    bool CodeEntryShown,

    /// <summary>The label for the step's single commit button, which names the route it will take.</summary>
    string PairActionLabel,
    bool AccountIdIsAutomatic,
    string AccountIdNote,
    string? LinkError,
    string PairingStatus,
    string PairingHint,
    bool CanGoBack,
    string SuggestedName,
    string DoneSubtext)
{
    /// <summary>True while a step change should not be interrupted — the pairing exchange.</summary>
    public bool IsPairing => Step == AddConsoleStep.Pairing;
}

/// <summary>Raised when the flow ends with a saved console, and whether the user asked to play immediately.</summary>
public sealed record AddConsoleCompletion(Ripcord.Core.Consoles.PairedConsole Console, bool ConnectNow);

/// <summary>Tunables, all defaulted to what the flow shipped with.</summary>
public sealed class AddConsoleFlowOptions
{
    /// <summary>
    /// Long enough for a resting console, which answers more slowly than an awake one. Generous because results
    /// stream in as they arrive rather than appearing all at once when the window closes.
    /// </summary>
    public TimeSpan SearchWindow { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>How long to give the console to answer a registration request before giving up.</summary>
    public TimeSpan RegistrationTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long to give the account route, which is a longer exchange than the code route by construction: a
    /// WebSocket upgrade, a session create, a command to the console, the console generating and publishing the
    /// seed, and only then the same registration POST the code route makes. Generous rather than tight, because
    /// the console's own step is the slow one and giving up early costs the user another walk through the flow.
    /// </summary>
    public TimeSpan AccountPairingTimeout { get; init; } = TimeSpan.FromSeconds(75);

    /// <summary>The shortest link code that is worth sending. Consoles show eight digits.</summary>
    public int MinimumPasscodeLength { get; init; } = 8;
}
