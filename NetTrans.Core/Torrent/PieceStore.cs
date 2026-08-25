using NetTrans.Download;

namespace NetTrans.Torrent;

/// <summary>
/// Where a torrent's verified pieces land.
///
/// A torrent addresses its content as one flat byte stream, but the content is
/// a set of files, so a single piece can span two of them -- and a piece is
/// only written once its SHA-1 matches, because a peer that sends bad bytes is
/// a normal event rather than an exceptional one.
/// </summary>
public sealed class PieceStore : IAsyncDisposable
{
    private readonly TorrentMetainfo _torrent;
    private readonly IFileSinkFactory _sinks;
    private readonly string _root;
    private readonly Dictionary<string, IFileSink> _open = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PieceStore(TorrentMetainfo torrent, IFileSinkFactory sinks, string root)
    {
        _torrent = torrent;
        _sinks = sinks;
        _root = root;
    }

    /// <summary>Where each of the torrent's files goes.</summary>
    public string PathOf(TorrentEntry file) => Path.Combine(_root, file.Path);

    /// <summary>
    /// Verifies a piece and writes it. Returns false when the bytes do not hash
    /// to what the torrent said, in which case nothing is written and the piece
    /// stays wanted.
    /// </summary>
    public async Task<bool> WriteAsync(int index, byte[] piece, CancellationToken cancellationToken)
    {
        if (piece.Length != _torrent.LengthOfPiece(index)) return false;
        if (!_torrent.Verify(index, piece)) return false;

        foreach (var (file, fileOffset, pieceOffset, length) in _torrent.Locate(index))
        {
            var sink = await OpenAsync(file, cancellationToken).ConfigureAwait(false);

            await sink.WriteAsync(
                fileOffset,
                piece.AsMemory((int)pieceOffset, (int)length),
                cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>Pushes everything written so far to disk.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        List<IFileSink> sinks;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            sinks = _open.Values.ToList();
        }
        finally
        {
            _gate.Release();
        }

        foreach (var sink in sinks) await sink.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens each file once and keeps the handle. A torrent of ten thousand
    /// small files would otherwise reopen one per piece, and a piece that spans
    /// two files would reopen both.
    /// </summary>
    private async Task<IFileSink> OpenAsync(TorrentEntry file, CancellationToken cancellationToken)
    {
        string path = PathOf(file);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_open.TryGetValue(path, out var existing)) return existing;

            // Pre-sized: a torrent knows every file's length up front, so the
            // disk can be claimed before an hour of downloading is spent.
            var sink = await _sinks.OpenAsync(path, file.Length, cancellationToken).ConfigureAwait(false);
            _open[path] = sink;

            return sink;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<IFileSink> sinks;

        lock (_open)
        {
            sinks = _open.Values.ToList();
            _open.Clear();
        }

        foreach (var sink in sinks) await sink.DisposeAsync().ConfigureAwait(false);

        _gate.Dispose();
    }
}

/// <summary>
/// A piece being assembled out of 16 KiB blocks.
///
/// Blocks arrive out of order and a peer may send one twice, so this tracks
/// which are still outstanding rather than counting bytes -- counting would let
/// a duplicate block finish a piece that has a hole in it.
/// </summary>
public sealed class PieceBuffer
{
    private readonly bool[] _received;
    private readonly byte[] _bytes;

    public PieceBuffer(int index, int length)
    {
        Index = index;
        _bytes = new byte[length];
        _received = new bool[(length + PeerWire.BlockLength - 1) / PeerWire.BlockLength];
    }

    public int Index { get; }

    public int Length => _bytes.Length;

    public int BlockCount => _received.Length;

    public bool IsComplete => _received.All(received => received);

    /// <summary>The offset and length of one block of this piece.</summary>
    public (int Offset, int Length) Block(int block)
    {
        int offset = block * PeerWire.BlockLength;
        return (offset, Math.Min(PeerWire.BlockLength, _bytes.Length - offset));
    }

    /// <summary>Every block not yet received, in order.</summary>
    public IEnumerable<int> Missing()
    {
        for (int i = 0; i < _received.Length; i++)
        {
            if (!_received[i]) yield return i;
        }
    }

    /// <summary>
    /// Takes a block. Returns false for one that does not line up with a block
    /// boundary or runs past the piece -- a peer sending those is broken or
    /// probing, and either way its bytes are not usable.
    /// </summary>
    public bool Add(int offset, ReadOnlySpan<byte> data)
    {
        if (offset < 0 || offset % PeerWire.BlockLength != 0) return false;
        if (data.Length == 0 || offset + data.Length > _bytes.Length) return false;

        int block = offset / PeerWire.BlockLength;
        if (block >= _received.Length) return false;

        var (_, expected) = Block(block);
        if (data.Length != expected) return false;

        data.CopyTo(_bytes.AsSpan(offset));
        _received[block] = true;

        return true;
    }

    /// <summary>The assembled piece. Only meaningful once every block has arrived.</summary>
    public byte[] ToArray() => _bytes;
}
