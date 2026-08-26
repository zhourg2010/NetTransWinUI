using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;

namespace NetTrans.Net.Ftp;

/// <summary>
/// Opens the sockets an FTP session needs, kept behind an interface so the
/// protocol can be tested against an in-memory server instead of the network.
///
/// FTP uses two connections: a control channel that carries commands, and a
/// fresh data connection per transfer. Both come from here.
/// </summary>
public interface IFtpConnector
{
    Task<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken);
}

/// <summary>The real one.</summary>
public sealed class TcpFtpConnector : IFtpConnector
{
    public static TcpFtpConnector Instance { get; } = new();

    public async Task<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        var client = new TcpClient { NoDelay = true };

        try
        {
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            client.Dispose();
            throw new FtpException($"无法连接 {host}:{port}：{exception.Message}", exception);
        }

        return new NetworkStream(client.GetStream(), client);
    }

    /// <summary>Keeps the socket alive for as long as the stream is, and closes both together.</summary>
    private sealed class NetworkStream : Stream
    {
        private readonly Stream _inner;
        private readonly TcpClient _client;

        public NetworkStream(Stream inner, TcpClient client)
        {
            _inner = inner;
            _client = client;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (!disposing) return;

            _inner.Dispose();
            _client.Dispose();
        }
    }
}

/// <summary>Wraps a control or data stream in TLS, for FTPS.</summary>
public interface IFtpTls
{
    Task<Stream> AuthenticateAsync(Stream stream, string host, CancellationToken cancellationToken);
}

/// <summary>The real one, over SslStream.</summary>
public sealed class SslFtpTls : IFtpTls
{
    public static SslFtpTls Instance { get; } = new();

    public async Task<Stream> AuthenticateAsync(Stream stream, string host, CancellationToken cancellationToken)
    {
        var ssl = new SslStream(stream, leaveInnerStreamOpen: false);

        await ssl.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.None, // let the OS pick, which is what keeps this working
            },
            cancellationToken).ConfigureAwait(false);

        return ssl;
    }
}

/// <summary>Anything the server refused, or answered in a way we cannot use.</summary>
public sealed class FtpException : Exception
{
    public FtpException(string message, Exception? inner = null) : base(message, inner)
    {
    }

    /// <summary>The reply code, when the failure was a reply rather than a socket.</summary>
    public int Code { get; init; }
}
