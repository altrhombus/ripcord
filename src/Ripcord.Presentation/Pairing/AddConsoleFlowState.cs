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
