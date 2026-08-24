using NetTrans.Models;

namespace NetTrans.Net;

/// <summary>
/// The 新版本 notice.
///
/// The handoff leaves this as "should come from a server-side version query",
/// but there is no such API to call. What a plain HTTP server does tell us is
/// whether the file at the same URL has changed since we fetched it -- a new
/// length, a new ETag or a new Last-Modified -- which is the same question a
/// version query would answer, asked in the only way the web actually supports.
/// </summary>
public static class VersionCheck
{
    /// <summary>
    /// Re-probes a finished download's URL. Returns what changed, or null when
    /// the server is still serving exactly what we already have.
    /// </summary>
    public static async Task<NewVersionInfo?> CheckAsync(
        DownloadItem item,
        IHttpTransport transport,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(item.Url, UriKind.Absolute, out var url)) return null;

        RemoteFileInfo info;
        try
        {
            info = await transport.ProbeAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A version check is a nicety; a server that will not answer just
            // means we do not know, not that anything is wrong.
            return null;
        }

        if (!HasChanged(item, info)) return null;

        return new NewVersionInfo(
            info.FileName.Length > 0 ? info.FileName : item.Name,
            info.HasKnownLength ? info.Length : item.Size,
            DescribeWhen(info));
    }

    private static bool HasChanged(DownloadItem item, RemoteFileInfo info)
    {
        // A validator is the strongest signal, when both sides have one.
        if (!string.IsNullOrEmpty(item.SourceETag) && !string.IsNullOrEmpty(info.ETag))
        {
            return !string.Equals(item.SourceETag, info.ETag, StringComparison.Ordinal);
        }

        if (!string.IsNullOrEmpty(item.SourceLastModified) && !string.IsNullOrEmpty(info.LastModified))
        {
            return !string.Equals(item.SourceLastModified, info.LastModified, StringComparison.Ordinal);
        }

        // Otherwise a different length is the only evidence available. Equal
        // lengths with no validators are treated as unchanged rather than
        // nagging about a file that is probably the same.
        return info.HasKnownLength && item.Size > 0 && info.Length != item.Size;
    }

    private static string DescribeWhen(RemoteFileInfo info)
    {
        if (string.IsNullOrEmpty(info.LastModified)) return "未知时间";
        if (!DateTimeOffset.TryParse(info.LastModified, out var published)) return info.LastModified;

        var age = DateTimeOffset.UtcNow - published;

        // Rounded, not truncated: HTTP dates carry no sub-second part, so a
        // file published exactly three days ago arrives as 2.9999 days and
        // would otherwise read "2 天前".
        return age.TotalDays >= 2 ? $"{(int)Math.Round(age.TotalDays)} 天前"
            : age.TotalDays >= 1 ? "昨天"
            : age.TotalHours >= 1 ? $"{(int)Math.Round(age.TotalHours)} 小时前"
            : "刚刚";
    }
}
