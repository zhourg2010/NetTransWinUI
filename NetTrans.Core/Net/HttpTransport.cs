using System.Net;
using System.Net.Http.Headers;

namespace NetTrans.Net;

/// <summary>
/// <see cref="IHttpTransport"/> over HttpClient.
///
/// Probing is deliberately belt-and-braces: many servers answer HEAD with no
/// length, or claim Accept-Ranges and then ignore the header. So HEAD is tried
/// first and, if it does not settle both questions, a one-byte range GET
/// decides -- a 206 with a Content-Range is the only proof that ranges really
/// work.
/// </summary>
public sealed class HttpTransport : IHttpTransport, IDisposable
{
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public HttpTransport(HttpClient? client = null, string? userAgent = null)
    {
        _ownsClient = client is null;
        _client = client ?? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.None, // ranges and encoded bodies do not mix
            MaxConnectionsPerServer = 32,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan, // per-read cancellation is handled by the caller
        };

        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            _client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        }
    }

    public async Task<RemoteFileInfo> ProbeAsync(Uri url, CancellationToken cancellationToken)
    {
        var head = await TryHeadAsync(url, cancellationToken).ConfigureAwait(false);

        long length = head?.Content.Headers.ContentLength ?? -1;
        bool advertisesRanges = head?.Headers.AcceptRanges.Contains("bytes") == true;
        string? etag = head?.Headers.ETag?.Tag;
        string? lastModified = head?.Content.Headers.LastModified?.ToString("R");
        string? contentType = head?.Content.Headers.ContentType?.MediaType;
        string name = FileNameFrom(url, head);

        head?.Dispose();

        // HEAD is advisory. A one-byte range GET is the only answer that counts.
        if (length <= 0 || !advertisesRanges)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, 0);

            using var response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                advertisesRanges = true;
                if (response.Content.Headers.ContentRange?.Length is { } total) length = total;
            }
            else
            {
                // 200 to a range request means the server ignored it.
                advertisesRanges = false;
                if (response.Content.Headers.ContentLength is { } full) length = full;
            }

            etag ??= response.Headers.ETag?.Tag;
            lastModified ??= response.Content.Headers.LastModified?.ToString("R");
            contentType ??= response.Content.Headers.ContentType?.MediaType;
            if (name.Length == 0) name = FileNameFrom(url, response);
        }

        return new RemoteFileInfo(length, advertisesRanges, etag, lastModified, name, contentType);
    }

    public async Task<Stream> OpenAsync(Uri url, long from, long? to, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (from > 0 || to is not null) request.Headers.Range = new RangeHeaderValue(from, to);

        var response = await _client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            response.EnsureSuccessStatusCode();

            // Asking for a range and getting the whole file back would silently
            // corrupt every segment after the first, so refuse it outright.
            if (from > 0 && response.StatusCode != HttpStatusCode.PartialContent)
            {
                throw new HttpRequestException(
                    $"服务器忽略了断点续传请求（返回 {(int)response.StatusCode}），无法从第 {from} 字节继续。");
            }

            return await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private async Task<HttpResponseMessage?> TryHeadAsync(Uri url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            var response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode) return response;

            response.Dispose();
            return null;
        }
        catch (HttpRequestException)
        {
            // Plenty of servers simply refuse HEAD; fall through to the range GET.
            return null;
        }
    }

    private static string FileNameFrom(Uri url, HttpResponseMessage? response)
    {
        string? disposition = response?.Content.Headers.ContentDisposition?.FileNameStar
            ?? response?.Content.Headers.ContentDisposition?.FileName;

        if (!string.IsNullOrWhiteSpace(disposition))
        {
            return Sanitise(disposition.Trim('"'));
        }

        string path = url.IsAbsoluteUri ? url.AbsolutePath : url.OriginalString;
        string last = path.TrimEnd('/').Split('/').LastOrDefault() ?? "";
        last = Uri.UnescapeDataString(last);

        return last.Length == 0 ? "未命名下载" : Sanitise(last);
    }

    private static string Sanitise(string name)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return name.Length == 0 ? "未命名下载" : name;
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }
}
