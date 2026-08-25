using System.Net;
using System.Security.Cryptography;

namespace NetTrans.Torrent;

/// <summary>
/// BEP 9, ut_metadata: fetching a torrent's info dictionary from the peers
/// themselves.
///
/// This is the whole reason a magnet link works. A magnet names a torrent by
/// its info hash and nothing else -- no file list, no piece hashes, no length --
/// so before a single byte of content can be requested, the metainfo has to be
/// obtained from someone who already has it.
///
/// It rides on BEP 10's extended messages: a handshake advertising which
/// extensions each side speaks and under which numbers, then metadata requested
/// in 16 KiB pieces and reassembled.
/// </summary>
public static class MetadataExchange
{
    /// <summary>Metadata is cut into 16 KiB pieces, like everything else on this wire.</summary>
    public const int PieceLength = 16 * 1024;

    /// <summary>The id of BEP 10's own handshake, which is fixed. Everything else is negotiated.</summary>
    public const byte HandshakeId = 0;

    /// <summary>The number we tell peers to use when sending us ut_metadata messages.</summary>
    public const byte OurMetadataId = 1;

    /// <summary>
    /// A torrent's metadata cannot sensibly be this large; a peer claiming
    /// otherwise is trying to make us allocate.
    /// </summary>
    public const int MaxMetadataBytes = 8 * 1024 * 1024;

    private const int Request = 0;
    private const int Data = 1;
    private const int Reject = 2;

    /// <summary>Our BEP 10 handshake: what we speak, and under what numbers.</summary>
    public static PeerMessage Handshake()
    {
        var body = Bencode.Dictionary(
            ("m", Bencode.Dictionary(("ut_metadata", Bencode.Number(OurMetadataId)))),
            ("v", Bencode.String("NetTrans 1.0")));

        return PeerMessage.Extended(HandshakeId, Bencode.Encode(body));
    }

    /// <summary>
    /// What a peer's extended handshake told us: the id to use when sending it
    /// ut_metadata, and how large the metadata is.
    /// </summary>
    /// <param name="MetadataId">Zero when the peer does not speak ut_metadata.</param>
    /// <param name="MetadataSize">Zero when it did not say.</param>
    public sealed record Capabilities(byte MetadataId, int MetadataSize)
    {
        public bool SupportsMetadata => MetadataId != 0;
    }

    /// <summary>Reads a peer's extended handshake.</summary>
    public static Capabilities ReadHandshake(ReadOnlySpan<byte> payload)
    {
        // The first byte is the extended id; the rest is a bencoded dictionary.
        if (payload.Length < 2) return new Capabilities(0, 0);

        try
        {
            var body = Bencode.DecodeDictionary(payload[1..].ToArray());

            long id = body.Dictionary("m")?.Number("ut_metadata") ?? 0;
            long size = body.Number("metadata_size") ?? 0;

            return new Capabilities(
                id is > 0 and < 256 ? (byte)id : (byte)0,
                size is > 0 and <= MaxMetadataBytes ? (int)size : 0);
        }
        catch (BencodeException)
        {
            // A peer whose handshake is not bencode is not one we can talk
            // extensions with; it is still fine for blocks.
            return new Capabilities(0, 0);
        }
    }

    /// <summary>Asks a peer for one 16 KiB piece of the metadata.</summary>
    public static PeerMessage RequestPiece(byte metadataId, int piece) =>
        PeerMessage.Extended(metadataId, Bencode.Encode(Bencode.Dictionary(
            ("msg_type", Bencode.Number(Request)),
            ("piece", Bencode.Number(piece)))));

    /// <summary>Answers with one piece: a bencoded header, then the raw bytes after it.</summary>
    public static PeerMessage SendPiece(byte metadataId, int piece, int totalSize, ReadOnlySpan<byte> bytes)
    {
        var header = Bencode.Encode(Bencode.Dictionary(
            ("msg_type", Bencode.Number(Data)),
            ("piece", Bencode.Number(piece)),
            ("total_size", Bencode.Number(totalSize))));

        var body = new byte[header.Length + bytes.Length];
        header.CopyTo(body, 0);
        bytes.CopyTo(body.AsSpan(header.Length));

        return PeerMessage.Extended(metadataId, body);
    }

    public static PeerMessage RejectPiece(byte metadataId, int piece) =>
        PeerMessage.Extended(metadataId, Bencode.Encode(Bencode.Dictionary(
            ("msg_type", Bencode.Number(Reject)),
            ("piece", Bencode.Number(piece)))));

    /// <summary>What arrived in a ut_metadata message.</summary>
    /// <param name="Kind">0 request, 1 data, 2 reject.</param>
    /// <param name="Piece">Which 16 KiB piece it is about.</param>
    /// <param name="TotalSize">Stated size of the whole metadata, for a data message.</param>
    /// <param name="Bytes">The piece's bytes, for a data message.</param>
    public sealed record Incoming(int Kind, int Piece, int TotalSize, byte[] Bytes)
    {
        public bool IsData => Kind == Data;

        public bool IsRequest => Kind == Request;

        public bool IsReject => Kind == Reject;
    }

    /// <summary>
    /// Reads a ut_metadata message.
    ///
    /// The awkward part of BEP 9 is that a data message is a bencoded
    /// dictionary followed immediately by raw bytes, with no length between
    /// them -- so where the payload starts can only be known by decoding the
    /// dictionary and noting where it ended. That is what the decoder's
    /// recorded spans are for.
    /// </summary>
    public static Incoming? Read(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2) return null;

        var body = payload[1..].ToArray();

        BDictionary header;
        try
        {
            header = Bencode.DecodeDictionary(body);
        }
        catch (BencodeException)
        {
            return null;
        }

        int kind = (int)(header.Number("msg_type") ?? -1);
        int piece = (int)(header.Number("piece") ?? -1);
        if (kind < 0 || piece < 0) return null;

        int total = (int)(header.Number("total_size") ?? 0);

        // The dictionary's own length is where the raw bytes begin.
        int offset = header.Start + header.Length;
        var bytes = offset < body.Length ? body.AsSpan(offset).ToArray() : Array.Empty<byte>();

        return new Incoming(kind, piece, total, bytes);
    }

    /// <summary>How many 16 KiB pieces a metadata of this size is cut into.</summary>
    public static int PieceCount(int totalSize) => (totalSize + PieceLength - 1) / PieceLength;
}

/// <summary>
/// Collects the metadata pieces as they arrive and hands back the info
/// dictionary once it is whole and hashes to the info hash we asked for.
/// </summary>
public sealed class MetadataBuffer
{
    private readonly object _gate = new();
    private readonly byte[] _infoHash;

    private byte[]? _bytes;
    private bool[]? _received;

    public MetadataBuffer(byte[] infoHash) => _infoHash = infoHash;

    /// <summary>Zero until a peer has told us the size.</summary>
    public int TotalSize { get; private set; }

    public bool IsSized => TotalSize > 0;

    public bool IsComplete
    {
        get
        {
            lock (_gate) return _received is not null && _received.All(received => received);
        }
    }

    /// <summary>
    /// Sets the size, from a peer's extended handshake. A peer offering a
    /// different size later is ignored: the first plausible one wins, and the
    /// hash check at the end is what actually decides.
    /// </summary>
    public bool Size(int totalSize)
    {
        if (totalSize is <= 0 or > MetadataExchange.MaxMetadataBytes) return false;

        lock (_gate)
        {
            if (IsSized) return TotalSize == totalSize;

            TotalSize = totalSize;
            _bytes = new byte[totalSize];
            _received = new bool[MetadataExchange.PieceCount(totalSize)];

            return true;
        }
    }

    /// <summary>The pieces still missing, in order.</summary>
    public IReadOnlyList<int> Missing()
    {
        lock (_gate)
        {
            if (_received is null) return Array.Empty<int>();

            var missing = new List<int>();

            for (int i = 0; i < _received.Length; i++)
            {
                if (!_received[i]) missing.Add(i);
            }

            return missing;
        }
    }

    /// <summary>
    /// Takes a piece. Returns false for one that does not fit -- the wrong
    /// length, or an index past the end -- since a peer sending those is broken
    /// or probing.
    /// </summary>
    public bool Add(int piece, ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            if (_bytes is null || _received is null) return false;
            if (piece < 0 || piece >= _received.Length) return false;

            int offset = piece * MetadataExchange.PieceLength;
            int expected = Math.Min(MetadataExchange.PieceLength, _bytes.Length - offset);

            // Every piece is full except the last. A short one anywhere else
            // would leave a hole that the hash would catch, but refusing it
            // here says why.
            if (bytes.Length != expected) return false;

            bytes.CopyTo(_bytes.AsSpan(offset));
            _received[piece] = true;

            return true;
        }
    }

    /// <summary>
    /// The metainfo, once every piece has arrived and the whole thing hashes to
    /// the info hash the magnet named. Null while incomplete.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// It is complete and does not hash right, which means a peer lied or the
    /// pieces came from two different torrents.
    /// </exception>
    public TorrentMetainfo? Build(IReadOnlyList<Uri> trackers)
    {
        byte[] bytes;

        lock (_gate)
        {
            if (_bytes is null || _received is null || !_received.All(received => received)) return null;

            bytes = _bytes;
        }

        return TorrentMetainfo.FromInfoDictionary(bytes, _infoHash, trackers);
    }

    /// <summary>Whether the collected bytes hash to what was asked for, without throwing.</summary>
    public bool Verifies()
    {
        lock (_gate)
        {
            if (_bytes is null || _received is null || !_received.All(received => received)) return false;

            return SHA1.HashData(_bytes).AsSpan().SequenceEqual(_infoHash);
        }
    }

    /// <summary>
    /// Throws away everything collected so far.
    ///
    /// Needed because the pieces can come from several peers: if the assembled
    /// whole does not hash, one of them lied, and there is no way to tell
    /// which. Starting over is the only honest response.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            TotalSize = 0;
            _bytes = null;
            _received = null;
        }
    }
}

/// <summary>
/// Fetches a torrent's metainfo from one peer, for a magnet link.
///
/// Deliberately a separate conversation from <see cref="PeerSession"/>: until
/// the metainfo arrives there is no piece count, no file list and no way to
/// verify anything, so none of what a normal session does can happen yet.
/// </summary>
public sealed class MetadataSession
{
    private readonly Stream _stream;
    private readonly MetadataBuffer _buffer;

    public MetadataSession(Stream stream, MetadataBuffer buffer)
    {
        _stream = stream;
        _buffer = buffer;
    }

    public IPEndPoint? Address { get; init; }

    /// <summary>
    /// Handshakes, asks for every piece of the metadata, and returns when the
    /// buffer is full or the peer stops being useful.
    /// </summary>
    public async Task RunAsync(byte[] infoHash, byte[] peerId, CancellationToken cancellationToken)
    {
        await _stream
            .WriteAsync(PeerWire.BuildHandshake(infoHash, peerId, supportsExtended: true), cancellationToken)
            .ConfigureAwait(false);

        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        var theirs = PeerWire.ParseHandshake(
            await PeerWire.ReadExactAsync(_stream, PeerWire.HandshakeLength, cancellationToken).ConfigureAwait(false));

        if (!theirs.InfoHash.AsSpan().SequenceEqual(infoHash))
        {
            throw new PeerException("对方握手的 info_hash 与磁力链不符。");
        }

        // A peer without BEP 10 has no way to send metadata, whatever else it
        // can do for us later.
        if (!theirs.SupportsExtended) throw new PeerException("对方不支持扩展协议，无法提供元数据。");

        await SendAsync(MetadataExchange.Handshake(), cancellationToken).ConfigureAwait(false);

        byte metadataId = 0;

        while (!cancellationToken.IsCancellationRequested && !_buffer.IsComplete)
        {
            var message = await PeerWire.ReadMessageAsync(_stream, cancellationToken).ConfigureAwait(false);
            if (message.Kind != PeerMessageKind.Extended) continue;

            // The extended handshake is always id 0; anything else is one of
            // the extensions it named.
            if (message.Payload.Length > 0 && message.Payload[0] == MetadataExchange.HandshakeId)
            {
                var capabilities = MetadataExchange.ReadHandshake(message.Payload);

                if (!capabilities.SupportsMetadata) throw new PeerException("对方不提供 ut_metadata。");
                if (!_buffer.Size(capabilities.MetadataSize)) throw new PeerException("对方给出的元数据大小不可用。");

                metadataId = capabilities.MetadataId;

                foreach (int piece in _buffer.Missing())
                {
                    await SendAsync(MetadataExchange.RequestPiece(metadataId, piece), cancellationToken)
                        .ConfigureAwait(false);
                }

                continue;
            }

            if (metadataId == 0) continue;

            var incoming = MetadataExchange.Read(message.Payload);
            if (incoming is null) continue;

            // A reject means this peer does not have that piece; nothing to do
            // but let another peer supply it.
            if (incoming.IsReject) throw new PeerException($"对方拒绝提供第 {incoming.Piece} 块元数据。");

            if (incoming.IsData) _buffer.Add(incoming.Piece, incoming.Bytes);
        }
    }

    private async Task SendAsync(PeerMessage message, CancellationToken cancellationToken)
    {
        await _stream.WriteAsync(PeerWire.Encode(message), cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
