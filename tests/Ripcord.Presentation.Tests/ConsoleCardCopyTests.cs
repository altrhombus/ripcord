using Ripcord.Core.Consoles;
using Ripcord.Presentation.Consoles;
using Ripcord.Presentation.Threading;
using Xunit;

namespace Ripcord.Presentation.Tests;

/// <summary>
/// What the card's menu and dialogs say, and which detail rows exist at all.
///
/// <para>
/// None of this was reachable by a test before: it was twenty-two English strings and a run of
/// <c>if</c> statements inside a <c>ContentDialog</c> assembled by hand in the page, on the one surface
/// every user meets first.
/// </para>
/// </summary>
public class ConsoleCardCopyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    private static ConsoleCardViewModel Card(PairedConsole console)
        => new(console, new ImmediateUiDispatcher(), () => Now);

    private static PairedConsole Console(
        string? nickname = null,
        string? reportedName = null,
        string? hostId = null,
        string? systemVersion = null,
        DateTimeOffset? lastConnected = null)
        => new(Id: "host-id", Name: "PlayStation 5", Host: "10.0.0.7", Platform: "Ps5", CredentialBlob: "")
        {
            Nickname = nickname,
            ReportedName = reportedName,
            HostId = hostId,
            SystemVersion = systemVersion,
            LastConnectedUtc = lastConnected,
        };

    private static IReadOnlyList<string> LabelsOf(PairedConsole console)
        => [.. ConsoleCardCopy.Details(Card(console)).Select(detail => detail.Label)];

    [Fact]
    public void AConsoleThatKnowsNothingAboutItselfStillHasFourRows()
    {
        // Name, console, address and status always resolve, so the dialog is never empty.
        IReadOnlyList<ConsoleDetail> rows = ConsoleCardCopy.Details(Card(Console()));

        Assert.Equal(4, rows.Count);
        Assert.All(rows, row => Assert.False(string.IsNullOrWhiteSpace(row.Label)));
        Assert.All(rows, row => Assert.False(string.IsNullOrWhiteSpace(row.Value)));
    }

    [Fact]
    public void OptionalFactsAppearOnlyWhenThereIsSomethingToShow()
    {
        // These are known only for consoles paired since discovery started carrying them. A row of blanks
        // would be worse than a shorter dialog, because it reads as data that is missing rather than absent.
        IReadOnlyList<ConsoleDetail> rows = ConsoleCardCopy.Details(Card(Console(
            reportedName: "Living Room PS5",
            hostId: "ABCD1234",
            systemVersion: "25.02",
            lastConnected: Now.AddMinutes(-30))));

        // Seven, not eight: with no nickname set, DisplayName already resolves to the reported name, so that
        // row is correctly suppressed as a duplicate of the title. Asserted here rather than only in the
        // dedicated case below, because it is the behaviour a reader of this test would otherwise misread.
        Assert.Equal(7, rows.Count);
        Assert.Contains(rows, row => row.Value == "ABCD1234");
        Assert.Contains(rows, row => row.Value == "25.02");
        Assert.DoesNotContain(rows, row => row.Label == "Reported name");
    }

    [Fact]
    public void EmptyStringsCountAsAbsentRatherThanAsAValue()
    {
        Assert.Equal(4, LabelsOf(Console(reportedName: "", hostId: "", systemVersion: "")).Count);
    }

    [Fact]
    public void TheReportedNameIsOmittedWhenItIsAlreadyTheNameOnScreen()
    {
        // Repeating the display name three rows below itself is noise, and noise makes the rows that DO
        // differ harder to spot - which is the entire job of this dialog.
        IReadOnlyList<ConsoleDetail> same = ConsoleCardCopy.Details(Card(Console(reportedName: "PlayStation 5")));
        Assert.Equal(4, same.Count);

        // But it earns its row the moment the user has renamed the console, because then the two disagree
        // and which one the console answers to is exactly what someone opened this to find out.
        IReadOnlyList<ConsoleDetail> renamed = ConsoleCardCopy.Details(
            Card(Console(nickname: "Upstairs", reportedName: "PlayStation 5")));

        Assert.Equal(5, renamed.Count);
        Assert.Contains(renamed, row => row.Value == "PlayStation 5");
    }

    [Fact]
    public void TheRemovePromptNamesTheConsoleItIsAboutToForget()
    {
        // The dialog can be raised from any card in a grid, so "this console" is not enough to be sure which
        // pairing is about to be discarded - and it is not undoable.
        string prompt = ConsoleCardCopy.RemovePrompt("Living Room PS5");

        Assert.Contains("Living Room PS5", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("{0}", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryPieceOfCopyResolvesToSomething()
    {
        // A missing .resx entry returns null from the generated accessor rather than throwing, so a typo
        // would reach a dialog as an empty button before anyone noticed.
        string[] all =
        [
            ConsoleCardCopy.MenuRename, ConsoleCardCopy.MenuDetails, ConsoleCardCopy.MenuRemove,
            ConsoleCardCopy.DetailsClose, ConsoleCardCopy.RenameTitle, ConsoleCardCopy.RenameExplanation,
            ConsoleCardCopy.RenameHint, ConsoleCardCopy.RenameSave, ConsoleCardCopy.Cancel,
            ConsoleCardCopy.Ok, ConsoleCardCopy.RenameFailed, ConsoleCardCopy.RemoveTitle,
            ConsoleCardCopy.RemoveConfirm, ConsoleCardCopy.RemoveFailed,
        ];

        Assert.All(all, piece => Assert.False(string.IsNullOrWhiteSpace(piece)));
    }
}
