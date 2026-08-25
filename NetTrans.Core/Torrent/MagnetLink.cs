using System.Web;

namespace NetTrans.Torrent;

/// <summary>
/// A magnet link: an info hash, optionally a display name, a size and some
/// trackers. It is not a torrent -- it names one. The metainfo has to be
/// fetched from peers before anything can be downloaded (BEP 9).
/// </summary>
/// <param name="InfoHash">20 bytes. The whole point of the link.</param>
/// <param name="DisplayName">dn, a hint for the row's name until the real one arrives.</param>
/// <param name="Trackers">tr, in the order given.</param>
/// <param name="Length">xl, when the link states one. Zero otherwise.</param>
public sealed record MagnetLink(byte[] InfoHash, string? DisplayName, IReadOnlyList<Uri> Trackers, long Length)
{
    /// <summary>The info hash as the 40 lower-case hex characters everything displays it as.</summary>
    public string InfoHashHex => Convert.ToHexString(InfoHash).ToLowerInvariant();

    public static bool IsMagnet(string? text) =>
        text is not null && text.TrimStart().StartsWith("magnet:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses a magnet link, or returns null when it is not one this can act on.</summary>
    public static MagnetLink? Parse(string? text)
    {
        if (!IsMagnet(text)) return null;

        string query = text!.Trim();
        int question = query.IndexOf('?');
        if (question < 0) return null;

        var parameters = HttpUtility.ParseQueryString(query[(question + 1)..]);

        byte[]? infoHash = null;

        // xt can appear more than once (a v1 and a v2 hash on the same link);
        // the first BitTorrent v1 hash is the one this can use.
        foreach (string? value in parameters.GetValues("xt") ?? Array.Empty<string>())
        {
            infoHash = ParseInfoHash(value);
            if (infoHash is not null) break;
        }

        if (infoHash is null) return null;

        var trackers = new List<Uri>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string? value in parameters.GetValues("tr") ?? Array.Empty<string>())
        {
            if (value is null) continue;
            if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var tracker)) continue;
            if (!TorrentMetainfo.IsAnnounceScheme(tracker)) continue;
            if (!seen.Add(tracker.AbsoluteUri)) continue;

            trackers.Add(tracker);
        }

        long length = long.TryParse(parameters["xl"], out long parsed) && parsed > 0 ? parsed : 0;

        return new MagnetLink(infoHash, Trim(parameters["dn"]), trackers, length);
    }

    /// <summary>
    /// "urn:btih:" followed by the hash as 40 hex characters or as 32 base32
    /// ones, both of which are in wide use.
    /// </summary>
    internal static byte[]? ParseInfoHash(string? xt)
    {
        const string prefix = "urn:btih:";

        if (xt is null) return null;

        string value = xt.Trim();
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

        value = value[prefix.Length..].Trim();

        if (value.Length == 40)
        {
            try
            {
                return Convert.FromHexString(value);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        return value.Length == 32 ? FromBase32(value) : null;
    }

    /// <summary>RFC 4648 base32, which is the other spelling of an info hash.</summary>
    private static byte[]? FromBase32(string text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        var bytes = new byte[TorrentMetainfo.HashLength];
        int bitBuffer = 0;
        int bitCount = 0;
        int written = 0;

        foreach (char c in text.ToUpperInvariant())
        {
            int index = alphabet.IndexOf(c, StringComparison.Ordinal);
            if (index < 0) return null;

            bitBuffer = (bitBuffer << 5) | index;
            bitCount += 5;

            if (bitCount < 8) continue;

            bitCount -= 8;

            if (written >= bytes.Length) return null;
            bytes[written++] = (byte)(bitBuffer >> bitCount);
        }

        return written == bytes.Length ? bytes : null;
    }

    private static string? Trim(string? value)
    {
        string? trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
