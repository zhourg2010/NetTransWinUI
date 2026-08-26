namespace NetTrans.Net.Ftp;

/// <summary>
/// FTP and FTPS behind the same interface as HTTP, so a transfer, its
/// segmenting and its resume logic do not have to know which one they got.
///
/// One session per data transfer. FTP has no way to say "stop sending" other
/// than closing the connection, and a segmented download stops the moment its
/// own range is full -- so a session that outlived a transfer would be left
/// holding a half-sent file.
/// </summary>
public sealed class FtpTransport : IHttpTransport
{
    private readonly IFtpConnector _connector;
    private readonly IFtpTls _tls;
    private readonly RequestProfiles? _profiles;

    public FtpTransport(IFtpConnector? connector = null, IFtpTls? tls = null, RequestProfiles? profiles = null)
    {
        _connector = connector ?? TcpFtpConnector.Instance;
        _tls = tls ?? SslFtpTls.Instance;
        _profiles = profiles;
    }

    /// <summary>Whether this transport handles the URL's scheme.</summary>
    public static bool Handles(Uri url) => url.IsAbsoluteUri && url.Scheme is "ftp" or "ftps";

    /// <summary>The same question about text that may not even be a URL.</summary>
    public static bool Handles(string? text) =>
        Uri.TryCreate((text ?? "").Trim(), UriKind.Absolute, out var url) && Handles(url);

    public async Task<RemoteFileInfo> ProbeAsync(Uri url, CancellationToken cancellationToken)
    {
        await using var session = await OpenAsync(url, cancellationToken).ConfigureAwait(false);

        string path = PathOf(url);

        long length = await session.SizeAsync(path, cancellationToken).ConfigureAwait(false);
        string? modified = await session.ModifiedAsync(path, cancellationToken).ConfigureAwait(false);

        return new RemoteFileInfo(
            length,

            // Splitting needs both a length and a server that restarts; FTP
            // has no equivalent of a 206 to prove it any earlier than the
            // first REST, which is refused loudly if it does not hold.
            length > 0 && session.SupportsRestart,
            ETag: null,
            modified,
            NameOf(url));
    }

    public async Task<Stream> OpenAsync(Uri url, long from, long? to, CancellationToken cancellationToken)
    {
        var session = await OpenAsync(url, cancellationToken).ConfigureAwait(false);

        try
        {
            var data = await session.RetrieveAsync(PathOf(url), from, cancellationToken).ConfigureAwait(false);

            // `to` is not expressible: FTP sends to the end of the file and the
            // caller stops reading when its segment is full, which closes this
            // and with it the session.
            return new FtpDownloadStream(data, session);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<FtpSession> OpenAsync(Uri url, CancellationToken cancellationToken)
    {
        var profile = _profiles?.For(url) ?? RequestProfile.FromUserInfo(url);

        var session = new FtpSession(url.Host, url.Port, _connector, _tls);

        try
        {
            await session
                .OpenAsync(
                    profile?.User,
                    profile?.Password,
                    secure: url.Scheme.Equals("ftps", StringComparison.OrdinalIgnoreCase),
                    cancellationToken)
                .ConfigureAwait(false);

            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The path as the server names it, with percent-escapes undone.</summary>
    internal static string PathOf(Uri url) => Uri.UnescapeDataString(url.AbsolutePath);

    internal static string NameOf(Uri url)
    {
        string last = PathOf(url).TrimEnd('/').Split('/').LastOrDefault() ?? "";

        foreach (char invalid in Path.GetInvalidFileNameChars()) last = last.Replace(invalid, '_');

        return last.Length == 0 ? "未命名下载" : last;
    }

    /// <summary>
    /// The data connection, with the session it belongs to. Closing one closes
    /// the other -- there is nothing useful left to do with a session whose
    /// transfer has been abandoned.
    /// </summary>
    private sealed class FtpDownloadStream : Stream
    {
        private readonly Stream _data;
        private readonly FtpSession _session;

        private bool _closed;

        public FtpDownloadStream(Stream data, FtpSession session)
        {
            _data = data;
            _session = session;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _data.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _data.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _data.ReadAsync(buffer, offset, count, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask DisposeAsync()
        {
            if (_closed) return;
            _closed = true;

            await _data.DisposeAsync().ConfigureAwait(false);
            await _session.DisposeAsync().ConfigureAwait(false);

            GC.SuppressFinalize(this);
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposing || _closed) return;
            _closed = true;

            // The async path is the one callers use; this is here for the
            // `using` that forgets the await.
            _data.Dispose();
            _session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
