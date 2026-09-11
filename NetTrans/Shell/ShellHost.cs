using System.ComponentModel;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using NetTrans.Diagnostics;
using NetTrans.Interop;
using NetTrans.Models;
using NetTrans.Services;
using NetTrans.ViewModels;
using NetTrans.Views;
using NetTrans.Views.Controls;
using Windows.Graphics;

namespace NetTrans.Shell;

/// <summary>
/// Owns the three frames the design calls for -- the task window, the inspector
/// window and the island -- and everything that is a property of the shell
/// rather than of one frame: magnetic docking, the island following the task
/// frame, 贴边隐藏 and the 老板键.
/// </summary>
public sealed class ShellHost : IDisposable
{
    private const double FrameWidth = 536;
    private const double FrameHeight = 680;
    private const double IslandGap = 14;
    private const double IslandCollapsedWidth = 152;
    private const double IslandCollapsedHeight = 37;
    private const double IslandExpandedWidth = 300;
    private const double IslandExpandedHeight = 52;

    /// <summary>Only 36px of the frame stays on screen once it is edge-hidden.</summary>
    private const double EdgePeek = 36;

    private const int BossHotKeyId = 0xB055;

    private readonly ShellViewModel _viewModel;

    private Window _mainWindow = null!;
    private Window _inspectorWindow = null!;
    private Window _islandWindow = null!;

    private WindowChrome _mainChrome = null!;
    private WindowChrome _inspectorChrome = null!;
    private WindowChrome _islandChrome = null!;

    private MainShell _mainShell = null!;
    private InspectorShell _inspectorShell = null!;
    private IslandControl _island = null!;

    private DockManager _dock = null!;
    private DispatcherTimer? _edgeTimer;
    private bool _edgeHidden;

    public ShellHost(ShellViewModel viewModel)
    {
        _viewModel = viewModel;
    }

    public Window MainWindow => _mainWindow;

    public void Start()
    {
        // Each frame is logged as it is built: three windows, three lots of
        // Win32, and if one of them is what fails then knowing which one is
        // most of the answer.
        Startup.Log("主窗口");
        BuildMainFrame();

        Startup.Log("详情窗口");
        BuildInspectorFrame();

        Startup.Log("灵动岛");
        BuildIsland();

        _dock = new DockManager(_mainChrome, _inspectorChrome);
        _dock.DockChanged += OnDockChanged;
        _dock.Snapped += (_, _) => _inspectorShell.PlaySnapIn();
        _dock.MainMoved += (_, _) => PositionIsland();

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.Engine.Ticked += (_, _) => RefreshIsland();

        PlaceFrames();
        ApplyInspectorVisibility();
        ApplyIslandVisibility();
        OnDockChanged(this, EventArgs.Empty);

        _mainChrome.HotKeyPressed += OnHotKey;
        ApplyBossHotKey();

        _viewModel.ThemeChanged += (_, theme) => ApplyTheme(theme);
        ApplyTheme(_viewModel.Theme);

        Startup.Log("激活主窗口");
        _mainWindow.Activate();
        if (_viewModel.EdgeHide) ScheduleEdgeHide();
    }

    /// <summary>
    /// 主题, applied to every window rather than only to the one in front.
    ///
    /// A window's theme is a property of its root element, so each frame is set
    /// separately -- and the brush cache that view models read from is told as
    /// well, or half the app would keep the colours of the theme that was in
    /// force when it started.
    /// </summary>
    private void ApplyTheme(string theme)
    {
        var requested = theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        foreach (var element in new FrameworkElement?[] { _mainShell, _inspectorShell, _island })
        {
            if (element is not null) element.RequestedTheme = requested;
        }

        ThemeBrushes.SetTheme(requested switch
        {
            ElementTheme.Light => ApplicationTheme.Light,
            ElementTheme.Dark => ApplicationTheme.Dark,
            _ => Application.Current.RequestedTheme,
        });

        // Everything that caches a brush re-reads it on its next refresh, and
        // the rows are the one thing that would otherwise wait for a transfer
        // to move before repainting.
        _mainShell?.Repaint();
        _inspectorShell?.Repaint();
    }

    // ── frames ────────────────────────────────────────────────────────────
    private void BuildMainFrame()
    {
        _mainShell = new MainShell { ViewModel = _viewModel };
        _mainWindow = new Window { Title = "netX", Content = _mainShell };

        _mainChrome = new WindowChrome(_mainWindow);
        _mainChrome.MakeFrameless();
        _mainChrome.SetContentSize(FrameWidth, FrameHeight);

        _mainShell.AttachChrome(_mainChrome);
        _mainShell.CloseRequested += (_, _) => Application.Current.Exit();
        _mainShell.MinimizeRequested += (_, _) =>
        {
            if (_mainWindow.AppWindow.Presenter is OverlappedPresenter presenter) presenter.Minimize();
        };

        _mainShell.PointerEntered += (_, _) => SlideIn();
        _mainShell.PointerExited += (_, _) => ScheduleEdgeHide();
    }

    private void BuildInspectorFrame()
    {
        _inspectorShell = new InspectorShell { ViewModel = _viewModel };
        _inspectorWindow = new Window { Title = "详细信息", Content = _inspectorShell };

        _inspectorChrome = new WindowChrome(_inspectorWindow);
        _inspectorChrome.MakeFrameless();
        _inspectorChrome.MakeUtilityWindow();
        _inspectorChrome.SetContentSize(FrameWidth, FrameHeight);

        _inspectorShell.AttachChrome(_inspectorChrome);
        _inspectorShell.CloseRequested += (_, _) => _viewModel.ShowInspector = false;
        _inspectorShell.AttachToggleRequested += (_, _) =>
        {
            if (_dock.Dock is null) _dock.Attach();
            else _dock.Detach();
        };
    }

    private void BuildIsland()
    {
        _island = new IslandControl();
        _islandWindow = new Window { Title = "netX 悬浮窗", Content = _island };

        _islandChrome = new WindowChrome(_islandWindow);
        _islandChrome.MakeFrameless(resizable: false, keepShadow: false);
        _islandChrome.MakeUtilityWindow(noActivate: true, topMost: true);
        _islandChrome.SetContentSize(IslandCollapsedWidth, IslandCollapsedHeight, cornerRadius: IslandCollapsedHeight / 2);

        _island.ExpandedChanged += (_, expanded) =>
        {
            double width = expanded ? IslandExpandedWidth : IslandCollapsedWidth;
            double height = expanded ? IslandExpandedHeight : IslandCollapsedHeight;
            _islandChrome.SetContentSize(width, height, cornerRadius: expanded ? 20 : height / 2);
            PositionIsland();
            RefreshIsland();
        };

        _island.MenuRequested += (_, _) =>
        {
            _mainWindow.Activate();
            _mainShell.ShowTrayMenu();
        };
    }

    /// <summary>Centres the docked pair on the work area, leaving room for the island above it.</summary>
    private void PlaceFrames()
    {
        var area = DisplayArea.GetFromWindowId(_mainWindow.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        double scale = _mainChrome.Scale;

        int frameWidth = (int)Math.Round(FrameWidth * scale);
        int frameHeight = (int)Math.Round(FrameHeight * scale);
        int islandRoom = (int)Math.Round((IslandExpandedHeight + IslandGap) * scale);

        int x = area.X + Math.Max(14, (area.Width - frameWidth * 2) / 2);
        int y = area.Y + Math.Max(islandRoom, (area.Height - frameHeight) / 2);

        _mainChrome.MoveTo(x, y);
        _inspectorChrome.MoveTo(x + frameWidth, y);
        PositionIsland();
    }

    private void PositionIsland()
    {
        if (!_viewModel.ShowIsland) return;

        // Painted rects, not window rects: the invisible resize border is not
        // the same width on every side, so centring the window rects leaves the
        // island visibly off-centre over the frame below it.
        var main = _mainChrome.ClientBoundsPx;
        var island = _islandChrome.ClientBoundsPx;
        double scale = _mainChrome.Scale;

        int x = main.X + (main.Width - island.Width) / 2;
        int y = main.Y - island.Height - (int)Math.Round(IslandGap * scale);
        _islandChrome.MoveClientTo(x, y);
    }

    private void RefreshIsland()
    {
        if (!_viewModel.ShowIsland) return;

        _island.Update(
            _viewModel.OverallFraction,
            _viewModel.TotalSpeedValue,
            _viewModel.TotalSpeedUnit,
            _viewModel.IslandSubtitle,
            _viewModel.SpeedHistory);
    }

    // ── docking ───────────────────────────────────────────────────────────
    private void OnDockChanged(object? sender, EventArgs e)
    {
        var dock = _dock.Dock;

        // Bonded frames square off the two corners along the shared edge.
        _mainChrome.ApplyCorners(_viewModel.ShowInspector ? dock : null);
        _inspectorChrome.ApplyCorners(_viewModel.ShowInspector ? DockGeometry.Opposite(dock) : null);
        _inspectorShell.SetDocked(dock is not null);

        // The neighbour's shadow across the shared edge, which two separate
        // top-level windows do not get for free.
        _mainShell.SetBondShadow(_viewModel.ShowInspector ? dock : null);
    }

    // ── shell toggles ─────────────────────────────────────────────────────
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ShellViewModel.ShowInspector):
                ApplyInspectorVisibility();
                OnDockChanged(this, EventArgs.Empty);
                break;

            case nameof(ShellViewModel.ShowIsland):
                ApplyIslandVisibility();
                break;

            case nameof(ShellViewModel.EdgeHide):
                if (_viewModel.EdgeHide) ScheduleEdgeHide();
                else SlideIn();
                break;

            case nameof(ShellViewModel.BossMode):
                ApplyBossMode();
                break;

            case nameof(ShellViewModel.BossKey):
                ApplyBossHotKey();
                break;
        }
    }

    private void ApplyInspectorVisibility()
    {
        if (_viewModel.ShowInspector)
        {
            _inspectorWindow.AppWindow.Show(activateWindow: false);
            _dock.Reflow();
        }
        else
        {
            _inspectorWindow.AppWindow.Hide();
        }
    }

    private void ApplyIslandVisibility()
    {
        if (_viewModel.ShowIsland)
        {
            _islandWindow.AppWindow.Show(activateWindow: false);
            PositionIsland();
            RefreshIsland();
        }
        else
        {
            _islandWindow.AppWindow.Hide();
        }
    }

    /// <summary>Ctrl+Alt+H: everything disappears, downloads keep running.</summary>
    private void OnHotKey(object? sender, int id)
    {
        if (id != BossHotKeyId) return;
        _mainWindow.DispatcherQueue.TryEnqueue(() => _viewModel.BossMode = !_viewModel.BossMode);
    }

    /// <summary>
    /// Registers 老板键 as configured, replacing whatever was registered
    /// before. A combination another application already owns cannot be taken,
    /// and a setting that will not parse is no combination at all -- either way
    /// the shell says so rather than leaving a dead key the user thinks works.
    /// </summary>
    private void ApplyBossHotKey()
    {
        _mainChrome.UnregisterHotKey(BossHotKeyId);

        if (HotKeyBinding.Parse(_viewModel.BossKey) is not { } binding)
        {
            _viewModel.Say($"无法识别的快捷键：{_viewModel.BossKey}");
            return;
        }

        if (_mainChrome.RegisterHotKey(BossHotKeyId, (uint)binding.Modifiers, (uint)binding.VirtualKey)) return;

        _viewModel.Say($"{binding} 已被其他程序占用");
    }

    private void ApplyBossMode()
    {
        if (_viewModel.BossMode)
        {
            _mainWindow.AppWindow.Hide();
            _inspectorWindow.AppWindow.Hide();
            _islandWindow.AppWindow.Hide();
            return;
        }

        _mainWindow.AppWindow.Show();
        ApplyInspectorVisibility();
        ApplyIslandVisibility();
    }

    // ── 贴边隐藏 ──────────────────────────────────────────────────────────
    private void ScheduleEdgeHide()
    {
        if (!_viewModel.EdgeHide || _edgeHidden || _viewModel.BossMode) return;

        _edgeTimer?.Stop();
        _edgeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _edgeTimer.Tick += (_, _) =>
        {
            // PointerExited also fires while the pointer moves between child
            // elements, so confirm against the real cursor before sliding away.
            if (IsPointerOverShell()) return;
            _edgeTimer?.Stop();
            SlideOut();
        };
        _edgeTimer.Start();
    }

    private bool IsPointerOverShell()
    {
        if (!NativeMethods.GetCursorPos(out var cursor)) return false;

        return Contains(_mainChrome.BoundsPx, cursor)
            || (_viewModel.ShowInspector && Contains(_inspectorChrome.BoundsPx, cursor))
            || (_viewModel.ShowIsland && Contains(_islandChrome.BoundsPx, cursor));
    }

    private static bool Contains(RectInt32 rect, NativeMethods.POINT point) =>
        new FrameRect(rect.X, rect.Y, rect.Width, rect.Height).Contains(point.X, point.Y);

    private void SlideOut()
    {
        if (_edgeHidden || !_viewModel.EdgeHide) return;

        var area = DisplayArea.GetFromWindowId(_mainWindow.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var bounds = _mainChrome.BoundsPx;
        int target = area.X + area.Width - (int)Math.Round(EdgePeek * _mainChrome.Scale);

        _edgeHidden = true;
        AnimateMainTo(target, bounds.Y);
    }

    private void SlideIn()
    {
        _edgeTimer?.Stop();
        if (!_edgeHidden) return;

        var area = DisplayArea.GetFromWindowId(_mainWindow.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var bounds = _mainChrome.BoundsPx;
        int target = area.X + area.Width - bounds.Width;

        _edgeHidden = false;
        AnimateMainTo(target, bounds.Y);
    }

    /// <summary>.34s slide on the shared bezier, with the inspector and island following.</summary>
    private void AnimateMainTo(int targetX, int targetY)
    {
        var from = _mainChrome.BoundsPx;
        int dx = targetX - from.X;
        int dy = targetY - from.Y;
        if (dx == 0 && dy == 0) return;

        long started = Environment.TickCount64;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };

        timer.Tick += (_, _) =>
        {
            double t = Math.Clamp((Environment.TickCount64 - started) / 340.0, 0, 1);
            double eased = Easing.Standard(t);
            _mainChrome.MoveTo(from.X + (int)Math.Round(dx * eased), from.Y + (int)Math.Round(dy * eased));
            _dock.Reflow();
            PositionIsland();

            if (t >= 1) timer.Stop();
        };

        timer.Start();
    }

    /// <summary>
    /// 逐屏截图: drives the shell through every state the handoff specifies and
    /// writes each one to a PNG.
    ///
    /// The order is the order the files come out in, so the directory listing
    /// reads as a tour of the app. Everything reachable through the view model
    /// is driven that way; the four overlays that only a click can open get an
    /// internal hook on the frame that owns them.
    /// </summary>
    public async Task CaptureScreensAsync(string directory)
    {
        var walk = new ScreenWalk(directory);
        var model = _viewModel;

        int w = (int)FrameWidth;
        int h = (int)FrameHeight;

        // ── the task frame ────────────────────────────────────────────────
        await walk.CaptureAsync("main-list", _mainShell, w, h);

        // Before anything touches the list. Every filter and tab change below
        // rebuilds the rows, and the first attempt at this screen came after
        // one -- so it grabbed a row the repeater had already recycled, set the
        // hover on that, and captured a frame with nothing on it.
        //
        // A screen the walk cannot reach has to say so rather than vanish.
        var row = _mainShell.RealisedRows().FirstOrDefault();
        if (row is null) Startup.Log("拍 main-row-hover 跳过：一行都没找到");
        else
        {
            row.ShowSwipe();
            await walk.CaptureAsync("main-row-hover", _mainShell, w, h);
            row.HideSwipe();
        }

        model.IsListExpanded = true;
        await walk.CaptureAsync("main-list-expanded", _mainShell, w, h);
        model.IsListExpanded = false;

        model.DenseRows = true;
        await walk.CaptureAsync("main-dense", _mainShell, w, h);
        model.DenseRows = false;

        model.Tab = "done";
        await walk.CaptureAsync("main-tab-done", _mainShell, w, h);
        model.Tab = "all";

        model.Query = "没有这个东西";
        await walk.CaptureAsync("main-filtered-empty", _mainShell, w, h);
        model.Query = "";

        _mainShell.ShowAddMenu();
        await walk.CaptureAsync("main-menu-add", _mainShell, w, h);
        _mainShell.ClosePopover();

        _mainShell.ShowSortMenu();
        await walk.CaptureAsync("main-menu-sort", _mainShell, w, h);
        _mainShell.ClosePopover();

        if (model.VisibleTasks.FirstOrDefault() is { } task)
        {
            _mainShell.ShowRowMenu(task);
            await walk.CaptureAsync("main-menu-row", _mainShell, w, h);
            _mainShell.ClosePopover();

            // 贴着底边再来一张。下载中的行有 11 项，菜单比半扇窗还高 ——
            // 从窗底往上 60px 的地方点开，夹位置要是没做对，菜单就会有
            // 一截落在窗外。默认那个 (150,150) 永远放得下，拍多少张都
            // 照不出这个毛病。
            _mainShell.ShowRowMenu(task, new Windows.Foundation.Point(150, h - 60));
            await walk.CaptureAsync("main-menu-row-low", _mainShell, w, h);
            _mainShell.ClosePopover();
        }

        _mainShell.ShowDropTarget(true);
        await walk.CaptureAsync("main-drop", _mainShell, w, h);
        _mainShell.ShowDropTarget(false);

        model.Toast = "链接已复制";
        await walk.CaptureAsync("main-toast", _mainShell, w, h);
        model.Toast = null;

        // ── the sheets ────────────────────────────────────────────────────
        foreach (var sheet in new[] { "add", "batch", "torrent", "sniff", "prefs", "tidy", "bigfiles" })
        {
            model.ActiveSheet = sheet;
            await walk.CaptureAsync("sheet-" + sheet, _mainShell, w, h);

            // 种子内容 is the half of the torrent sheet the handoff draws, and
            // it only exists once a torrent is loaded -- so the walk was
            // photographing the other half and the file list kept going
            // unlooked-at.
            if (sheet == "torrent" && _mainShell.OpenSheet is Views.Sheets.TorrentSheet torrent)
            {
                torrent.LoadForScreenshot(SampleTorrent.Write(Path.Combine(directory, "fixtures")));
                await walk.CaptureAsync("sheet-torrent-contents", _mainShell, w, h);
            }
        }

        model.ActiveSheet = null;

        // ── the inspector ─────────────────────────────────────────────────
        foreach (var (tab, name) in new[] { ("info", "overview"), ("blocks", "blocks"), ("conn", "connections"), ("log", "log") })
        {
            _inspectorShell.ShowTab(tab);
            await walk.CaptureAsync("inspector-" + name, _inspectorShell, w, h);
        }

        _inspectorShell.ShowTab("info");

        // ── the island ────────────────────────────────────────────────────
        await walk.CaptureAsync("island-collapsed", _island,
            (int)IslandCollapsedWidth, (int)IslandCollapsedHeight);

        _island.SetExpanded(true);
        await walk.CaptureAsync("island-expanded", _island,
            (int)IslandExpandedWidth, (int)IslandExpandedHeight);
        _island.SetExpanded(false);

        Startup.Log("逐屏截图结束");
    }

    public void Dispose()
    {
        _edgeTimer?.Stop();
        _mainChrome.UnregisterHotKey(BossHotKeyId);
        _dock.Dispose();
        _islandChrome.Dispose();
        _inspectorChrome.Dispose();
        _mainChrome.Dispose();
    }
}
