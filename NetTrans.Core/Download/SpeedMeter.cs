namespace NetTrans.Download;

/// <summary>
/// Throughput over a sliding window. The design shows a live rate on every row,
/// in the ring, per connection and in the island, and all of them come from one
/// of these.
/// </summary>
public sealed class SpeedMeter
{
    private readonly TimeSpan _window;
    private readonly Queue<(DateTimeOffset At, long Bytes)> _samples = new();
    private long _windowBytes;

    public SpeedMeter(TimeSpan window)
    {
        if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window), "The window must be positive.");
        _window = window;
    }

    /// <summary>Total bytes ever recorded, which is what the row's 已接收 shows.</summary>
    public long Total { get; private set; }

    public void Record(long bytes, DateTimeOffset now)
    {
        if (bytes <= 0) return;

        Total += bytes;
        _windowBytes += bytes;
        _samples.Enqueue((now, bytes));
        Trim(now);
    }

    /// <summary>
    /// Bytes per second across the window. Measured over the span the samples
    /// actually cover rather than the nominal window, so a transfer that has
    /// only just started does not read as artificially slow.
    /// </summary>
    public double BytesPerSecond(DateTimeOffset now)
    {
        Trim(now);
        if (_samples.Count == 0) return 0;

        var oldest = _samples.Peek().At;
        double seconds = (now - oldest).TotalSeconds;

        // Everything arrived in the same instant: report over the shortest span
        // we can distinguish rather than dividing by zero.
        if (seconds <= 0.001) seconds = 0.001;

        return _windowBytes / seconds;
    }

    /// <summary>Drops the meter to zero, for a task that has been paused.</summary>
    public void Reset()
    {
        _samples.Clear();
        _windowBytes = 0;
    }

    private void Trim(DateTimeOffset now)
    {
        var cutoff = now - _window;
        while (_samples.Count > 0 && _samples.Peek().At < cutoff)
        {
            _windowBytes -= _samples.Dequeue().Bytes;
        }

        if (_windowBytes < 0) _windowBytes = 0;
    }
}
