using NetTrans.Download;

namespace NetTrans.Net;

/// <summary>What one mirror managed.</summary>
/// <param name="Name">How the mirror is labelled in the report.</param>
/// <param name="Bytes">Bytes actually read before the budget or the time limit ran out.</param>
/// <param name="Connect">From asking for the file to the stream being open -- DNS, TCP, TLS and the server thinking. Distance, not bandwidth.</param>
/// <param name="Elapsed">How long the whole read took.</param>
/// <param name="Error">Why it stopped early, when it did.</param>
public sealed record SpeedSample(
    string Name,
    Uri Url,
    long Bytes,
    TimeSpan Connect,
    TimeSpan Elapsed,
    string? Error = null)
{
    public bool Ok => Error is null && Bytes > 0;

    /// <summary>
    /// Throughput over the transfer itself, with the connection setup taken
    /// out: a mirror on the other side of the planet answers slowly and then
    /// streams fast, and one number covering both hides which half is the
    /// problem. Connect is reported separately for exactly that reason.
    /// </summary>
    public double BytesPerSecond
    {
        get
        {
            var moving = Elapsed - Connect;
            return Bytes > 0 && moving > TimeSpan.Zero ? Bytes / moving.TotalSeconds : 0;
        }
    }
}

/// <summary>
/// 测速: read a fixed number of bytes off a URL and time it.
///
/// It goes through <see cref="IHttpTransport"/> and <see cref="IClock"/> like
/// everything else, so the same code measures a real mirror in a live test and
/// a scripted one in a unit test.
/// </summary>
public static class SpeedProbe
{
    /// <summary>Enough to get past TCP slow start without downloading an ISO.</summary>
    public const long DefaultBudget = 8 * 1024 * 1024;

    public static async Task<SpeedSample> MeasureAsync(
        IHttpTransport transport,
        string name,
        Uri url,
        long budget,
        TimeSpan limit,
        IClock clock,
        CancellationToken cancellationToken = default)
    {
        var started = clock.UtcNow;
        var connect = TimeSpan.Zero;
        long read = 0;

        try
        {
            // A ranged request, because that is how a transfer reads: asking
            // for the whole of a 3 GB file and hanging up after 8 MB measures
            // the mirror's patience as much as its speed.
            await using var stream = await transport
                .OpenAsync(url, 0, budget - 1, cancellationToken)
                .ConfigureAwait(false);

            connect = clock.UtcNow - started;

            var buffer = new byte[128 * 1024];

            while (read < budget)
            {
                int wanted = (int)Math.Min(buffer.Length, budget - read);
                int got = await stream.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
                if (got == 0) break;

                read += got;

                // The budget is what makes mirrors comparable; the limit is
                // what stops one slow mirror from owning the run.
                if (clock.UtcNow - started >= limit) break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure)
        {
            return new SpeedSample(name, url, read, connect, clock.UtcNow - started, Describe(failure));
        }

        return new SpeedSample(name, url, read, connect, clock.UtcNow - started);
    }

    /// <summary>
    /// Measures each mirror in turn.
    ///
    /// In turn, not at once: mirrors are being compared over one link, and
    /// several transfers sharing it would each measure a fraction of it and
    /// rank by who got scheduled first.
    /// </summary>
    public static async Task<IReadOnlyList<SpeedSample>> MeasureAllAsync(
        IHttpTransport transport,
        IEnumerable<(string Name, Uri Url)> mirrors,
        long budget,
        TimeSpan limit,
        IClock clock,
        IProgress<SpeedSample>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var samples = new List<SpeedSample>();

        foreach (var (name, url) in mirrors)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sample = await MeasureAsync(transport, name, url, budget, limit, clock, cancellationToken).ConfigureAwait(false);
            samples.Add(sample);
            progress?.Report(sample);
        }

        return samples;
    }

    /// <summary>One line, since a timeout and a refused connection read the same otherwise.</summary>
    private static string Describe(Exception failure) => failure switch
    {
        TaskCanceledException or OperationCanceledException => "超时",
        HttpRequestException http => http.StatusCode is { } status ? $"HTTP {(int)status}" : "连不上",
        IOException => "连接中断",
        _ => failure.GetType().Name,
    };
}
