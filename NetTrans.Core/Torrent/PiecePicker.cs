namespace NetTrans.Torrent;

/// <summary>
/// Decides which piece to ask a peer for next, and keeps two peers from being
/// sent after the same one.
///
/// The strategy is rarest-first among the pieces still wanted, which is what
/// keeps a swarm healthy: everyone grabbing piece 0 first leaves the rare
/// pieces rarer still, and a torrent whose last piece has one seed stalls for
/// everybody. Sequential order is the tie-break, so a partly-done file is
/// contiguous where it can be.
/// </summary>
public sealed class PiecePicker
{
    private readonly object _gate = new();
    private readonly bool[] _done;
    private readonly bool[] _assigned;
    private readonly bool[] _wanted;
    private readonly int[] _availability;

    public PiecePicker(int pieces)
    {
        _done = new bool[pieces];
        _assigned = new bool[pieces];
        _wanted = new bool[pieces];
        _availability = new int[pieces];

        Array.Fill(_wanted, true);
    }

    /// <summary>
    /// 顺序下载. Rarest-first is better for the swarm; sequential is what makes
    /// a partly-downloaded video playable, which is the only reason to want it.
    /// </summary>
    public bool Sequential { get; set; }

    /// <summary>
    /// The classic way a torrent stalls: the last piece is with one slow peer
    /// and everybody waits on it.
    ///
    /// With this on, once every piece still wanted has been handed to someone,
    /// it may be handed to another peer as well and whoever answers first wins.
    /// The condition is deliberately "nothing unassigned left" rather than a
    /// count of remaining pieces: a threshold of four would put a four-piece
    /// torrent in the endgame from the first request, duplicating everything.
    /// </summary>
    public bool Endgame { get; set; }

    /// <summary>Whether duplicate requests are being handed out right now.</summary>
    public bool InEndgame
    {
        get
        {
            lock (_gate) return Endgame && Remaining() > 0 && !AnythingUnassigned();
        }
    }

    public int Count => _done.Length;

    public int CompletedCount
    {
        get
        {
            lock (_gate) return _done.Count(done => done);
        }
    }

    /// <summary>Whether any wanted piece is still free to hand out. Called under the lock.</summary>
    private bool AnythingUnassigned()
    {
        for (int i = 0; i < _done.Length; i++)
        {
            if (_wanted[i] && !_done[i] && !_assigned[i]) return true;
        }

        return false;
    }

    /// <summary>Wanted and not yet had. Called under the lock.</summary>
    private int Remaining()
    {
        int remaining = 0;

        for (int i = 0; i < _done.Length; i++)
        {
            if (_wanted[i] && !_done[i]) remaining++;
        }

        return remaining;
    }

    /// <summary>
    /// Whether everything still wanted has landed. A deselected file's pieces
    /// do not hold this back -- that is the point of deselecting it.
    /// </summary>
    public bool IsComplete
    {
        get
        {
            lock (_gate) return Remaining() == 0;
        }
    }

    /// <summary>How many wanted pieces are still missing.</summary>
    public int RemainingCount
    {
        get
        {
            lock (_gate) return Remaining();
        }
    }

    /// <summary>
    /// Narrows the download to the pieces these cover -- 选择文件, for the
    /// multi-file torrents where half the content is extras nobody asked for.
    ///
    /// A piece that straddles a selected and a deselected file is still
    /// wanted: it cannot be had in halves.
    /// </summary>
    public void WantOnly(IEnumerable<int> pieces)
    {
        var keep = new HashSet<int>(pieces);

        lock (_gate)
        {
            for (int i = 0; i < _wanted.Length; i++) _wanted[i] = keep.Contains(i);
        }
    }

    /// <summary>Whether a piece is wanted at all.</summary>
    public bool IsWanted(int index)
    {
        lock (_gate) return index >= 0 && index < _wanted.Length && _wanted[index];
    }

    /// <summary>Whether a piece is already verified and written.</summary>
    public bool IsDone(int index)
    {
        lock (_gate) return index >= 0 && index < _done.Length && _done[index];
    }

    /// <summary>A snapshot of what has been finished, as a bitfield.</summary>
    public byte[] Bitfield()
    {
        lock (_gate)
        {
            var bits = new byte[PeerWire.BitfieldLength(_done.Length)];

            for (int i = 0; i < _done.Length; i++)
            {
                if (_done[i]) PeerWire.SetPiece(bits, i);
            }

            return bits;
        }
    }

    /// <summary>Marks pieces a previous run already finished.</summary>
    public void Restore(byte[] bitfield)
    {
        lock (_gate)
        {
            for (int i = 0; i < _done.Length; i++)
            {
                if (PeerWire.HasPiece(bitfield, i)) _done[i] = true;
            }
        }
    }

    /// <summary>Counts a peer's pieces towards rarity.</summary>
    public void Saw(byte[] bitfield)
    {
        lock (_gate)
        {
            for (int i = 0; i < _availability.Length; i++)
            {
                if (PeerWire.HasPiece(bitfield, i)) _availability[i]++;
            }
        }
    }

    /// <summary>Counts one more piece towards rarity, for a `have` message.</summary>
    public void Saw(int index)
    {
        lock (_gate)
        {
            if (index >= 0 && index < _availability.Length) _availability[index]++;
        }
    }

    /// <summary>Uncounts a peer's pieces when it disconnects.</summary>
    public void Left(byte[] bitfield)
    {
        lock (_gate)
        {
            for (int i = 0; i < _availability.Length; i++)
            {
                if (PeerWire.HasPiece(bitfield, i) && _availability[i] > 0) _availability[i]--;
            }
        }
    }

    /// <summary>
    /// The next piece this peer should be asked for, or -1 when it has nothing
    /// wanted. The piece is reserved until it is completed or given back.
    /// </summary>
    public int Take(byte[] peerBitfield)
    {
        lock (_gate)
        {
            // Only once there is nothing unassigned left to hand out: then a
            // duplicate request costs one piece of bandwidth and saves waiting
            // out the slowest peer in the swarm.
            bool endgame = Endgame && !AnythingUnassigned();

            int best = -1;
            int rarity = int.MaxValue;

            for (int i = 0; i < _done.Length; i++)
            {
                if (_done[i] || !_wanted[i]) continue;
                if (_assigned[i] && !endgame) continue;
                if (!PeerWire.HasPiece(peerBitfield, i)) continue;

                // Sequential takes the first one it can, so a partly-downloaded
                // file is playable from the front.
                if (Sequential)
                {
                    best = i;
                    break;
                }

                // Availability of zero means no connected peer claims it, which
                // for a piece this peer has means our count is stale; treat it
                // as the rarest rather than skipping it.
                int seen = _availability[i] == 0 ? 1 : _availability[i];

                if (seen >= rarity) continue;

                rarity = seen;
                best = i;
            }

            if (best >= 0) _assigned[best] = true;

            return best;
        }
    }

    /// <summary>Gives a reserved piece back, for a peer that choked or died mid-piece.</summary>
    public void Return(int index)
    {
        lock (_gate)
        {
            if (index >= 0 && index < _assigned.Length) _assigned[index] = false;
        }
    }

    /// <summary>Marks a piece verified and written. It is never handed out again.</summary>
    public void Complete(int index)
    {
        lock (_gate)
        {
            if (index < 0 || index >= _done.Length) return;

            _done[index] = true;
            _assigned[index] = false;
        }
    }

    /// <summary>
    /// Whether anything is still wanted that this peer could provide -- which
    /// is what "interested" means on the wire.
    /// </summary>
    public bool WantsAnythingFrom(byte[] peerBitfield)
    {
        lock (_gate)
        {
            for (int i = 0; i < _done.Length; i++)
            {
                if (!_done[i] && _wanted[i] && PeerWire.HasPiece(peerBitfield, i)) return true;
            }

            return false;
        }
    }
}
