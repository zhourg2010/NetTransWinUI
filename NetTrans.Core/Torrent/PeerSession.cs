using System.Net;
using System.Net.Sockets;

namespace NetTrans.Torrent;

/// <summary>
/// Opens a connection to a peer. Abstracted so the swarm can be tested against
/// a pair of in-memory streams rather than a network.
/// </summary>
public interface IPeerConnector
{
    Task<Stream> ConnectAsync(IPEndPoint peer, CancellationToken cancellationToken);
}

/// <summary>The real socket.</summary>
public sealed class TcpPeerConnector : IPeerConnector
{
    /// <summary>Most peers in a tracker's list are gone; waiting long on each wastes the swarm.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    public async Task<Stream> ConnectAsync(IPEndPoint peer, CancellationToken cancellationToken)
    {
        var client = new TcpClient(peer.AddressFamily);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);

        try
        {
            await client.ConnectAsync(peer.Address, peer.Port, deadline.Token).ConfigureAwait(false);

            // Small writes, sent now: a request held back by Nagle's algorithm
            // is a round trip of latency on every block.
            client.NoDelay = true;

            return client.GetStream();
        }
        catch (Exception exception)
        {
            client.Dispose();

            throw exception is OperationCanceledException && !cancellationToken.IsCancellationRequested
                ? new PeerException($"连接 {peer} 超时。")
                : new PeerException($"无法连接 {peer}：{exception.Message}", exception);
        }
    }
}

/// <summary>
/// One conversation with one peer: handshake, then take pieces from the picker
/// and fetch them a block at a time until there is nothing left to want.
///
/// The protocol is not a request/response one. A peer chokes and unchokes us
/// when it likes, sends blocks in whatever order it finishes them, and may send
/// a block we already have. So this reads messages in a loop and reacts, rather
/// than waiting for the answer to a particular request.
/// </summary>
public sealed class PeerSession
{
    /// <summary>
    /// How many block requests to keep in flight. One at a time would spend a
    /// round trip per 16 KiB; too many and a slow peer holds pieces hostage.
    /// </summary>
    private const int PipelineDepth = 8;

    /// <summary>
    /// How many pieces a peer may fail to hash before we stop talking to it.
    ///
    /// One is bad luck -- a single corrupt block ruins the piece. Repeatedly is
    /// a peer that is broken or malicious, and re-fetching from it forever
    /// would stall the torrent on that piece while other peers wait.
    /// </summary>
    private const int MaxBadPieces = 2;

    private readonly Stream _stream;
    private readonly TorrentMetainfo _torrent;
    private readonly PiecePicker _picker;
    private readonly PieceStore _store;

    private byte[] _peerBitfield;
    private bool _choked = true;
    private bool _interested;

    /// <summary>Whether we are refusing to serve this peer. Everyone starts choked.</summary>
    private bool _choking = true;


    public PeerSession(
        Stream stream,
        TorrentMetainfo torrent,
        PiecePicker picker,
        PieceStore store,
        IPEndPoint? address = null)
    {
        _stream = stream;
        _torrent = torrent;
        _picker = picker;
        _store = store;
        Address = address;

        _peerBitfield = new byte[PeerWire.BitfieldLength(torrent.PieceCount)];
    }

    public IPEndPoint? Address { get; }

    /// <summary>
    /// Whether to stay connected and keep serving once there is nothing left to
    /// download from this peer.
    /// </summary>
    public bool Seed { get; set; } = true;

    /// <summary>Whether this peer wants something we have. Shown in the inspector's 连接 tab.</summary>
    public bool PeerIsInterested { get; private set; }

    /// <summary>Bytes of verified pieces this peer contributed.</summary>
    public long Downloaded { get; private set; }

    /// <summary>
    /// Bytes served to this peer.
    ///
    /// Not bookkeeping: this is what a tracker is told, and a client that only
    /// ever reports zero is one a private tracker is right to ban and a public
    /// swarm is right to choke.
    /// </summary>
    public long Uploaded { get; private set; }

    /// <summary>Pieces this peer sent that did not hash right.</summary>
    public int BadPieces { get; private set; }

    /// <summary>Raised whenever a piece is verified and written, so the shell can move a bar.</summary>
    public event EventHandler<int>? PieceCompleted;

    /// <summary>Raised with the size of each block served, so upload can be metered.</summary>
    public event EventHandler<int>? BlockServed;

    /// <summary>
    /// Raised with the size of each block that arrives, so this peer's rate can
    /// be metered as it comes rather than in whole-piece jumps.
    /// </summary>
    public event EventHandler<int>? BlockReceived;

    /// <summary>
    /// Runs until the torrent is complete, the peer has nothing left we want,
    /// or the connection ends.
    /// </summary>
    public async Task RunAsync(byte[] infoHash, byte[] peerId, CancellationToken cancellationToken)
    {
        var handshake = await ExchangeHandshakeAsync(infoHash, peerId, cancellationToken).ConfigureAwait(false);

        // A peer that answers with a different torrent's hash is misrouted or
        // lying; either way its blocks are not ours.
        if (!handshake.InfoHash.AsSpan().SequenceEqual(infoHash))
        {
            throw new PeerException("对方握手的 info_hash 与本任务不符。");
        }

        // Tell it what we already have, so it knows whether we are worth
        // anything to it.
        await SendAsync(PeerMessage.Bitfield(_picker.Bitfield()), cancellationToken).ConfigureAwait(false);

        PieceBuffer? current = null;
        int wanted = -1;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Nothing left to fetch. Leaving here is what makes a client a
                // leech: the peer on the other end may still want something
                // from us, and a finished download is exactly when we are worth
                // the most to it.
                if (_picker.IsComplete)
                {
                    if (!Seed) break;

                    await ServeUntilDoneAsync(cancellationToken).ConfigureAwait(false);
                    break;
                }

                // Take a piece as soon as we are unchoked and have none.
                if (current is null && !_choked)
                {
                    wanted = _picker.Take(_peerBitfield);

                    // Nothing this peer has that we still want. That is the end
                    // of the conversation only if we are not seeding: a peer we
                    // want nothing from may still want something from us, and
                    // hanging up on it is exactly the behaviour that makes a
                    // client unwelcome.
                    if (wanted < 0)
                    {
                        if (!Seed) break;

                        await UpdateInterestAsync(cancellationToken).ConfigureAwait(false);
                        await ServeUntilDoneAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    }

                    current = new PieceBuffer(wanted, (int)_torrent.LengthOfPiece(wanted));
                    await RequestAsync(current, cancellationToken).ConfigureAwait(false);
                }

                await UpdateInterestAsync(cancellationToken).ConfigureAwait(false);

                var message = await PeerWire.ReadMessageAsync(_stream, cancellationToken).ConfigureAwait(false);

                switch (message.Kind)
                {
                    case PeerMessageKind.Choke:
                        _choked = true;

                        // Whatever was half-collected is not ours to hold: give
                        // the piece back so another peer can take it.
                        if (current is not null)
                        {
                            _picker.Return(current.Index);
                            current = null;
                        }

                        break;

                    case PeerMessageKind.Unchoke:
                        _choked = false;

                        // Requests made before a choke are forgotten by the peer,
                        // so anything outstanding is re-asked here.
                        if (current is not null) await RequestAsync(current, cancellationToken).ConfigureAwait(false);
                        break;

                    case PeerMessageKind.Bitfield:
                        AcceptBitfield(message.Payload);
                        break;

                    case PeerMessageKind.Have:
                        if (message.PieceIndex >= 0 && message.PieceIndex < _torrent.PieceCount)
                        {
                            PeerWire.SetPiece(_peerBitfield, message.PieceIndex);
                            _picker.Saw(message.PieceIndex);
                        }

                        break;

                    case PeerMessageKind.Interested:
                        PeerIsInterested = true;

                        // Unchoked as soon as they ask. A real client rations
                        // its upload slots; refusing outright is what makes a
                        // leech, so the default here is to say yes.
                        if (_choking)
                        {
                            _choking = false;
                            await SendAsync(PeerMessage.Unchoke, cancellationToken).ConfigureAwait(false);
                        }

                        break;

                    case PeerMessageKind.NotInterested:
                        PeerIsInterested = false;
                        break;

                    case PeerMessageKind.Request:
                        await ServeAsync(message, cancellationToken).ConfigureAwait(false);
                        break;

                    case PeerMessageKind.Piece:
                        if (current is null || message.PieceIndex != current.Index) break;

                        // A block that does not line up is refused rather than
                        // written at whatever offset the peer named.
                        if (!current.Add(message.BlockOffset, message.Block)) break;

                        BlockReceived?.Invoke(this, message.Block.Length);

                        if (!current.IsComplete)
                        {
                            await RequestAsync(current, cancellationToken).ConfigureAwait(false);
                            break;
                        }

                        if (await FinishAsync(current, cancellationToken).ConfigureAwait(false))
                        {
                            current = null;
                            break;
                        }

                        // Bad bytes. Give the piece back so another peer can
                        // have it, and stop talking to this one if it keeps
                        // happening -- retrying from the same peer forever
                        // stalls the whole torrent on one piece.
                        _picker.Return(current.Index);
                        current = null;

                        if (BadPieces >= MaxBadPieces)
                        {
                            throw new PeerException($"对方发来 {BadPieces} 个校验失败的分片。");
                        }

                        break;
                }
            }
        }
        finally
        {
            // Anything reserved and unfinished goes back to the picker, or the
            // torrent would stall one piece short.
            if (current is not null) _picker.Return(current.Index);

            _picker.Left(_peerBitfield);
        }
    }

    /// <summary>
    /// Answers a peer's request for a block, when we have the piece and are not
    /// choking them.
    ///
    /// A request for something we do not have is ignored rather than answered
    /// with an error: the protocol has no error, and a peer asking for a piece
    /// we never announced is either stale or probing.
    /// </summary>
    private async Task ServeAsync(PeerMessage request, CancellationToken cancellationToken)
    {
        if (_choking) return;
        if (!_picker.IsDone(request.PieceIndex)) return;

        // The length is theirs, so it is bounded before it becomes an
        // allocation and a disk read.
        if (request.BlockLength is <= 0 or > PeerWire.BlockLength * 2) return;

        var block = await _store
            .ReadAsync(request.PieceIndex, request.BlockOffset, request.BlockLength, cancellationToken)
            .ConfigureAwait(false);

        if (block is null) return;

        await SendAsync(
            PeerMessage.Piece(request.PieceIndex, request.BlockOffset, block),
            cancellationToken).ConfigureAwait(false);

        Uploaded += block.Length;
        BlockServed?.Invoke(this, block.Length);
    }

    private async Task<PeerHandshake> ExchangeHandshakeAsync(byte[] infoHash, byte[] peerId, CancellationToken cancellationToken)
    {
        await _stream.WriteAsync(PeerWire.BuildHandshake(infoHash, peerId), cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        var reply = await PeerWire
            .ReadExactAsync(_stream, PeerWire.HandshakeLength, cancellationToken)
            .ConfigureAwait(false);

        return PeerWire.ParseHandshake(reply);
    }

    private void AcceptBitfield(byte[] bitfield)
    {
        // A bitfield of the wrong size, or with bits set past the last piece,
        // is a broken peer. Ignoring it leaves the peer at "has nothing", which
        // costs us this connection rather than a corrupt download.
        if (!PeerWire.IsValidBitfield(bitfield, _torrent.PieceCount)) return;

        _peerBitfield = bitfield;
        _picker.Saw(bitfield);
    }

    private async Task UpdateInterestAsync(CancellationToken cancellationToken)
    {
        bool want = _picker.WantsAnythingFrom(_peerBitfield);
        if (want == _interested) return;

        _interested = want;
        await SendAsync(want ? PeerMessage.Interested : PeerMessage.NotInterested, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Tops the pipeline back up to its depth with blocks still missing.</summary>
    private async Task RequestAsync(PieceBuffer piece, CancellationToken cancellationToken)
    {
        if (_choked) return;

        foreach (int block in piece.Missing().Take(PipelineDepth))
        {
            var (offset, length) = piece.Block(block);
            await SendAsync(PeerMessage.Request(piece.Index, offset, length), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> FinishAsync(PieceBuffer piece, CancellationToken cancellationToken)
    {
        if (!await _store.WriteAsync(piece.Index, piece.ToArray(), cancellationToken).ConfigureAwait(false))
        {
            BadPieces++;
            return false;
        }

        Downloaded += piece.Length;
        _picker.Complete(piece.Index);

        // Telling the swarm we have it is what makes us useful to it.
        await SendAsync(PeerMessage.Have(piece.Index), cancellationToken).ConfigureAwait(false);

        PieceCompleted?.Invoke(this, piece.Index);
        return true;
    }

    /// <summary>
    /// Keeps answering requests after we have stopped downloading, until the
    /// peer goes away or we are told to stop.
    /// </summary>
    private async Task ServeUntilDoneAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await PeerWire.ReadMessageAsync(_stream, cancellationToken).ConfigureAwait(false);

            switch (message.Kind)
            {
                case PeerMessageKind.Interested:
                    PeerIsInterested = true;

                    if (_choking)
                    {
                        _choking = false;
                        await SendAsync(PeerMessage.Unchoke, cancellationToken).ConfigureAwait(false);
                    }

                    break;

                case PeerMessageKind.NotInterested:
                    PeerIsInterested = false;
                    break;

                case PeerMessageKind.Request:
                    await ServeAsync(message, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    private async Task SendAsync(PeerMessage message, CancellationToken cancellationToken)
    {
        await _stream.WriteAsync(PeerWire.Encode(message), cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
