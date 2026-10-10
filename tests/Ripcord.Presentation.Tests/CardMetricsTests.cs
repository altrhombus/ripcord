using Ripcord.Presentation.Consoles;
using Xunit;

namespace Ripcord.Presentation.Tests;

/// <summary>
/// The console grid's layout rule.
///
/// <para>
/// These exist because the rule has to hold on displays nobody here owns — an ultrawide, a 4K desktop, a
/// 1280×800 handheld and a television reporting 100% scale — and the alternative to testing it is finding out
/// on someone else's screen that it was written for one window size.
/// </para>
/// </summary>
public class CardMetricsTests
{
    [Fact]
    public void OneConsole_IsTheHero_WhateverTheViewport()
    {
        foreach (double width in new double[] { 480, 800, 1280, 1920, 3440 })
        {
            CardLayout layout = CardMetrics.For(width, consoleCount: 1);

            Assert.Equal(CardDensity.Hero, layout.Density);
            Assert.Equal(1, layout.MaxColumns);
        }
    }

    /// <summary>
    /// A second hero-sized ghost tile beside the only console would make "add another" look like half the
    /// page's purpose. The hero layout offers a quiet link instead, so the tile must not be in the collection.
    /// </summary>
    [Fact]
    public void TheHeroDoesNotCarryTheAddTile()
    {
        Assert.False(CardMetrics.For(1280, consoleCount: 1).ShowAddTile);
        Assert.True(CardMetrics.For(1280, consoleCount: 2).ShowAddTile);
    }

    /// <summary>
    /// Zero consoles means the first-run surface is showing and the grid is not rendered. The rule still has
    /// to return something sane rather than a zero-width cell, because a caller that renders anyway should get
    /// a card rather than a crash.
    /// </summary>
    [Fact]
    public void NoConsoles_StillYieldsAUsableCell()
    {
        CardLayout layout = CardMetrics.For(1280, consoleCount: 0);

        Assert.True(layout.CellWidth > 0);
        Assert.True(layout.CellHeight > 0);
        Assert.False(layout.ShowAddTile);
    }

    [Theory]
    [InlineData(480, 1)]
    [InlineData(639, 1)]
    [InlineData(640, 2)]
    [InlineData(1007, 2)]
    [InlineData(1008, int.MaxValue)]
    [InlineData(3440, int.MaxValue)]
    public void ColumnsAreCappedOnTheWindowsBreakpoints(double width, int expected)
        => Assert.Equal(expected, CardMetrics.For(width, consoleCount: 4).MaxColumns);

    /// <summary>
    /// The step up is at 1920, which is where a maximised desktop window and a Big Picture session on a
    /// television both land. Asserted on both sides of the boundary because an off-by-one here is invisible
    /// until someone drags a window across it.
    /// </summary>
    [Fact]
    public void TheCellStepsUpAtTheWideBreakpoint()
    {
        Assert.Equal(CardDensity.Grid, CardMetrics.For(1919, consoleCount: 4).Density);
        Assert.Equal(CardMetrics.GridCellWidth, CardMetrics.For(1919, consoleCount: 4).CellWidth);

        Assert.Equal(CardDensity.Roomy, CardMetrics.For(1920, consoleCount: 4).Density);
        Assert.Equal(CardMetrics.RoomyCellWidth, CardMetrics.For(1920, consoleCount: 4).CellWidth);
    }

    /// <summary>
    /// The type ramp belongs to Windows. This rule may grow Ripcord's own furniture and nothing else, so the
    /// roomy cell has to be a real step rather than a rounding difference — if it were only a few pixels
    /// bigger the whole breakpoint would be costing complexity for nothing.
    /// </summary>
    [Fact]
    public void TheRoomyCellIsAMeaningfulStep()
    {
        Assert.True(CardMetrics.RoomyCellWidth >= CardMetrics.GridCellWidth * 1.2);
        Assert.True(CardMetrics.RoomyCellHeight >= CardMetrics.GridCellHeight * 1.2);
    }

    /// <summary>
    /// The wedge is a proportion of the card, not a chip. The dense card's share is the one that had to come
    /// down: at 92 of 280 it was a third of a card already short of text column, and three separate clipping
    /// incidents are recorded against that budget.
    /// </summary>
    [Fact]
    public void TheWedgeKeepsItsShareOfEachCard()
    {
        double dense = CardMetrics.WedgeWidth(CardDensity.Grid) / (CardMetrics.GridCellWidth - CardMetrics.Gutter);
        double roomy = CardMetrics.WedgeWidth(CardDensity.Roomy) / (CardMetrics.RoomyCellWidth - CardMetrics.Gutter);
        double hero = CardMetrics.WedgeWidth(CardDensity.Hero) / 520;

        Assert.InRange(dense, 0.24, 0.28);
        Assert.InRange(roomy, 0.30, 0.34);
        Assert.InRange(hero, 0.33, 0.37);
    }

    /// <summary>
    /// The visible card is the cell minus the container's gutter. Getting this wrong is not obvious, which is
    /// why it was wrong once already: at ItemHeight 176 the card came out 164 tall while the template's rows
    /// were sized for 176, and the only Height="*" row absorbed the whole shortfall and clipped.
    /// </summary>
    [Fact]
    public void EveryCellLeavesRoomForTheGutter()
    {
        foreach (int count in new[] { 1, 2, 5 })
        {
            foreach (double width in new double[] { 800, 1280, 2560 })
            {
                CardLayout layout = CardMetrics.For(width, count);

                Assert.True(layout.CellWidth - CardMetrics.Gutter > 0);
                Assert.True(layout.CellHeight - CardMetrics.Gutter > 0);
            }
        }
    }

    /// <summary>
    /// The hero follows the window: half the page, between the smallest and largest hero. It was a fixed 520 at
    /// every size, a strip in a big window and clipped through its play mark in a small one (2026-10-08).
    /// </summary>
    [Theory]
    [InlineData(640, CardMetrics.HeroMinCardWidth)]
    [InlineData(1280, 640)]
    [InlineData(1600, 800)]
    [InlineData(3440, CardMetrics.HeroMaxCardWidth)]
    public void TheHero_IsHalfThePage_BetweenItsBounds(double viewport, double card)
    {
        CardLayout layout = CardMetrics.For(viewport, consoleCount: 1);

        Assert.Equal(card, layout.Geometry.CardWidth);
        Assert.Equal(card + CardMetrics.Gutter, layout.CellWidth);
    }

    /// <summary>
    /// The hero keeps growing with the window until it meets the page column, so Home's title can sit where every
    /// other page's does (showcase review, decision E).
    /// </summary>
    [Theory]
    [InlineData(1280, 640)]
    [InlineData(1920, 808)]
    [InlineData(3440, 808)]
    public void TheHero_StopsAtThePageColumn(double viewport, double card)
    {
        CardLayout layout = CardMetrics.For(viewport, consoleCount: 1, columnWidth: 820);

        Assert.Equal(card, layout.Geometry.CardWidth);
        Assert.True(layout.CellWidth <= 820);
        Assert.Equal(card >= CardMetrics.HeroLargeTypeWidth ? CardNameStep.TitleLarge : CardNameStep.Title,
            layout.Geometry.NameStep);
    }

    /// <summary>The smallest window the app allows still holds the whole card, padding and gutter included.</summary>
    [Fact]
    public void TheHero_FitsTheMinimumWindow()
    {
        const double window = 640;
        CardLayout layout = CardMetrics.For(window, consoleCount: 1);

        Assert.True(layout.CellWidth <= window - (2 * CardMetrics.PagePadding));
    }

    /// <summary>
    /// The proportion, the wedge's share and so the diagonal's angle hold at every size; only the size changes.
    /// </summary>
    [Theory]
    [InlineData(640)]
    [InlineData(1280)]
    [InlineData(3440)]
    public void TheHero_KeepsItsProportionAndItsWedge(double viewport)
    {
        CardGeometry g = CardMetrics.For(viewport, consoleCount: 1).Geometry;
        double height = CardMetrics.For(viewport, consoleCount: 1).CellHeight - CardMetrics.Gutter;

        Assert.InRange(g.WedgeWidth / g.CardWidth, 0.34, 0.36);
        Assert.True(height >= CardMetrics.HeroMinCardHeight);
        Assert.True(g.InsetRight > g.WedgeWidth);
    }

    /// <summary>
    /// Text steps up the platform's ramp once, at a breakpoint, rather than scaling with the card (showcase plan,
    /// decision A). Asserted on both sides, as the other breakpoints are.
    /// </summary>
    [Fact]
    public void TheHerosText_StepsUpTheRampAtTheBreakpoint()
    {
        CardGeometry below = CardMetrics.For(1436, consoleCount: 1).Geometry;
        CardGeometry above = CardMetrics.For(1440, consoleCount: 1).Geometry;

        Assert.Equal(CardNameStep.Title, below.NameStep);
        Assert.False(below.StatusIsBody);
        Assert.Equal(CardNameStep.TitleLarge, above.NameStep);
        Assert.True(above.StatusIsBody);
    }

    /// <summary>The grid cards are unchanged by any of this: subtitle names, caption status, their old insets.</summary>
    [Fact]
    public void GridCards_KeepTheirOwnGeometry()
    {
        CardGeometry g = CardMetrics.For(1280, consoleCount: 4).Geometry;

        Assert.Equal(CardNameStep.Subtitle, g.NameStep);
        Assert.Equal(CardMetrics.WedgeWidth(CardDensity.Grid), g.WedgeWidth);
        Assert.Equal(CardMetrics.GridCellWidth - CardMetrics.Gutter, g.CardWidth);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void LargerText_MakesTheCardTallerAndNothingElse(int consoles)
    {
        // At 225% text a fixed cell clipped the status line and cut the caption at the wedge (2026-10-08).
        CardLayout normal = CardMetrics.For(1280, consoles);
        CardLayout large = CardMetrics.For(1280, consoles, textScale: 2.25);

        Assert.Equal(normal.CellHeight + (CardMetrics.TextHeight * 1.25), large.CellHeight);
        Assert.Equal(normal.CellWidth, large.CellWidth);
        Assert.Equal(normal.Density, large.Density);
    }

    [Fact]
    public void TextScale_BelowOne_IsTreatedAsOne()
        => Assert.Equal(CardMetrics.For(1280, 1).CellHeight, CardMetrics.For(1280, 1, textScale: 0.5).CellHeight);
}
