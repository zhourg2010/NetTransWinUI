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
    private readonly DispatcherTimer _countdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly QueueDrain _drain = new();

    private CompletionAction _pendingAction = CompletionAction.Nothing;

    public IDownloadEngine Engine { get; }
    public AppSettings Settings { get; }
    public ISettingsStore SettingsStore => _settingsStore;

    /// <summary>The task list after tab / category / search / sort, then folded to five rows.</summary>
    public ObservableCollection<DownloadItemViewModel> VisibleTasks { get; } = new();

    /// <summary>Selected task ids; the last one drives the inspector, matching the handoff's `sel`.</summary>
    public ObservableCollection<int> SelectedIds { get; } = new();

    /// <summary>
    /// <paramref name="settings"/> is passed in when the host already loaded
    /// them -- the engine is configured from the same instance, so a change in
    /// the 设置 sheet reaches both.
    /// </summary>
    public ShellViewModel(
        IDownloadEngine engine,
        IClipboardWatcher clipboardWatcher,
        ISettingsStore settingsStore,
        AppSettings? settings = null)
    {
        Engine = engine;
        _clipboardWatcher = clipboardWatcher;
        _settingsStore = settingsStore;
        Settings = settings ?? settingsStore.Load();

        _denseRows = Settings.DenseRows;
        _theme = Settings.Theme;
        _sortKey = Settings.SortKey;
        _sortDirection = Settings.SortDirection;
        _showInspector = Settings.ShowInspector;
        _showIsland = Settings.ShowIsland;
        _edgeHide = Settings.EdgeHide;
        _bossKey = Settings.BossKey;
        _watchClipboard = Settings.WatchClipboard;

        Engine.Tasks.CollectionChanged += OnTasksChanged;
        Engine.Ticked += OnEngineTicked;
        Engine.Completed += OnTaskCompleted;

        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast = null; };
        _bannerTimer.Tick += (_, _) => { _bannerTimer.Stop(); Banner = null; };
        _countdownTimer.Tick += (_, _) => CountDown();

        if (Engine.Tasks.FirstOrDefault() is { } first) SelectedIds.Add(first.Id);
        Rebuild();

        _clipboardWatcher.UrlDetected += OnClipboardUrlDetected;
        if (WatchClipboard) _clipboardWatcher.Start();
    }

    // ── list state ────────────────────────────────────────────────────────
    [ObservableProperty] private string _tab = "all";           // all | active | done
    [ObservableProperty] private string _category = "all";
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _isSearchOpen;
    [ObservableProperty] private string _sortKey;               // added | name | size | progress | speed
    [ObservableProperty] private string _sortDirection;         // asc | desc
    [ObservableProperty] private bool _denseRows;

    /// <summary>跟随系统 | 浅色 | 深色, as auto | light | dark.</summary>
    [ObservableProperty] private string _theme;
    [ObservableProperty] private bool _isListExpanded;

    [ObservableProperty] private int _hiddenCount;
    [ObservableProperty] private bool _canFold;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private DownloadItemViewModel? _current;

    // ── shell layers ──────────────────────────────────────────────────────
    [ObservableProperty] private bool _showInspector;
    [ObservableProperty] private bool _showIsland;
    [ObservableProperty] private bool _edgeHide;

    /// <summary>老板键, as the sheet spells it. The shell re-registers on change.</summary>
    [ObservableProperty] private string _bossKey;

    [ObservableProperty] private bool _watchClipboard;
    [ObservableProperty] private bool _bossMode;
    [ObservableProperty] private string? _activeSheet;          // add | batch | torrent | sniff | prefs
    [ObservableProperty] private string? _toast;
    [ObservableProperty] private DownloadItemViewModel? _banner;
    [ObservableProperty] private bool _isDropTarget;

    /// <summary>
    /// 全部完成后, once the queue has drained: the label of what is about to
    /// happen, and how long is left to stop it. Null when nothing is pending.
    /// </summary>
    [ObservableProperty] private string? _pendingActionLabel;

    [ObservableProperty] private int _pendingActionSeconds;
    [ObservableProperty] private string _pendingUrl = "https://";

    /// <summary>Which task the 重命名 sheet is about.</summary>
    [ObservableProperty] private DownloadItemViewModel? _renameTarget;

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

    [RelayCommand]
    private void SetTheme(string theme) => Theme = theme is "light" or "dark" ? theme : "auto";

    /// <summary>Raised when 主题 changes, so each window can re-theme itself.</summary>
    public event EventHandler<string>? ThemeChanged;

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

        if (!FileActions.OpenFolder(Current.SavePath)) Say($"打不开 {Current.SavePath}");
    }

    [RelayCommand]
    private void OpenFile(DownloadItemViewModel? item)
    {
        item ??= Current;
        if (item is null || Engine.PathOf(item.Id) is not { } path) return;

        if (!FileActions.Open(path)) Say($"打不开“{item.Name}”，文件可能已被移动");
    }

    [RelayCommand]
    private void RevealFile(DownloadItemViewModel? item)
    {
        item ??= Current;
        if (item is null || Engine.PathOf(item.Id) is not { } path) return;

        if (!FileActions.Reveal(path)) Say($"打不开 {item.SavePath}");
    }

    /// <summary>校验 SHA-256. Hashing a large file takes a while, so it reports at both ends.</summary>
    [RelayCommand]
    private async Task VerifyAsync(DownloadItemViewModel? item)
    {
        item ??= Current;
        if (item is null) return;

        Say($"正在校验“{item.Name}”…");

        string? result = await Engine.VerifyAsync(item.Id);
        Say(result ?? $"找不到“{item.Name}”，无法校验");
    }

    [RelayCommand]
    private async Task CheckUpdateAsync(DownloadItemViewModel? item)
    {
        item ??= Current;
        if (item is null) return;

        Say(await Engine.CheckForUpdateAsync(item.Id)
            ? "服务器上有更新版本"
            : "已是服务器上的最新版本");
    }

    /// <summary>重命名, from the rename sheet.</summary>
    public void RenameTask(DownloadItemViewModel item, string newName)
    {
        if (item.IsRunning)
        {
            Say("下载中的任务无法重命名，请先暂停");
            return;
        }

        Say(Engine.Rename(item.Id, newName) ? $"已重命名为“{newName}”" : "重命名失败，该名称可能已被占用");
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

        // Nothing moved: the common case while sorting by progress or speed,
        // which rebuilds on every tick.
        if (!Differs(shown))
        {
            SyncSelection();
            RaiseShellState();
            return;
        }

        // Sync in place so rows keep their hover/animation state across ticks.
        // The scan per row stays: it only runs when the order actually moved,
        // and what is on screen is bounded by the fold.
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

    /// <summary>Whether the visible list already is what it should be, in order.</summary>
    private bool Differs(List<DownloadItemViewModel> shown)
    {
        if (VisibleTasks.Count != shown.Count) return true;

        for (int i = 0; i < shown.Count; i++)
        {
            if (!ReferenceEquals(VisibleTasks[i], shown[i])) return true;
        }

        return false;
    }

    private void SyncSelection()
    {
        foreach (var task in Engine.Tasks) task.IsSelected = SelectedIds.Contains(task.Id);

        int last = SelectedIds.Count > 0 ? SelectedIds[^1] : -1;
        Current = Engine.Tasks.FirstOrDefault(t => t.Id == last);

        // Per-connection rates are built for the inspected task alone, so the
        // engine has to be told which one that is.
        Engine.Inspected = ShowInspector && Current is not null ? Current.Id : 0;

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

        WatchForDrain();
    }

    // ── 全部完成后 ────────────────────────────────────────────────────────

    private const int CountdownSeconds = 20;

    /// <summary>
    /// Arms the configured action the moment the last transfer stops. Nothing
    /// happens straight away: the shell counts down first, so an action the
    /// user set hours ago is never a surprise they cannot stop.
    /// </summary>
    private void WatchForDrain()
    {
        int busy = Engine.Tasks.Count(task => task.Status is DownloadStatus.Downloading or DownloadStatus.Queued);
        if (!_drain.Drained(busy)) return;

        var action = SettingsRules.WhenAllComplete(Settings.WhenAllComplete);
        if (action == CompletionAction.Nothing) return;

        // Already counting down from an earlier batch: leave it be rather than
        // restarting the clock under the user.
        if (_pendingAction != CompletionAction.Nothing) return;

        _pendingAction = action;
        PendingActionLabel = SettingsRules.Describe(action);
        PendingActionSeconds = CountdownSeconds;
        _countdownTimer.Start();
    }

    private void CountDown()
    {
        PendingActionSeconds--;
        if (PendingActionSeconds > 0) return;

        var action = _pendingAction;
        ClearPendingAction();

        if (PowerActions.Run(action)) return;

        Say($"系统拒绝了{SettingsRules.Describe(action)}");
    }

    /// <summary>取消 on the countdown: this batch only, not the setting.</summary>
    [RelayCommand]
    private void CancelPendingAction()
    {
        if (_pendingAction == CompletionAction.Nothing) return;

        string label = SettingsRules.Describe(_pendingAction);
        ClearPendingAction();
        _drain.Disarm();
        Say($"已取消{label}");
    }

    private void ClearPendingAction()
    {
        _countdownTimer.Stop();
        _pendingAction = CompletionAction.Nothing;
        PendingActionLabel = null;
        PendingActionSeconds = 0;
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

    partial void OnThemeChanged(string value)
    {
        Settings.Theme = value;
        Persist();

        // The windows watch this one: a theme that only took effect on the next
        // start would be the kind of setting nobody trusts.
        ThemeChanged?.Invoke(this, value);
    }
    partial void OnShowInspectorChanged(bool value)
    {
        Settings.ShowInspector = value;
        Persist();

        Engine.Inspected = value && Current is not null ? Current.Id : 0;

        OnPropertyChanged(nameof(InspectorTooltip));
    }
    partial void OnShowIslandChanged(bool value) { Settings.ShowIsland = value; Persist(); }
    partial void OnEdgeHideChanged(bool value) { Settings.EdgeHide = value; Persist(); }

    partial void OnBossKeyChanged(string value) { Settings.BossKey = value; Persist(); }

    partial void OnWatchClipboardChanged(bool value)
    {
        Settings.WatchClipboard = value;
        Persist();

        // The watcher is a live subscription to the system clipboard, so the
        // switch has to reach it now rather than at the next start.
        if (value) _clipboardWatcher.Start();
        else _clipboardWatcher.Stop();
    }
    partial void OnHiddenCountChanged(int value) => OnPropertyChanged(nameof(FoldLabel));

    partial void OnCategoryChanged(string value)
    {
        Rebuild();
        OnPropertyChanged(nameof(HasCategoryFilter));
        OnPropertyChanged(nameof(CategoryLabel));
    }

    public void Persist()
    {
        // 同时下载 and 全局限速 have to reach the running engine, not just the file.
        Engine.ApplySettings(Settings);

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
