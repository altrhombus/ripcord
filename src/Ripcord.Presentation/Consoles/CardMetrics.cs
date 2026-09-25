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
public readonly record struct CardLayout(
    CardDensity Density,
    double CellWidth,
    double CellHeight,
    int MaxColumns,
    bool ShowAddTile);

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
    /// The hero cell: a 520×176 card inside a 532×188 one.
    ///
    /// <para>
    /// The width is definite rather than a maximum, and that is load-bearing. As a <c>MaxWidth</c> on a centred
    /// panel it collapsed to content and the card came out around 420 — which does not look wrong on its own,
    /// but the wedge beside it is a fixed 184, so it silently became 44% of the card rather than the 35% it is
    /// drawn as. A proportion expressed as one fixed number beside one elastic one is not a proportion.
    /// </para>
    /// </summary>
    public const double HeroCellWidth = 532;

    /// <summary>See <see cref="HeroCellWidth"/>. The hero is the same height as a grid card, not taller.</summary>
    public const double HeroCellHeight = 188;

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
    /// <param name="consoleCount">
    /// Paired consoles, NOT counting the ghost add tile. One means the hero; zero means the first-run surface
    /// is showing instead and the grid is not rendered at all — the layout returned for it is the hero's, so a
    /// caller that renders anyway gets something sane rather than a division by zero.
    /// </param>
    public static CardLayout For(double viewportWidth, int consoleCount)
    {
        // One console is the hero, whatever the viewport. A grid of one is a list pretending to be a choice,
        // and the viewport does not change that — a hero on a narrow window is still the fastest path to the
        // only thing the page can do.
        if (consoleCount <= 1)
        {
            return new CardLayout(CardDensity.Hero, HeroCellWidth, HeroCellHeight, MaxColumns: 1, ShowAddTile: false);
        }

        bool roomy = viewportWidth >= WideViewportWidth;

        double cellWidth = roomy ? RoomyCellWidth : GridCellWidth;
        double cellHeight = roomy ? RoomyCellHeight : GridCellHeight;

        // The column cap is about readability, not about what fits: ItemsWrapGrid would otherwise take two
        // columns at 639 px because the arithmetic allows it.
        int maxColumns = viewportWidth switch
        {
            > 0 and < SingleColumnWidth => 1,
            >= SingleColumnWidth and < TwoColumnWidth => 2,
            _ => int.MaxValue,
        };

        return new CardLayout(
            roomy ? CardDensity.Roomy : CardDensity.Grid,
            cellWidth,
            cellHeight,
            maxColumns,
            ShowAddTile: true);
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
        CardDensity.Hero => 184,
        CardDensity.Roomy => 112,
        _ => 72,
    };
}
