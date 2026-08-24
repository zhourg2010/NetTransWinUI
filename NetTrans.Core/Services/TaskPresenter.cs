using NetTrans.Models;

namespace NetTrans.Services;

/// <summary>
/// Every string the design derives from a task, in one place. These are the
/// handoff's own expressions from Row() and Inspector() in mini-ios2.jsx, so
/// they can be checked against values generated from that source.
/// </summary>
public static class TaskPresenter
{
    /// <summary>STATE_CN.</summary>
    public static string StatusText(DownloadStatus status) => status switch
    {
        DownloadStatus.Downloading => "下载中",
        DownloadStatus.Paused => "已暂停",
        DownloadStatus.Completed => "已完成",
        DownloadStatus.Error => "出错",
        _ => "排队中",
    };

    /// <summary>Completion as a 0..1 fraction, clamped like the prototype's Math.min(100, ...).</summary>
    public static double Fraction(long done, long size) =>
        size <= 0 ? 0 : Math.Clamp(done / (double)size, 0, 1);

    public static double Percent(long done, long size) => Fraction(done, size) * 100;

    /// <summary>`.rsub`: the second line of a row, which says something different in every state.</summary>
    public static string SubText(DownloadItem task) => task.Status switch
    {
        DownloadStatus.Completed => task.Checksum is { Length: > 0 } checksum
            ? $"{FormatHelpers.Bytes(task.Size)} · {checksum}"
            : FormatHelpers.Bytes(task.Size),
        DownloadStatus.Error => $"{task.ErrorMessage} · 已重试 {task.Retries} 次",
        DownloadStatus.Queued => "排队中，等待空闲通道",
        DownloadStatus.Paused => $"已暂停 · {FormatHelpers.Bytes(task.Done)} / {FormatHelpers.Bytes(task.Size)}",
        _ => $"{FormatHelpers.Speed(task.Speed)} · {FormatHelpers.Eta(task.Size - task.Done, task.Speed)}",
    };

    /// <summary>`.rpct` / `.badge`: 完成, 失败, an em dash while queued, or the percentage.</summary>
    public static string TrailingText(DownloadItem task) => task.Status switch
    {
        DownloadStatus.Completed => "完成",
        DownloadStatus.Error => "失败",
        DownloadStatus.Queued => "—",
        _ => PercentText(task.Done, task.Size) + "%",
    };

    /// <summary>The prototype's pct.toFixed(0): halves away from zero.</summary>
    public static string PercentText(long done, long size) =>
        Math.Round(Percent(done, size), MidpointRounding.AwayFromZero).ToString("F0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The track is hidden for finished and not-yet-started tasks.</summary>
    public static bool ShowProgress(DownloadStatus status) =>
        status is not (DownloadStatus.Completed or DownloadStatus.Queued);

    /// <summary>`.ring__s`: live speed, falling back to the state name when stalled.</summary>
    public static string RingSubtitle(DownloadItem task)
    {
        string speed = FormatHelpers.Speed(task.Speed);
        return speed.Length > 0 ? speed : StatusText(task.Status);
    }

    /// <summary>`.ringcap`: 已接收 / 总量 · 剩余时间.</summary>
    public static string RingCaption(DownloadItem task) =>
        $"{FormatHelpers.Bytes(task.Done)} / {FormatHelpers.Bytes(task.Size)} · " +
        (task.Status == DownloadStatus.Completed ? "已完成" : FormatHelpers.Eta(task.Size - task.Done, task.Speed));

    /// <summary>`.newver__m span`: 版本名 · 体积 · 发布于 时间.</summary>
    public static string NewVersionSubtitle(NewVersionInfo version) =>
        $"{version.Version} · {FormatHelpers.Bytes(version.Size)} · 发布于 {version.Published}";

    /// <summary>The hover action's label: 暂停 while running, 重试 after a failure, otherwise 继续.</summary>
    public static string ToggleLabel(DownloadStatus status) => status switch
    {
        DownloadStatus.Downloading => "暂停",
        DownloadStatus.Error => "重试",
        _ => "继续",
    };
}
