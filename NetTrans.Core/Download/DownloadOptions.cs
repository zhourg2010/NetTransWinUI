namespace NetTrans.Download;

/// <summary>Knobs for one transfer, most of which the 设置 sheet exposes.</summary>
/// <param name="Connections">How many ranges to fetch in parallel.</param>
/// <param name="MinimumSegmentLength">Below this, a file is not split further.</param>
/// <param name="BufferSize">Read buffer per connection.</param>
/// <param name="MaxRetries">Attempts per connection before the transfer fails.</param>
/// <param name="RetryDelay">Base backoff between attempts; it doubles each time.</param>
/// <param name="ResumeSaveInterval">How often the resume sidecar is rewritten.</param>
/// <param name="SpeedWindow">Window the throughput readouts average over.</param>
public sealed record DownloadOptions(
    int Connections = 8,
    long MinimumSegmentLength = 1024 * 1024,
    int BufferSize = 64 * 1024,
    int MaxRetries = 3,
    TimeSpan? RetryDelay = null,
    TimeSpan? ResumeSaveInterval = null,
    TimeSpan? SpeedWindow = null)
{
    public TimeSpan Backoff => RetryDelay ?? TimeSpan.FromSeconds(2);

    public TimeSpan ResumeInterval => ResumeSaveInterval ?? TimeSpan.FromSeconds(5);

    public TimeSpan Window => SpeedWindow ?? TimeSpan.FromSeconds(3);
}

/// <summary>How a transfer ended.</summary>
public enum JobOutcome
{
    Completed,
    Paused,
    Failed,
}
