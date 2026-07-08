namespace NetTrans.Models;

/// <summary>
/// Plain data record for one transfer. Mutable fields (Done/Speed/Status/...)
/// are the ones StubDownloadEngine ticks; DownloadItemViewModel wraps this and
/// exposes the observable/bindable surface the views consume.
/// </summary>
public sealed class DownloadItem
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required string Host { get; init; }
    public required FileKind Kind { get; init; }
    public required long Size { get; init; }
    public required string Category { get; init; }
    public string SavePath { get; init; } = @"C:\Users\you\Downloads";
    public int SegmentCount { get; init; } = 8;
    public string Url { get; set; } = "";
    public bool IsScheduled { get; set; }

    public long Done { get; set; }
    public double Speed { get; set; }
    public DownloadStatus Status { get; set; }
    public string? ErrorMessage { get; set; }
    public string StartedAt { get; set; } = "";
    public double DownloadElapsedSeconds { get; set; }

    // Completed/history-only
    public string? CompletedWhen { get; set; }
    public string? Duration { get; set; }

    public double[] SegmentProgress { get; set; } = System.Array.Empty<double>();
    public double[] SpeedHistory { get; set; } = System.Array.Empty<double>();

    public IReadOnlyList<MirrorSource> Mirrors { get; init; } = System.Array.Empty<MirrorSource>();
}
