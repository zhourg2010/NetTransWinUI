using NetTrans.Net;

namespace NetTrans.Tests.Fakes;

/// <summary>
/// A server in a box: serves a byte array, optionally refuses ranges, and can
/// be told to drop the connection partway through so retry and resume can be
/// exercised.
/// </summary>
public sealed class FakeHttpTransport : IHttpTransport
{
    private readonly byte[] _content;
    private int _dropsRemaining;

    public FakeHttpTransport(byte[] content, bool supportsRanges = true)
    {
        _content = content;
        SupportsRanges = supportsRanges;
    }

    public bool SupportsRanges { get; set; }

    /// <summary>Reported as the file length; null means "the real length".</summary>
    public long? ReportedLength { get; set; }

    public string? ETag { get; set; } = "\"v1\"";

    public string FileName { get; set; } = "payload.bin";

    /// <summary>Every range asked for, so a test can assert how the file was split.</summary>
    public List<(long From, long? To)> Requests { get; } = new();

    public int ProbeCount { get; private set; }

    /// <summary>Fails this many of the next opens partway through the body.</summary>
    public int DropConnections
    {
        get => _dropsRemaining;
        set => _dropsRemaining = value;
    }

    /// <summary>How many bytes a dropped connection delivers before it fails.</summary>
    public int BytesBeforeDrop { get; set; } = 16;

    /// <summary>Fails every open outright, for the "server is down" case.</summary>
    public Exception? OpenFailure { get; set; }

    /// <summary>
    /// Runs once, after the first chunk of the body has been handed over. Lets a
    /// test pause or cancel at a known point instead of racing the transfer.
    /// </summary>
    public Action? AfterFirstRead { get; set; }

    /// <summary>Awaited before every open, so a test can hold transfers open and observe the queue.</summary>
    public Func<Task>? BeforeOpen { get; set; }

    public Task<RemoteFileInfo> ProbeAsync(Uri url, CancellationToken cancellationToken)
    {
        ProbeCount++;
        return Task.FromResult(new RemoteFileInfo(
            ReportedLength ?? _content.Length,
            SupportsRanges,
            ETag,
            LastModified: null,
            FileName));
    }

    public async Task<Stream> OpenAsync(Uri url, long from, long? to, CancellationToken cancellationToken)
    {
        lock (Requests) Requests.Add((from, to));

        if (BeforeOpen is { } gate) await gate().WaitAsync(cancellationToken).ConfigureAwait(false);

        if (OpenFailure is not null) throw OpenFailure;

        long last = to ?? _content.Length - 1;
        if (from >= _content.Length) return new MemoryStream(Array.Empty<byte>());

        last = Math.Min(last, _content.Length - 1);
        int count = (int)(last - from + 1);
        var slice = _content.AsSpan((int)from, count).ToArray();

        if (Interlocked.Decrement(ref _dropsRemaining) >= 0)
        {
            return new DroppingStream(slice, BytesBeforeDrop);
        }

        Interlocked.Increment(ref _dropsRemaining);

        Stream body = new MemoryStream(slice);
        if (AfterFirstRead is { } hook) body = new SignallingStream(body, hook);

        return body;
    }

    /// <summary>Runs a callback once the first chunk has been read.</summary>
    private sealed class SignallingStream : Stream
    {
        private readonly Stream _inner;
        private Action? _hook;

        public SignallingStream(Stream inner, Action hook)
        {
            _inner = inner;
            _hook = hook;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (Interlocked.Exchange(ref _hook, null) is { } hook) hook();

            return read;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = _inner.Read(buffer, offset, count);
            if (Interlocked.Exchange(ref _hook, null) is { } hook) hook();
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Delivers a few bytes and then throws, the way a real dropped connection does.</summary>
    private sealed class DroppingStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _limit;
        private int _position;

        public DroppingStream(byte[] data, int limit)
        {
            _data = data;
            _limit = limit;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _limit) throw new IOException("连接被服务器重置");

            int take = Math.Min(Math.Min(count, _limit - _position), _data.Length - _position);
            if (take <= 0) throw new IOException("连接被服务器重置");

            Array.Copy(_data, _position, buffer, offset, take);
            _position += take;
            return take;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= _limit) throw new IOException("连接被服务器重置");

            int take = Math.Min(Math.Min(buffer.Length, _limit - _position), _data.Length - _position);
            if (take <= 0) throw new IOException("连接被服务器重置");

            _data.AsSpan(_position, take).CopyTo(buffer.Span);
            _position += take;
            return ValueTask.FromResult(take);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
