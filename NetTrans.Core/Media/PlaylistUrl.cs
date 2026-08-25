namespace NetTrans.Media;

/// <summary>
/// Whether a URL points at a playlist rather than a file, which is what decides
/// the kind of transfer a task gets.
///
/// The judgement is made from the URL because it has to be made before the task
/// starts -- the queue picks a job when it starts one, not after a probe -- and
/// a playlist URL is one of the few things on the web that is reliably named
/// after what it is.
/// </summary>
public static class PlaylistUrl
{
    public static bool IsPlaylist(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) && IsPlaylist(parsed);

    public static bool IsPlaylist(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps) return false;

        return Extension(url) is "m3u8" or "m3u" or "mpd";
    }

    /// <summary>
    /// MPEG-DASH, which this can recognise but not yet fetch. Kept apart from
    /// HLS so the refusal can say which one it is.
    /// </summary>
    public static bool IsDash(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) && Extension(parsed) == "mpd";

    private static string Extension(Uri url)
    {
        // The query is where a CDN puts its signature, and it routinely ends in
        // something that looks like an extension; only the path counts.
        string name = url.AbsolutePath.Split('/').LastOrDefault() ?? "";
        int dot = name.LastIndexOf('.');

        return dot > 0 && dot < name.Length - 1 ? name[(dot + 1)..].ToLowerInvariant() : "";
    }
}
