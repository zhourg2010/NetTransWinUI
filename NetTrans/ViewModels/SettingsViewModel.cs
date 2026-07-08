using CommunityToolkit.Mvvm.ComponentModel;
using NetTrans.Models;
using NetTrans.Services;

namespace NetTrans.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _store;
    private readonly AppSettings _settings;

    [ObservableProperty]
    private string _currentPage = "general";

    [ObservableProperty]
    private string _defaultSavePath;

    [ObservableProperty]
    private int _maxSimultaneousDownloads;

    [ObservableProperty]
    private int _segmentsPerFile;

    [ObservableProperty]
    private bool _verifyChecksums;

    [ObservableProperty]
    private bool _autoExtractArchives;

    [ObservableProperty]
    private bool _notifyOnCompletion;

    [ObservableProperty]
    private bool _launchAtSignIn;

    [ObservableProperty]
    private bool _resumeInterruptedDownloads;

    [ObservableProperty]
    private bool _scheduleEnabled;

    [ObservableProperty]
    private string _offPeakStart;

    [ObservableProperty]
    private string _offPeakEnd;

    [ObservableProperty]
    private bool _captureEdge;

    [ObservableProperty]
    private bool _captureChrome;

    [ObservableProperty]
    private bool _captureFirefox;

    [ObservableProperty]
    private bool _notifyOnCapture;

    [ObservableProperty]
    private int _maxConnectionsPerServer;

    [ObservableProperty]
    private bool _preallocateFiles;

    [ObservableProperty]
    private string _userAgent;

    /// <summary>
    /// Takes the caller's already-loaded AppSettings/store (ShellViewModel's) rather than
    /// loading its own copy, so a Settings-dialog edit and a shell-level edit (accent/theme/
    /// throttle) in the same session can't silently overwrite each other on save.
    /// </summary>
    public SettingsViewModel(AppSettings settings, ISettingsStore store)
    {
        _store = store;
        _settings = settings;

        DefaultSavePath = _settings.DefaultSavePath;
        MaxSimultaneousDownloads = _settings.MaxSimultaneousDownloads;
        SegmentsPerFile = _settings.SegmentsPerFile;
        VerifyChecksums = _settings.VerifyChecksums;
        AutoExtractArchives = _settings.AutoExtractArchives;
        NotifyOnCompletion = _settings.NotifyOnCompletion;
        LaunchAtSignIn = _settings.LaunchAtSignIn;
        ResumeInterruptedDownloads = _settings.ResumeInterruptedDownloads;
        ScheduleEnabled = _settings.ScheduleEnabled;
        OffPeakStart = _settings.OffPeakStart;
        OffPeakEnd = _settings.OffPeakEnd;
        CaptureEdge = _settings.CaptureEdge;
        CaptureChrome = _settings.CaptureChrome;
        CaptureFirefox = _settings.CaptureFirefox;
        NotifyOnCapture = _settings.NotifyOnCapture;
        MaxConnectionsPerServer = _settings.MaxConnectionsPerServer;
        PreallocateFiles = _settings.PreallocateFiles;
        UserAgent = _settings.UserAgent;
    }

    partial void OnDefaultSavePathChanged(string value) => Persist(s => s.DefaultSavePath = value);
    partial void OnMaxSimultaneousDownloadsChanged(int value) => Persist(s => s.MaxSimultaneousDownloads = value);
    partial void OnSegmentsPerFileChanged(int value) => Persist(s => s.SegmentsPerFile = value);
    partial void OnVerifyChecksumsChanged(bool value) => Persist(s => s.VerifyChecksums = value);
    partial void OnAutoExtractArchivesChanged(bool value) => Persist(s => s.AutoExtractArchives = value);
    partial void OnNotifyOnCompletionChanged(bool value) => Persist(s => s.NotifyOnCompletion = value);
    partial void OnLaunchAtSignInChanged(bool value) => Persist(s => s.LaunchAtSignIn = value);
    partial void OnResumeInterruptedDownloadsChanged(bool value) => Persist(s => s.ResumeInterruptedDownloads = value);
    partial void OnScheduleEnabledChanged(bool value) => Persist(s => s.ScheduleEnabled = value);
    partial void OnOffPeakStartChanged(string value) => Persist(s => s.OffPeakStart = value);
    partial void OnOffPeakEndChanged(string value) => Persist(s => s.OffPeakEnd = value);
    partial void OnCaptureEdgeChanged(bool value) => Persist(s => s.CaptureEdge = value);
    partial void OnCaptureChromeChanged(bool value) => Persist(s => s.CaptureChrome = value);
    partial void OnCaptureFirefoxChanged(bool value) => Persist(s => s.CaptureFirefox = value);
    partial void OnNotifyOnCaptureChanged(bool value) => Persist(s => s.NotifyOnCapture = value);
    partial void OnMaxConnectionsPerServerChanged(int value) => Persist(s => s.MaxConnectionsPerServer = value);
    partial void OnPreallocateFilesChanged(bool value) => Persist(s => s.PreallocateFiles = value);
    partial void OnUserAgentChanged(string value) => Persist(s => s.UserAgent = value);

    private void Persist(Action<AppSettings> apply)
    {
        apply(_settings);
        _store.Save(_settings);
    }
}
