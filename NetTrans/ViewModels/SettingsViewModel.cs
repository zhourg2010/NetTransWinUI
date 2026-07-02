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
    }

    partial void OnDefaultSavePathChanged(string value) => Persist(s => s.DefaultSavePath = value);
    partial void OnMaxSimultaneousDownloadsChanged(int value) => Persist(s => s.MaxSimultaneousDownloads = value);
    partial void OnSegmentsPerFileChanged(int value) => Persist(s => s.SegmentsPerFile = value);
    partial void OnVerifyChecksumsChanged(bool value) => Persist(s => s.VerifyChecksums = value);
    partial void OnAutoExtractArchivesChanged(bool value) => Persist(s => s.AutoExtractArchives = value);
    partial void OnNotifyOnCompletionChanged(bool value) => Persist(s => s.NotifyOnCompletion = value);
    partial void OnLaunchAtSignInChanged(bool value) => Persist(s => s.LaunchAtSignIn = value);
    partial void OnResumeInterruptedDownloadsChanged(bool value) => Persist(s => s.ResumeInterruptedDownloads = value);

    private void Persist(Action<AppSettings> apply)
    {
        apply(_settings);
        _store.Save(_settings);
    }
}
