using NetTrans.Models;

namespace NetTrans.Services;

/// <summary>A frame's bounds in physical pixels.</summary>
public readonly record struct FrameRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;
}

/// <summary>A frame's top-left corner in physical pixels.</summary>
public readonly record struct FramePoint(int X, int Y);

/// <summary>
/// The maths behind the Winamp-style docking: where a bonded inspector sits,
/// which edge a dragged inspector is close enough to bond to, and where the
/// blue guide is drawn. Pure integers, so it can be tested without a window.
/// </summary>
public static class DockGeometry
{
    /// <summary>SNAP in the handoff: 18 design pixels.</summary>
    public const int SnapThresholdDips = 18;

    /// <summary>The inspector's top-left corner when bonded to <paramref name="side"/> of the task frame.</summary>
    public static FramePoint DockPosition(DockSide side, FrameRect main, FrameRect inspector) => side switch
    {
        DockSide.Right => new FramePoint(main.Right, main.Y),
        DockSide.Left => new FramePoint(main.X - inspector.Width, main.Y),
        DockSide.Bottom => new FramePoint(main.X, main.Bottom),
        _ => new FramePoint(main.X, main.Y - inspector.Height),
    };

    /// <summary>
    /// The edge a frame proposed at <paramref name="proposed"/> would bond to,
    /// or null when none is within <paramref name="threshold"/>. Distance is
    /// measured corner to corner on both axes, as `nearest()` does, and ties go
    /// to the smallest dx + dy in right, left, bottom, top order.
    /// </summary>
    public static DockSide? Nearest(FrameRect proposed, FrameRect main, int threshold)
    {
        DockSide? best = null;
        int bestDistance = int.MaxValue;

        foreach (DockSide side in new[] { DockSide.Right, DockSide.Left, DockSide.Bottom, DockSide.Top })
        {
            var position = DockPosition(side, main, proposed);
            int dx = Math.Abs(proposed.X - position.X);
            int dy = Math.Abs(proposed.Y - position.Y);

            if (dx > threshold || dy > threshold) continue;
            if (dx + dy >= bestDistance) continue;

            best = side;
            bestDistance = dx + dy;
        }

        return best;
    }

    /// <summary>`.snapline`: a bar of <paramref name="thickness"/> along the shared edge, inset at both ends.</summary>
    public static FrameRect GuideRect(DockSide side, FrameRect main, int thickness, int inset)
    {
        int half = thickness / 2;

        return side switch
        {
            DockSide.Right => new FrameRect(main.Right - half, main.Y + inset, thickness, main.Height - inset * 2),
            DockSide.Left => new FrameRect(main.X - half, main.Y + inset, thickness, main.Height - inset * 2),
            DockSide.Bottom => new FrameRect(main.X + inset, main.Bottom - half, main.Width - inset * 2, thickness),
            _ => new FrameRect(main.X + inset, main.Y - half, main.Width - inset * 2, thickness),
        };
    }

    /// <summary>The edge whose corners the inspector squares off, given where the task frame bonded.</summary>
    public static DockSide? Opposite(DockSide? side) => side switch
    {
        DockSide.Right => DockSide.Left,
        DockSide.Left => DockSide.Right,
        DockSide.Bottom => DockSide.Top,
        DockSide.Top => DockSide.Bottom,
        _ => null,
    };
}
