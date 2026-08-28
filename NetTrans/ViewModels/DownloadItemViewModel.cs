using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using NetTrans.Models;
using NetTrans.Services;

namespace NetTrans.ViewModels;

/// <summary>
/// One task, in the shape the list row and the inspector bind to. Every derived
/// string here is the handoff's own expression for that string -- see Row() and
/// Inspector() in mini-ios2.jsx.
/// </summary>
public sealed partial class DownloadItemViewModel : ObservableObject
{
    public DownloadItem Model { get; }

    public DownloadItemViewModel(DownloadItem model)
    {
        Model = model;
        _done = model.Done;
        _speed = model.Speed;
        _status = model.Status;
        _connections = model.Connections;
        _priority = model.Priority;
        _speedLimit = model.SpeedLimit;
    }

    public int Id => Model.Id;
    public string Name => Model.Name;
    public string Host => Model.Host;
    public FileKind Kind => Model.Kind;
    public long Size => Model.Size;
    public string Category => Model.Category;
    public string Url => Model.Url;
    public string SavePath => Model.SavePath;
    public string AddedAt => Model.AddedAt;
    public int Retries => Model.Retries;
    public int[] Blocks => Model.Blocks;
    public double[] ConnectionSpeeds => Model.ConnectionSpeeds;
    public double[] SpeedHistory => Model.SpeedHistory;
    public IReadOnlyList<LogEntry> Log => Model.Log;

    [ObservableProperty] private long _done;
    [ObservableProperty] private double _speed;
    [ObservableProperty] private DownloadStatus _status;
    [ObservableProperty] private int _connections;
    [ObservableProperty] private TaskPriority _priority;
    [ObservableProperty] private string _speedLimit;
    [ObservableProperty] private bool _isSelected;

    /// <summary>Set when the server holds a newer build; clearing it also drops the row's 新版本 tag.</summary>
    public NewVersionInfo? NewerVersion
    {
        get => Model.NewerVersion;
        set
        {
            if (Model.NewerVersion == value) return;
            Model.NewerVersion = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasNewerVersion));
            OnPropertyChanged(nameof(NewVersionSubtitle));
        }
    }

    public string? Checksum
    {
        get => Model.Checksum;
        set
        {
            if (Model.Checksum == value) return;
            Model.Checksum = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ChecksumText));
            OnPropertyChanged(nameof(SubText));
        }
    }

    public string? ErrorMessage
    {
        get => Model.ErrorMessage;
        set
        {
            if (Model.ErrorMessage == value) return;
            Model.ErrorMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SubText));
        }
    }

    // ── derived: state ────────────────────────────────────────────────────
    public bool IsDone => Status == DownloadStatus.Completed;
    public bool IsRunning => Status == DownloadStatus.Downloading;
    public bool IsError => Status == DownloadStatus.Error;
    public bool IsQueued => Status == DownloadStatus.Queued;
    public bool IsPaused => Status == DownloadStatus.Paused;
    public bool IsBitTorrent => Model.Peers is not null;
    public bool HasNewerVersion => Model.NewerVersion is not null;
    public bool IsHighPriority => Priority == TaskPriority.High;

    // Every derived string below comes from TaskPresenter, which lives in
    // NetTrans.Core and is checked against values generated from the handoff's
    // own source -- see NetTrans.Core.Tests.
    public string StatusText => TaskPresenter.StatusText(Status);

    public double Percent => TaskPresenter.Percent(Done, Size);
    public double Fraction => TaskPresenter.Fraction(Done, Size);

    // ── derived: row ──────────────────────────────────────────────────────
    public string SubText => TaskPresenter.SubText(Model);

    /// <summary>The row's trailing readout: 完成 badge, 失败, an em dash while queued, or the percentage.</summary>
    public string TrailingText => TaskPresenter.TrailingText(Model);

    /// <summary>The progress track is hidden for finished and not-yet-started tasks.</summary>
    public bool ShowProgress => TaskPresenter.ShowProgress(Status);

    /// <summary>Paused rows draw the fill in --gray instead of --blue.</summary>
    public Brush ProgressBrush => Resource(IsPaused ? "GrayBrush" : "BlueBrush");

    public Brush SubBrush => Resource(IsError ? "RedBrush" : "Label2Brush");

    public Brush TileBrush => new SolidColorBrush(ParseColor(Model.Tint));

    public string KindGlyph => Glyph(Kind switch
    {
        FileKind.Disc => "IconDisc",
        FileKind.Film => "IconFilm",
        FileKind.Zip => "IconZip",
        FileKind.Music => "IconMusic",
        _ => "IconDoc",
    });

    /// <summary>The hover action's label: 暂停 while running, 重试 after a failure, otherwise 继续.</summary>
    public string ToggleLabel => TaskPresenter.ToggleLabel(Status);

    public string ToggleGlyph => Glyph(IsRunning ? "IconPauseFill" : "IconPlayFill");

    // ── derived: inspector ────────────────────────────────────────────────
    public Brush RingBrush => Resource(Status switch
    {
        DownloadStatus.Error => "RedBrush",
        DownloadStatus.Completed => "GreenBrush",
        DownloadStatus.Downloading => "BlueBrush",
        _ => "GrayBrush",
    });

    /// <summary>Ring centre subtitle: live speed, falling back to the state name when stalled.</summary>
    public string RingSubtitle => TaskPresenter.RingSubtitle(Model);

    public string RingCaption => TaskPresenter.RingCaption(Model);

    public string PercentText => TaskPresenter.PercentText(Done, Size);
    public string ConnectionsText => Connections > 0 ? Connections.ToString() : "—";
    public string SpeedText => FormatHelpers.SpeedOrDash(Speed);
    public string ChecksumText => Checksum is { Length: > 0 } c ? c : "未启用";
    public string PeersSeedsText => $"{Model.Peers} / {Model.Seeds}";
    public string PeersText => Model.Peers?.ToString() ?? "—";
    public string SeedsText => Model.Seeds?.ToString() ?? "—";
    public string RatioText => Model.Ratio?.ToString("0.00") ?? "—";
    public string UploadText => FormatHelpers.SpeedOrDash(Model.UploadSpeed);
    public string RetriesText => $"{Retries} 次";
    public string AverageSpeedText => SpeedHistory.Length == 0
        ? FormatHelpers.SpeedOrDash(Speed * 0.82)
        : FormatHelpers.SpeedOrDash(SpeedHistory.Average());
    public string PeakSpeedText => FormatHelpers.SpeedOrDash(Math.Max(Model.PeakSpeed, Speed));

    public string NewVersionSubtitle => Model.NewerVersion is { } newer
        ? TaskPresenter.NewVersionSubtitle(newer)
        : "";

    /// <summary>
    /// Called by the engine after it mutates the model, to refresh every derived
    /// readout at once.
    ///
    /// "At once" is the point. Each of the four writes below used to raise the
    /// nine derived readouts on its own, so a moving row fired them four times
    /// per tick, and the six below went out whether or not anything had changed
    /// -- for every row in the list, twice a second, including the ones sitting
    /// finished. A binding update is not free, so a quiet row now costs nothing
    /// and a busy one costs one round instead of four.
    /// </summary>
    public void Refresh()
    {
        // Status is the one write whose setter has news of its own, so whether
        // it moved decides who does the raising.
        bool statusMoved = Status != Model.Status;

        bool moved =
            statusMoved ||
            Done != Model.Done ||
            Connections != Model.Connections ||
            Math.Abs(Speed - Model.Speed) > 0.5 ||

            // A seeding torrent moves none of the above; what changes is what
            // it has sent, and the inspector is watching that.
            _lastUploaded != Model.Uploaded ||
            _lastLog != Model.Log.Count;

        _lastUploaded = Model.Uploaded;
        _lastLog = Model.Log.Count;

        _quiet = true;

        try
        {
            Done = Model.Done;
            Speed = Model.Speed;
            Connections = Model.Connections;

            _quiet = !statusMoved;
            Status = Model.Status;
        }
        finally
        {
            _quiet = false;
        }

        if (!moved) return;

        // The status setter has already raised these when it fired.
        if (!statusMoved) RaiseDerived();

        OnPropertyChanged(nameof(Blocks));
        OnPropertyChanged(nameof(ConnectionSpeeds));
        OnPropertyChanged(nameof(SpeedHistory));
        OnPropertyChanged(nameof(Log));
        OnPropertyChanged(nameof(AverageSpeedText));
        OnPropertyChanged(nameof(PeakSpeedText));
        OnPropertyChanged(nameof(UploadText));
        OnPropertyChanged(nameof(RatioText));
        OnPropertyChanged(nameof(PeersText));
        OnPropertyChanged(nameof(SeedsText));
        OnPropertyChanged(nameof(PeersSeedsText));
    }

    /// <summary>
    /// Set while <see cref="Refresh"/> writes the four values, so their setters
    /// update the model without each raising the same nine readouts.
    /// </summary>
    private bool _quiet;

    private long _lastUploaded = -1;
    private int _lastLog = -1;

    // TaskPresenter derives everything from the model, so a view-model write has
    // to reach the model before the derived readouts are raised.
    partial void OnDoneChanged(long value)
    {
        Model.Done = value;
        if (!_quiet) RaiseDerived();
    }

    partial void OnSpeedChanged(double value)
    {
        Model.Speed = value;
        if (!_quiet) RaiseDerived();
    }

    partial void OnConnectionsChanged(int value)
    {
        Model.Connections = value;
        if (!_quiet) RaiseDerived();
    }

    partial void OnStatusChanged(DownloadStatus value)
    {
        Model.Status = value;
        if (_quiet) return;

        RaiseDerived();
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(IsQueued));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ShowProgress));
        OnPropertyChanged(nameof(ProgressBrush));
        OnPropertyChanged(nameof(SubBrush));
        OnPropertyChanged(nameof(RingBrush));
        OnPropertyChanged(nameof(ToggleLabel));
        OnPropertyChanged(nameof(ToggleGlyph));
    }

    partial void OnPriorityChanged(TaskPriority value)
    {
        Model.Priority = value;
        OnPropertyChanged(nameof(IsHighPriority));
    }

    partial void OnSpeedLimitChanged(string value) => Model.SpeedLimit = value;

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(Percent));
        OnPropertyChanged(nameof(Fraction));
        OnPropertyChanged(nameof(SubText));
        OnPropertyChanged(nameof(TrailingText));
        OnPropertyChanged(nameof(RingSubtitle));
        OnPropertyChanged(nameof(RingCaption));
        OnPropertyChanged(nameof(PercentText));
        OnPropertyChanged(nameof(ConnectionsText));
        OnPropertyChanged(nameof(SpeedText));
    }

    private static Brush Resource(string key) => ThemeBrushes.Get(key);

    /// <summary>The icon's path data. StrokeIcon builds the geometry itself.</summary>
    private static string Glyph(string key) => Views.IconResources.Data(key);

    private static Windows.UI.Color ParseColor(string hex)
    {
        string h = hex.TrimStart('#');
        byte r = Convert.ToByte(h.Substring(0, 2), 16);
        byte g = Convert.ToByte(h.Substring(2, 2), 16);
        byte b = Convert.ToByte(h.Substring(4, 2), 16);
        return Windows.UI.Color.FromArgb(0xFF, r, g, b);
    }
}
