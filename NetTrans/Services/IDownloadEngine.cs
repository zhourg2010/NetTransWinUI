using System.Collections.ObjectModel;
using NetTrans.Models;
using NetTrans.Net;
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

/// <summary>
/// What the 种子 sheet asks for, per task.
/// </summary>
/// <param name="Sequential">顺序下载, for previewing a video before it finishes.</param>
/// <param name="Limits">做种限制.</param>
/// <param name="UploadLimit">上传限速 in bytes per second; zero is 不限.</param>
/// <param name="Files">
/// 选择文件: paths inside the torrent to fetch. Null or empty means all of them,
/// and only a .torrent read from disk can offer the choice up front -- a magnet
/// does not know its own file list until peers have told it.
/// </param>
public sealed record TorrentTaskOptions(
    bool Sequential,
    NetTrans.Torrent.SeedingLimits Limits,
    double UploadLimit = 0,
    IReadOnlyList<string>? Files = null);

public interface IDownloadEngine
{
    /// <summary>Every task, in queue order. The shell filters and sorts a view over this.</summary>
    ObservableCollection<DownloadItemViewModel> Tasks { get; }

    /// <summary>Shared with the sheets, so 批量下载 and 视频嗅探 go out through the same client the transfers do.</summary>
    IHttpTransport Transport { get; }

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

    /// <summary>
    /// The torrent settings that cannot sensibly be global. Ignored for a task
    /// that is not a torrent.
    /// </summary>
    void ApplyTorrentOptions(int id, TorrentTaskOptions options);

    /// <summary>
    /// Remembers the page a link came from, so later requests to that host
    /// carry it as the Referer.
    ///
    /// Plenty of sites serve a media URL only to a request that says which page
    /// it was on, and 403 anything else -- which is exactly why a link works in
    /// the browser and not here.
    /// </summary>
    void RememberReferer(Uri url, Uri page);

    /// <summary>
    /// 强制校验: hash what is on disk again instead of trusting the resume
    /// record. Takes effect the next time the task runs, and starts it if it is
    /// not running. Does nothing for a task that is not a torrent.
    /// </summary>
    void Recheck(int id);

    /// <summary>Where a task's bytes are, or will be.</summary>
    string? PathOf(int id);

    /// <summary>校验 SHA-256. Returns the text for the 校验 row, or null if there was nothing to hash.</summary>
    Task<string?> VerifyAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Asks the server whether the file has changed. True when a 新版本 notice was raised.</summary>
    Task<bool> CheckForUpdateAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>重命名. Fails while a task is running, or if the new name is taken.</summary>
    bool Rename(int id, string newName);
}
