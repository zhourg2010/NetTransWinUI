using System.Text.RegularExpressions;

namespace NetTrans.Net;

/// <summary>One link found on a page.</summary>
/// <param name="Url">Absolute URL.</param>
/// <param name="Name">The file name it would be saved as.</param>
/// <param name="Extension">Lower-case, without the dot; empty when there is none.</param>
/// <param name="SizeBytes">Filled in by <see cref="LinkSizer"/>; null until the server has been asked.</param>
public sealed record DiscoveredLink(Uri Url, string Name, string Extension, long? SizeBytes = null)
{
    /// <summary>Looks like another page rather than a file to download.</summary>
    public bool IsPage => Extension is "" or "htm" or "html" or "php" or "asp" or "aspx" or "jsp";
}

/// <summary>
/// Pulls hrefs and srcs out of markup. Deliberately a scanner rather than a
/// parser: 批量下载 needs the links on a page, not a DOM, and a regex over the
/// attribute is both enough and immune to the malformed HTML that real
/// download index pages are full of.
/// </summary>
public static partial class LinkExtractor
{
    public static IReadOnlyList<DiscoveredLink> Extract(string html, Uri baseUrl)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var links = new List<DiscoveredLink>();

        foreach (Match match in AttributePattern().Matches(html))
        {
            string value = match.Groups["url"].Value.Trim();
            if (value.Length == 0) continue;
            if (value.StartsWith('#')) continue;
            if (value.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) continue;
            if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            if (value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) continue;

            if (!Uri.TryCreate(baseUrl, value, out var absolute)) continue;
            if (absolute.Scheme != Uri.UriSchemeHttp && absolute.Scheme != Uri.UriSchemeHttps) continue;

            // Ignore the fragment when deduplicating: #section links are the
            // same document.
            string key = absolute.GetLeftPart(UriPartial.Query);
            if (!seen.Add(key)) continue;

            links.Add(Describe(absolute));
        }

        return links;
    }

    /// <summary>Splits a URL into the name it would be saved as and its extension.</summary>
    public static DiscoveredLink Describe(Uri url)
    {
        string path = url.AbsolutePath.TrimEnd('/');
        string name = Uri.UnescapeDataString(path.Split('/').LastOrDefault() ?? "");

        int dot = name.LastIndexOf('.');
        string extension = dot > 0 && dot < name.Length - 1 ? name[(dot + 1)..].ToLowerInvariant() : "";

        return new DiscoveredLink(url, name, extension);
    }

    /// <summary>Reads the 后缀筛选 field: "pdf; zip; png" or "pdf,zip".</summary>
    public static IReadOnlyList<string> ParseExtensions(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return Array.Empty<string>();

        return filter
            .Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.TrimStart('.').ToLowerInvariant())
            .Where(part => part.Length > 0)
            .Distinct()
            .ToList();
    }

    [GeneratedRegex("""(?:href|src)\s*=\s*(?:"(?<url>[^"]*)"|'(?<url>[^']*)'|(?<url>[^\s>'"]+))""", RegexOptions.IgnoreCase)]
    private static partial Regex AttributePattern();
}
