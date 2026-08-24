namespace NetTrans.Net;

/// <summary>The 批量下载 sheet's form, as options.</summary>
/// <param name="Depth">0 = 仅本页; 1..3 follow that many levels of same-site pages.</param>
/// <param name="SameSiteOnly">仅限本站.</param>
/// <param name="Extensions">后缀筛选; empty means every non-page link.</param>
/// <param name="MaxResults">Stops runaway crawls of a site that links to itself endlessly.</param>
/// <param name="MaxPages">How many pages may be fetched in total.</param>
public sealed record CrawlOptions(
    int Depth = 1,
    bool SameSiteOnly = true,
    IReadOnlyList<string>? Extensions = null,
    int MaxResults = 200,
    int MaxPages = 40);

/// <summary>
/// 批量下载: fetch a page, take every downloadable link off it, and optionally
/// follow same-site pages a level or two deeper.
/// </summary>
public sealed class PageCrawler
{
    private readonly IHttpTransport _transport;

    public PageCrawler(IHttpTransport transport) => _transport = transport;

    /// <summary>Pages that could not be fetched, so the sheet can say so rather than silently finding less.</summary>
    public List<string> Failures { get; } = new();

    public async Task<IReadOnlyList<DiscoveredLink>> CrawlAsync(
        Uri start,
        CrawlOptions options,
        CancellationToken cancellationToken = default)
    {
        var wanted = options.Extensions ?? Array.Empty<string>();
        var results = new List<DiscoveredLink>();
        var foundUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Key(start) };

        var frontier = new Queue<(Uri Url, int Depth)>();
        frontier.Enqueue((start, 0));

        while (frontier.Count > 0 && results.Count < options.MaxResults && visited.Count <= options.MaxPages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (page, depth) = frontier.Dequeue();

            string html;
            try
            {
                html = await PageReader.ReadAsync(_transport, page, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                Failures.Add($"{page}: {exception.Message}");
                continue;
            }

            foreach (var link in LinkExtractor.Extract(html, page))
            {
                if (options.SameSiteOnly && !SameSite(link.Url, start)) continue;

                if (link.IsPage)
                {
                    // Pages are only worth following while there is depth left.
                    if (depth >= options.Depth) continue;
                    if (visited.Count >= options.MaxPages) continue;
                    if (visited.Add(Key(link.Url))) frontier.Enqueue((link.Url, depth + 1));
                    continue;
                }

                if (wanted.Count > 0 && !wanted.Contains(link.Extension)) continue;
                if (!foundUrls.Add(Key(link.Url))) continue;

                results.Add(link);
                if (results.Count >= options.MaxResults) break;
            }
        }

        return results;
    }

    private static bool SameSite(Uri url, Uri origin) =>
        string.Equals(url.Host, origin.Host, StringComparison.OrdinalIgnoreCase);

    private static string Key(Uri url) => url.GetLeftPart(UriPartial.Query);
}
