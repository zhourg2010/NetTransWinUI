using System.Net;

namespace NetTrans.Net;

/// <summary>A file in the same directory that looks like a newer edition of the dead one.</summary>
/// <param name="Url">Where the replacement lives.</param>
/// <param name="Name">Its file name, which is what a person recognises.</param>
public sealed record Replacement(Uri Url, string Name);

/// <summary>
/// 源地址失效了，同目录里找一下.
///
/// The single most common way a download fails is not a bug in the downloader:
/// it is a link that was fine when it was copied and is gone now. Publishers
/// delete the old edition the day the new one ships -- Canonical removes a
/// point release from releases.ubuntu.com as soon as the next one is out, so
/// ubuntu-24.04.2-desktop-amd64.iso answers 404 and ubuntu-24.04.4-desktop-amd64.iso
/// is sitting right beside it.
///
/// A 404 that says only "服务器返回 404" leaves the person to work that out.
/// This turns it into "同目录里有 …-24.04.4-…". It is a hint attached to the
/// error, never an automatic substitution: swapping in a different file behind
/// someone's back is how you hand them something they did not ask for.
/// </summary>
public static class ReplacementLink
{
    /// <summary>Directory listings can be long; this is what will be read of one.</summary>
    private const int MaxListingBytes = 512 * 1024;

    /// <summary>
    /// Statuses that mean "not here", as opposed to "not now". A 500 or a 429
    /// is the server having a bad day and the link is probably fine.
    /// </summary>
    public static bool WorthLooking(HttpStatusCode status) =>
        status is HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.Forbidden;

    public static bool WorthLooking(Exception exception) =>
        exception is HttpRequestException { StatusCode: { } status } && WorthLooking(status);

    /// <summary>
    /// Reads the directory the dead link sits in and picks the best candidate.
    /// Returns null for anything it is not sure about -- a wrong guess here is
    /// worse than no guess.
    /// </summary>
    public static async Task<Replacement?> FindAsync(
        IHttpTransport transport,
        Uri dead,
        CancellationToken cancellationToken = default)
    {
        if (dead.Scheme != Uri.UriSchemeHttp && dead.Scheme != Uri.UriSchemeHttps) return null;

        var directory = new Uri(dead, ".");
        if (directory == dead) return null;

        string listing;
        try
        {
            listing = await PageReader.ReadAsync(transport, directory, MaxListingBytes, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // No listing, no suggestion. A server that will not index itself is
            // the common case, not an error worth reporting.
            return null;
        }

        return Pick(dead, LinkExtractor.Extract(listing, directory));
    }

    /// <summary>
    /// The decision itself, with no network in it.
    ///
    /// Two names are the same file in different editions when they are
    /// character-for-character equal outside their digits: ubuntu-24.04.2-desktop-amd64.iso
    /// and ubuntu-24.04.4-desktop-amd64.iso both reduce to ubuntu-#.#.#-desktop-amd64.iso,
    /// while ubuntu-24.04.4-live-server-amd64.iso does not. That is a stricter
    /// test than a similarity score, and being strict is the point: the only
    /// suggestion worth making is one that is obviously right.
    /// </summary>
    public static Replacement? Pick(Uri dead, IEnumerable<DiscoveredLink> siblings)
    {
        var gone = LinkExtractor.Describe(dead);
        if (gone.Name.Length == 0) return null;

        var (shape, version) = Shape(gone.Name);

        // A name with no digits in it has no edition to compare, so anything
        // "similar" would be a guess.
        if (version.Count == 0) return null;

        Replacement? best = null;
        IReadOnlyList<long> bestVersion = version;

        foreach (var link in siblings)
        {
            if (link.Name.Length == 0) continue;
            if (!string.Equals(link.Extension, gone.Extension, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(link.Name, gone.Name, StringComparison.OrdinalIgnoreCase)) continue;

            var (candidateShape, candidateVersion) = Shape(link.Name);
            if (!string.Equals(candidateShape, shape, StringComparison.Ordinal)) continue;

            // Strictly newer, or this is not a replacement -- an older edition
            // beside a dead one is not what anybody is looking for.
            if (Compare(candidateVersion, bestVersion) <= 0) continue;

            best = new Replacement(link.Url, link.Name);
            bestVersion = candidateVersion;
        }

        return best;
    }

    /// <summary>
    /// The name with every run of digits replaced by '#', plus those runs as
    /// numbers. Case-folded, because a directory index is not consistent about it.
    /// </summary>
    private static (string Shape, IReadOnlyList<long> Version) Shape(string name)
    {
        var shape = new System.Text.StringBuilder(name.Length);
        var numbers = new List<long>();

        for (int i = 0; i < name.Length;)
        {
            if (!char.IsAsciiDigit(name[i]))
            {
                shape.Append(char.ToLowerInvariant(name[i]));
                i++;
                continue;
            }

            int start = i;
            while (i < name.Length && char.IsAsciiDigit(name[i])) i++;

            shape.Append('#');

            // A run long enough to be a build stamp rather than a version still
            // orders correctly as a number, until it does not fit one -- at
            // which point ordering it at all would be made up.
            numbers.Add(long.TryParse(name[start..i], out long value) ? value : 0);
        }

        return (shape.ToString(), numbers);
    }

    /// <summary>Compares two version tuples left to right. Equal shapes always give equal lengths.</summary>
    private static int Compare(IReadOnlyList<long> left, IReadOnlyList<long> right)
    {
        for (int i = 0; i < Math.Min(left.Count, right.Count); i++)
        {
            int order = left[i].CompareTo(right[i]);
            if (order != 0) return order;
        }

        return left.Count.CompareTo(right.Count);
    }
}
