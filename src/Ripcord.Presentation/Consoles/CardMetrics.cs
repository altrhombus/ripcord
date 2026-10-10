namespace Ripcord.Presentation.Consoles;

/// <summary>How large a console card is drawn, and therefore how much it is allowed to say.</summary>
public enum CardDensity
{
    /// <summary>The grid card. The dense case: several consoles, each read at a glance.</summary>
    Grid,

    /// <summary>
    /// The grid card at a wide viewport. Same content, more room — see the note on
    /// <see cref="CardMetrics.WideViewportWidth"/> for why growing the card is not the same thing as growing
    /// the text.
    /// </summary>
    Roomy,

    /// <summary>
    /// The one-console layout. The console <em>is</em> the page, so the card can name its own action and put
    /// the status and the last-played caption on one line instead of two.
    /// </summary>
    Hero,
}

/// <summary>
/// The cell size, column cap and density for the console grid.
/// </summary>
/// <param name="Density">How much the card is allowed to say. See <see cref="CardDensity"/>.</param>
/// <param name="CellWidth">
/// <c>ItemsWrapGrid.ItemWidth</c>. This is the CELL, which contains the container's gutter margin as well as
/// the card — the visible card is <see cref="Gutter"/> smaller in each axis.
/// </param>
/// <param name="CellHeight"><c>ItemsWrapGrid.ItemHeight</c>, on the same terms.</param>
/// <param name="MaxColumns"><c>ItemsWrapGrid.MaximumRowsOrColumns</c>.</param>
/// <param name="ShowAddTile">
/// Whether the ghost "add a console" tile belongs in the collection. False at <see cref="CardDensity.Hero"/>,
/// where a second hero-sized tile beside the only console would make adding one look like half the page's
/// purpose; the hero layout offers a quiet link instead.
/// </param>
/// <param name="Geometry">How the card inside the cell is drawn. See <see cref="CardGeometry"/>.</param>
public readonly record struct CardLayout(
    CardDensity Density,
    double CellWidth,
    double CellHeight,
    int MaxColumns,
    bool ShowAddTile,
    CardGeometry Geometry);

/// <summary>Which step of the platform's type ramp the console's name takes.</summary>
public enum CardNameStep
{
    Subtitle,
    Title,
    TitleLarge,
}

/// <summary>
/// How a card is drawn at the size it is being given: the wedge, the insets, and which step of the type ramp
/// its text takes. Plain numbers, so the rule is tested here and the front end only turns them into
/// thicknesses and styles.
/// </summary>
/// <param name="CardWidth">The visible card, without the gutter.</param>
/// <param name="WedgeWidth">The play wedge, a proportion of the card. See <see cref="CardMetrics.WedgeShare"/>.</param>
/// <param name="InsetLeft">Space between the card's leading edge and its text.</param>
/// <param name="InsetRight">The text column's trailing inset: the wedge, its slant and a little air.</param>
/// <param name="InsetVertical">Space above and below the text.</param>
/// <param name="NameStep">The console name's step on the type ramp.</param>
/// <param name="StatusIsBody">The status line is Body rather than Caption, on a card big enough to read across a room.</param>
/// <param name="MarkHeight">The family mark beside "PS5"; its width follows the mark's 9 : 16.</param>
public readonly record struct CardGeometry(
    double CardWidth,
    double WedgeWidth,
    double InsetLeft,
    double InsetRight,
    double InsetVertical,
    CardNameStep NameStep,
    bool StatusIsBody,
    double MarkHeight);

/// <summary>
/// Decides how the console grid is laid out, from the viewport and the number of consoles.
///
/// <para>
/// <b>Why this is one function and not two layouts.</b> The one-console hero used to be a separate panel with
/// its own markup and its own <c>RenderHero()</c>, and the two drifted: the hero could not show the "checking"
/// spinner the grid card shows, its overflow button was under the touch minimum, and a change made to one was
/// routinely not made to the other. Every fact the hero rendered was already on <see cref="ConsoleCardState"/>
/// — the only reason it needed code at all was that it was not inside an items control. So it is now the same
/// card at a third density, and divergence is unrepresentable rather than discouraged.
/// </para>
///
/// <para>
/// <b>Windows owns text size; Ripcord owns the size of Ripcord's own cards.</b> That distinction is what makes
/// <see cref="WideViewportWidth"/> legitimate where the deleted <c>AppScale</c> was not. The type ramp is the
/// platform's and never moves here; what responds is this app's own furniture, and it responds to the viewport
/// rather than to a preference, so there is nothing for anyone to find, choose or get wrong.
/// </para>
///
/// <para>
/// Pure and unit-tested, for the same reason <see cref="Sessions.HudPlacement"/> is: the alternative is
/// discovering on someone else's display that the rule was written for one window size.
/// </para>
/// </summary>
public static class CardMetrics
{
    /// <summary>
    /// The gutter each container carries, as <c>Margin="0,0,12,12"</c>. <c>ItemsWrapGrid</c> has no spacing of
    /// its own, so the gutter lives inside the cell and the visible card is this much smaller than the cell.
    /// </summary>
    public const double Gutter = 12;

    /// <summary>The dense cell: a 280×176 card inside a 292×188 cell.</summary>
    public const double GridCellWidth = 292;

    /// <summary>See <see cref="GridCellWidth"/>.</summary>
    public const double GridCellHeight = 188;

    /// <summary>
    /// The roomy cell: a 348×220 card inside a 360×232 one.
    ///
    /// <para>
    /// The dense card leaves about 164 px of text column once the 92 px wedge and the padding are taken out,
    /// and that budget has now failed three separate times — a truncated "Played 11 Sep 2", a status row that
    /// lost its bottom third, and a two-line console name overflowing the card on the first screen anyone
    /// sees. 348 px of card leaves about 204, which fits all three.
    /// </para>
    /// </summary>
    public const double RoomyCellWidth = 360;

    /// <summary>See <see cref="RoomyCellWidth"/>.</summary>
    public const double RoomyCellHeight = 232;

    /// <summary>
    /// The hero takes this share of the page's width, between <see cref="HeroMinCardWidth"/> and
    /// <see cref="HeroMaxCardWidth"/>.
    ///
    /// <para>
    /// It was a fixed 520 × 176 at every window size: a small strip under a lot of nothing in a big window, and
    /// clipped through its play mark in a small one (visual audit, 2026-10-08). One console is the whole page,
    /// so the card is sized like a page's subject rather than like one tile of many.
    /// </para>
    /// </summary>
    public const double HeroShare = 0.5;

    /// <summary>The smallest hero, which is the old fixed one less a little, and fits the 640 px window.</summary>
    public const double HeroMinCardWidth = 440;

    /// <summary>
    /// The largest hero. Past this a card stops reading as a card and starts reading as a banner, and the eye has
    /// to travel from the name to the wedge.
    /// </summary>
    public const double HeroMaxCardWidth = 880;

    /// <summary>The hero's width to height, the old 520 × 176's, held as it grows.</summary>
    public const double HeroAspect = 520.0 / 176.0;

    /// <summary>The hero is never shorter than the grid card, whatever its proportion says.</summary>
    public const double HeroMinCardHeight = 176;

    /// <summary>
    /// Where the hero's text steps up the platform's type ramp: its name from Title to Title Large and its status
    /// from Caption to Body. A card twice as wide with the same small text reads as empty; stepping the ramp at a
    /// breakpoint is how Fluent pages answer more room (showcase plan, decision A, 2026-10-09).
    /// </summary>
    public const double HeroLargeTypeWidth = 720;

    /// <summary>The page's own padding each side, which the hero must fit inside.</summary>
    public const double PagePadding = 24;

    /// <summary>The wedge's share of the hero, as drawn on the original 520 card (184 of 520).</summary>
    public const double WedgeShare = 0.35;

    /// <summary>
    /// How much of a card's height is text at 100% text size: the name, the family line and the status line with
    /// their line heights. The rest is padding and the wedge's frame, which text size does not touch. Measured
    /// from the hero's rows; a card under-provisioned by a few pixels still trims cleanly, so it need not be exact.
    /// </summary>
    public const double TextHeight = 100;

    /// <summary>
    /// Where the card steps up a size.
    ///
    /// <para>
    /// This is where a maximised desktop window and a Big Picture session on a television both land, and one
    /// rule serves both. The television is the case worth stating: Windows sets display scale from the EDID,
    /// and a 55-inch 1080p set commonly reports 100%, so someone who arrived from a game launcher has tuned
    /// nothing and is reading a 292 px card from three metres.
    /// </para>
    /// </summary>
    public const double WideViewportWidth = 1920;

    /// <summary>
    /// Narrow enough that two cards side by side would each be narrower than their own content wants. Below
    /// this the grid takes one column, which is a readable card rather than two cramped ones.
    /// </summary>
    public const double SingleColumnWidth = 640;

    /// <summary>Below this the grid is capped at two columns. The standard Windows breakpoint.</summary>
    public const double TwoColumnWidth = 1008;

    /// <summary>
    /// Lay out the grid.
    /// </summary>
    /// <param name="viewportWidth">Width available to the page, in effective pixels.</param>
    /// <param name="textScale">
    /// Windows' text size, 1.0 to 2.25. The card's text grows with it and its frame does not, so the cell grows by
    /// the share of its height that is text: at 225% a fixed cell cut "Played 2 days ago" off at the wedge and
    /// pushed the status line out of the card (visual audit, 2026-10-08).
    /// </param>
    /// <param name="consoleCount">
    /// Paired consoles, NOT counting the ghost add tile. One means the hero; zero means the first-run surface
    /// is showing instead and the grid is not rendered at all — the layout returned for it is the hero's, so a
    /// caller that renders anyway gets something sane rather than a division by zero.
    /// </param>
    /// <param name="columnWidth">
    /// The page column's width, inside its padding: the hero grows with the window but never past it. Home sits in
    /// the same column as every other page, so its title stays put when you move between them (showcase review,
    /// decision E, 2026-10-09). Unbounded by default.
    /// </param>
    public static CardLayout For(
        double viewportWidth, int consoleCount, double textScale = 1.0, double columnWidth = double.PositiveInfinity)
    {
        double extra = TextHeight * (Math.Clamp(textScale, 1.0, 3.0) - 1.0);

        // One console is the hero, whatever the viewport. A grid of one is a list pretending to be a choice,
        // and the viewport does not change that — a hero on a narrow window is still the fastest path to the
        // only thing the page can do.
        if (consoleCount <= 1)
        {
            return Hero(viewportWidth, columnWidth, extra);
        }

        bool roomy = viewportWidth >= WideViewportWidth;

        double cellWidth = roomy ? RoomyCellWidth : GridCellWidth;
        double cellHeight = (roomy ? RoomyCellHeight : GridCellHeight) + extra;

        // The column cap is about readability, not about what fits: ItemsWrapGrid would otherwise take two
        // columns at 639 px because the arithmetic allows it.
        int maxColumns = viewportWidth switch
        {
            > 0 and < SingleColumnWidth => 1,
            >= SingleColumnWidth and < TwoColumnWidth => 2,
            _ => int.MaxValue,
        };

        CardDensity density = roomy ? CardDensity.Roomy : CardDensity.Grid;

        return new CardLayout(
            density,
            cellWidth,
            cellHeight,
            maxColumns,
            ShowAddTile: true,
            Geometry: roomy
                ? new CardGeometry(cellWidth - Gutter, WedgeWidth(density), 20, 124, 18, CardNameStep.Subtitle, false, 16)
                : new CardGeometry(cellWidth - Gutter, WedgeWidth(density), 16, 84, 14, CardNameStep.Subtitle, false, 16));
    }

    /// <summary>
    /// The one-console card, sized from the page. Its proportion, its wedge's share and the wedge's angle hold at
    /// every size; its text steps up the ramp once at <see cref="HeroLargeTypeWidth"/>.
    /// </summary>
    private static CardLayout Hero(double viewportWidth, double columnWidth, double extraTextHeight)
    {
        // Inside the page's padding and its column, and inside the cell's own gutter.
        double room = Math.Max(0, Math.Min(viewportWidth - (2 * PagePadding), columnWidth) - Gutter);
        double card = Math.Clamp(viewportWidth * HeroShare, HeroMinCardWidth, HeroMaxCardWidth);

        // Never wider than the page has room for. A window under the app's minimum (or a first measure at zero)
        // still gets the smallest hero rather than nothing.
        card = Math.Round(Math.Max(Math.Min(card, room), HeroMinCardWidth * 0.75));

        double height = Math.Round(Math.Max(HeroMinCardHeight, card / HeroAspect));
        bool large = card >= HeroLargeTypeWidth;

        var geometry = new CardGeometry(
            CardWidth: card,
            WedgeWidth: Math.Round(card * WedgeShare),
            InsetLeft: large ? 36 : 28,
            InsetRight: Math.Round(card * WedgeShare) + 16,
            InsetVertical: large ? 32 : 24,
            NameStep: large ? CardNameStep.TitleLarge : CardNameStep.Title,
            StatusIsBody: large,
            MarkHeight: large ? 20 : 16);

        return new CardLayout(
            CardDensity.Hero,
            card + Gutter,
            height + Gutter + extraTextHeight,
            MaxColumns: 1,
            ShowAddTile: false,
            geometry);
    }

    /// <summary>
    /// The wedge's width for a density, which is a proportion of the card rather than a chip size.
    ///
    /// <para>
    /// The slant is held as a ratio of the wedge's height, so the diagonal's angle is identical at every one of
    /// these — which is the point, because the diagonal is the app's signature and a signature with a different
    /// slope on every surface is not one.
    /// </para>
    ///
    /// <para>
    /// <b>The dense card's wedge is the one that shrinks.</b> 92 of 280 is a third of a card that is already
    /// short of text column; 72 is a quarter, and the 20 px goes to the name. The hero's 184 of 520 stays at
    /// 35% because the hero has the room to spend and is where the proportion was drawn.
    /// </para>
    /// </summary>
    public static double WedgeWidth(CardDensity density) => density switch
    {
        // The hero's is a share of whatever width it is given; this is its width on the 520 card it was drawn on.
        CardDensity.Hero => 184,
        CardDensity.Roomy => 112,
        _ => 72,
    };
}
