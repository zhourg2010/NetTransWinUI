namespace NetTrans.Models;

/// <summary>
/// Plain data record for one transfer -- the handoff's task object, field for
/// field. Sizes and speeds are bytes and bytes/second here rather than the
/// prototype's MB and KB/s; FormatHelpers renders them back to the same
/// strings the design specifies.
/// </summary>
public sealed class DownloadItem
{
    public required int Id { get; init; }

    /// <summary>Set from Content-Disposition once the transfer has probed, if the request did not name the file.</summary>
    public required string Name { get; set; }

    public required string Host { get; init; }
    public required FileKind Kind { get; init; }

    /// <summary>Set from the probe; a request only carries an estimate until then.</summary>
    public required long Size { get; set; }

    /// <summary>soft | video | doc | music | bt -- the category chips.</summary>
    public required string Category { get; init; }

    /// <summary>The tile colour. Defaults per kind, but the seed data overrides it per task.</summary>
    public string Tint { get; init; } = "#8E8E93";

    public string Url { get; set; } = "";
    public string SavePath { get; init; } = @"D:\Downloads";

    public long Done { get; set; }

    /// <summary>
    /// Bytes served to other peers. Zero for everything but a torrent, where it
    /// is the number a tracker measures a share ratio with.
    /// </summary>
    public long Uploaded { get; set; }
    public double Speed { get; set; }
    public DownloadStatus Status { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>Live connection count (`conns`), not the configured segment count.</summary>
    public int Connections { get; set; }

    /// <summary>How many connections the task was created with, which is what the file gets split into.</summary>
    public int RequestedConnections { get; init; } = 8;

    public TaskPriority Priority { get; set; } = TaskPriority.Normal;

    /// <summary>Per-task speed cap as shown in the dropdown ("不限", "1 MB/s", ...).</summary>
    public string SpeedLimit { get; set; } = "不限";

    public int Retries { get; set; }
    public string AddedAt { get; set; } = "";
    /// <summary>The 校验 row's state text ("SHA-256 待校验" / "SHA-256 已校验").</summary>
    public string? Checksum { get; set; }

    /// <summary>The hash actually computed, once 校验 SHA-256 has run.</summary>
    public string? Sha256 { get; set; }

    /// <summary>Validators recorded when the transfer probed, so a later 新版本 check has something to compare against.</summary>
    public string? SourceETag { get; set; }

    public string? SourceLastModified { get; set; }

    /// <summary>BT-only fields; null on plain HTTP tasks, which switches the inspector's row from 节点/种子 to 校验.</summary>
    public int? Peers { get; set; }
    public int? Seeds { get; set; }
    public double? Ratio { get; set; }
    public double UploadSpeed { get; set; }

    public NewVersionInfo? NewerVersion { get; set; }

    public List<LogEntry> Log { get; } = new();

    /// <summary>0 = pending, 1 = complete, 2 = in flight -- the 96-cell 分块 grid.</summary>
    public int[] Blocks { get; set; } = System.Array.Empty<int>();

    /// <summary>Per-connection speeds in bytes/s, one entry per live connection.</summary>
    public double[] ConnectionSpeeds { get; set; } = System.Array.Empty<double>();

    /// <summary>Recent throughput samples for the inspector's session bar chart.</summary>
    public double[] SpeedHistory { get; set; } = System.Array.Empty<double>();

    public double PeakSpeed { get; set; }
}
