using Ripcord.Presentation.Resources;

namespace Ripcord.Presentation;

/// <summary>
/// A failure as a person reads it: what happened and what to do, in plain words; and the raw text it came from,
/// kept for a bug report. <see cref="Technical"/> is empty when the message was already plain.
/// </summary>
public sealed record PlainFailure(string Message, string Technical);

/// <summary>
/// Plain words for the failures that reach a screen.
///
/// <para>
/// <b>Why (review, 2026-10-05).</b> Every failure showed its raw text: a wrong or expired pairing code read
/// "Registration was rejected by the console (HTTP 403, RP-Application-Reason …)". The raw text is still worth
/// having, for a bug report, so it is kept as <see cref="PlainFailure.Technical"/> and shown small, under the
/// plain sentence that leads. Matching is on markers in the raw text, the same text the backends write; a
/// failure with no known marker is shown as it is, since it was written for a person already.
/// </para>
/// </summary>
public static class FailureCopy
{
    private static readonly (string Marker, Func<string> Message)[] PairingRules =
    [
        // Order matters: the 403 case before the general rejection.
        ("rejected by the console (HTTP 403", () => Strings.Failure_PairingCodeRejected),
        ("rejected by the console", () => Strings.Failure_PairingRefused),
        ("Could not reach the console", () => Strings.Failure_PairingUnreachable),
        ("did not contain a valid pairing record", () => Strings.Failure_PairingBadAnswer),
        ("Registration cipher is not available", () => Strings.Failure_PairingNoConstants),
        ("PlayStation Network refused the request", () => Strings.Failure_PsnRefused),
        ("Couldn't reach PlayStation Network", () => Strings.Failure_PsnUnreachable),
    ];

    // Failures already written for a person: shown as they are. Checked first, so a broader rule below can't
    // replace one with something vaguer.
    private static readonly string[] AlreadyPlain =
    [
        "login passcode", "rejected the passcode", "accepted the passcode",
        "paired without an account", "Sign in to your PlayStation Network account",
        "is not an address this console can be reached at", "dropped each one immediately",
        "This build cannot connect through an account",
    ];

    private static readonly (string Marker, Func<string> Message)[] ConnectRules =
    [
        ("rejected (HTTP 403", () => Strings.Failure_ConnectRefused),
        ("rejected (HTTP 401", () => Strings.Failure_ConnectRefused),

        // The account route's session registration, refused. Worded differently from /sess/init's, so it fell
        // through to the general sentence, which told someone connecting over the internet to check they were
        // on the same network (visual audit, 2026-10-08).
        ("rejected by the console (HTTP 403", () => Strings.Failure_ConnectRefused),
        ("Pair the console again", () => Strings.Failure_ConnectRepair),
        ("did not wake", () => Strings.Connect_DidNotWakeDetail),
        ("didn't wake", () => Strings.Connect_DidNotWakeDetail),
        ("The console ended the session", () => Strings.Failure_ConnectEndedWhileStarting),
        ("Control setup timed out", () => Strings.Failure_ConnectNoAnswer),
        ("didn't answer within", () => Strings.Failure_ConnectNoAnswer),
        ("no INIT_ACK", () => Strings.Failure_ConnectStreamDidNotStart),
        ("Stream key agreement did not complete", () => Strings.Failure_ConnectStreamDidNotStart),
        ("stream bring-up did not complete", () => Strings.Failure_ConnectStreamDidNotStart),
        ("never joined the session", () => Strings.Failure_ConnectOverInternet),
        ("registration seed", () => Strings.Failure_ConnectOverInternet),
        ("never offered its candidates", () => Strings.Failure_ConnectOverInternet),
        ("offered no candidate address", () => Strings.Failure_ConnectOverInternet),
        ("control association", () => Strings.Failure_ConnectOverInternet),
        ("did not open a connection to this console", () => Strings.Failure_ConnectOverInternet),
        ("Couldn't reach PlayStation Network", () => Strings.Failure_ConnectPsnUnreachable),
        ("PlayStation Network refused the request", () => Strings.Failure_ConnectPsnRefused),
        ("Signed out while", () => Strings.Failure_ConnectSignIn),
        ("Not signed in", () => Strings.Failure_ConnectSignIn),
    ];

    /// <summary>
    /// A connect failure, from the session controller's final reason. Anything unrecognised gets a general
    /// sentence and keeps its raw text as the technical detail: most of what the protocol layers write is for a
    /// developer, and the few plain ones are listed above.
    /// </summary>
    public static PlainFailure ForConnect(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new PlainFailure(Strings.Failure_ConnectGeneral, string.Empty);
        }

        foreach (string marker in AlreadyPlain)
        {
            if (raw.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return new PlainFailure(raw, string.Empty);
            }
        }

        foreach ((string marker, Func<string> message) in ConnectRules)
        {
            if (raw.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return new PlainFailure(message(), raw);
            }
        }

        return new PlainFailure(Strings.Failure_ConnectGeneral, raw);
    }

    /// <summary>A pairing failure, from the registrar's or the account pairing's reason.</summary>
    public static PlainFailure ForPairing(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new PlainFailure(Strings.Pairing_Failed, string.Empty);
        }

        foreach ((string marker, Func<string> message) in PairingRules)
        {
            if (raw.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return new PlainFailure(message(), raw);
            }
        }

        return new PlainFailure(raw, string.Empty);
    }
}
