namespace NetTrans.Download;

/// <summary>
/// The 全局限速 and per-task 速度上限 dropdowns, as a token bucket. A rate of
/// zero or less means 不限 and never delays.
///
/// Thread-safe, because the global bucket is shared: several HTTP transfers and
/// every peer of a torrent take from the same one at the same time, and the
/// refill timestamp is not a value that survives being written by two threads
/// at once.
/// </summary>
public sealed class TokenBucket
{
    private readonly double _burstSeconds;
    private readonly object _gate = new();
    private double _tokens;
    private DateTimeOffset _lastRefill;
    private double _bytesPerSecond;

    public TokenBucket(double bytesPerSecond, DateTimeOffset now, double burstSeconds = 1.0)
    {
        if (burstSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(burstSeconds), "The burst must be positive.");

        _bytesPerSecond = bytesPerSecond;
        _burstSeconds = burstSeconds;
        _lastRefill = now;
        _tokens = IsUnlimited ? 0 : bytesPerSecond * burstSeconds;
    }

    public bool IsUnlimited
    {
        get { lock (_gate) return _bytesPerSecond <= 0; }
    }

    /// <summary>Capacity, in bytes per second. Changing it takes effect on the next take.</summary>
    public double BytesPerSecond
    {
        get { lock (_gate) return _bytesPerSecond; }

        set
        {
            lock (_gate)
            {
                _bytesPerSecond = value;
                if (value <= 0) return;

                _tokens = Math.Min(_tokens, value * _burstSeconds);
            }
        }
    }

    /// <summary>
    /// Consumes <paramref name="bytes"/> and returns how long the caller has to
    /// wait first. A single read larger than the whole burst is allowed through
    /// rather than deadlocking; it just borrows against the next window.
    /// </summary>
    public TimeSpan Take(long bytes, DateTimeOffset now)
    {
        if (bytes <= 0) return TimeSpan.Zero;

        lock (_gate)
        {
            if (_bytesPerSecond <= 0) return TimeSpan.Zero;

            Refill(now);
            _tokens -= bytes;

            if (_tokens >= 0) return TimeSpan.Zero;
            return TimeSpan.FromSeconds(-_tokens / _bytesPerSecond);
        }
    }

    private void Refill(DateTimeOffset now)
    {
        double elapsed = (now - _lastRefill).TotalSeconds;
        if (elapsed <= 0) return;

        _lastRefill = now;
        _tokens = Math.Min(_tokens + elapsed * _bytesPerSecond, _bytesPerSecond * _burstSeconds);
    }
}
