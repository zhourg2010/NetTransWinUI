using NetTrans.Models;
using NetTrans.Services;
using Xunit;

namespace NetTrans.Tests;

/// <summary>Docking maths, against the positions and thresholds App2 computes.</summary>
public class DockGeometryTests
{
    private const int Width = 536;
    private const int Height = 680;

    private static readonly FrameRect Main = new(100, 200, Width, Height);
    private static readonly FrameRect Inspector = new(0, 0, Width, Height);

    public static TheoryData<DockSide> Sides()
    {
        var data = new TheoryData<DockSide>();
        foreach (var position in Golden.Data.DockPositions) data.Add(position.Side);
        return data;
    }

    public static TheoryData<int, int> NearestOffsets()
    {
        var data = new TheoryData<int, int>();
        foreach (var nearest in Golden.Data.Nearest) data.Add(nearest.OffsetX, nearest.OffsetY);
        return data;
    }

    [Theory]
    [MemberData(nameof(Sides))]
    public void Dock_positions_match_the_prototype(DockSide side)
    {
        var expected = Golden.Data.DockPositions.Single(p => p.Side == side);
        var actual = DockGeometry.DockPosition(side, Main, Inspector);

        Assert.Equal(expected.X, actual.X);
        Assert.Equal(expected.Y, actual.Y);
    }

    [Theory]
    [MemberData(nameof(NearestOffsets))]
    public void Nearest_matches_the_prototype(int offsetX, int offsetY)
    {
        var expected = Golden.Data.Nearest.Single(n => n.OffsetX == offsetX && n.OffsetY == offsetY);

        Assert.Equal(expected.FromRight, NearestFrom(DockSide.Right, offsetX, offsetY));
        Assert.Equal(expected.FromLeft, NearestFrom(DockSide.Left, offsetX, offsetY));
        Assert.Equal(expected.FromBottom, NearestFrom(DockSide.Bottom, offsetX, offsetY));
        Assert.Equal(expected.FromTop, NearestFrom(DockSide.Top, offsetX, offsetY));
    }

    [Fact]
    public void Eighteen_pixels_snaps_and_nineteen_does_not()
    {
        Assert.NotNull(NearestFrom(DockSide.Right, 18, 18));
        Assert.Null(NearestFrom(DockSide.Right, 19, 0));
        Assert.Null(NearestFrom(DockSide.Right, 0, 19));
    }

    [Fact]
    public void The_guide_runs_the_shared_edge_inset_at_both_ends()
    {
        var guide = DockGeometry.GuideRect(DockSide.Right, Main, thickness: 3, inset: 8);

        Assert.Equal(Main.Right - 1, guide.X);
        Assert.Equal(Main.Y + 8, guide.Y);
        Assert.Equal(3, guide.Width);
        Assert.Equal(Height - 16, guide.Height);
    }

    [Fact]
    public void The_horizontal_guide_runs_the_other_way()
    {
        var guide = DockGeometry.GuideRect(DockSide.Bottom, Main, thickness: 3, inset: 8);

        Assert.Equal(Main.X + 8, guide.X);
        Assert.Equal(Main.Bottom - 1, guide.Y);
        Assert.Equal(Width - 16, guide.Width);
        Assert.Equal(3, guide.Height);
    }

    [Fact]
    public void Bonded_frames_square_off_facing_edges()
    {
        Assert.Equal(DockSide.Left, DockGeometry.Opposite(DockSide.Right));
        Assert.Equal(DockSide.Right, DockGeometry.Opposite(DockSide.Left));
        Assert.Equal(DockSide.Top, DockGeometry.Opposite(DockSide.Bottom));
        Assert.Equal(DockSide.Bottom, DockGeometry.Opposite(DockSide.Top));
        Assert.Null(DockGeometry.Opposite(null));
    }

    [Fact]
    public void A_docked_inspector_sits_flush_with_no_gap()
    {
        var position = DockGeometry.DockPosition(DockSide.Right, Main, Inspector);
        Assert.Equal(Main.Right, position.X);
        Assert.Equal(Main.Y, position.Y);
    }

    private static DockSide? NearestFrom(DockSide side, int offsetX, int offsetY)
    {
        var anchor = DockGeometry.DockPosition(side, Main, Inspector);
        var proposed = new FrameRect(anchor.X + offsetX, anchor.Y + offsetY, Width, Height);
        return DockGeometry.Nearest(proposed, Main, DockGeometry.SnapThresholdDips);
    }
}
