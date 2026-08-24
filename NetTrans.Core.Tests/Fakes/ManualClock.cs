using NetTrans.Download;

namespace NetTrans.Tests.Fakes;

/// <summary>
/// A clock the test drives. Delays return immediately but still advance time,
/// so rate limiting and backoff are exercised without the suite ever sleeping.
/// </summary>
public sealed class ManualClock : IClock
{
    public ManualClock(DateTimeOffset? start = null) =>
        UtcNow = start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public DateTimeOffset UtcNow { get; private set; }

    /// <summary>Everything the transfer was asked to wait for, in order.</summary>
    public List<TimeSpan> Delays { get; } = new();

    public TimeSpan TotalDelay => Delays.Aggregate(TimeSpan.Zero, (sum, delay) => sum + delay);

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (delay > TimeSpan.Zero)
        {
            Delays.Add(delay);
            UtcNow += delay;
        }

        return Task.CompletedTask;
    }

    public void Advance(TimeSpan by) => UtcNow += by;
}
