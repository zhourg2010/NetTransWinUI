using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using NetTrans.Interop;
using NetTrans.Shell;
using Windows.Graphics;

namespace NetTrans.Services;

/// <summary>
/// Winamp-style magnetic docking between the task frame and the inspector
/// frame, per the handoff's Interactions section: bring an edge of the
/// inspector within 18px of the main frame and a blue guide appears; let go and
/// it eases flush over .24s and then travels with the main frame until pulled
/// away.
/// </summary>
public sealed class DockManager : IDisposable
{
    private const double SnapThresholdDips = 18;
    private const int SettleMilliseconds = 240;

    private readonly WindowChrome _main;
    private readonly WindowChrome _side;
    private readonly SnapGuideWindow _guide = new();
    private readonly DispatcherQueue _dispatcher;

    private DockSide? _candidate;
    private bool _sideIsMoving;
    private DispatcherTimer? _settleTimer;

    /// <summary>The edge the inspector is currently bonded to, or null when it floats free.</summary>
    public DockSide? Dock { get; private set; }

    /// <summary>Raised after <see cref="Dock"/> changes, so the frames can re-square their corners.</summary>
    public event EventHandler? DockChanged;

    /// <summary>Raised once a settle finishes, to play the `snapIn` scale bounce.</summary>
    public event EventHandler? Snapped;

    /// <summary>Raised whenever the main frame moves, so the island can follow it.</summary>
    public event EventHandler? MainMoved;

    public DockManager(WindowChrome main, WindowChrome side, DockSide? initial = DockSide.Right)
    {
        _main = main;
        _side = side;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Dock = initial;

        _main.Moved += OnMainMoved;
        _side.MoveStarted += OnSideMoveStarted;
        _side.Moving += OnSideMoving;
        _side.MoveEnded += OnSideMoveEnded;
    }

    /// <summary>Puts the inspector flush against <paramref name="side"/> of the main frame right now.</summary>
    public void Attach(DockSide side = DockSide.Right)
    {
        Dock = side;
        var target = DockPosition(side);
        AnimateSideTo(target);
        RaiseDockChanged();
    }

    /// <summary>Breaks the bond and nudges the inspector clear, like the handoff's `detach()`.</summary>
    public void Detach()
    {
        if (Dock is null) return;
        Dock = null;
        var b = _side.BoundsPx;
        double s = _side.Scale;
        AnimateSideTo(new PointInt32(b.X + (int)(34 * s), b.Y + (int)(26 * s)));
        RaiseDockChanged();
    }

    /// <summary>Re-glues the inspector to the main frame without animating (used after the main frame moves).</summary>
    public void Reflow()
    {
        if (Dock is not { } side) return;
        var p = DockPosition(side);
        _side.MoveTo(p.X, p.Y);
    }

    private void OnMainMoved(object? sender, EventArgs e)
    {
        if (Dock is not null && !_sideIsMoving) Reflow();
        MainMoved?.Invoke(this, EventArgs.Empty);
    }

    private void OnSideMoveStarted(object? sender, EventArgs e) => _sideIsMoving = true;

    private void OnSideMoving(object? sender, RectInt32 proposed)
    {
        _candidate = Nearest(proposed);

        if (_candidate is { } side)
        {
            _guide.ShowAt(GuideRect(side));
        }
        else
        {
            _guide.Hide();
            if (Dock is not null)
            {
                // Pulled away: unbond live so the corners round back immediately.
                Dock = null;
                RaiseDockChanged();
            }
        }
    }

    private void OnSideMoveEnded(object? sender, EventArgs e)
    {
        _sideIsMoving = false;
        _guide.Hide();

        if (_candidate is not { } side)
        {
            if (Dock is not null)
            {
                Dock = null;
                RaiseDockChanged();
            }
            return;
        }

        Dock = side;
        _candidate = null;
        AnimateSideTo(DockPosition(side));
        RaiseDockChanged();
    }

    /// <summary>Nearest dock edge within the 18px threshold, measured corner-to-corner like the handoff's `nearest()`.</summary>
    private DockSide? Nearest(RectInt32 proposed)
    {
        int threshold = (int)Math.Round(SnapThresholdDips * _side.Scale);
        DockSide? best = null;
        int bestDistance = int.MaxValue;

        foreach (DockSide side in Enum.GetValues<DockSide>())
        {
            var p = DockPosition(side);
            int dx = Math.Abs(proposed.X - p.X);
            int dy = Math.Abs(proposed.Y - p.Y);
            if (dx > threshold || dy > threshold) continue;
            if (dx + dy >= bestDistance) continue;
            best = side;
            bestDistance = dx + dy;
        }

        return best;
    }

    private PointInt32 DockPosition(DockSide side)
    {
        var m = _main.BoundsPx;
        var s = _side.BoundsPx;
        return side switch
        {
            DockSide.Right => new PointInt32(m.X + m.Width, m.Y),
            DockSide.Left => new PointInt32(m.X - s.Width, m.Y),
            DockSide.Bottom => new PointInt32(m.X, m.Y + m.Height),
            _ => new PointInt32(m.X, m.Y - s.Height),
        };
    }

    /// <summary>The `.snapline` rect: 3px thick, inset 8px from the shared edge's ends.</summary>
    private RectInt32 GuideRect(DockSide side)
    {
        var m = _main.BoundsPx;
        double scale = _main.Scale;
        int thickness = Math.Max(1, (int)Math.Round(3 * scale));
        int inset = (int)Math.Round(8 * scale);
        int half = thickness / 2;

        return side switch
        {
            DockSide.Right => new RectInt32(m.X + m.Width - half, m.Y + inset, thickness, m.Height - inset * 2),
            DockSide.Left => new RectInt32(m.X - half, m.Y + inset, thickness, m.Height - inset * 2),
            DockSide.Bottom => new RectInt32(m.X + inset, m.Y + m.Height - half, m.Width - inset * 2, thickness),
            _ => new RectInt32(m.X + inset, m.Y - half, m.Width - inset * 2, thickness),
        };
    }

    /// <summary>Eases the inspector to <paramref name="target"/> over .24s on the shared bezier.</summary>
    private void AnimateSideTo(PointInt32 target)
    {
        _settleTimer?.Stop();

        var from = _side.BoundsPx;
        int dx = target.X - from.X;
        int dy = target.Y - from.Y;
        if (dx == 0 && dy == 0)
        {
            Snapped?.Invoke(this, EventArgs.Empty);
            return;
        }

        var started = Environment.TickCount64;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _settleTimer = timer;

        timer.Tick += (_, _) =>
        {
            double t = Math.Clamp((Environment.TickCount64 - started) / (double)SettleMilliseconds, 0, 1);
            double eased = Easing.Standard(t);
            _side.MoveTo(from.X + (int)Math.Round(dx * eased), from.Y + (int)Math.Round(dy * eased));

            if (t < 1) return;
            timer.Stop();
            if (ReferenceEquals(_settleTimer, timer)) _settleTimer = null;
            Snapped?.Invoke(this, EventArgs.Empty);
        };

        timer.Start();
    }

    private void RaiseDockChanged() => _dispatcher.TryEnqueue(() => DockChanged?.Invoke(this, EventArgs.Empty));

    public void Dispose()
    {
        _settleTimer?.Stop();
        _main.Moved -= OnMainMoved;
        _side.MoveStarted -= OnSideMoveStarted;
        _side.Moving -= OnSideMoving;
        _side.MoveEnded -= OnSideMoveEnded;
        _guide.Dispose();
    }
}
