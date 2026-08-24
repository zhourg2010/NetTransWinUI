namespace NetTrans.Net;

/// <summary>
/// Asks the server how big each crawled link is, so 批量下载 can show a size per
/// row and honour 最小文件.
///
/// One probe per link, a few at a time: a page of two hundred links would
/// otherwise open two hundred connections at once, which is how a crawler gets
/// a site to block you.
/// </summary>
public static class LinkSizer
{
    public const int DefaultConcurrency = 6;

    public static async Task<IReadOnlyList<DiscoveredLink>> MeasureAsync(
        IHttpTransport transport,
        IReadOnlyList<DiscoveredLink> links,
        int concurrency = DefaultConcurrency,
        CancellationToken cancellationToken = default)
    {
        if (links.Count == 0) return links;

        var sized = new DiscoveredLink[links.Count];
        using var gate = new SemaphoreSlim(Math.Max(1, concurrency));

        var probes = links.Select(async (link, index) =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                var info = await transport.ProbeAsync(link.Url, cancellationToken).ConfigureAwait(false);
                sized[index] = link with { SizeBytes = info.HasKnownLength ? info.Length : null };
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // A link that will not answer is still a link worth offering.
                sized[index] = link;
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(probes).ConfigureAwait(false);
        return sized;
    }

    /// <summary>The 最小文件 dropdown. Links of unknown size are kept -- dropping them would hide real files.</summary>
    public static IReadOnlyList<DiscoveredLink> AtLeast(IReadOnlyList<DiscoveredLink> links, long minimumBytes) =>
        minimumBytes <= 0
            ? links
            : links.Where(link => link.SizeBytes is null || link.SizeBytes >= minimumBytes).ToList();

    /// <summary>Reads the 最小文件 labels: 不限 / 100 KB / 1 MB / 10 MB.</summary>
    public static long ParseMinimum(string? label) => (long)Download.SpeedLimits.Parse(label);
}
