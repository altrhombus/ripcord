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
            Assert.Equal(CardMetrics.HeroCellWidth, layout.CellWidth);
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
        double hero = CardMetrics.WedgeWidth(CardDensity.Hero) / (CardMetrics.HeroCellWidth - CardMetrics.Gutter);

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
}
