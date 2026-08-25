using System.Net;
using NetTrans.Torrent;

namespace NetTrans.Tests.Fakes;

/// <summary>
/// A seeding peer in a box: speaks the wire protocol over an in-memory pipe and
/// serves blocks out of the content the test holds.
/// </summary>
public sealed class FakeSeed
{
    private readonly TorrentMetainfo _torrent;
    private readonly byte[] _content;

    public FakeSeed(TorrentMetainfo torrent, byte[] content)
    {
        _torrent = torrent;
        _content = content;
    }

    /// <summary>Which pieces this peer claims. Null means all of them.</summary>
    public int[]? Has { get; set; }

    /// <summary>Wait for our interested before unchoking, as a real peer would.</summary>
    public bool ChokeUntilInterested { get; set; } = true;

    /// <summary>Corrupt these pieces on the way out, to exercise verification.</summary>
    public HashSet<int> Corrupt { get; } = new();

    /// <summary>Announce the bitfield as `have` messages instead of one bitfield.</summary>
    public bool UseHaveMessages { get; set; }

    /// <summary>Blocks served, so a test can see the pipeline working.</summary>
    public int BlocksServed { get; private set; }

    /// <summary>Runs the peer's side of a connection until it ends.</summary>
    public async Task ServeAsync(Stream stream, byte[] infoHash, CancellationToken cancellationToken)
    {
        try
        {
            // The handshake is symmetric; ours goes out regardless of theirs.
            var theirs = await PeerWire
                .ReadExactAsync(stream, PeerWire.HandshakeLength, cancellationToken)
                .ConfigureAwait(false);

            PeerWire.ParseHandshake(theirs);

            await stream
                .WriteAsync(PeerWire.BuildHandshake(infoHash, Enumerable.Repeat((byte)'S', 20).ToArray()), cancellationToken)
                .ConfigureAwait(false);

            foreach (var announcement in Announcements())
            {
                await SendAsync(stream, announcement, cancellationToken).ConfigureAwait(false);
            }

            if (!ChokeUntilInterested) await SendAsync(stream, PeerMessage.Unchoke, cancellationToken).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await PeerWire.ReadMessageAsync(stream, cancellationToken).ConfigureAwait(false);

                switch (message.Kind)
                {
                    case PeerMessageKind.Interested:
                        await SendAsync(stream, PeerMessage.Unchoke, cancellationToken).ConfigureAwait(false);
                        break;

                    case PeerMessageKind.Request:
                        await ServeBlockAsync(stream, message, cancellationToken).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (Exception)
        {
            // The client hung up, which is how every one of these ends.
        }
    }

    /// <summary>
    /// What this peer says it has. A real client sends one bitfield, but plenty
    /// send a run of `have` messages instead, and both have to work.
    /// </summary>
    private IEnumerable<PeerMessage> Announcements()
    {
        var pieces = Has ?? Enumerable.Range(0, _torrent.PieceCount).ToArray();

        if (UseHaveMessages)
        {
            foreach (int piece in pieces) yield return PeerMessage.Have(piece);
            yield break;
        }

        var bits = new byte[PeerWire.BitfieldLength(_torrent.PieceCount)];
        foreach (int piece in pieces) PeerWire.SetPiece(bits, piece);

        yield return PeerMessage.Bitfield(bits);
    }

    private async Task ServeBlockAsync(Stream stream, PeerMessage request, CancellationToken cancellationToken)
    {
        int piece = request.PieceIndex;
        int offset = request.BlockOffset;
        int length = request.BlockLength;

        if (piece < 0 || piece >= _torrent.PieceCount) return;
        if (offset < 0 || length <= 0) return;

        long start = piece * _torrent.PieceLength + offset;
        if (start + length > _content.Length) return;

        var block = _content.AsSpan((int)start, length).ToArray();

        // A corrupt piece is served as plausible bytes that will not hash.
        if (Corrupt.Contains(piece)) block[0] ^= 0xFF;

        BlocksServed++;

        await SendAsync(stream, PeerMessage.Piece(piece, offset, block), cancellationToken).ConfigureAwait(false);
    }

    private static async Task SendAsync(Stream stream, PeerMessage message, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(PeerWire.Encode(message), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Connects a client to <see cref="FakeSeed"/>s over in-memory duplex pipes,
/// so the swarm can be exercised without a network.
/// </summary>
public sealed class FakePeerConnector : IPeerConnector
{
    private readonly Dictionary<string, FakeSeed> _seeds = new(StringComparer.Ordinal);
    private readonly byte[] _infoHash;

    public FakePeerConnector(byte[] infoHash) => _infoHash = infoHash;

    /// <summary>Peers that refuse the connection, as most of a tracker's list does.</summary>
    public HashSet<string> Dead { get; } = new(StringComparer.Ordinal);

    public int Connections { get; private set; }

    public FakePeerConnector Add(IPEndPoint address, FakeSeed seed)
    {
        _seeds[address.ToString()] = seed;
        return this;
    }

    public Task<Stream> ConnectAsync(IPEndPoint peer, CancellationToken cancellationToken)
    {
        string key = peer.ToString();

        if (Dead.Contains(key) || !_seeds.TryGetValue(key, out var seed))
        {
            return Task.FromException<Stream>(new PeerException($"无法连接 {peer}。"));
        }

        Connections++;

        var (ours, theirs) = DuplexPipe.Create();
        _ = Task.Run(() => seed.ServeAsync(theirs, _infoHash, cancellationToken), CancellationToken.None);

        return Task.FromResult(ours);
    }
}

/// <summary>Two streams wired to each other, standing in for a socket pair.</summary>
public static class DuplexPipe
{
    public static (Stream A, Stream B) Create()
    {
        var toB = new BlockingPipe();
        var toA = new BlockingPipe();

        return (new PipeStream(toA, toB), new PipeStream(toB, toA));
    }

    /// <summary>Reads from one pipe, writes to the other.</summary>
    private sealed class PipeStream : Stream
    {
        private readonly BlockingPipe _read;
        private readonly BlockingPipe _write;

        public PipeStream(BlockingPipe read, BlockingPipe write)
        {
            _read = read;
            _write = write;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _read.ReadAsync(buffer, cancellationToken);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            _write.Write(buffer.Span);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Write(byte[] buffer, int offset, int count) => _write.Write(buffer.AsSpan(offset, count));

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _read.Close();
            _write.Close();

            base.Dispose(disposing);
        }
    }

    /// <summary>A byte queue a reader can wait on.</summary>
    private sealed class BlockingPipe
    {
        private readonly Queue<byte> _bytes = new();
        private readonly SemaphoreSlim _available = new(0);
        private readonly object _gate = new();

        private bool _closed;

        public void Write(ReadOnlySpan<byte> data)
        {
            lock (_gate)
            {
                foreach (byte b in data) _bytes.Enqueue(b);
            }

            _available.Release(data.Length);
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (buffer.Length == 0) return 0;

            // Block for the first byte, then take whatever else is ready.
            await _available.WaitAsync(cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                if (_closed && _bytes.Count == 0) return 0;

                int read = 0;
                buffer.Span[read++] = _bytes.Dequeue();

                while (read < buffer.Length && _bytes.Count > 0 && _available.Wait(0))
                {
                    buffer.Span[read++] = _bytes.Dequeue();
                }

                return read;
            }
        }

        public void Close()
        {
            lock (_gate) _closed = true;

            // Wake anyone waiting so they see the close rather than hanging.
            _available.Release(1);
        }
    }
}
