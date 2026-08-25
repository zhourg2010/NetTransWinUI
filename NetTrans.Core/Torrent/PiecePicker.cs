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
    private readonly int[] _availability;

    public PiecePicker(int pieces)
    {
        _done = new bool[pieces];
        _assigned = new bool[pieces];
        _availability = new int[pieces];
    }

    public int Count => _done.Length;

    public int CompletedCount
    {
        get
        {
            lock (_gate) return _done.Count(done => done);
        }
    }

    public bool IsComplete
    {
        get
        {
            lock (_gate) return _done.All(done => done);
        }
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
            int best = -1;
            int rarity = int.MaxValue;

            for (int i = 0; i < _done.Length; i++)
            {
                if (_done[i] || _assigned[i]) continue;
                if (!PeerWire.HasPiece(peerBitfield, i)) continue;

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
                if (!_done[i] && PeerWire.HasPiece(peerBitfield, i)) return true;
            }

            return false;
        }
    }
}
