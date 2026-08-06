using Ripcord.Core.Input;
using Xunit;

namespace Ripcord.Presentation.Tests;

/// <summary>
/// Guards the button-prompt position: our own badge, text inside it, never a vendor symbol set.
///
/// <para>
/// A test rather than a comment because this is the one piece of the interface where reaching for a vendor's
/// iconography is the obvious thing to do — their symbols are what users recognise, a symbol font is one line
/// of XAML away, and the change would look like an improvement in review. The rule needs to fail a build, not
/// rely on the next person having read <c>ButtonLabels</c>.
/// </para>
/// </summary>
public class ButtonLabelTests
{
    /// <summary>
    /// The geometric shapes a console vendor's face buttons are drawn as. Their NAMES are fine and are what we
    /// use — a word is a description. The glyphs are the mark.
    /// </summary>
    private const string ForbiddenGlyphs = "✕✖×⨯○◯◦△▲▵□■▫◻";

    [Theory]
    [InlineData(PadFamily.Generic)]
    [InlineData(PadFamily.Vendor)]
    public void BadgeTextIsNeverAVendorGlyph(PadFamily family)
    {
        foreach (PromptButton button in Enum.GetValues<PromptButton>())
        {
            string text = ButtonLabels.BadgeText(button, family);

            Assert.False(
                text.AsSpan().IndexOfAny(ForbiddenGlyphs) >= 0,
                $"Badge text for {button} on a {family} pad is '{text}', which contains a vendor button glyph. "
                + "The badge is ours and the text inside it is a word or a letter — see ButtonLabels.");

            Assert.False(string.IsNullOrWhiteSpace(text), $"Badge text for {button} on a {family} pad is empty.");
        }
    }

    /// <summary>
    /// A badge alone is illegible at couch distance, meaningless to anyone who has not memorised the pad, and
    /// silent to a screen reader — so the API must not be able to produce one.
    /// </summary>
    [Fact]
    public void DescribeAlwaysCarriesTheVerb()
    {
        var prompt = new InputPrompt(PromptButton.South, "Select");

        foreach (PadFamily family in Enum.GetValues<PadFamily>())
        {
            string described = ButtonLabels.Describe(prompt, family);

            Assert.Contains("Select", described, StringComparison.Ordinal);
            Assert.Contains(ButtonLabels.BadgeText(PromptButton.South, family), described, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A stream forwards every button to the console, so any prompt shown over one would be this app claiming
    /// to know what the game does with that button.
    /// </summary>
    [Fact]
    public void ASessionOffersNoPrompts()
        => Assert.Empty(ButtonLabels.DefaultFor(InputScopeKind.Session));

    [Theory]
    [InlineData(InputScopeKind.Chrome)]
    [InlineData(InputScopeKind.Modal)]
    public void EveryPromptHasAVerb(InputScopeKind kind)
    {
        foreach (InputPrompt prompt in ButtonLabels.DefaultFor(kind))
        {
            Assert.False(string.IsNullOrWhiteSpace(prompt.Verb), $"A {kind} prompt for {prompt.Button} has no verb.");
        }
    }

    /// <summary>
    /// Enter and Escape are the same everywhere and in every other app; printing them along the bottom of the
    /// window is noise a keyboard user learns to stop reading. A key label means "nobody would guess this".
    /// </summary>
    [Fact]
    public void OnlyTheNonObviousPromptsCarryAKeyLabel()
    {
        IReadOnlyList<InputPrompt> chrome = ButtonLabels.DefaultFor(InputScopeKind.Chrome);

        Assert.All(
            chrome.Where(p => p.Button is PromptButton.South or PromptButton.East),
            p => Assert.Null(p.KeyLabel));

        Assert.Contains(chrome, p => p.KeyLabel is not null);
    }
}
