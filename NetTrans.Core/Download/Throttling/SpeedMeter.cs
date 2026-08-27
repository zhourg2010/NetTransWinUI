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
    private DateTimeOffset? _started;

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

        _started ??= now;
        Total += bytes;
        _windowBytes += bytes;
        _samples.Enqueue((now, bytes));
        Trim(now);
    }

    /// <summary>
    /// Bytes per second across the window.
    ///
    /// Divided by how long the meter has been running, capped at the window --
    /// not by the span between the surviving samples. Measuring between samples
    /// looks right until only one is left inside the window, at which point the
    /// span collapses towards zero and the rate explodes; that is what made a
    /// row briefly read hundreds of MB/s after a quiet moment.
    /// </summary>
    public double BytesPerSecond(DateTimeOffset now)
    {
        Trim(now);
        if (_samples.Count == 0 || _started is not { } started) return 0;

        double elapsed = Math.Min(_window.TotalSeconds, (now - started).TotalSeconds);

        // Everything so far arrived in the same instant; report over the
        // shortest span worth trusting rather than dividing by ~zero.
        if (elapsed < 0.05) elapsed = 0.05;

        return _windowBytes / elapsed;
    }

    /// <summary>Drops the meter to zero, for a task that has been paused.</summary>
    public void Reset()
    {
        _samples.Clear();
        _windowBytes = 0;
        _started = null;
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
