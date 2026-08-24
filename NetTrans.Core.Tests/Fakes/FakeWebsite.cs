using System.Text;
using NetTrans.Net;

namespace NetTrans.Tests.Fakes;

/// <summary>
/// A site of several documents, for the crawler and the sniffer. The download
/// transport serves one payload; these need many URLs.
/// </summary>
public sealed class FakeWebsite : IHttpTransport
{
    private readonly Dictionary<string, byte[]> _pages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _sizes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unreachable = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every URL that was fetched, in order.</summary>
    public List<string> Fetched { get; } = new();

    /// <summary>Every URL that was probed, in order.</summary>
    public List<string> Probed { get; } = new();

    public FakeWebsite Page(string url, string html)
    {
        _pages[url] = Encoding.UTF8.GetBytes(html);
        return this;
    }

    /// <summary>Registers a file that only ever gets probed, with the length the server reports.</summary>
    public FakeWebsite File(string url, long length)
    {
        _sizes[url] = length;
        return this;
    }

    /// <summary>Makes a URL fail, so the crawler's error handling can be exercised.</summary>
    public FakeWebsite Broken(string url)
    {
        _unreachable.Add(url);
        return this;
    }

    public Task<RemoteFileInfo> ProbeAsync(Uri url, CancellationToken cancellationToken)
    {
        Probed.Add(url.AbsoluteUri);

        if (_unreachable.Contains(url.AbsoluteUri)) throw new HttpRequestException("unreachable");

        long length = _sizes.TryGetValue(url.AbsoluteUri, out long size) ? size
            : _pages.TryGetValue(url.AbsoluteUri, out var page) ? page.Length
            : -1;

        return Task.FromResult(new RemoteFileInfo(
            length,
            SupportsRanges: true,
            ETag: null,
            LastModified: null,
            FileName: url.AbsolutePath.Split('/').LastOrDefault() ?? "file"));
    }

    public Task<Stream> OpenAsync(Uri url, long from, long? to, CancellationToken cancellationToken)
    {
        Fetched.Add(url.AbsoluteUri);

        if (_unreachable.Contains(url.AbsoluteUri)) throw new HttpRequestException("unreachable");
        if (!_pages.TryGetValue(url.AbsoluteUri, out var body)) throw new HttpRequestException("404");

        return Task.FromResult<Stream>(new MemoryStream(body));
    }
}

/// <summary>A transport whose probe answers can be dictated, for the 新版本 check.</summary>
public sealed class ScriptedProbe : IHttpTransport
{
    private readonly RemoteFileInfo? _info;
    private readonly Exception? _failure;

    public ScriptedProbe(RemoteFileInfo info) => _info = info;

    public ScriptedProbe(Exception failure) => _failure = failure;

    public int Probes { get; private set; }

    public Task<RemoteFileInfo> ProbeAsync(Uri url, CancellationToken cancellationToken)
    {
        Probes++;
        return _failure is not null
            ? Task.FromException<RemoteFileInfo>(_failure)
            : Task.FromResult(_info!);
    }

    public Task<Stream> OpenAsync(Uri url, long from, long? to, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This transport only answers probes.");
}
