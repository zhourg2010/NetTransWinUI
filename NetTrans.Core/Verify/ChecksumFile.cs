namespace NetTrans.Verify;

/// <summary>One line of a published checksum file.</summary>
/// <param name="Kind">Recognised from the digest's length, or from the algorithm name on a BSD-style line.</param>
/// <param name="Hash">Lower-case hex.</param>
/// <param name="Name">The file the digest belongs to, without any directory part. Null for a file that is nothing but a hash.</param>
public sealed record ChecksumEntry(HashKind Kind, string Hash, string? Name);

/// <summary>
/// Reads the checksum files projects actually publish. Three shapes cover
/// nearly all of them:
///
///   coreutils   d41d8c…  ubuntu.iso        (two spaces, or one, or " *" for binary mode)
///   BSD         SHA256 (ubuntu.iso) = d41d8c…
///   bare        d41d8c…                    (a per-file .sha256 sidecar)
///
/// Anything else on the page is skipped rather than rejected: GPG-signed
/// SHA256SUMS files wrap the list in armour, and release notes put prose
/// around it.
/// </summary>
public static class ChecksumFile
{
    /// <summary>A checksum list is a list; past this we are reading something else.</summary>
    public const int MaxLines = 20_000;

    public static IReadOnlyList<ChecksumEntry> Parse(string text)
    {
        var entries = new List<ChecksumEntry>();
        if (string.IsNullOrEmpty(text)) return entries;

        int lines = 0;
        foreach (var raw in text.Split('\n'))
        {
            if (++lines > MaxLines) break;

            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;

            if ((ParseBsd(line) ?? ParseCoreutils(line)) is { } entry) entries.Add(entry);
        }

        return entries;
    }

    /// <summary>
    /// Picks the digest for one file out of a parsed list.
    /// </summary>
    /// <param name="singleEntryWins">
    /// True for a sidecar fetched as "&lt;url&gt;.sha256": it belongs to this file
    /// by construction, whatever name it does or does not carry inside — the
    /// name on disk is often not the name on the server. False for a shared
    /// SHA256SUMS, where taking the wrong line would be a real error.
    /// </param>
    public static ChecksumEntry? Find(IReadOnlyList<ChecksumEntry> entries, string fileName, bool singleEntryWins)
    {
        if (entries.Count == 0) return null;

        string wanted = Basename(fileName);

        var named = entries
            .Where(entry => entry.Name is { Length: > 0 } name && string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // Some lists carry both an MD5 and a SHA-256 line per file. Check the
        // strongest one we can.
        if (named.Length > 0) return named.MaxBy(entry => entry.Kind);

        var anonymous = entries.Where(entry => string.IsNullOrEmpty(entry.Name)).ToArray();
        if (anonymous.Length > 0) return anonymous.MaxBy(entry => entry.Kind);

        return singleEntryWins && entries.Count == 1 ? entries[0] : null;
    }

    /// <summary>SHA256 (ubuntu.iso) = d41d8c…</summary>
    private static ChecksumEntry? ParseBsd(string line)
    {
        int open = line.IndexOf('(');
        if (open <= 0) return null;

        int close = line.IndexOf(')', open + 1);
        if (close < 0) return null;

        int equals = line.IndexOf('=', close + 1);
        if (equals < 0) return null;

        if (!HashKinds.TryParse(line[..open].Trim(), out var kind)) return null;

        string hash = line[(equals + 1)..].Trim();
        if (!IsHex(hash, kind.HexLength())) return null;

        return new ChecksumEntry(kind, hash.ToLowerInvariant(), Basename(line[(open + 1)..close].Trim()));
    }

    /// <summary>d41d8c…  ubuntu.iso — and the bare-hash case, which is the same line without a name.</summary>
    private static ChecksumEntry? ParseCoreutils(string line)
    {
        int split = line.IndexOfAny(new[] { ' ', '\t' });
        string hash = split < 0 ? line : line[..split];

        if (!HashKinds.TryFromHexLength(hash.Length, out var kind)) return null;
        if (!IsHex(hash, hash.Length)) return null;

        // " *name" is coreutils' binary mode; "?name" is what it writes for a
        // name it could not encode, and is no worse a name than none.
        string name = split < 0 ? "" : line[(split + 1)..].TrimStart(' ', '\t', '*');

        return new ChecksumEntry(kind, hash.ToLowerInvariant(), name.Length == 0 ? null : Basename(name));
    }

    private static bool IsHex(string text, int length) =>
        text.Length == length && text.All(Uri.IsHexDigit);

    /// <summary>Checksum files name files relative to their own directory; only the last part is comparable.</summary>
    private static string Basename(string name) => HashRecord.FileNameOf(name);
}
