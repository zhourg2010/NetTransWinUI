using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using NetTrans.Converters;
using NetTrans.Models;
using NetTrans.Services;

namespace NetTrans.ViewModels;

public sealed partial class ShellViewModel : ObservableObject
{
    public IDownloadEngine Engine { get; }
    private readonly IClipboardWatcher _clipboardWatcher;
    private readonly ISettingsStore _settingsStore;
    private readonly AppSettings _settings;

    /// <summary>The single loaded AppSettings/store, shared with SettingsViewModel so both never save independent stale snapshots over each other.</summary>
    public AppSettings Settings => _settings;
    public ISettingsStore SettingsStore => _settingsStore;

    public ObservableCollection<DownloadItemViewModel> FilteredActiveDownloads { get; } = new();
    public ObservableCollection<DownloadItemViewModel> FilteredCompletedDownloads { get; } = new();

    [ObservableProperty]
    private string _currentSection = "active";

    [ObservableProperty]
    private string _searchQuery = "";

    [ObservableProperty]
    private string _statusFilter = "all"; // all | downloading | queued | paused | issues

    [ObservableProperty]
    private DownloadItemViewModel? _selectedItem;

    [ObservableProperty]
    private ElementTheme _appTheme;

    [ObservableProperty]
    private string _accentHex = "#0067C0";

    [ObservableProperty]
    private string _density = "comfy";

    [ObservableProperty]
    private bool _showDetailPane = true;

    [ObservableProperty]
    private bool _isThrottled;

    [ObservableProperty]
    private bool _isPasteBarVisible;

    [ObservableProperty]
    private string _pasteBarUrl = "";

    [ObservableProperty]
    private string _pasteBarHost = "";

    [ObservableProperty]
    private bool _newDownloadDialogOpen;

    [ObservableProperty]
    private bool _settingsDialogOpen;

    [ObservableProperty]
    private string _prefillUrl = "";

    public string TotalSpeedText => FormatHelpers.Speed(Engine.TotalSpeed);
    public string MonthlyTotalText => FormatHelpers.Bytes(Engine.BytesTransferredThisMonth);

    public string PageTitle => CurrentSection switch
    {
        "active" => "Active downloads",
        "completed" => "Completed",
        "scheduled" => "Scheduled",
        "history" => "History",
        "settings" => "Settings",
        _ => "",
    };

    public string PageSubtitle => CurrentSection switch
    {
        "active" => BuildActiveSubtitle(),
        "completed" => $"{Engine.CompletedDownloads.Count} items · {MonthlyTotalText} this month",
        "scheduled" => "Next: Tonight at 02:00",
        "history" => "All transfers from the last 90 days",
        "settings" => "Preferences for NetTrans",
        _ => "",
    };

    public Visibility FilterBarVisible => CurrentSection is "active" or "completed" or "history" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DetailPaneVisible => ShowDetailPane && CurrentSection == "active" ? Visibility.Visible : Visibility.Collapsed;

    private string BuildActiveSubtitle()
    {
        int downloading = Engine.ActiveDownloads.Count(d => d.IsDownloading);
        int queued = Engine.ActiveDownloads.Count(d => d.IsQueued);
        int paused = Engine.ActiveDownloads.Count(d => d.IsPaused);
        int error = Engine.ActiveDownloads.Count(d => d.IsError);
        return $"{downloading} downloading · {queued} queued · {paused} paused · {error} error";
    }

    public static readonly string[] AccentSwatches = ["#0067C0", "#107C10", "#8764B8", "#C42B1C", "#CA5010", "#038387"];

    public ShellViewModel(IDownloadEngine engine, IClipboardWatcher clipboardWatcher, ISettingsStore settingsStore)
    {
        Engine = engine;
        _clipboardWatcher = clipboardWatcher;
        _settingsStore = settingsStore;
        _settings = settingsStore.Load();

        AccentHex = _settings.Accent;
        Density = _settings.Density;
        ShowDetailPane = _settings.ShowDetailPane;
        IsThrottled = _settings.Throttled;
        engine.IsThrottled = _settings.Throttled;
        AppTheme = _settings.Theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        ApplyAccent(AccentHex);

        Engine.ActiveDownloads.CollectionChanged += (_, _) => { WireItemNotifications(); RefreshFilteredLists(); };
        Engine.CompletedDownloads.CollectionChanged += (_, _) => { WireItemNotifications(); RefreshFilteredLists(); };
        Engine.Ticked += (_, _) => RefreshLiveStats();
        WireItemNotifications();
        RefreshFilteredLists();
        SelectedItem = Engine.ActiveDownloads.FirstOrDefault();

        _clipboardWatcher.UrlDetected += OnClipboardUrlDetected;
        _clipboardWatcher.Start();
    }

    private void OnClipboardUrlDetected(object? sender, ClipboardUrlDetected e)
    {
        PasteBarUrl = e.Url;
        PasteBarHost = e.Host;
        IsPasteBarVisible = true;
    }

    partial void OnSearchQueryChanged(string value) => RefreshFilteredLists();
    partial void OnStatusFilterChanged(string value) => RefreshFilteredLists();

    partial void OnCurrentSectionChanged(string value)
    {
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageSubtitle));
        OnPropertyChanged(nameof(FilterBarVisible));
        OnPropertyChanged(nameof(DetailPaneVisible));
    }

    partial void OnShowDetailPaneChanged(bool value) => OnPropertyChanged(nameof(DetailPaneVisible));

    private void RefreshFilteredLists()
    {
        var q = SearchQuery.Trim().ToLowerInvariant();

        IEnumerable<DownloadItemViewModel> active = Engine.ActiveDownloads;
        if (!string.IsNullOrEmpty(q))
        {
            active = active.Where(d => d.Name.ToLowerInvariant().Contains(q) || d.Host.ToLowerInvariant().Contains(q));
        }
        active = StatusFilter switch
        {
            "downloading" => active.Where(d => d.IsDownloading),
            "queued" => active.Where(d => d.IsQueued),
            "paused" => active.Where(d => d.IsPaused),
            "issues" => active.Where(d => d.IsError),
            _ => active,
        };

        FilteredActiveDownloads.Clear();
        foreach (var item in active) FilteredActiveDownloads.Add(item);

        IEnumerable<DownloadItemViewModel> completed = Engine.CompletedDownloads;
        if (!string.IsNullOrEmpty(q))
        {
            completed = completed.Where(d => d.Name.ToLowerInvariant().Contains(q) || d.Host.ToLowerInvariant().Contains(q));
        }
        FilteredCompletedDownloads.Clear();
        foreach (var item in completed) FilteredCompletedDownloads.Add(item);

        OnPropertyChanged(nameof(TotalSpeedText));
        OnPropertyChanged(nameof(MonthlyTotalText));
    }

    public void RefreshLiveStats()
    {
        OnPropertyChanged(nameof(TotalSpeedText));
        OnPropertyChanged(nameof(MonthlyTotalText));
    }

    private readonly Dictionary<DownloadItemViewModel, PropertyChangedEventHandler> _wired = new();

    private void WireItemNotifications()
    {
        var current = new HashSet<DownloadItemViewModel>(Engine.ActiveDownloads.Concat(Engine.CompletedDownloads));

        foreach (var vm in current)
        {
            if (!_wired.ContainsKey(vm))
            {
                PropertyChangedEventHandler handler = (_, e) =>
                {
                    if (e.PropertyName == nameof(DownloadItemViewModel.IsChecked)) RecomputePickedCount();
                    if (e.PropertyName == nameof(DownloadItemViewModel.Status))
                    {
                        OnPropertyChanged(nameof(PageSubtitle));
                        // Re-bucket into the active status filter (e.g. a Downloading item that
                        // was just Paused shouldn't linger in a "Downloading" filtered view).
                        if (StatusFilter != "all") RefreshFilteredLists();
                    }
                };
                vm.PropertyChanged += handler;
                _wired[vm] = handler;
            }
        }

        // Evict + unsubscribe items no longer in either collection (e.g. removed via Engine.Remove)
        // so removed DownloadItemViewModels aren't kept alive for the app's lifetime.
        foreach (var stale in _wired.Keys.Where(vm => !current.Contains(vm)).ToList())
        {
            stale.PropertyChanged -= _wired[stale];
            _wired.Remove(stale);
        }

        RecomputePickedCount();
    }

    [ObservableProperty]
    private int _pickedCount;

    public string SelectedCountText => $"{PickedCount} selected";
    public Visibility SelectedCountVisible => PickedCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void RecomputePickedCount()
    {
        PickedCount = Engine.ActiveDownloads.Concat(Engine.CompletedDownloads).Count(d => d.IsChecked);
        OnPropertyChanged(nameof(SelectedCountText));
        OnPropertyChanged(nameof(SelectedCountVisible));
    }

    [RelayCommand]
    private void SetSection(string section) => CurrentSection = section;

    [RelayCommand]
    private void OpenNewDownload()
    {
        PrefillUrl = IsPasteBarVisible ? PasteBarUrl : "";
        NewDownloadDialogOpen = true;
    }

    [RelayCommand]
    private void OpenSettings() => SettingsDialogOpen = true;

    [RelayCommand]
    private void PauseAll() => Engine.PauseAll();

    [RelayCommand]
    private void ResumeAll() => Engine.ResumeAll();

    [RelayCommand]
    private void DismissPasteBar() => IsPasteBarVisible = false;

    [RelayCommand]
    private void DownloadFromPasteBar()
    {
        OpenNewDownload();
    }

    [RelayCommand]
    private void ToggleThrottle()
    {
        IsThrottled = !IsThrottled;
        Engine.IsThrottled = IsThrottled;
        _settings.Throttled = IsThrottled;
        _settingsStore.Save(_settings);
    }

    [RelayCommand]
    private void CloseDetail() => ShowDetailPane = false;

    public void SetAccent(string hex)
    {
        AccentHex = hex;
        ApplyAccent(hex);
        _settings.Accent = hex;
        _settingsStore.Save(_settings);
    }

    public void SetTheme(string theme)
    {
        AppTheme = theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        _settings.Theme = theme;
        _settingsStore.Save(_settings);
    }

    public void SetDensity(string density)
    {
        Density = density;
        _settings.Density = density;
        _settingsStore.Save(_settings);
    }

    private static void ApplyAccent(string hex)
    {
        var color = BindingHelpers.ColorFromHex(hex);
        var resources = Application.Current.Resources;
        resources["AccentColor"] = color;
        resources["AccentBrush"] = new SolidColorBrush(color);
        resources["AccentHoverBrush"] = new SolidColorBrush(color) { Opacity = 0.9 };
    }
}
