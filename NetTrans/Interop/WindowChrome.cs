using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using NetTrans.Models;
using Windows.Graphics;

namespace NetTrans.Interop;

/// <summary>
/// One borderless 16px-rounded frame. Wraps the Win32 work WinUI does not
/// expose: exact corner radius, squaring off the bonded edge (`.bond-r`/`-l`/
/// `-t`/`-b`), starting a drag from ordinary content (`.nav` is `cursor: grab`),
/// and observing the OS move loop so the dock manager can snap.
/// </summary>
public sealed class WindowChrome : IDisposable
{
    private readonly Window _window;
    private readonly NativeMethods.SubclassProc _subclass;
    private static nuint _nextSubclassId = 1;
    private readonly nuint _subclassId;
    private bool _disposed;
    private double _dipWidth;
    private double _dipHeight;
    private DockSide? _squared;
    private double _cornerRadius = 16;

    /// <summary>Fires for every WM_MOVING with the rect the OS is proposing, in physical pixels.</summary>
    public event EventHandler<RectInt32>? Moving;

    /// <summary>Fires for every WM_MOVE, i.e. after the window actually moved.</summary>
    public event EventHandler? Moved;

    public event EventHandler? MoveStarted;
    public event EventHandler? MoveEnded;

    /// <summary>Fires with the hotkey id from WM_HOTKEY.</summary>
    public event EventHandler<int>? HotKeyPressed;

    public nint Handle { get; }

    /// <summary>
    /// Whether the window is subclassed. False means no docking, no edge-hide
    /// and no hotkey -- but a window that opens, which is the trade.
    /// </summary>
    public bool Subclassed { get; }

    public WindowChrome(Window window)
    {
        _window = window;
        Handle = WinRT.Interop.WindowNative.GetWindowHandle(window);

        _subclassId = _nextSubclassId++;
        _subclass = SubclassProc;

        // Subclassing is how docking, the move loop and the 老板键 hear from the
        // OS. Losing it costs those; it must not cost the window itself, which
        // is what an exception here used to do -- silently, before anything was
        // on screen.
        try
        {
            Subclassed = NativeMethods.SetWindowSubclass(Handle, _subclass, _subclassId, 0);

            if (!Subclassed) Diagnostics.Startup.Log($"SetWindowSubclass 失败：{Marshal.GetLastWin32Error()}");
        }
        catch (Exception exception)
        {
            Diagnostics.Startup.Log($"SetWindowSubclass 不可用：{exception.GetType().Name}: {exception.Message}");
        }
    }

    /// <summary>Physical pixels per DIP. The handoff's numbers are DIPs, the Win32 calls are pixels.</summary>
    public double Scale => NativeMethods.GetDpiForWindow(Handle) / 96.0;

    public AppWindow AppWindow => _window.AppWindow;

    public RectInt32 BoundsPx
    {
        get
        {
            NativeMethods.GetWindowRect(Handle, out var r);
            return new RectInt32(r.Left, r.Top, r.Width, r.Height);
        }
    }

    /// <summary>
    /// The rectangle the frame actually paints, in screen pixels.
    ///
    /// Not the same as <see cref="BoundsPx"/>: the resize border is kept for
    /// the DWM shadow, so the window rect is larger than the client area by a
    /// few invisible pixels. Docking two frames by their window rects leaves
    /// exactly that much daylight between the two visible edges, which is why
    /// everything about docking measures this instead.
    /// </summary>
    public RectInt32 ClientBoundsPx
    {
        get
        {
            NativeMethods.GetClientRect(Handle, out var c);

            var origin = new NativeMethods.POINT();
            NativeMethods.ClientToScreen(Handle, ref origin);

            return new RectInt32(origin.X, origin.Y, c.Width, c.Height);
        }
    }

    /// <summary>How far the painted area sits inside the window rect.</summary>
    public (int X, int Y) FramePx
    {
        get
        {
            var outer = BoundsPx;
            var client = ClientBoundsPx;
            return (client.X - outer.X, client.Y - outer.Y);
        }
    }

    /// <summary>Moves the frame so its painted top-left lands on the given screen point.</summary>
    public void MoveClientTo(int xPx, int yPx)
    {
        var (offsetX, offsetY) = FramePx;
        MoveTo(xPx - offsetX, yPx - offsetY);
    }

    /// <summary>
    /// Chrome-less frame. The border is kept (and then clipped away by the
    /// corner region) because dropping WS_THICKFRAME also drops the DWM drop
    /// shadow, and the design leans on that shadow heavily.
    /// </summary>
    public void MakeFrameless(bool resizable = false, bool keepShadow = true)
    {
        if (_window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(keepShadow, false);
            presenter.IsResizable = resizable;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = true;
        }

        // Our own region draws the corners; stop DWM from rounding on top of it.
        int pref = NativeMethods.DWMWCP_DONOTROUND;
        NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }

    /// <summary>Hides the frame from the taskbar/alt-tab and (optionally) keeps it from taking focus.</summary>
    public void MakeUtilityWindow(bool noActivate = false, bool topMost = false)
    {
        nint ex = NativeMethods.GetWindowLongPtr(Handle, NativeMethods.GWL_EXSTYLE);
        ex |= NativeMethods.WS_EX_TOOLWINDOW;
        if (noActivate) ex |= NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLongPtr(Handle, NativeMethods.GWL_EXSTYLE, ex);

        if (topMost)
        {
            NativeMethods.SetWindowPos(Handle, -1 /* HWND_TOPMOST */, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
    }

    /// <summary>Sizes the window so its content box is exactly <paramref name="dipWidth"/> x <paramref name="dipHeight"/> DIPs.</summary>
    public void SetContentSize(double dipWidth, double dipHeight, double cornerRadius = 16)
    {
        _dipWidth = dipWidth;
        _dipHeight = dipHeight;
        _cornerRadius = cornerRadius;
        double s = Scale;

        // ResizeClient, not Resize: Resize sets the *window* rect, and with the
        // resize border still on, that leaves the client -- everything XAML
        // paints -- smaller than the frame by a few pixels. The corner region
        // below is cut to the design's size either way, so those pixels showed
        // up as an unpainted strip down the right edge and along the bottom.
        int wantWidth = (int)Math.Round(dipWidth * s);
        int wantHeight = (int)Math.Round(dipHeight * s);

        _window.AppWindow.ResizeClient(new SizeInt32(wantWidth, wantHeight));

        // ResizeClient is documented to leave the client exactly that size, and
        // on a frame with no title bar it does not: the caption it assumes is
        // there gets counted anyway, so the client came back 31px taller. XAML
        // laid out into all 711 of them -- putting the toolbar at 711-49 -- and
        // the corner region below then clipped the window back to 680, which is
        // why the seven toolbar icons were sliced off at the bottom edge.
        //
        // So measure and correct through the window rect, which is unambiguous.
        // Twice at most: if it has not converged by then it is not going to.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            NativeMethods.GetClientRect(Handle, out var client);

            int growWidth = wantWidth - client.Width;
            int growHeight = wantHeight - client.Height;
            if (growWidth == 0 && growHeight == 0) break;

            var outer = BoundsPx;
            _window.AppWindow.Resize(new SizeInt32(outer.Width + growWidth, outer.Height + growHeight));
        }

        ApplyCorners(_squared);
    }

    /// <summary>
    /// 16px rounded region, with the corners on <paramref name="squaredSide"/>
    /// flattened when the frame is bonded to a neighbour.
    /// </summary>
    public void ApplyCorners(DockSide? squaredSide)
    {
        _squared = squaredSide;
        if (_dipWidth <= 0 || _dipHeight <= 0) return;

        double s = Scale;
        int w = (int)Math.Round(_dipWidth * s);
        int h = (int)Math.Round(_dipHeight * s);
        int r = (int)Math.Round(_cornerRadius * s);

        // SetWindowRgn works in window coordinates, where (0,0) is the outside
        // of the border -- but what is painted starts at the client origin. The
        // region has to be shifted there, or it exposes border on one side and
        // clips content on the other.
        var (x, y) = FramePx;

        // CreateRoundRectRgn is exclusive on right/bottom, hence the +1.
        nint region = NativeMethods.CreateRoundRectRgn(x, y, x + w + 1, y + h + 1, r * 2, r * 2);

        if (squaredSide is { } side)
        {
            nint patch = side switch
            {
                DockSide.Right => NativeMethods.CreateRectRgn(x + w - r, y, x + w + 1, y + h + 1),
                DockSide.Left => NativeMethods.CreateRectRgn(x, y, x + r, y + h + 1),
                DockSide.Bottom => NativeMethods.CreateRectRgn(x, y + h - r, x + w + 1, y + h + 1),
                _ => NativeMethods.CreateRectRgn(x, y, x + w + 1, y + r),
            };
            NativeMethods.CombineRgn(region, region, patch, NativeMethods.RGN_OR);
            NativeMethods.DeleteObject(patch);
        }

        // SetWindowRgn takes ownership of the region; it must not be deleted here.
        NativeMethods.SetWindowRgn(Handle, region, true);
    }

    /// <summary>Hands the drag to the OS move loop, so WM_MOVING/WM_EXITSIZEMOVE drive docking.</summary>
    public void BeginDrag()
    {
        NativeMethods.ReleaseCapture();
        NativeMethods.SendMessage(Handle, NativeMethods.WM_NCLBUTTONDOWN, NativeMethods.HTCAPTION, 0);
    }

    public void MoveTo(int xPx, int yPx) =>
        NativeMethods.SetWindowPos(Handle, 0, xPx, yPx, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

    public bool RegisterHotKey(int id, uint modifiers, uint virtualKey) =>
        NativeMethods.RegisterHotKey(Handle, id, modifiers | NativeMethods.MOD_NOREPEAT, virtualKey);

    public void UnregisterHotKey(int id) => NativeMethods.UnregisterHotKey(Handle, id);

    private nint SubclassProc(nint hWnd, uint msg, nint wParam, nint lParam, nuint id, nint refData)
    {
        switch (msg)
        {
            case NativeMethods.WM_ENTERSIZEMOVE:
                MoveStarted?.Invoke(this, EventArgs.Empty);
                break;

            case NativeMethods.WM_MOVING when Moving is not null:
            {
                var r = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.RECT>(lParam);
                Moving.Invoke(this, new RectInt32(r.Left, r.Top, r.Width, r.Height));
                break;
            }

            case NativeMethods.WM_MOVE:
                Moved?.Invoke(this, EventArgs.Empty);
                break;

            case NativeMethods.WM_EXITSIZEMOVE:
                MoveEnded?.Invoke(this, EventArgs.Empty);
                break;

            case NativeMethods.WM_HOTKEY:
                HotKeyPressed?.Invoke(this, (int)wParam);
                break;

            case NativeMethods.WM_DPICHANGED:
                // The region is in physical pixels, so it has to be rebuilt for the new scale.
                _window.DispatcherQueue.TryEnqueue(() => SetContentSize(_dipWidth, _dipHeight, _cornerRadius));
                break;
        }

        return NativeMethods.DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (Subclassed) NativeMethods.RemoveWindowSubclass(Handle, _subclass, _subclassId);
    }
}
