using NetTrans.Net.Ftp;

namespace NetTrans.Net;

/// <summary>
/// Sends each URL to the transport that speaks its scheme.
///
/// Everything above this -- segmenting, resume, retries, the rate limiter, the
/// playlist and torrent jobs -- is written against one interface and has no
/// reason to learn a second. So FTP arrives as another implementation of that
/// interface rather than as a branch in every transfer.
/// </summary>
public sealed class SchemeTransport : IHttpTransport, IDisposable
{
    private readonly IHttpTransport _http;
    private readonly IHttpTransport _ftp;

    public SchemeTransport(IHttpTransport http, IHttpTransport? ftp = null)
    {
        _http = http;
        _ftp = ftp ?? new FtpTransport();
    }

    public Task<RemoteFileInfo> ProbeAsync(Uri url, CancellationToken cancellationToken) =>
        For(url).ProbeAsync(url, cancellationToken);

    public Task<Stream> OpenAsync(Uri url, long from, long? to, CancellationToken cancellationToken) =>
        For(url).OpenAsync(url, from, to, cancellationToken);

    private IHttpTransport For(Uri url) => FtpTransport.Handles(url) ? _ftp : _http;

    public void Dispose()
    {
        (_http as IDisposable)?.Dispose();
        (_ftp as IDisposable)?.Dispose();
    }
}
