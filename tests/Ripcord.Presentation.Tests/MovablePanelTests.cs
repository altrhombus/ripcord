using Ripcord.Core.Settings;
using Ripcord.Presentation.Sessions;
using Xunit;

namespace Ripcord.Presentation.Tests;

public sealed class MovablePanelTests
{
    [Fact]
    public void APanelInTheCorner_StaysInTheCorner_WhenTheWindowGrows()
    {
        PanelFrame frame = MovablePanel.ToFrame(new PanelRect(1520, 600, 400, 600), 1920, 1200);

        PanelRect bigger = MovablePanel.Place(frame, 5120, 1440);

        Assert.Equal(5120 - 400, bigger.Left, 3);
        Assert.Equal(1440 - 600, bigger.Top, 3);
    }

    [Fact]
    public void APanelBiggerThanTheWindow_ShrinksToFit_AndStaysInside()
    {
        var frame = new PanelFrame(1600, 1000, 1, 1);

        PanelRect placed = MovablePanel.Place(frame, 1280, 720);

        Assert.Equal(new PanelRect(0, 0, 1280, 720), placed);
    }

    [Fact]
    public void Move_StopsAtTheWindowEdges()
    {
        var start = new PanelRect(100, 100, 400, 300);

        Assert.Equal(new PanelRect(0, 0, 400, 300), MovablePanel.Move(start, -500, -500, 1920, 1080));
        Assert.Equal(new PanelRect(1520, 780, 400, 300), MovablePanel.Move(start, 5000, 5000, 1920, 1080));
    }

    [Fact]
    public void Resize_KeepsTheOppositeEdges_AndStopsAtTheMinimum()
    {
        var start = new PanelRect(500, 200, 400, 300);

        PanelRect fromLeft = MovablePanel.Resize(start, PanelEdges.Left, 1000, 0, 1920, 1080);
        Assert.Equal(start.Right, fromLeft.Right, 3);
        Assert.Equal(MovablePanel.MinimumWidth, fromLeft.Width, 3);

        PanelRect corner = MovablePanel.Resize(start, PanelEdges.Right | PanelEdges.Bottom, 5000, 5000, 1920, 1080);
        Assert.Equal(new PanelRect(500, 200, 1420, 880), corner);
    }

    [Fact]
    public void EdgesAt_FindsCornersAndEdges_AndNothingInTheMiddle()
    {
        Assert.Equal(PanelEdges.Left | PanelEdges.Top, MovablePanel.EdgesAt(400, 300, 2, 2));
        Assert.Equal(PanelEdges.Right, MovablePanel.EdgesAt(400, 300, 398, 150));
        Assert.Equal(PanelEdges.Bottom, MovablePanel.EdgesAt(400, 300, 200, 296));
        Assert.Equal(PanelEdges.None, MovablePanel.EdgesAt(400, 300, 200, 150));
    }

    [Fact]
    public void AWideShortPanel_LaysItsGroupsOutInARow()
    {
        Assert.True(MovablePanel.ArrangesAsRow(1600, 240));
        Assert.False(MovablePanel.ArrangesAsRow(400, 900));
        Assert.False(MovablePanel.ArrangesAsRow(800, 200));
    }
}
