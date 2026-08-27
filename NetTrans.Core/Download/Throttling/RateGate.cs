namespace NetTrans.Download;

/// <summary>
/// Where a transfer asks permission before moving bytes.
///
/// HTTP transfers throttle inside their own read loop, where there is one
/// bucket and one thread. A torrent has neither: a dozen peer connections move
/// bytes at once, in both directions, so the cap has to live somewhere they can
/// all reach.
/// </summary>
public interface IRateGate
{
    /// <summary>
    /// Waits until <paramref name="bytes"/> may pass. Returns immediately when
    /// the cap is 不限.
    /// </summary>
    Task PassAsync(int bytes, CancellationToken cancellationToken);
}

/// <summary>
/// A gate over one or two token buckets -- the task's own cap and the global
/// one, whichever asks for the longer wait.
///
/// The buckets are taken from first and the waiting happens after: making a
/// peer hold anything across its delay would serialise the swarm into a single
/// file, which is the opposite of what many peers are for.
/// </summary>
public sealed class RateGate : IRateGate
{
    private readonly TokenBucket _own;
    private readonly TokenBucket? _shared;
    private readonly IClock _clock;

    public RateGate(double bytesPerSecond, IClock clock, TokenBucket? shared = null)
    {
        _clock = clock;
        _own = new TokenBucket(bytesPerSecond, clock.UtcNow);
        _shared = shared;
    }

    /// <summary>The cap in bytes per second; zero or less means 不限.</summary>
    public double BytesPerSecond
    {
        get => _own.BytesPerSecond;
        set => _own.BytesPerSecond = value;
    }

    public async Task PassAsync(int bytes, CancellationToken cancellationToken)
    {
        if (bytes <= 0) return;

        var now = _clock.UtcNow;
        var wait = _own.Take(bytes, now);

        if (_shared is not null)
        {
            var shared = _shared.Take(bytes, now);
            if (shared > wait) wait = shared;
        }

        if (wait > TimeSpan.Zero) await _clock.DelayAsync(wait, cancellationToken).ConfigureAwait(false);
    }
}
