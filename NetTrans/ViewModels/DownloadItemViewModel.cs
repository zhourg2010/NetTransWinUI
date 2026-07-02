using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using NetTrans.Converters;
using NetTrans.Models;
using NetTrans.Services;

namespace NetTrans.ViewModels;

public sealed partial class DownloadItemViewModel : ObservableObject
{
    private readonly IDownloadEngine _engine;

    public DownloadItem Model { get; }

    public int Id => Model.Id;
    public string Name => Model.Name;
    public string Host => Model.Host;
    public FileKind Kind => Model.Kind;
    public long Size => Model.Size;
    public string Category => Model.Category;
    public string SavePath => Model.SavePath;
    public int SegmentCount => Model.SegmentProgress.Length > 0 ? Model.SegmentProgress.Length : Model.SegmentCount;
    public IReadOnlyList<MirrorSource> Mirrors => Model.Mirrors;
    public string StartedAt => Model.StartedAt;
    public string? CompletedWhen => Model.CompletedWhen;
    public string? Duration => Model.Duration;

    [ObservableProperty]
    private long _done;

    [ObservableProperty]
    private double _speed;

    [ObservableProperty]
    private DownloadStatus _status;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isChecked;

    public double[] SegmentProgress => Model.SegmentProgress;
    public double[] SpeedHistory => Model.SpeedHistory;
    public string AvgSpeedText => SpeedHistory.Length == 0 ? "—" : FormatHelpers.Speed(SpeedHistory.Average());
    public string PeakSpeedText => SpeedHistory.Length == 0 ? "—" : FormatHelpers.Speed(SpeedHistory.Max());

    public double Percent => Size <= 0 ? 0 : Math.Clamp(Done / (double)Size * 100.0, 0, 100);
    public string PercentText => Status == DownloadStatus.Downloading ? $"{Percent:0.0}%" : $"{Percent:0}%";
    public string DoneText => FormatHelpers.Bytes(Done);
    public string SizeText => FormatHelpers.Bytes(Size);
    public string SpeedText => FormatHelpers.Speed(Speed);
    public string EtaText
    {
        get
        {
            if (Status != DownloadStatus.Downloading || Speed <= 0) return "—";
            var remaining = Size - Done;
            if (remaining <= 0) return "00:00:00";
            var seconds = remaining / Speed;
            return FormatHelpers.Eta(TimeSpan.FromSeconds(seconds));
        }
    }

    public bool IsError => Status == DownloadStatus.Error;
    public bool IsDownloading => Status == DownloadStatus.Downloading;
    public bool IsPaused => Status == DownloadStatus.Paused;
    public bool IsQueued => Status == DownloadStatus.Queued;
    public bool IsCompleted => Status == DownloadStatus.Completed;
    public bool CanResume => IsPaused || IsQueued || IsError;

    public Visibility NormalMetaVisibility => IsError ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ErrorMetaVisibility => IsError ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PulseVisibility => IsDownloading ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PauseButtonVisibility => IsDownloading ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ResumeButtonVisibility => CanResume ? Visibility.Visible : Visibility.Collapsed;
    public string StatusLabel => BindingHelpers.StatusLabel(Status);
    public Brush StatusTextBrush => BindingHelpers.StatusBrush(Status);
    public Brush StatusDotBrush => BindingHelpers.StatusBrush(Status);

    public DownloadItemViewModel(DownloadItem model, IDownloadEngine engine)
    {
        Model = model;
        _engine = engine;
        Done = model.Done;
        Speed = model.Speed;
        Status = model.Status;
        ErrorMessage = model.ErrorMessage;
    }

    /// <summary>Called by StubDownloadEngine's tick to push new progress into the bound properties.</summary>
    public void ApplyTick(long done, double speed, DownloadStatus status, string? error)
    {
        Done = done;
        Speed = speed;
        Status = status;
        ErrorMessage = error;
        OnPropertyChanged(nameof(Percent));
        OnPropertyChanged(nameof(PercentText));
        OnPropertyChanged(nameof(DoneText));
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(EtaText));
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(IsQueued));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(SegmentProgress));
        OnPropertyChanged(nameof(SpeedHistory));
        OnPropertyChanged(nameof(NormalMetaVisibility));
        OnPropertyChanged(nameof(ErrorMetaVisibility));
        OnPropertyChanged(nameof(PulseVisibility));
        OnPropertyChanged(nameof(PauseButtonVisibility));
        OnPropertyChanged(nameof(ResumeButtonVisibility));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusTextBrush));
        OnPropertyChanged(nameof(StatusDotBrush));
        OnPropertyChanged(nameof(AvgSpeedText));
        OnPropertyChanged(nameof(PeakSpeedText));
    }

    [RelayCommand]
    private void Pause() => _engine.Pause(Id);

    [RelayCommand]
    private void Resume() => _engine.Resume(Id);

    [RelayCommand]
    private void Retry() => _engine.Retry(Id);

    [RelayCommand]
    private void Remove() => _engine.Remove(Id);

    [RelayCommand]
    private void ToggleChecked() => IsChecked = !IsChecked;
}
