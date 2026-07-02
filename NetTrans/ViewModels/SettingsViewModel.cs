using CommunityToolkit.Mvvm.ComponentModel;
using NetTrans.Models;
using NetTrans.Services;

namespace NetTrans.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _store;
    private readonly AppSettings _settings;

    [ObservableProperty]
    public partial string CurrentPage { get; set; } = "general";

    [ObservableProperty]
    public partial string DefaultSavePath { get; set; }

    [ObservableProperty]
    public partial int MaxSimultaneousDownloads { get; set; }

    [ObservableProperty]
    public partial int SegmentsPerFile { get; set; }

    [ObservableProperty]
    public partial bool VerifyChecksums { get; set; }

    [ObservableProperty]
    public partial bool AutoExtractArchives { get; set; }

    [ObservableProperty]
    public partial bool NotifyOnCompletion { get; set; }

    [ObservableProperty]
    public partial bool LaunchAtSignIn { get; set; }

    [ObservableProperty]
    public partial bool ResumeInterruptedDownloads { get; set; }

    public SettingsViewModel(ISettingsStore store)
    {
        _store = store;
        _settings = store.Load();

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
