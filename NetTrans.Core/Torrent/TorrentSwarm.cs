using System.Net;
using NetTrans.Download;

namespace NetTrans.Torrent;

/// <summary>How a torrent is going, for the row and the inspector.</summary>
/// <param name="Downloaded">Bytes of verified pieces.</param>
/// <param name="Total">Bytes the torrent holds.</param>
/// <param name="Pieces">Verified pieces.</param>
/// <param name="TotalPieces">Pieces the torrent has.</param>
/// <param name="ConnectedPeers">Peers currently talking to us.</param>
/// <param name="KnownPeers">Peers a tracker has told us about.</param>
/// <param name="Uploaded">Bytes served to peers, which is what a tracker counts as a share.</param>
public sealed record SwarmProgress(
    long Downloaded,
    long Total,
    int Pieces,
    int TotalPieces,
    int ConnectedPeers,
    int KnownPeers,
    long Uploaded = 0);

/// <summary>
/// The download itself: announce, connect to what comes back, and keep enough
/// peers busy to finish.
///
/// A swarm is mostly disappointment. Half a tracker's peers are gone, a third
/// of the rest never unchoke, and the ones that do come and go -- so this is
/// built around peers failing rather than around them working: a session that
/// dies is replaced from the pool, and the announce is repeated for more.
/// </summary>
public sealed class TorrentSwarm
{
    private readonly TorrentMetainfo _torrent;
    private readonly IPeerConnector _connector;
    private readonly TrackerPool _trackers;
    private readonly PieceStore _store;
    private readonly PiecePicker _picker;
    private readonly byte[] _peerId;
    private readonly IClockNow _now;

    private readonly object _gate = new();
    private readonly HashSet<string> _attempted = new(StringComparer.Ordinal);
    private readonly Queue<IPEndPoint> _pool = new();

    private int _connected;
    private int _known;
    private long _uploaded;

    public TorrentSwarm(
        TorrentMetainfo torrent,
        IPeerConnector connector,
        TrackerPool trackers,
        PieceStore store,
        PiecePicker picker,
        byte[]? peerId = null,
        IClockNow? now = null)
    {
        _torrent = torrent;
        _connector = connector;
        _trackers = trackers;
        _store = store;
        _picker = picker;
        _peerId = peerId ?? TrackerProtocol.NewPeerId();
        _now = now ?? SystemNow.Instance;
    }

    /// <summary>How many peers to talk to at once.</summary>
    public int MaxPeers { get; set; } = 8;

    /// <summary>The port we claim to listen on. Peers use it to call back.</summary>
    public int Port { get; set; } = 6881;

    /// <summary>
    /// Whether to keep serving peers after there is nothing left to fetch from
    /// them. Off makes a leech, which public swarms choke and private trackers
    /// ban, so it is on.
    /// </summary>
    public bool Seed { get; set; } = true;

    /// <summary>When to stop seeding. Unlimited until a caller says otherwise.</summary>
    public SeedingLimits SeedingLimits { get; set; } = SeedingLimits.Forever;

    /// <summary>When the download finished, which is when the seeding clock starts.</summary>
    public DateTimeOffset? SeedingSince { get; private set; }

    /// <summary>The share ratio as a tracker would compute it.</summary>
    public double Ratio => SeedingLimits.RatioOf(
        Volatile.Read(ref _uploaded),
        DownloadedBytes(_picker.CompletedCount));

    /// <summary>
    /// Whether seeding has met the configured limit. Checked by the caller that
    /// owns the lifetime, since stopping is its decision rather than ours.
    /// </summary>
    public bool SeedingLimitReached(DateTimeOffset now) =>
        SeedingSince is { } since &&
        SeedingLimits.Reached(
            Volatile.Read(ref _uploaded),
            DownloadedBytes(_picker.CompletedCount),
            now - since);

    /// <summary>
    /// Whether peers may be found by any means other than the trackers.
    ///
    /// A torrent with the private flag set says no: no DHT, no peer exchange,
    /// no local discovery. There is none of that here yet, so this is currently
    /// a promise rather than a restriction -- but it is the flag anything of
    /// that kind has to consult before it is added.
    /// </summary>
    public bool PeerDiscoveryAllowed => !_torrent.IsPrivate;

    /// <summary>Raised whenever a piece lands, so progress can be shown without polling.</summary>
    public event EventHandler<SwarmProgress>? Progressed;

    /// <summary>Raised for anything worth putting in the inspector's log.</summary>
    public event EventHandler<string>? Said;

    public SwarmProgress Progress
    {
        get
        {
            int pieces = _picker.CompletedCount;

            lock (_gate)
            {
                return new SwarmProgress(
                    DownloadedBytes(pieces),
                    _torrent.TotalLength,
                    pieces,
                    _torrent.PieceCount,
                    _connected,
                    _known,
                    _uploaded);
            }
        }
    }

    /// <summary>Runs until the torrent is complete or the caller gives up.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var sessions = new List<Task>();
        bool announcedStart = false;

        // Racing the last few pieces is what stops a torrent hanging on one
        // slow peer while everything else waits.
        _picker.Endgame = true;

        while (!cancellationToken.IsCancellationRequested && !_picker.IsComplete)
        {
            if (NeedsPeers())
            {
                await AnnounceAsync(
                    announcedStart ? AnnounceEvent.None : AnnounceEvent.Started,
                    cancellationToken).ConfigureAwait(false);

                announcedStart = true;
            }

            // Fill the free slots from the pool.
            while (sessions.Count < MaxPeers && TryTakePeer(out var peer))
            {
                sessions.Add(TalkToAsync(peer, cancellationToken));
            }

            if (sessions.Count == 0)
            {
                // Nothing to talk to and nothing to try: wait for the tracker's
                // interval rather than spinning on it.
                Say("暂时没有可用的 peer，等待重新通告…");

                await Task.Delay(_trackers.Interval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var finished = await Task.WhenAny(sessions).ConfigureAwait(false);
            sessions.Remove(finished);
        }

        // Let whatever is still running unwind before the store is closed under
        // it, but do not wait on a peer that has stopped answering.
        await Task.WhenAny(Task.WhenAll(sessions), Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None))
            .ConfigureAwait(false);

        await _store.FlushAsync(CancellationToken.None).ConfigureAwait(false);

        if (!_picker.IsComplete) return;

        SeedingSince = _now.UtcNow;

        // Telling the tracker is what makes the completion count, and it costs
        // one request.
        await AnnounceAsync(AnnounceEvent.Completed, CancellationToken.None).ConfigureAwait(false);

        if (Seed) await SeedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps serving after the download is done, until the configured limit is
    /// met or the caller stops us.
    ///
    /// This is where 做种限制 is actually enforced. Storing the limit and never
    /// checking it would make the setting a decoration -- and on a private
    /// tracker, a promise about an account's ratio that nothing keeps.
    /// </summary>
    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        // A zero-length limit is "stop as soon as it finishes", which is a real
        // choice and has to be honoured before a single peer is served.
        if (SeedingLimitReached(_now.UtcNow))
        {
            Say($"已达到做种限制（{SeedingLimits.Describe()}），停止做种");
            return;
        }

        Say(SeedingLimits.IsUnlimited ? "开始做种" : $"开始做种，限制：{SeedingLimits.Describe()}");

        var sessions = new List<Task>();

        while (!cancellationToken.IsCancellationRequested)
        {
            if (SeedingLimitReached(_now.UtcNow))
            {
                Say($"已达到做种限制（分享率 {Ratio:0.00}），停止做种");
                break;
            }

            while (sessions.Count < MaxPeers && TryTakePeer(out var peer))
            {
                sessions.Add(TalkToAsync(peer, cancellationToken));
            }

            if (sessions.Count == 0)
            {
                // Nobody to serve. Re-announce on the tracker's own interval
                // rather than spinning, and let the limit be checked each time.
                await AnnounceAsync(AnnounceEvent.None, cancellationToken).ConfigureAwait(false);

                if (sessions.Count == 0 && !TryPeek())
                {
                    await Task.Delay(_trackers.Interval, cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            var finished = await Task.WhenAny(sessions).ConfigureAwait(false);
            sessions.Remove(finished);
        }
    }

    private bool TryPeek()
    {
        lock (_gate) return _pool.Count > 0;
    }

    /// <summary>Tells the trackers we are leaving, so they drop us before the interval expires.</summary>
    public async Task StopAsync()
    {
        try
        {
            await AnnounceAsync(AnnounceEvent.Stopped, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A courtesy. A tracker that will not hear it loses nothing but a
            // slot for one interval.
        }
    }

    private async Task AnnounceAsync(AnnounceEvent what, CancellationToken cancellationToken)
    {
        long done = DownloadedBytes(_picker.CompletedCount);

        var request = new AnnounceRequest(
            _torrent.InfoHash,
            _peerId,
            Port,
            // Reported honestly. A client that always says zero is one a
            // private tracker is right to ban.
            Uploaded: Volatile.Read(ref _uploaded),
            Downloaded: done,
            Left: Math.Max(0, _torrent.TotalLength - done),
            what,
            NumWant: Math.Max(MaxPeers * 4, 30));

        try
        {
            var response = await _trackers
                .AnnounceAsync(_torrent.Trackers, request, cancellationToken)
                .ConfigureAwait(false);

            int added = 0;

            lock (_gate)
            {
                foreach (var peer in response.Peers)
                {
                    // A peer already tried is not tried again this run; the
                    // tracker returns the same list every interval.
                    if (!_attempted.Add(peer.ToString())) continue;

                    _pool.Enqueue(peer);
                    added++;
                }

                _known = _attempted.Count;
            }

            Say($"通告完成：{response.Seeders} 个做种、{response.Leechers} 个下载，新增 {added} 个 peer");
        }
        catch (TrackerException exception) when (what != AnnounceEvent.Started)
        {
            // A re-announce that fails is survivable; the peers we have keep
            // working.
            Say($"重新通告失败：{exception.Message}");
        }
    }

    private async Task TalkToAsync(IPEndPoint peer, CancellationToken cancellationToken)
    {
        Stream? stream = null;

        try
        {
            stream = await _connector.ConnectAsync(peer, cancellationToken).ConfigureAwait(false);

            lock (_gate) _connected++;

            var session = new PeerSession(stream, _torrent, _picker, _store, peer) { Seed = Seed };
            session.PieceCompleted += (_, _) => Progressed?.Invoke(this, Progress);
            session.BlockServed += (_, bytes) => Interlocked.Add(ref _uploaded, bytes);

            await session.RunAsync(_torrent.InfoHash, _peerId, cancellationToken).ConfigureAwait(false);

            if (session.BadPieces > 0) Say($"{peer} 发来 {session.BadPieces} 个校验失败的分片");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception exception)
        {
            // One peer failing is the normal case, not an error to report.
            _ = exception;
        }
        finally
        {
            if (stream is not null)
            {
                lock (_gate) _connected--;

                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private bool TryTakePeer(out IPEndPoint peer)
    {
        lock (_gate)
        {
            if (_pool.Count > 0)
            {
                peer = _pool.Dequeue();
                return true;
            }
        }

        peer = default!;
        return false;
    }

    private bool NeedsPeers()
    {
        lock (_gate) return _pool.Count == 0 && _connected < MaxPeers;
    }

    /// <summary>
    /// Progress in bytes, from the pieces verified. The last piece is short, so
    /// counting whole pieces would overstate a nearly-finished torrent.
    /// </summary>
    private long DownloadedBytes(int pieces)
    {
        if (pieces <= 0) return 0;
        if (pieces >= _torrent.PieceCount) return _torrent.TotalLength;

        long total = 0;

        for (int i = 0; i < _torrent.PieceCount; i++)
        {
            if (_picker.IsDone(i)) total += _torrent.LengthOfPiece(i);
        }

        return total;
    }

    private void Say(string message) => Said?.Invoke(this, message);
}

/// <summary>
/// 断点续传 for a torrent, which is neither a byte offset nor a segment count
/// but a bitfield: pieces arrive out of order and each is verified on its own,
/// so what has to survive a restart is exactly which ones landed.
/// </summary>
/// <param name="InfoHash">Hex. A different torrent is a different file.</param>
/// <param name="Bitfield">Base64 of the completed-piece bitfield.</param>
public sealed record TorrentResumeState(string InfoHash, string Bitfield)
{
    public static TorrentResumeState From(TorrentMetainfo torrent, PiecePicker picker) =>
        new(Convert.ToHexString(torrent.InfoHash), Convert.ToBase64String(picker.Bitfield()));

    /// <summary>The saved bitfield, or null when it is not this torrent's.</summary>
    public byte[]? BitfieldFor(TorrentMetainfo torrent)
    {
        if (!string.Equals(InfoHash, Convert.ToHexString(torrent.InfoHash), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var bits = Convert.FromBase64String(Bitfield);
            return bits.Length == PeerWire.BitfieldLength(torrent.PieceCount) ? bits : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>Reads and writes the torrent sidecar next to the download.</summary>
public sealed class TorrentResumeStore
{
    private static readonly System.Text.Json.JsonSerializerOptions Options = new() { WriteIndented = false };

    public static TorrentResumeStore Instance { get; } = new();

    public static string SidecarPath(string targetPath) => targetPath + ".nettrans-bt";

    public async Task SaveAsync(string targetPath, TorrentResumeState state, CancellationToken cancellationToken)
    {
        try
        {
            string? directory = Path.GetDirectoryName(SidecarPath(targetPath));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            await using var stream = File.Create(SidecarPath(targetPath));
            await System.Text.Json.JsonSerializer
                .SerializeAsync(stream, state, Options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Losing the sidecar costs a re-download, not the app.
        }
    }

    public async Task<TorrentResumeState?> LoadAsync(string targetPath, CancellationToken cancellationToken)
    {
        string path = SidecarPath(targetPath);
        if (!File.Exists(path)) return null;

        try
        {
            await using var stream = File.OpenRead(path);
            return await System.Text.Json.JsonSerializer
                .DeserializeAsync<TorrentResumeState>(stream, Options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public void Delete(string targetPath)
    {
        try
        {
            string path = SidecarPath(targetPath);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // A stale sidecar is harmless: the next run revalidates it.
        }
    }
}
