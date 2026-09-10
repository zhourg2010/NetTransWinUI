namespace NetTrans.Models;

/// <summary>
/// Everything the 设置 sheet exposes, plus the shell toggles that live in the
/// tray menu. Persisted next to the executable in portable mode -- the sheet's
/// own footnote promises no registry writes.
/// </summary>
public sealed class AppSettings
{
    // 下载
    public string DefaultSavePath { get; set; } = @"D:\Downloads";
    public bool FoldersByCategory { get; set; } = true;
    public int MaxSimultaneousDownloads { get; set; } = 3;

    /// <summary>Global cap as shown in the dropdown ("不限", "4 MB/s", ...).</summary>
    public string GlobalSpeedLimit { get; set; } = "不限";
    public bool UncappedAtNight { get; set; }

    // 队列与计划
    public string OffPeakStart { get; set; } = "23:00";
    public string OffPeakEnd { get; set; } = "07:00";
    public string RetryPolicy { get; set; } = "3 次";

    /// <summary>无操作 | 退出程序 | 休眠 | 关机</summary>
    public string WhenAllComplete { get; set; } = "无操作";

    /// <summary>系统代理 | 不使用代理 | host:port. Read once, when the transport is built.</summary>
    public string Proxy { get; set; } = "系统代理";

    // 行为
    public bool WatchClipboard { get; set; } = true;
    public bool NotifyOnCompletion { get; set; } = true;
    public bool VerifyChecksums { get; set; } = true;

    /// <summary>哈希库: remember what finished files hash to, and check the next download against it.</summary>
    public bool UseHashDatabase { get; set; } = true;

    /// <summary>联网核对: look for a digest the server published next to the file (.sha256, SHA256SUMS).</summary>
    public bool CheckChecksumsOnline { get; set; } = true;

    public bool ScanOnCompletion { get; set; } = true;
    public bool EdgeHide { get; set; } = true;
    public string BossKey { get; set; } = "Ctrl + Alt + H";

    // shell state the tray menu and 显示与排序 menu remember
    public bool ShowIsland { get; set; } = true;
    public bool ShowInspector { get; set; } = true;
    public bool DenseRows { get; set; }
    public string SortKey { get; set; } = "added";
    public string SortDirection { get; set; } = "asc";
    /// <summary>
    /// light | dark | auto. 浅色 by default, because the handoff is light: it
    /// defines one palette and no dark one, so 跟随系统 on a machine set to dark
    /// shows colours the design never specified.
    /// </summary>
    public string Theme { get; set; } = "light";
}
