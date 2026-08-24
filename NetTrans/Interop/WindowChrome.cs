using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace NetTrans.Interop;

/// <summary>Which edge of the main window a frame is bonded to (the handoff's `dock`).</summary>
public enum DockSide
{
    Right,
    Left,
    Bottom,
    Top,
}

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

    public WindowChrome(Window window)
    {
        _window = window;
        Handle = WinRT.Interop.WindowNative.GetWindowHandle(window);

        _subclassId = _nextSubclassId++;
        _subclass = SubclassProc;
        NativeMethods.SetWindowSubclass(Handle, _subclass, _subclassId, 0);
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
        _window.AppWindow.Resize(new SizeInt32((int)Math.Round(dipWidth * s), (int)Math.Round(dipHeight * s)));
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

        // CreateRoundRectRgn is exclusive on right/bottom, hence the +1.
        nint region = NativeMethods.CreateRoundRectRgn(0, 0, w + 1, h + 1, r * 2, r * 2);

        if (squaredSide is { } side)
        {
            nint patch = side switch
            {
                DockSide.Right => NativeMethods.CreateRectRgn(w - r, 0, w + 1, h + 1),
                DockSide.Left => NativeMethods.CreateRectRgn(0, 0, r, h + 1),
                DockSide.Bottom => NativeMethods.CreateRectRgn(0, h - r, w + 1, h + 1),
                _ => NativeMethods.CreateRectRgn(0, 0, w + 1, r),
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
        NativeMethods.RemoveWindowSubclass(Handle, _subclass, _subclassId);
    }
}
