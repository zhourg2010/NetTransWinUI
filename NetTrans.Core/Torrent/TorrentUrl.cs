namespace NetTrans.Torrent;

/// <summary>
/// Whether what the user pasted or dropped is a torrent, and which kind.
///
/// Decided from the text because the queue picks a job before it starts one,
/// and the three forms are unambiguous: a magnet scheme, a path to a .torrent,
/// or an http(s) URL ending in .torrent.
/// </summary>
public static class TorrentUrl
{
    public static bool IsTorrent(string? text) => IsMagnet(text) || IsTorrentFile(text);

    public static bool IsMagnet(string? text) => MagnetLink.IsMagnet(text);

    /// <summary>A .torrent on disk or on the web.</summary>
    public static bool IsTorrentFile(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        string trimmed = text.Trim();

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var url) &&
            (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps))
        {
            // The query is where a site puts its passkey, and it routinely ends
            // in something extension-shaped; only the path counts.
            return url.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase);
        }

        // A local path. Not checked for existence here: whether the file is
        // there is the job's problem, and saying "not a torrent" for a typo in
        // a path would be the wrong error.
        return trimmed.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) &&
               trimmed.IndexOfAny(Path.GetInvalidPathChars()) < 0;
    }

    /// <summary>The name to show before the metainfo arrives.</summary>
    public static string Describe(string? text)
    {
        if (MagnetLink.Parse(text) is { } magnet) return magnet.DisplayName ?? $"磁力链 {magnet.InfoHashHex[..12]}…";
        if (IsTorrentFile(text)) return Path.GetFileNameWithoutExtension(text!.Split('?')[0]);

        return "未命名种子";
    }
}
