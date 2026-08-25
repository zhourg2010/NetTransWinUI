namespace NetTrans.Torrent;

/// <summary>
/// Announces to every tracker a torrent lists and pools what comes back.
///
/// Trackers are unreliable in a way that has to be designed around rather than
/// reported: a public torrent routinely lists a dozen, half of them long dead.
/// So a tracker that fails is noted and skipped, and the announce as a whole
/// only fails when every single one did.
/// </summary>
public sealed class TrackerPool
{
    private readonly IReadOnlyList<ITrackerClient> _clients;
    private readonly Dictionary<string, string> _failures = new(StringComparer.Ordinal);

    public TrackerPool(params ITrackerClient[] clients) => _clients = clients;

    /// <summary>Why each tracker that failed did, for the inspector's log.</summary>
    public IReadOnlyDictionary<string, string> Failures
    {
        get
        {
            lock (_failures) return new Dictionary<string, string>(_failures, StringComparer.Ordinal);
        }
    }

    /// <summary>How long the shortest interval any tracker asked for is.</summary>
    public TimeSpan Interval { get; private set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Announces to all of them at once and returns the union of the peers.
    /// </summary>
    /// <exception cref="TrackerException">Every tracker failed, with the first reason.</exception>
    public async Task<AnnounceResponse> AnnounceAsync(
        IReadOnlyList<Uri> trackers,
        AnnounceRequest request,
        CancellationToken cancellationToken)
    {
        var usable = trackers.Where(tracker => _clients.Any(client => client.CanAnnounceTo(tracker))).ToList();

        if (usable.Count == 0) throw new TrackerException("没有可用的 tracker。");

        var results = await Task.WhenAll(usable.Select(tracker => AnnounceOneAsync(tracker, request, cancellationToken)))
            .ConfigureAwait(false);

        var answered = results.Where(result => result is not null).Select(result => result!).ToList();

        if (answered.Count == 0)
        {
            lock (_failures)
            {
                throw new TrackerException(_failures.Count > 0
                    ? $"所有 tracker 都失败了，例如：{_failures.Values.First()}"
                    : "所有 tracker 都失败了。");
            }
        }

        Interval = answered.Min(result => result.Interval);

        return new AnnounceResponse(
            TrackerProtocol.Distinct(answered.SelectMany(result => result.Peers)),
            Interval,
            answered.Max(result => result.Seeders),
            answered.Max(result => result.Leechers));
    }

    private async Task<AnnounceResponse?> AnnounceOneAsync(
        Uri tracker,
        AnnounceRequest request,
        CancellationToken cancellationToken)
    {
        var client = _clients.First(client => client.CanAnnounceTo(tracker));

        try
        {
            var response = await client.AnnounceAsync(tracker, request, cancellationToken).ConfigureAwait(false);

            lock (_failures) _failures.Remove(tracker.AbsoluteUri);

            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // One dead tracker out of a dozen is the normal state of a public
            // torrent, not something to fail the download over.
            lock (_failures) _failures[tracker.AbsoluteUri] = exception.Message;

            return null;
        }
    }
}
