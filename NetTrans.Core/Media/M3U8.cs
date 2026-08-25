using System.Globalization;

namespace NetTrans.Media;

/// <summary>How a segment is encrypted, from #EXT-X-KEY.</summary>
public enum HlsEncryption
{
    None,

    /// <summary>METHOD=AES-128: the whole segment, AES-128-CBC with PKCS#7.</summary>
    Aes128,

    /// <summary>METHOD=SAMPLE-AES: only the samples, and it needs the codec to unpick. Refused.</summary>
    SampleAes,
}

/// <summary>The key one run of segments is encrypted with.</summary>
/// <param name="Method">What the tag asked for.</param>
/// <param name="KeyUri">Where to fetch the 16-byte key, absolute.</param>
/// <param name="Iv">The explicit IV, or null when it comes from the sequence number.</param>
public sealed record HlsKey(HlsEncryption Method, Uri? KeyUri, byte[]? Iv);

/// <summary>
/// One segment of a media playlist.
/// </summary>
/// <param name="Url">Absolute.</param>
/// <param name="Duration">#EXTINF, seconds. The only size estimate a playlist gives.</param>
/// <param name="SequenceNumber">Media sequence number, which an implicit AES IV is derived from.</param>
/// <param name="Key">Null when this run is in the clear.</param>
/// <param name="ByteRangeOffset">Set when the segment is a slice of a larger file (#EXT-X-BYTERANGE).</param>
/// <param name="ByteRangeLength">Length of that slice.</param>
public sealed record HlsSegment(
    Uri Url,
    double Duration,
    long SequenceNumber,
    HlsKey? Key = null,
    long? ByteRangeOffset = null,
    long? ByteRangeLength = null);

/// <summary>One quality on offer in a master playlist.</summary>
/// <param name="Url">The media playlist for this rendition, absolute.</param>
/// <param name="Bandwidth">BANDWIDTH in bits per second; 0 when the tag omitted it.</param>
/// <param name="Width">From RESOLUTION, or 0.</param>
/// <param name="Height">From RESOLUTION, or 0.</param>
/// <param name="Codecs">CODECS verbatim, or empty.</param>
public sealed record HlsVariant(Uri Url, long Bandwidth, int Width, int Height, string Codecs)
{
    /// <summary>The label the 视频嗅探 sheet shows: 1080p when the tag said so, else a bitrate.</summary>
    public string Quality => Height > 0
        ? $"{Height}p"
        : Bandwidth > 0
            ? $"{Math.Round(Bandwidth / 1000d):F0} kbps"
            : "视频";
}

/// <summary>A parsed media playlist: the segments, and what the file they build is.</summary>
/// <param name="Segments">In play order.</param>
/// <param name="InitSegment">#EXT-X-MAP, present for fMP4 streams and absent for MPEG-TS.</param>
/// <param name="IsLive">No #EXT-X-ENDLIST: the playlist is still growing.</param>
/// <param name="TotalDuration">Sum of the EXTINFs, seconds.</param>
public sealed record HlsMedia(
    IReadOnlyList<HlsSegment> Segments,
    Uri? InitSegment,
    bool IsLive,
    double TotalDuration);

/// <summary>
/// An M3U8 reader covering what a downloader actually needs: the variants in a
/// master playlist, and the segments, keys and init segment in a media one.
///
/// It is deliberately not a full HLS implementation -- there is no live edge
/// following, no discontinuity handling and no SAMPLE-AES -- but it is exact
/// about the parts it does read, because a segment fetched from a
/// misinterpreted URI is a corrupt file rather than an error.
/// </summary>
public static class M3U8
{
    /// <summary>Whether the text is a master playlist (variants) rather than a media one (segments).</summary>
    public static bool IsMaster(string text) =>
        Lines(text).Any(line => line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal));

    /// <summary>
    /// The renditions of a master playlist, best first. A media playlist has
    /// none, and returns empty rather than throwing.
    /// </summary>
    public static IReadOnlyList<HlsVariant> ParseMaster(string text, Uri playlistUrl)
    {
        var variants = new List<HlsVariant>();
        Dictionary<string, string>? pending = null;

        foreach (string line in Lines(text))
        {
            if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
            {
                pending = Attributes(line["#EXT-X-STREAM-INF:".Length..]);
                continue;
            }

            if (line.StartsWith('#') || pending is null) continue;

            // The URI is on the line after the tag, which is what makes the
            // pending attributes belong to it.
            if (!TryResolve(line, playlistUrl, out var url))
            {
                pending = null;
                continue;
            }

            (int width, int height) = Resolution(Value(pending, "RESOLUTION"));

            variants.Add(new HlsVariant(
                url,
                Number(Value(pending, "BANDWIDTH")),
                width,
                height,
                Value(pending, "CODECS") ?? ""));

            pending = null;
        }

        return variants
            .OrderByDescending(variant => variant.Height)
            .ThenByDescending(variant => variant.Bandwidth)
            .ToList();
    }

    /// <summary>
    /// The segments of a media playlist. Relative URIs are resolved against
    /// <paramref name="playlistUrl"/>, which is what makes a nested playlist
    /// work at all.
    /// </summary>
    public static HlsMedia ParseMedia(string text, Uri playlistUrl)
    {
        var segments = new List<HlsSegment>();

        long sequence = 0;
        double duration = 0;
        double total = 0;
        bool endList = false;
        Uri? map = null;
        HlsKey? key = null;
        long? rangeOffset = null;
        long? rangeLength = null;

        // The offset of a #EXT-X-BYTERANGE with no explicit start: it continues
        // where the previous one ended.
        long nextByteOffset = 0;

        foreach (string line in Lines(text))
        {
            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                // "#EXTINF:9.009,title" -- the title after the comma is free text.
                string value = line["#EXTINF:".Length..].Split(',')[0];
                duration = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                    ? parsed
                    : 0;
                continue;
            }

            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
            {
                sequence = Number(line["#EXT-X-MEDIA-SEQUENCE:".Length..]);
                continue;
            }

            if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal))
            {
                key = ParseKey(line["#EXT-X-KEY:".Length..], playlistUrl);
                continue;
            }

            if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                var attributes = Attributes(line["#EXT-X-MAP:".Length..]);
                if (Value(attributes, "URI") is { } uri && TryResolve(uri, playlistUrl, out var resolved)) map = resolved;
                continue;
            }

            if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal))
            {
                (rangeLength, rangeOffset) = ParseByteRange(line["#EXT-X-BYTERANGE:".Length..], nextByteOffset);
                continue;
            }

            if (line.StartsWith("#EXT-X-ENDLIST", StringComparison.Ordinal))
            {
                endList = true;
                continue;
            }

            if (line.StartsWith('#')) continue;

            if (TryResolve(line, playlistUrl, out var segmentUrl))
            {
                segments.Add(new HlsSegment(segmentUrl, duration, sequence, key, rangeOffset, rangeLength));
                total += duration;
                sequence++;

                if (rangeOffset is { } offset && rangeLength is { } length) nextByteOffset = offset + length;
            }

            duration = 0;
            rangeOffset = null;
            rangeLength = null;
        }

        return new HlsMedia(segments, map, IsLive: !endList, total);
    }

    private static HlsKey? ParseKey(string attributeList, Uri playlistUrl)
    {
        var attributes = Attributes(attributeList);
        string method = Value(attributes, "METHOD") ?? "NONE";

        // METHOD=NONE ends an encrypted run rather than describing one.
        if (string.Equals(method, "NONE", StringComparison.OrdinalIgnoreCase)) return null;

        var encryption = method.ToUpperInvariant() switch
        {
            "AES-128" => HlsEncryption.Aes128,
            "SAMPLE-AES" or "SAMPLE-AES-CTR" => HlsEncryption.SampleAes,
            _ => HlsEncryption.SampleAes, // Unknown: refused the same way, rather than fetched and mangled.
        };

        Uri? keyUri = null;
        if (Value(attributes, "URI") is { } uri && TryResolve(uri, playlistUrl, out var resolved)) keyUri = resolved;

        return new HlsKey(encryption, keyUri, ParseIv(Value(attributes, "IV")));
    }

    /// <summary>The IV as 16 bytes, or null when the tag omitted it or wrote it wrong.</summary>
    internal static byte[]? ParseIv(string? value)
    {
        if (value is null) return null;

        string hex = value.Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
        if (hex.Length != 32) return null;

        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>"length@offset", where a missing offset continues the previous range.</summary>
    private static (long? Length, long? Offset) ParseByteRange(string value, long nextOffset)
    {
        var parts = value.Trim().Split('@');
        if (!long.TryParse(parts[0], out long length)) return (null, null);

        long offset = parts.Length > 1 && long.TryParse(parts[1], out long explicitOffset) ? explicitOffset : nextOffset;
        return (length, offset);
    }

    private static (int Width, int Height) Resolution(string? value)
    {
        if (value is null) return (0, 0);

        var parts = value.Split('x', 'X');
        return parts.Length == 2 && int.TryParse(parts[0], out int width) && int.TryParse(parts[1], out int height)
            ? (width, height)
            : (0, 0);
    }

    /// <summary>
    /// Splits an attribute list on commas that are not inside quotes -- CODECS
    /// values contain commas of their own, so a plain Split would tear them.
    /// </summary>
    internal static Dictionary<string, string> Attributes(string list)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        int start = 0;
        bool quoted = false;

        for (int i = 0; i <= list.Length; i++)
        {
            if (i < list.Length && list[i] == '"') quoted = !quoted;
            if (i < list.Length && (list[i] != ',' || quoted)) continue;

            Add(attributes, list[start..i]);
            start = i + 1;
        }

        return attributes;
    }

    private static void Add(Dictionary<string, string> attributes, string pair)
    {
        int equals = pair.IndexOf('=');
        if (equals <= 0) return;

        string name = pair[..equals].Trim();
        string value = pair[(equals + 1)..].Trim().Trim('"');

        if (name.Length > 0) attributes[name] = value;
    }

    private static string? Value(Dictionary<string, string> attributes, string name) =>
        attributes.TryGetValue(name, out var value) && value.Length > 0 ? value : null;

    private static long Number(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : 0;

    private static bool TryResolve(string value, Uri baseUrl, out Uri absolute)
    {
        absolute = baseUrl;

        string trimmed = value.Trim();
        if (trimmed.Length == 0) return false;
        if (!Uri.TryCreate(baseUrl, trimmed, out var candidate)) return false;
        if (candidate.Scheme != Uri.UriSchemeHttp && candidate.Scheme != Uri.UriSchemeHttps) return false;

        absolute = candidate;
        return true;
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Split('\n').Select(line => line.TrimEnd('\r').Trim()).Where(line => line.Length > 0);
}
