namespace Ripcord.Presentation.Consoles;

using Ripcord.Presentation.Resources;

/// <summary>One row of the console details dialog.</summary>
/// <param name="Label">What the row is, already localized.</param>
/// <param name="Value">The console's answer, as the card would show it.</param>
public sealed record ConsoleDetail(string Label, string Value);

/// <summary>
/// Everything the card's overflow menu and its dialogs say.
///
/// <para>
/// <b>Why this is a type and not markup.</b> These surfaces are built in code — a <c>MenuFlyout</c> and three
/// <c>ContentDialog</c>s assembled by hand — so they could carry no <c>x:Uid</c>, and the app's resource
/// guard deliberately refuses entries that no markup references. The result was that the app's most
/// important page held twenty-two English strings the catalogue had never heard of: the one page that could
/// be neither translated nor tested.
/// </para>
///
/// <para>
/// Which rows appear is a decision, not a lookup — most of these facts are only known for consoles paired
/// since discovery started carrying them, so each is shown only when there is something to show rather than
/// as a row of blanks. That decision belongs with the rest of what a surface says.
/// </para>
/// </summary>
public static class ConsoleCardCopy
{
    public static string MenuRename => Strings.Console_MenuRename;

    public static string MenuDetails => Strings.Console_MenuDetails;

    public static string MenuRemove => Strings.Console_MenuRemove;

    public static string DetailsClose => Strings.Console_DetailsClose;

    public static string RenameTitle => Strings.Console_RenameTitle;

    public static string RenameExplanation => Strings.Console_RenameExplanation;

    public static string RenameHint => Strings.Console_RenameHint;

    public static string RenameSave => Strings.Console_RenameSave;

    public static string Cancel => Strings.Console_Cancel;

    public static string Ok => Strings.Console_Ok;

    public static string RenameFailed => Strings.Console_RenameFailed;

    public static string RemoveTitle => Strings.Console_RemoveTitle;

    public static string RemoveConfirm => Strings.Console_RemoveConfirm;

    public static string RemoveFailed => Strings.Console_RemoveFailed;

    /// <summary>
    /// The destructive confirmation's body. Names the console, because "this console" in a dialog that can be
    /// raised from any card in a grid is not enough to be sure which one is about to be forgotten.
    /// </summary>
    public static string RemovePrompt(string displayName)
        => string.Format(Strings.Console_RemovePrompt, displayName);

    /// <summary>
    /// The details rows, in display order, omitting anything this console does not know about itself.
    /// </summary>
    public static IReadOnlyList<ConsoleDetail> Details(ConsoleCardViewModel card)
    {
        ArgumentNullException.ThrowIfNull(card);

        List<ConsoleDetail> rows =
        [
            new(Strings.Console_DetailName, card.State.DisplayName),
            new(Strings.Console_DetailConsole, card.Family.LongName),
            new(Strings.Console_DetailAddress, card.Console.Host),
            new(Strings.Console_DetailStatus, card.State.StatusLabel),
        ];

        // Only when it differs: a "reported name" row repeating the name three lines above it is noise that
        // makes the rows that do differ harder to spot.
        if (card.Console.ReportedName is { Length: > 0 } reported && reported != card.State.DisplayName)
        {
            rows.Add(new ConsoleDetail(Strings.Console_DetailReportedName, reported));
        }

        if (card.Console.HostId is { Length: > 0 } hostId)
        {
            rows.Add(new ConsoleDetail(Strings.Console_DetailHostId, hostId));
        }

        if (card.Console.SystemVersion is { Length: > 0 } version)
        {
            rows.Add(new ConsoleDetail(Strings.Console_DetailSystemVersion, version));
        }

        if (card.State.LastConnectedLabel is { } played)
        {
            rows.Add(new ConsoleDetail(Strings.Console_DetailLastPlayed, played));
        }

        return rows;
    }
}
