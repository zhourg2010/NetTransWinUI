using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using NetTrans.Models;
using NetTrans.Services;

namespace NetTrans.ViewModels;

/// <summary>
/// The state block the handoff spells out under "State Management": tasks, sel,
/// tab, cat, q, sortKey/sortDir, open5, detail, dense, islandOn/edge/boss and
/// the transient sheet / toast / banner / drop layers. The two frames and the
/// island all bind to this one instance.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IClipboardWatcher _clipboardWatcher;
    private readonly ISettingsStore _settingsStore;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromMilliseconds(1700) };
    private readonly DispatcherTimer _bannerTimer = new() { Interval = TimeSpan.FromMilliseconds(3600) };

    public IDownloadEngine Engine { get; }
    public AppSettings Settings { get; }
    public ISettingsStore SettingsStore => _settingsStore;

    /// <summary>The task list after tab / category / search / sort, then folded to five rows.</summary>
    public ObservableCollection<DownloadItemViewModel> VisibleTasks { get; } = new();

    /// <summary>Selected task ids; the last one drives the inspector, matching the handoff's `sel`.</summary>
    public ObservableCollection<int> SelectedIds { get; } = new();

    public ShellViewModel(IDownloadEngine engine, IClipboardWatcher clipboardWatcher, ISettingsStore settingsStore)
    {
        Engine = engine;
        _clipboardWatcher = clipboardWatcher;
        _settingsStore = settingsStore;
        Settings = settingsStore.Load();

        _denseRows = Settings.DenseRows;
        _sortKey = Settings.SortKey;
        _sortDirection = Settings.SortDirection;
        _showInspector = Settings.ShowInspector;
        _showIsland = Settings.ShowIsland;
        _edgeHide = Settings.EdgeHide;

        Engine.Tasks.CollectionChanged += OnTasksChanged;
        Engine.Ticked += OnEngineTicked;
        Engine.Completed += OnTaskCompleted;

        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast = null; };
        _bannerTimer.Tick += (_, _) => { _bannerTimer.Stop(); Banner = null; };

        if (Engine.Tasks.FirstOrDefault() is { } first) SelectedIds.Add(first.Id);
        Rebuild();

        _clipboardWatcher.UrlDetected += OnClipboardUrlDetected;
        if (Settings.WatchClipboard) _clipboardWatcher.Start();
    }

    // ── list state ────────────────────────────────────────────────────────
    [ObservableProperty] private string _tab = "all";           // all | active | done
    [ObservableProperty] private string _category = "all";
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _isSearchOpen;
    [ObservableProperty] private string _sortKey;               // added | name | size | progress | speed
    [ObservableProperty] private string _sortDirection;         // asc | desc
    [ObservableProperty] private bool _denseRows;
    [ObservableProperty] private bool _isListExpanded;

    [ObservableProperty] private int _hiddenCount;
    [ObservableProperty] private bool _canFold;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private DownloadItemViewModel? _current;

    // ── shell layers ──────────────────────────────────────────────────────
    [ObservableProperty] private bool _showInspector;
    [ObservableProperty] private bool _showIsland;
    [ObservableProperty] private bool _edgeHide;
    [ObservableProperty] private bool _bossMode;
    [ObservableProperty] private string? _activeSheet;          // add | batch | torrent | sniff | prefs
    [ObservableProperty] private string? _toast;
    [ObservableProperty] private DownloadItemViewModel? _banner;
    [ObservableProperty] private bool _isDropTarget;
    [ObservableProperty] private string _pendingUrl = "https://";

    // ── derived header readouts ───────────────────────────────────────────
    public int ActiveCount => TaskQuery.ActiveCount(Engine.Tasks, t => t.Model, Category);
    public int DoneCount => TaskQuery.DoneCount(Engine.Tasks, t => t.Model, Category);
    public int SelectionCount => SelectedIds.Count;
    public bool IsRunning => Engine.IsRunning;
    public bool HasCategoryFilter => Category != "all";
    public string CategoryLabel => CategoryName(Category);

    /// <summary>"· 3 项 · 1.9 MB/s · 已选 2" -- the `em` half of the nav title.</summary>
    public string TitleDetail
    {
        get
        {
            var (value, unit) = FormatHelpers.SpeedParts(Engine.TotalSpeed);
            string detail = $"· {ActiveCount} 项 · {value} {unit}";
            return SelectionCount > 1 ? $"{detail} · 已选 {SelectionCount}" : detail;
        }
    }

    public string TabAllLabel => "全部";
    public string TabActiveLabel => $"进行中 {ActiveCount}";
    public string TabDoneLabel => $"已完成 {DoneCount}";
    public string FoldLabel => IsListExpanded ? "收起" : $"展开更多 {HiddenCount} 项";
    public string ToggleAllTooltip => IsRunning ? "全部暂停" : "全部开始";
    public string RemoveTooltip => SelectionCount > 1 ? $"删除 {SelectionCount} 项" : "删除";
    public string InspectorTooltip => ShowInspector ? "关闭详情窗口" : "打开详情窗口";
    public bool CanRemove => SelectionCount > 0;
    public bool CanOpenFolder => Current is not null;

    // ── island ────────────────────────────────────────────────────────────
    public IReadOnlyList<double> SpeedHistory => Engine.SpeedHistory;
    public string TotalSpeedValue => FormatHelpers.SpeedParts(Engine.TotalSpeed).Value;
    public string TotalSpeedUnit => FormatHelpers.SpeedParts(Engine.TotalSpeed).Unit;
    public string IslandSubtitle
    {
        get
        {
            string up = FormatHelpers.Speed(Engine.UploadSpeed);
            if (up.Length == 0) up = "0 KB/s";
            return $"↑ {up} · 平均 {FormatHelpers.SpeedOrDash(Average())}";
        }
    }

    /// <summary>Aggregate completion across every unfinished task, for the island ring.</summary>
    public double OverallFraction
    {
        get
        {
            long size = Engine.Tasks.Sum(t => t.Size);
            long done = Engine.Tasks.Sum(t => t.Done);
            return size <= 0 ? 0 : Math.Clamp(done / (double)size, 0, 1);
        }
    }

    // ── commands ──────────────────────────────────────────────────────────
    [RelayCommand]
    private void SetTab(string tab) => Tab = tab;

    [RelayCommand]
    private void SetCategory(string category) => Category = category;

    [RelayCommand]
    private void ClearCategory() => Category = "all";

    [RelayCommand]
    private void ToggleSearch()
    {
        IsSearchOpen = !IsSearchOpen;
        if (!IsSearchOpen) Query = "";
    }

    [RelayCommand]
    private void ToggleFold() => IsListExpanded = !IsListExpanded;

    [RelayCommand]
    private void SetSort(string key)
    {
        if (SortKey == key) SortDirection = SortDirection == "asc" ? "desc" : "asc";
        else SortKey = key;
    }

    [RelayCommand]
    private void SetDense(bool dense) => DenseRows = dense;

    /// <summary>Click selects; Ctrl-click extends, exactly like the handoff's `pick()`.</summary>
    public void Select(int id, bool additive)
    {
        if (additive)
        {
            if (SelectedIds.Contains(id)) SelectedIds.Remove(id);
            else SelectedIds.Add(id);
        }
        else
        {
            SelectedIds.Clear();
            SelectedIds.Add(id);
        }

        SyncSelection();
    }

    [RelayCommand]
    private void ToggleTask(DownloadItemViewModel? item)
    {
        if (item is null) return;
        Engine.Toggle(item.Id);
        RaiseShellState();
    }

    [RelayCommand]
    private void RemoveTask(DownloadItemViewModel? item)
    {
        if (item is null) return;
        Engine.Remove(new[] { item.Id });
        SelectedIds.Clear();
        SyncSelection();
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        if (SelectedIds.Count == 0) return;
        Engine.Remove(SelectedIds.ToList());
        SelectedIds.Clear();
        SyncSelection();
    }

    [RelayCommand]
    private void ToggleAll()
    {
        Engine.ToggleAll();
        RaiseShellState();
    }

    [RelayCommand]
    private void MoveToFront(DownloadItemViewModel? item)
    {
        if (item is not null) Engine.MoveToFront(item.Id);
    }

    [RelayCommand]
    private void MoveToBack(DownloadItemViewModel? item)
    {
        if (item is not null) Engine.MoveToBack(item.Id);
    }

    [RelayCommand]
    private void Redownload(DownloadItemViewModel? item)
    {
        if (item is null) return;
        Engine.Redownload(item.Id);
        Say("已按新版本重新下载");
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (Current is null) return;
        Say($"已在文件夹中显示“{Current.Name}”");
    }

    [RelayCommand]
    private void ToggleInspector() => ShowInspector = !ShowInspector;

    [RelayCommand]
    private void OpenSheet(string sheet) => ActiveSheet = sheet;

    [RelayCommand]
    private void CloseSheet() => ActiveSheet = null;

    [RelayCommand]
    private void ToggleIsland() => ShowIsland = !ShowIsland;

    [RelayCommand]
    private void ToggleEdgeHide() => EdgeHide = !EdgeHide;

    [RelayCommand]
    private void ToggleBossMode() => BossMode = !BossMode;

    /// <summary>Adds a task from the 新建下载 sheet and reports it in the toast lane.</summary>
    public DownloadItemViewModel AddDownload(NewDownloadRequest request)
    {
        var task = Engine.Add(request);
        Select(task.Id, additive: false);
        Say("已添加 1 个任务");
        return task;
    }

    /// <summary>The `.toast` lane: one line, gone after 1.7s.</summary>
    public void Say(string message)
    {
        Toast = message;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ── plumbing ──────────────────────────────────────────────────────────
    private void Rebuild()
    {
        // The whole pipeline -- category, tab, search, sort, fold -- lives in
        // TaskQuery so it can be checked against the prototype's own output.
        var target = TaskQuery.Apply(Engine.Tasks, t => t.Model, Tab, Category, Query, SortKey, SortDirection);
        var fold = TaskQuery.Fold(target.Count, IsListExpanded);

        CanFold = fold.CanFold;
        HiddenCount = fold.Hidden;
        IsEmpty = target.Count == 0;

        var shown = target.Take(fold.Shown).ToList();

        // Sync in place so rows keep their hover/animation state across ticks.
        for (int i = 0; i < shown.Count; i++)
        {
            int existing = IndexOf(VisibleTasks, shown[i]);
            if (existing == i) continue;
            if (existing >= 0) VisibleTasks.Move(existing, i);
            else VisibleTasks.Insert(i, shown[i]);
        }

        while (VisibleTasks.Count > shown.Count) VisibleTasks.RemoveAt(VisibleTasks.Count - 1);

        SyncSelection();
        RaiseShellState();
    }

    private static int IndexOf(ObservableCollection<DownloadItemViewModel> list, DownloadItemViewModel item)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], item)) return i;
        }

        return -1;
    }

    private void SyncSelection()
    {
        foreach (var task in Engine.Tasks) task.IsSelected = SelectedIds.Contains(task.Id);

        int last = SelectedIds.Count > 0 ? SelectedIds[^1] : -1;
        Current = Engine.Tasks.FirstOrDefault(t => t.Id == last);

        OnPropertyChanged(nameof(SelectionCount));
        OnPropertyChanged(nameof(RemoveTooltip));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(CanOpenFolder));
        OnPropertyChanged(nameof(TitleDetail));
    }

    private void OnTasksChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void OnEngineTicked(object? sender, EventArgs e)
    {
        // Progress- and speed-ordered views reshuffle as the numbers move; the
        // other keys are stable, so only those two need a rebuild per tick.
        if (SortKey is "progress" or "speed") Rebuild();
        else RaiseShellState();
    }

    private void OnTaskCompleted(object? sender, DownloadItemViewModel task)
    {
        if (!Settings.NotifyOnCompletion) return;
        Banner = task;
        _bannerTimer.Stop();
        _bannerTimer.Start();
    }

    private void OnClipboardUrlDetected(object? sender, ClipboardUrlDetected e)
    {
        PendingUrl = e.Url;
        Say($"已从剪贴板检测到链接 · {e.Host}");
    }

    private double Average()
    {
        var history = Engine.SpeedHistory;
        return history.Count == 0 ? 0 : history.Average();
    }

    private void RaiseShellState()
    {
        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(DoneCount));
        OnPropertyChanged(nameof(TabActiveLabel));
        OnPropertyChanged(nameof(TabDoneLabel));
        OnPropertyChanged(nameof(TitleDetail));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(ToggleAllTooltip));
        OnPropertyChanged(nameof(SpeedHistory));
        OnPropertyChanged(nameof(TotalSpeedValue));
        OnPropertyChanged(nameof(TotalSpeedUnit));
        OnPropertyChanged(nameof(IslandSubtitle));
        OnPropertyChanged(nameof(OverallFraction));
    }

    // ── persistence ───────────────────────────────────────────────────────
    partial void OnTabChanged(string value) => Rebuild();
    partial void OnQueryChanged(string value) => Rebuild();
    partial void OnSortKeyChanged(string value) { Settings.SortKey = value; Persist(); Rebuild(); }
    partial void OnSortDirectionChanged(string value) { Settings.SortDirection = value; Persist(); Rebuild(); }
    partial void OnIsListExpandedChanged(bool value) { Rebuild(); OnPropertyChanged(nameof(FoldLabel)); }
    partial void OnDenseRowsChanged(bool value) { Settings.DenseRows = value; Persist(); }
    partial void OnShowInspectorChanged(bool value) { Settings.ShowInspector = value; Persist(); OnPropertyChanged(nameof(InspectorTooltip)); }
    partial void OnShowIslandChanged(bool value) { Settings.ShowIsland = value; Persist(); }
    partial void OnEdgeHideChanged(bool value) { Settings.EdgeHide = value; Persist(); }
    partial void OnHiddenCountChanged(int value) => OnPropertyChanged(nameof(FoldLabel));

    partial void OnCategoryChanged(string value)
    {
        Rebuild();
        OnPropertyChanged(nameof(HasCategoryFilter));
        OnPropertyChanged(nameof(CategoryLabel));
    }

    public void Persist()
    {
        try
        {
            _settingsStore.Save(Settings);
        }
        catch (Exception)
        {
            // A locked or read-only settings file must never take the shell down.
        }
    }

    /// <summary>CATS in the handoff.</summary>
    public static IReadOnlyList<(string Id, string Label)> Categories { get; } = new[]
    {
        ("all", "全部"), ("soft", "软件"), ("video", "视频"),
        ("doc", "文档"), ("music", "音乐"), ("bt", "BT"),
    };

    public static string CategoryName(string id) =>
        Categories.FirstOrDefault(c => c.Id == id).Label ?? "全部";
}
