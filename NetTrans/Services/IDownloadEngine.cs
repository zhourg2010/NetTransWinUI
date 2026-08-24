using System.Collections.ObjectModel;
using NetTrans.Models;
using NetTrans.ViewModels;

namespace NetTrans.Services;

/// <summary>What the 新建下载 sheet hands the engine.</summary>
public sealed record NewDownloadRequest(
    string Url,
    string SaveTo,
    string Category,
    int Connections,
    TaskPriority Priority,
    bool StartNow,
    string? ScheduledAt = null);

public interface IDownloadEngine
{
    /// <summary>Every task, in queue order. The shell filters and sorts a view over this.</summary>
    ObservableCollection<DownloadItemViewModel> Tasks { get; }

    double TotalSpeed { get; }
    double UploadSpeed { get; }

    /// <summary>The island's 26-bar sparkline, oldest sample first.</summary>
    IReadOnlyList<double> SpeedHistory { get; }

    /// <summary>True while at least one task is running -- drives the toolbar's play/pause glyph.</summary>
    bool IsRunning { get; }

    /// <summary>Raised once per tick so live-only readouts can refresh without a collection change.</summary>
    event EventHandler? Ticked;

    /// <summary>Raised when a task finishes, so the shell can drop the completion banner.</summary>
    event EventHandler<DownloadItemViewModel>? Completed;

    void Toggle(int id);
    void Remove(IEnumerable<int> ids);
    void ToggleAll();
    void MoveToFront(int id);
    void MoveToBack(int id);

    /// <summary>Restarts a task against the newer server-side build and clears its 新版本 flag.</summary>
    void Redownload(int id);

    DownloadItemViewModel Add(NewDownloadRequest request);

    /// <summary>Re-reads the 同时下载 and 全局限速 settings after the sheet changes them.</summary>
    void ApplySettings(AppSettings settings);
}
