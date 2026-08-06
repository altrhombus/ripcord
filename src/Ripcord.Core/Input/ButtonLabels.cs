namespace Ripcord.Core.Input;

/// <summary>Which family of pad is in the user's hands, for labelling purposes only.</summary>
public enum PadFamily
{
    /// <summary>Anything reporting through GameInput/XInput, and the assumption before a pad identifies itself.</summary>
    Generic,

    /// <summary>A pad on our own raw-HID engine, whose face buttons are named rather than lettered.</summary>
    Vendor,
}

/// <summary>A face button a prompt can refer to. Positional, never by any vendor's name for it.</summary>
public enum PromptButton
{
    South,
    East,
    West,
    North,
}

/// <summary>
/// One thing the user can do right now: a button, what it does, and optionally the key that does the same.
/// </summary>
/// <param name="Button">The face button, positionally.</param>
/// <param name="Verb">What it does here — "Select", "Back", "Options". Always shown; see <see cref="ButtonLabels"/>.</param>
/// <param name="KeyLabel">
/// The keyboard equivalent, and <b>only</b> where it is worth saying. Null means "obvious on a keyboard" —
/// Enter selects and Escape goes back everywhere in this app and in every other, so printing them along the
/// bottom of the window would be noise a keyboard user learns to stop reading. A prompt earns a key label by
/// being something nobody would guess.
/// </param>
public readonly record struct InputPrompt(PromptButton Button, string Verb, string? KeyLabel = null);

/// <summary>
/// What a button prompt says.
///
/// <para>
/// <b>This file exists to be the single place anyone has to look.</b> Button prompts are where a client like
/// this one is most tempted to reproduce a console vendor's iconography, because their symbols are what users
/// recognise. This project does not, anywhere, and centralising the decision means the claim can be checked in
/// one file rather than argued about per surface.
/// </para>
///
/// <para>
/// <b>The rule: our own badge, with text inside it.</b> The badge shape is ours. The content is a WORD or a
/// LETTER, never a reproduction of a vendor symbol set and never a symbol font containing one — those glyph
/// designs are the mark, and drawing them is the thing to avoid regardless of which font happens to be
/// installed. For a vendor pad the face buttons have names, and a name in our own badge is a plain description
/// of what the user is looking at; for everything else the letters A/B/X/Y are nobody's mark. This follows the
/// position the on-screen touch bar already took deliberately: text, not glyph designs.
/// </para>
///
/// <para>
/// <b>The verb always accompanies the badge</b> — "Cross · Select", never a lone badge. That is not only
/// caution: a badge alone is unreadable at couch distance, means nothing to someone who has not memorised the
/// layout, and is silent to a screen reader. The safe choice and the legible one are the same choice here,
/// which is why the API gives no way to render one without the other.
/// </para>
/// </summary>
public static class ButtonLabels
{
    /// <summary>
    /// The text inside the badge.
    ///
    /// <para>
    /// Vendor pads name their face buttons after shapes, so the badge says the shape's NAME. It does not draw
    /// the shape, and the difference between those two is the entire point of this file.
    /// </para>
    /// </summary>
    public static string BadgeText(PromptButton button, PadFamily family) => family switch
    {
        PadFamily.Vendor => button switch
        {
            PromptButton.South => "Cross",
            PromptButton.East => "Circle",
            PromptButton.West => "Square",
            PromptButton.North => "Triangle",
            _ => "?",
        },

        // Letters, in our own badge. A letter is nobody's mark.
        _ => button switch
        {
            PromptButton.South => "A",
            PromptButton.East => "B",
            PromptButton.West => "X",
            PromptButton.North => "Y",
            _ => "?",
        },
    };

    /// <summary>
    /// The whole prompt as one string — badge text and verb together, which is the only way either is offered.
    /// Also what a screen reader reads, so it has to be a sentence a person would say.
    /// </summary>
    public static string Describe(InputPrompt prompt, PadFamily family)
        => $"{BadgeText(prompt.Button, family)}: {prompt.Verb}";

    /// <summary>
    /// What a surface offers by default, by the kind of claim it has on the pad. A scope may override, but most
    /// do not need to, and a default that is right nearly always is what stops prompts drifting per page.
    /// </summary>
    public static IReadOnlyList<InputPrompt> DefaultFor(InputScopeKind kind) => kind switch
    {
        InputScopeKind.Chrome =>
        [
            new(PromptButton.South, "Select"),
            new(PromptButton.East, "Back"),

            // The one prompt worth showing a keyboard user: a context menu is reachable by pad and by
            // right-click, and the Menu key is the thing nobody thinks of.
            new(PromptButton.North, "Options", KeyLabel: "Menu"),
        ],

        InputScopeKind.Modal =>
        [
            new(PromptButton.South, "Select"),
            new(PromptButton.East, "Cancel"),
        ],

        // A stream forwards every button to the console. Prompts would be describing what the GAME does with
        // them, which this app cannot know and must not guess at.
        _ => [],
    };
}
