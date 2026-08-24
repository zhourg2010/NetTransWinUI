using System.Text;

namespace NetTrans.Net;

/// <summary>
/// Reads a page's markup through <see cref="IHttpTransport"/> so the crawler
/// and the sniffer share one transport (and one fake, in tests) with the
/// downloader.
/// </summary>
public static class PageReader
{
    /// <summary>Pages are read only to be scanned for links; this caps what a hostile or broken server can make us hold.</summary>
    public const int DefaultMaxBytes = 4 * 1024 * 1024;

    public static async Task<string> ReadAsync(
        IHttpTransport transport,
        Uri url,
        int maxBytes = DefaultMaxBytes,
        CancellationToken cancellationToken = default)
    {
        await using var stream = await transport.OpenAsync(url, 0, null, cancellationToken).ConfigureAwait(false);

        var buffer = new byte[64 * 1024];
        using var body = new MemoryStream();

        while (body.Length < maxBytes)
        {
            int wanted = (int)Math.Min(buffer.Length, maxBytes - body.Length);
            int read = await stream.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;

            body.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(body.ToArray());
    }
}
