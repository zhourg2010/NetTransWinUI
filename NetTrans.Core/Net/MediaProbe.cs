using System.Text.RegularExpressions;

namespace NetTrans.Net;

/// <summary>One playable source found on a page.</summary>
/// <param name="Url">Absolute URL of the stream or file.</param>
/// <param name="Quality">2160p / 1080p / 音轨 / 视频, as the sheet lists it.</param>
/// <param name="Format">MP4 / WEBM / M3U8 / M4A, upper-case.</param>
/// <param name="SizeBytes">Filled in by a probe; null until then, and for streams that have no single length.</param>
public sealed record MediaSource(Uri Url, string Quality, string Format, long? SizeBytes = null)
{
    /// <summary>A playlist rather than a file: it has to be resolved before it can be fetched.</summary>
    public bool IsPlaylist => Format is "M3U8" or "MPD";
}

/// <summary>
/// 视频嗅探. The portable build does not inject anything into a browser, so the
/// only thing to work with is the page's own markup: video and source elements,
/// the og:video card, and any media URL sitting in a script blob, which is where
/// most players keep theirs.
/// </summary>
public static partial class MediaProbe
{
    private static readonly string[] VideoExtensions = { "mp4", "webm", "mkv", "mov", "m3u8", "mpd", "flv", "ts" };
    private static readonly string[] AudioExtensions = { "m4a", "mp3", "aac", "opus", "flac", "wav", "ogg" };

    public static IReadOnlyList<MediaSource> Find(string html, Uri baseUrl)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = new List<MediaSource>();

        // Explicit elements first: they carry a label worth more than a guess
        // from the URL.
        foreach (Match element in SourceElementPattern().Matches(html))
        {
            string tag = element.Value;
            string? url = Attribute(tag, "src");
            if (url is null) continue;
            if (!TryResolve(url, baseUrl, out var absolute)) continue;
            if (!seen.Add(absolute.GetLeftPart(UriPartial.Query))) continue;

            string extension = ExtensionOf(absolute);
            string? label = Attribute(tag, "label") ?? Attribute(tag, "title") ?? Attribute(tag, "data-res");
            sources.Add(new MediaSource(absolute, label ?? QualityOf(absolute, extension), FormatOf(extension)));
        }

        // Then anything that merely looks like a media URL, wherever it sits.
        foreach (Match match in BareUrlPattern().Matches(html))
        {
            string url = match.Groups["url"].Value.Trim('"', '\'', '\\');
            if (!TryResolve(url, baseUrl, out var absolute)) continue;

            string extension = ExtensionOf(absolute);
            if (!VideoExtensions.Contains(extension) && !AudioExtensions.Contains(extension)) continue;
            if (!seen.Add(absolute.GetLeftPart(UriPartial.Query))) continue;

            sources.Add(new MediaSource(absolute, QualityOf(absolute, extension), FormatOf(extension)));
        }

        // Best quality first, audio last, which is the order the sheet lists.
        return sources
            .OrderByDescending(source => Rank(source.Quality))
            .ThenBy(source => source.Format, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The label the sheet shows: an explicit resolution, 音轨 for audio-only, else 视频.</summary>
    public static string QualityOf(Uri url, string extension)
    {
        if (AudioExtensions.Contains(extension)) return "音轨";

        string haystack = url.AbsoluteUri;

        if (ResolutionPattern().Match(haystack) is { Success: true } match) return match.Groups["h"].Value + "p";
        if (haystack.Contains("2160", StringComparison.Ordinal) || haystack.Contains("4k", StringComparison.OrdinalIgnoreCase)) return "2160p";
        if (haystack.Contains("1440", StringComparison.Ordinal)) return "1440p";
        if (haystack.Contains("1080", StringComparison.Ordinal)) return "1080p";
        if (haystack.Contains("720", StringComparison.Ordinal)) return "720p";
        if (haystack.Contains("480", StringComparison.Ordinal)) return "480p";

        return "视频";
    }

    public static string FormatOf(string extension) => extension.ToUpperInvariant();

    private static string ExtensionOf(Uri url)
    {
        string name = url.AbsolutePath.Split('/').LastOrDefault() ?? "";
        int dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1 ? name[(dot + 1)..].ToLowerInvariant() : "";
    }

    private static bool TryResolve(string value, Uri baseUrl, out Uri absolute)
    {
        absolute = baseUrl;
        if (value.Length == 0) return false;
        if (!Uri.TryCreate(baseUrl, value, out var candidate)) return false;
        if (candidate.Scheme != Uri.UriSchemeHttp && candidate.Scheme != Uri.UriSchemeHttps) return false;

        absolute = candidate;
        return true;
    }

    private static string? Attribute(string tag, string name)
    {
        var match = Regex.Match(tag, name + """\s*=\s*(?:"(?<v>[^"]*)"|'(?<v>[^']*)')""", RegexOptions.IgnoreCase);
        return match.Success && match.Groups["v"].Value.Length > 0 ? match.Groups["v"].Value : null;
    }

    /// <summary>Sorts 2160p above 1080p above 视频 above 音轨.</summary>
    private static int Rank(string quality)
    {
        if (quality == "音轨") return -1;
        if (quality == "视频") return 0;

        return int.TryParse(quality.TrimEnd('p'), out int height) ? height : 0;
    }

    [GeneratedRegex("""<(?:video|source|audio)\b[^>]*>""", RegexOptions.IgnoreCase)]
    private static partial Regex SourceElementPattern();

    [GeneratedRegex("""(?<url>(?:https?:)?//[^\s"'<>\\]+?\.(?:mp4|webm|mkv|mov|m3u8|mpd|flv|ts|m4a|mp3|aac|opus|flac|wav|ogg)(?:\?[^\s"'<>\\]*)?)""", RegexOptions.IgnoreCase)]
    private static partial Regex BareUrlPattern();

    [GeneratedRegex("""[_\-/.](?<h>2160|1440|1080|720|480|360)p?[_\-/.]""", RegexOptions.IgnoreCase)]
    private static partial Regex ResolutionPattern();
}

/// <summary>Finds the sources on a page and asks the server how big each one is.</summary>
public sealed class VideoSniffer
{
    private readonly IHttpTransport _transport;

    public VideoSniffer(IHttpTransport transport) => _transport = transport;

    public async Task<IReadOnlyList<MediaSource>> SniffAsync(Uri page, CancellationToken cancellationToken = default)
    {
        string html = await PageReader.ReadAsync(_transport, page, cancellationToken: cancellationToken).ConfigureAwait(false);
        var found = MediaProbe.Find(html, page);

        var sized = new List<MediaSource>(found.Count);

        foreach (var source in found)
        {
            // A playlist has no single length to ask about.
            if (source.IsPlaylist)
            {
                sized.Add(source);
                continue;
            }

            try
            {
                var info = await _transport.ProbeAsync(source.Url, cancellationToken).ConfigureAwait(false);
                sized.Add(source with { SizeBytes = info.HasKnownLength ? info.Length : null });
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Size is a nicety; a source that will not answer is still worth offering.
                sized.Add(source);
            }
        }

        return sized;
    }
}
