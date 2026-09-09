namespace NetTrans.Verify;

/// <summary>A place a published digest might be, and how much to trust a nameless line in it.</summary>
public sealed record ChecksumCandidate(Uri Url, bool SingleEntryWins);

/// <summary>
/// Where to look for a published digest, given the URL a file came from.
///
/// There is no hash service to ask -- the ones that exist want an API key and
/// index malware, not releases. What the web does have is a convention: a
/// sidecar next to the file, or one list for the whole directory. Both are
/// plain GETs against the server the file already came from, which is also why
/// this costs nothing in trust: it is the same origin.
/// </summary>
public static class ChecksumSources
{
    /// <summary>Strongest first, so a directory that publishes both is checked with SHA-256.</summary>
    private static readonly string[] Sidecars = { ".sha256", ".sha256sum", ".sha512", ".sha1", ".md5" };

    private static readonly string[] Siblings = { "SHA256SUMS", "SHA256SUMS.txt", "checksums.txt" };

    /// <summary>Eight requests is already a lot to spend on a nicety; nothing beyond this is tried.</summary>
    public const int MaxProbes = 8;

    public static IReadOnlyList<ChecksumCandidate> For(Uri url, string fileName)
    {
        var candidates = new List<ChecksumCandidate>();

        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps) return candidates;

        // The query string is how a CDN signs a link; a sidecar of that whole
        // thing is not a path anyone publishes.
        string path = url.GetLeftPart(UriPartial.Path);

        foreach (var suffix in Sidecars)
        {
            if (Uri.TryCreate(path + suffix, UriKind.Absolute, out var sidecar))
            {
                candidates.Add(new ChecksumCandidate(sidecar, SingleEntryWins: true));
            }
        }

        foreach (var name in Siblings)
        {
            if (Uri.TryCreate(new Uri(path), name, out var sibling))
            {
                candidates.Add(new ChecksumCandidate(sibling, SingleEntryWins: false));
            }
        }

        return candidates.Take(MaxProbes).ToList();
    }
}
