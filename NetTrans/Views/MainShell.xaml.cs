using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NetTrans.Interop;
using NetTrans.Models;
using NetTrans.Services;
using NetTrans.ViewModels;
using NetTrans.Views.Controls;
using NetTrans.Views.Sheets;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.UI;

namespace NetTrans.Views;

/// <summary>
/// The task frame. Owns the nav bar, the segmented control, the folded list,
/// the seven-icon toolbar and every in-frame overlay (sheets, popovers, toast,
/// completion banner, drop target).
/// </summary>
public sealed partial class MainShell : UserControl
{
    private readonly List<SegmentItem> _tabs = new()
    {
        new SegmentItem("all", "全部"),
        new SegmentItem("active", "进行中 0"),
        new SegmentItem("done", "已完成 0"),
    };

    private ShellViewModel? _viewModel;
    private WindowChrome? _chrome;
    /// <summary>
    /// .ctx is one class at one width, and the handoff draws all three menus
    /// with it. The 222 / 226 / 244 that used to be here were guesses; the
    /// README's "244px 宽" is prose, the CSS is the design.
    /// </summary>
    private const double PopoverWidth = 232;

    private PopoverControl? _popover;
    private FrameworkElement? _sheet;

    /// <summary>Raised when the red traffic light is clicked.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Raised when the yellow traffic light is clicked.</summary>
    public event EventHandler? MinimizeRequested;

    public ShellViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (ReferenceEquals(_viewModel, value)) return;
            if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

            _viewModel = value;

            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
                Tabs.SelectedId = _viewModel.Tab;
            }

            Bindings.Update();
            SyncTabs();
            SyncToggleAllGlyph();
        }
    }

    public MainShell()
    {
        InitializeComponent();
        Tabs.Items = _tabs;
        Toast.Opacity = 0;
    }

    /// <summary>Handed the frame's chrome so the nav bar can start an OS drag.</summary>
    public void AttachChrome(WindowChrome chrome) => _chrome = chrome;

    // ── nav bar ───────────────────────────────────────────────────────────
    private void OnNavPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // `.frame .nav { cursor: grab }` -- but not over the buttons sitting in it.
        if (IsWithinInteractive(e.OriginalSource as DependencyObject)) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        _chrome?.BeginDrag();
        e.Handled = true;
    }

    private static bool IsWithinInteractive(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase or TextBox) return true;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void OnCloseDotPressed(object sender, PointerRoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void OnMinimizeDotPressed(object sender, PointerRoutedEventArgs e)
    {
        MinimizeRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    /// <summary>
    /// The frame is a fixed 536x680, so there is nothing to zoom to. The green
    /// dot opens and closes the inspector frame instead -- the only thing that
    /// changes the shell's footprint.
    /// </summary>
    private void OnZoomDotPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel is not null) ViewModel.ShowInspector = !ViewModel.ShowInspector;
        e.Handled = true;
    }

    private void OnSearchClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.ToggleSearchCommand.Execute(null);
        if (ViewModel?.IsSearchOpen == true) DispatcherQueue.TryEnqueue(() => NavSearch.Focus(FocusState.Programmatic));
    }

    private void OnSearchLostFocus(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { Query.Length: 0, IsSearchOpen: true }) ViewModel.IsSearchOpen = false;
    }

    private void OnTabChanged(object? sender, string tab)
    {
        if (ViewModel is not null) ViewModel.Tab = tab;
    }

    // ── list ──────────────────────────────────────────────────────────────
    private void OnRowPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not TaskRow row) return;

        row.IsDense = ViewModel?.DenseRows == true;
        row.ShowSeparator = args.Index > 0;

        // Repeater elements are recycled, so subscribe once per instance.
        if (row.Tag is not null) return;
        row.Tag = "wired";

        row.RowInvoked += (_, e) => ViewModel?.Select(e.Item.Id, e.Additive);
        row.ToggleRequested += (_, item) => ViewModel?.ToggleTaskCommand.Execute(item);
        row.RemoveRequested += (_, item) => ViewModel?.RemoveTaskCommand.Execute(item);
        row.RowContextRequested += (_, request) => ShowContextMenu(request);
    }

    /// <summary>Re-reads the theme brushes every row caches.</summary>
    public void Repaint()
    {
        int count = Rows.ItemsSourceView?.Count ?? 0;

        for (int i = 0; i < count; i++)
        {
            if (Rows.TryGetElement(i) is TaskRow row) row.Repaint();
        }
    }

    private void RefreshRowDensity()
    {
        int count = Rows.ItemsSourceView?.Count ?? 0;
        for (int i = 0; i < count; i++)
        {
            if (Rows.TryGetElement(i) is TaskRow row)
            {
                row.IsDense = ViewModel?.DenseRows == true;
                row.ShowSeparator = i > 0;
            }
        }
    }

    // ── menus ─────────────────────────────────────────────────────────────
    /// <summary>
    /// The overlays the screenshot walker cannot reach through the view model,
    /// because they are opened by a click rather than by state.
    /// </summary>
    internal void ShowAddMenu() => OnAddMenuClick(this, new RoutedEventArgs());

    internal void ShowSortMenu() => ShowViewMenu("view");

    internal void ShowRowMenu(DownloadItemViewModel task) =>
        ShowContextMenu(new RowContextRequest(task, new Point(150, 150)));

    internal void ShowDropTarget(bool on) =>
        DropOverlay.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Closes whatever popover is open.
    ///
    /// A popover is dismissed by a click, so the screenshot walker had no way
    /// to close one: it opened the row menu, moved on, and every frame after
    /// that had a stale menu sitting on top of it. Evidence that carries the
    /// last screen's leftovers is worse than no evidence, because it looks like
    /// evidence.
    /// </summary>
    internal void ClosePopover() => DismissPopover();

    /// <summary>How far the neighbour's shadow reaches in, measured off the handoff's render.</summary>
    private const double BondShadowReach = 46;

    /// <summary>
    /// 缝上那一根线：设计稿里是详情窗的 <c>border-left: .5px rgba(0,0,0,.16)</c>。
    /// 242 的组底色被它压到 203。
    /// </summary>
    private const byte BondEdgeAlpha = 0x29;

    /// <summary>
    /// 柔和投影自己最深处的黑度。把设计稿里底色干净的 40 行平均出来，
    /// 缝左 1px 相对底色只暗 3.87%，10/255 = 3.92% 是最近的一档。
    /// 先前把整条 46px 都拉到 16%，暗了四倍，才有那片死板的暗带。
    /// </summary>
    private const byte BondShadowAlpha = 0x0A;

    /// <summary>
    /// 投影的形状，量自设计稿：一对对的 (位置, 占最深处的几成)，
    /// 位置 0 是够不到的外沿，1 是缝。中间是高斯尾巴，不是直线 ——
    /// 上一版拿两段直线拼，接缝处斜率差了 2.3 倍，肉眼就是一道棱。
    /// </summary>
    private static readonly (double At, double Of)[] BondShadowRamp =
    {
        (0.00, 0.00), (0.13, 0.08), (0.24, 0.14), (0.35, 0.20),
        (0.46, 0.29), (0.57, 0.41), (0.67, 0.53), (0.78, 0.69),
        (0.89, 0.86), (1.00, 1.00),
    };

    /// <summary>
    /// Draws the bonded neighbour's shadow across the shared edge.
    ///
    /// Null when nothing is attached. Only the frame underneath carries it —
    /// in the handoff the inspector's own edge stays flat.
    ///
    /// 两层：一片 46px 的柔和渐变，加缝上一根 1px 的硬线。设计稿里这两者
    /// 本就是两个东西（详情窗的 box-shadow 和它的 border-left），揉成一条
    /// 渐变就会把八成的黑度摊到整片上去。
    /// </summary>
    internal void SetBondShadow(DockSide? side)
    {
        if (side is not { } edge)
        {
            BondShadow.Visibility = Visibility.Collapsed;
            BondEdge.Visibility = Visibility.Collapsed;
            return;
        }

        var (from, to) = edge switch
        {
            DockSide.Right => (new Point(0, 0.5), new Point(1, 0.5)),
            DockSide.Left => (new Point(1, 0.5), new Point(0, 0.5)),
            DockSide.Bottom => (new Point(0.5, 0), new Point(0.5, 1)),
            _ => (new Point(0.5, 1), new Point(0.5, 0)),
        };

        bool horizontal = edge is DockSide.Right or DockSide.Left;

        var across = edge switch
        {
            DockSide.Right => HorizontalAlignment.Right,
            DockSide.Left => HorizontalAlignment.Left,
            _ => HorizontalAlignment.Stretch,
        };

        var down = edge switch
        {
            DockSide.Bottom => VerticalAlignment.Bottom,
            DockSide.Top => VerticalAlignment.Top,
            _ => VerticalAlignment.Stretch,
        };

        // 让开缝上那一格。设计稿里柔和投影是详情窗洒过来的、硬线是详情窗
        // 自己的 border-left，两者不叠；这边要是让渐变一路铺到最后一列，
        // 两层黑就乘在一起：1−(1−.039)(1−.161)=.194，242 被压到 195 而不是
        // 203。第一版就是这么差了 8 个灰阶。
        BondShadow.Width = horizontal ? BondShadowReach - 1 : double.NaN;
        BondShadow.Height = horizontal ? double.NaN : BondShadowReach - 1;
        BondShadow.HorizontalAlignment = across;
        BondShadow.VerticalAlignment = down;
        BondShadow.Margin = edge switch
        {
            DockSide.Right => new Thickness(0, 0, 1, 0),
            DockSide.Left => new Thickness(1, 0, 0, 0),
            DockSide.Bottom => new Thickness(0, 0, 0, 1),
            _ => new Thickness(0, 1, 0, 0),
        };

        var stops = new GradientStopCollection();
        foreach (var (at, of) in BondShadowRamp)
        {
            stops.Add(new GradientStop
            {
                Offset = at,
                Color = Color.FromArgb((byte)Math.Round(BondShadowAlpha * of), 0, 0, 0),
            });
        }

        BondShadow.Fill = new LinearGradientBrush
        {
            StartPoint = from,
            EndPoint = to,
            GradientStops = stops,
        };

        BondEdge.Width = horizontal ? 1 : double.NaN;
        BondEdge.Height = horizontal ? double.NaN : 1;
        BondEdge.HorizontalAlignment = across;
        BondEdge.VerticalAlignment = down;

        BondShadow.Visibility = Visibility.Visible;
        BondEdge.Visibility = Visibility.Visible;
    }

    /// <summary>The sheet currently open, so the walk can drive it into its other states.</summary>
    internal FrameworkElement? OpenSheet => _sheet;

    /// <summary>
    /// Every task row on screen, so the walker can open one's hover actions.
    ///
    /// Walks the tree rather than asking the repeater: TryGetElement over
    /// ItemsSourceView returned nothing, and the walk silently skipped the
    /// hover screen instead of failing, which is the same way a check stops
    /// checking without anyone noticing.
    /// </summary>
    internal IEnumerable<TaskRow> RealisedRows() => Descendants<TaskRow>(Rows);

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);

        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is T match) yield return match;
            else
            {
                foreach (var deeper in Descendants<T>(child)) yield return deeper;
            }
        }
    }

    private void OnAddMenuClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;

        ShowPopover(new[]
        {
            new PopoverItem("新建下载…", Glyph("IconPlus"), Invoke: () => ViewModel.ActiveSheet = "add"),
            new PopoverItem("批量下载…", Glyph("IconLayers"), Invoke: () => ViewModel.ActiveSheet = "batch"),
            new PopoverItem("打开种子 / 磁力链…", Glyph("IconMagnet"), Invoke: () => ViewModel.ActiveSheet = "torrent"),
            new PopoverItem("视频嗅探…", Glyph("IconFilm"), Invoke: () => ViewModel.ActiveSheet = "sniff"),

            // Tools rather than downloads, but this is the menu people open.
            new PopoverItem("深度整理…", Glyph("IconFolder"), SeparatorBefore: true,
                Invoke: () => ViewModel.ActiveSheet = "tidy"),
            new PopoverItem("大文件核对…", Glyph("IconCheck"),
                Invoke: () => ViewModel.ActiveSheet = "bigfiles"),
        }, new Point(536 - 232 - 12, 40), width: PopoverWidth);
    }

    private void OnViewMenuClick(object sender, RoutedEventArgs e) => ShowViewMenu(page: "view");

    private void ShowViewMenu(string page)
    {
        if (ViewModel is null) return;

        var items = new List<PopoverItem>();

        if (page == "view")
        {
            items.Add(new PopoverItem("大图", Glyph("IconRows"), IsChecked: !ViewModel.DenseRows,
                Invoke: () => ViewModel.DenseRows = false));
            items.Add(new PopoverItem("小图", Glyph("IconGrid"), IsChecked: ViewModel.DenseRows,
                Invoke: () => ViewModel.DenseRows = true));

            bool first = true;
            foreach (var (key, label) in new[]
                     {
                         ("added", "加入时间"), ("name", "名称"), ("size", "大小"),
                         ("progress", "进度"), ("speed", "速度"),
                     })
            {
                bool active = ViewModel.SortKey == key;
                var arrow = active
                    ? Glyph(ViewModel.SortDirection == "asc" ? "IconArrowUp" : "IconArrowDown")
                    : null;

                items.Add(new PopoverItem(label, arrow, SeparatorBefore: first,
                    Invoke: () => { ViewModel.SetSortCommand.Execute(key); ShowViewMenu("view"); },
                    KeepOpen: true));
                first = false;
            }

            bool firstTheme = true;
            foreach (var (key, label) in new[] { ("auto", "跟随系统"), ("light", "浅色"), ("dark", "深色") })
            {
                items.Add(new PopoverItem(
                    label,
                    IsChecked: ViewModel.Theme == key,
                    SeparatorBefore: firstTheme,
                    Invoke: () => { ViewModel.SetThemeCommand.Execute(key); ShowViewMenu("view"); },
                    KeepOpen: true));

                firstTheme = false;
            }

            items.Add(new PopoverItem("分类筛选", Glyph("IconChevron"), SeparatorBefore: true,
                Invoke: () => ShowViewMenu("cat"), KeepOpen: true));
        }
        else
        {
            items.Add(new PopoverItem("‹ 返回", Invoke: () => ShowViewMenu("view"), KeepOpen: true));

            bool first = true;
            foreach (var (id, label) in ShellViewModel.Categories)
            {
                items.Add(new PopoverItem(label, IsChecked: ViewModel.Category == id, SeparatorBefore: first,
                    Invoke: () => ViewModel.Category = id));
                first = false;
            }
        }

        ShowPopover(items, new Point(536 - 232 - 16, 46), width: PopoverWidth);
    }

    /// <summary>
    /// The handoff's tray menu. The prototype hangs it off a desktop tray pill;
    /// here the island owns it, since that is the always-visible element, and
    /// the menu is drawn inside the task frame so it has room.
    /// </summary>
    public void ShowTrayMenu()
    {
        if (ViewModel is null) return;

        ShowPopover(new[]
        {
            new PopoverItem("显示主窗口", Glyph("IconWindow"), Invoke: () =>
            {
                ViewModel.EdgeHide = false;
                ViewModel.ShowIsland = true;
            }),
            new PopoverItem(ViewModel.EdgeHide ? "取消贴边" : "贴边隐藏", Glyph("IconPin"),
                Invoke: () => ViewModel.ToggleEdgeHideCommand.Execute(null)),
            new PopoverItem("隐藏悬浮窗", Glyph("IconEye"),
                Invoke: () => ViewModel.ToggleIslandCommand.Execute(null)),
            new PopoverItem(ViewModel.IsRunning ? "全部暂停" : "全部开始", SeparatorBefore: true,
                Invoke: () => ViewModel.ToggleAllCommand.Execute(null)),
            new PopoverItem("速度限制…", Invoke: () => ViewModel.ActiveSheet = "prefs"),
            new PopoverItem("老板键：全部隐藏", Glyph("IconPower"), SeparatorBefore: true,
                Invoke: () => ViewModel.ToggleBossModeCommand.Execute(null)),
            new PopoverItem("退出", IsDestructive: true, SeparatorBefore: true,
                Invoke: () => CloseRequested?.Invoke(this, EventArgs.Empty)),
        }, new Point(14, 52), width: PopoverWidth);
    }

    private void ShowContextMenu(RowContextRequest request)
    {
        if (ViewModel is null) return;

        var item = request.Item;
        var items = new List<PopoverItem>();

        if (item.IsDone)
        {
            items.Add(new PopoverItem("打开文件", Glyph("IconOpen"),
                Invoke: () => ViewModel.OpenFileCommand.Execute(item)));
        }

        items.Add(new PopoverItem("在文件夹中显示", Glyph("IconFolder"),
            Invoke: () => ViewModel.RevealFileCommand.Execute(item)));

        if (!item.IsDone)
        {
            items.Add(new PopoverItem(item.ToggleLabel, item.ToggleGlyph, SeparatorBefore: true,
                Invoke: () => ViewModel.ToggleTaskCommand.Execute(item)));
        }

        items.Add(new PopoverItem("重新下载", Glyph("IconRedo"), SeparatorBefore: item.IsDone,
            Invoke: () => ViewModel.RedownloadCommand.Execute(item)));
        items.Add(new PopoverItem("详细信息", Glyph("IconInfo"),
            Invoke: () => ViewModel.ShowInspector = true));

        if (!item.IsDone)
        {
            items.Add(new PopoverItem("移到队首", Glyph("IconUp"), SeparatorBefore: true,
                Invoke: () => ViewModel.MoveToFrontCommand.Execute(item)));
            items.Add(new PopoverItem("移到队尾", Glyph("IconDown"),
                Invoke: () => ViewModel.MoveToBackCommand.Execute(item)));
        }

        items.Add(new PopoverItem("校验 SHA-256", Glyph("IconShield"), SeparatorBefore: item.IsDone,
            Invoke: () => ViewModel.VerifyCommand.Execute(item)));
        items.Add(new PopoverItem("检查更新", Glyph("IconRedo"),
            Invoke: () => ViewModel.CheckUpdateCommand.Execute(item)));
        items.Add(new PopoverItem("拷贝链接", Glyph("IconCopy"), SeparatorBefore: true, Invoke: () => CopyLink(item)));
        items.Add(new PopoverItem("重命名", Glyph("IconRename"), Invoke: () =>
        {
            ViewModel.RenameTarget = item;
            ViewModel.ActiveSheet = "rename";
        }));
        items.Add(new PopoverItem("删除", Glyph("IconTrash"), IsDestructive: true, SeparatorBefore: true,
            Invoke: () => ViewModel.RemoveTaskCommand.Execute(item)));

        // 不在这里夹位置：菜单有多高取决于这一行是什么状态（下载中多出
        // 暂停 / 移到队首 / 移到队尾，已完成多出打开文件），量过才知道。
        // PopoverControl 会在自己量完之后把整张卡收进窗口。
        ShowPopover(items, request.Position, width: PopoverWidth);
    }

    private void CopyLink(DownloadItemViewModel item)
    {
        var package = new DataPackage();
        package.SetText(item.Url);
        Clipboard.SetContent(package);
        ViewModel?.Say("已拷贝链接");
    }

    private void ShowPopover(IEnumerable<PopoverItem> items, Point position, double width)
    {
        DismissPopover();

        _popover = new PopoverControl();
        _popover.Dismissed += (_, _) => DismissPopover();

        OverlayHost.IsHitTestVisible = true;
        OverlayHost.Children.Add(_popover);
        _popover.Show(items, position, width);
    }

    private void DismissPopover()
    {
        if (_popover is null) return;
        OverlayHost.Children.Remove(_popover);
        _popover = null;
        OverlayHost.IsHitTestVisible = _sheet is not null;
    }

    // ── sheets ────────────────────────────────────────────────────────────
    private void ShowSheet(string? name)
    {
        if (_sheet is not null)
        {
            OverlayHost.Children.Remove(_sheet);
            _sheet = null;
        }

        if (ViewModel is null || name is null)
        {
            OverlayHost.IsHitTestVisible = _popover is not null;
            return;
        }

        // 重命名 needs a target; without one there is nothing to show.
        if (name == "rename" && ViewModel.RenameTarget is null)
        {
            ViewModel.ActiveSheet = null;
            return;
        }

        FrameworkElement sheet = name switch
        {
            "add" => new AddSheet(ViewModel),
            "batch" => new BatchSheet(ViewModel),
            "torrent" => new TorrentSheet(ViewModel),
            "sniff" => new SniffSheet(ViewModel),
            "rename" => new RenameSheet(ViewModel, ViewModel.RenameTarget!),
            "tidy" => new TidySheet(ViewModel),
            "bigfiles" => new BigFileSheet(ViewModel),
            _ => new SettingsSheet(ViewModel),
        };

        _sheet = sheet;
        OverlayHost.IsHitTestVisible = true;
        OverlayHost.Children.Add(sheet);
    }

    // ── drag and drop ─────────────────────────────────────────────────────
    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.IsCaptionVisible = false;
        DropOverlay.Visibility = Visibility.Visible;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => DropOverlay.Visibility = Visibility.Collapsed;

    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (ViewModel is null) return;

        string? url = null;
        if (e.DataView.Contains(StandardDataFormats.WebLink)) url = (await e.DataView.GetWebLinkAsync()).ToString();
        else if (e.DataView.Contains(StandardDataFormats.Text)) url = await e.DataView.GetTextAsync();

        if (string.IsNullOrWhiteSpace(url))
        {
            ViewModel.Say("拖入的内容不是链接");
            return;
        }

        ViewModel.PendingUrl = url.Trim();
        ViewModel.ActiveSheet = "add";
    }

    // ── view-model reactions ──────────────────────────────────────────────
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ShellViewModel.Tab):
                Tabs.SelectedId = ViewModel?.Tab;
                break;

            case nameof(ShellViewModel.TabActiveLabel):
            case nameof(ShellViewModel.TabDoneLabel):
                SyncTabs();
                break;

            case nameof(ShellViewModel.IsRunning):
                SyncToggleAllGlyph();
                break;

            case nameof(ShellViewModel.DenseRows):
                RefreshRowDensity();
                break;

            case nameof(ShellViewModel.IsListExpanded):
                Animations.Slide(FoldGlyphRotation, "Angle", ViewModel?.IsListExpanded == true ? 180 : 0, 200).Begin();
                break;

            case nameof(ShellViewModel.Toast):
                SyncToast();
                break;

            case nameof(ShellViewModel.Banner):
                SyncBanner();
                break;

            case nameof(ShellViewModel.PendingActionLabel):
            case nameof(ShellViewModel.PendingActionSeconds):
                SyncCountdown();
                break;

            case nameof(ShellViewModel.ActiveSheet):
                ShowSheet(ViewModel?.ActiveSheet);
                break;
        }
    }

    private void SyncTabs()
    {
        if (ViewModel is null) return;
        _tabs[1].Label = ViewModel.TabActiveLabel;
        _tabs[2].Label = ViewModel.TabDoneLabel;
    }

    private void SyncToggleAllGlyph() =>
        ToggleAllGlyph.Data = Glyph(ViewModel?.IsRunning == true ? "IconPauseFill" : "IconPlayFill");

    private void SyncToast()
    {
        string? message = ViewModel?.Toast;

        if (message is null)
        {
            var fadeOut = Animations.Fade(Toast, 0, 200);
            fadeOut.Completed += (_, _) =>
            {
                if (ViewModel?.Toast is null) Toast.Visibility = Visibility.Collapsed;
            };
            fadeOut.Begin();
            return;
        }

        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        ToastScale.ScaleX = 0.94;
        ToastScale.ScaleY = 0.94;
        Animations.Slide(ToastScale, "ScaleX", 1, 200).Begin();
        Animations.Slide(ToastScale, "ScaleY", 1, 200).Begin();
        Animations.Fade(Toast, 1, 200).Begin();
    }

    private void SyncBanner()
    {
        var task = ViewModel?.Banner;

        if (task is null)
        {
            Banner.Visibility = Visibility.Collapsed;
            return;
        }

        BannerSubtitle.Text = $"{task.Name} · {FormatHelpers.Bytes(task.Size)}";
        Banner.Visibility = Visibility.Visible;

        // `drop`: slides down 16px while fading in.
        BannerOffset.Y = -16;
        Banner.Opacity = 0;
        Animations.Slide(BannerOffset, "Y", 0, 340).Begin();
        Animations.Fade(Banner, 1, 340).Begin();
    }

    /// <summary>The 全部完成后 countdown: what is about to happen, and 取消.</summary>
    private void SyncCountdown()
    {
        if (ViewModel?.PendingActionLabel is not { } label)
        {
            Countdown.Visibility = Visibility.Collapsed;
            return;
        }

        bool wasHidden = Countdown.Visibility == Visibility.Collapsed;

        CountdownTitle.Text = $"下载已全部完成，即将{label}";
        CountdownSubtitle.Text = $"{ViewModel.PendingActionSeconds} 秒后执行 · 点“取消”停止";
        Countdown.Visibility = Visibility.Visible;

        // Only on the way in: re-fading every second would make the bar blink
        // once a tick while the user is trying to read it.
        if (!wasHidden) return;

        Countdown.Opacity = 0;
        Animations.Fade(Countdown, 1, 340).Begin();
    }

    private void OnCountdownCancelClick(object sender, RoutedEventArgs e) =>
        ViewModel?.CancelPendingActionCommand.Execute(null);

    private void OnBannerOpenClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;

        // The same command the row's 打开文件 uses, so a file that has been
        // moved reports that rather than the banner claiming it opened.
        if (ViewModel.Banner is { } task) ViewModel.OpenFileCommand.Execute(task);

        ViewModel.Banner = null;
    }

    private static string Glyph(string key) => IconResources.Data(key);
}
