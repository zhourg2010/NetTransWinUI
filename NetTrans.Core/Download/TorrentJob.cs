using NetTrans.Models;
using NetTrans.Net;
using NetTrans.Torrent;

namespace NetTrans.Download;

/// <summary>
/// A BitTorrent transfer, driven by the same queue as everything else.
///
/// It differs from the other two jobs in a way the queue has to tolerate rather
/// than hide: there is no server to ask how big the file is and no order in
/// which the bytes arrive. A magnet does not even know what the files are until
/// a peer has told it, so the row's name and size are unknown for the first few
/// seconds and then suddenly are not.
/// </summary>
public sealed class TorrentJob : ITransferJob
{
    private readonly IHttpTransport _transport;
    private readonly IFileSinkFactory _sinks;
    private readonly IClock _clock;
    private readonly DownloadOptions _options;
    private readonly IPeerConnector _connector;
    private readonly TorrentResumeStore? _resume;
    private readonly TokenBucket? _globalLimit;

    private readonly SpeedMeter _meter;
    private readonly SpeedMeter _uploadMeter;
    private readonly object _pauseGate = new();

    private CancellationTokenSource? _cancellation;
    private bool _pauseRequested;

    private TorrentSwarm? _swarm;
    private PiecePicker? _picker;
    private long _lastDone;
    private long _lastUploaded;
    private double _speedLimit;
    private double _uploadLimit;
    private RateGate? _downloadGate;
    private RateGate? _uploadGate;

    public TorrentJob(
        DownloadItem item,
        IHttpTransport transport,
        IFileSinkFactory sinks,
        IClock clock,
        DownloadOptions? options = null,
        IPeerConnector? connector = null,
        TorrentResumeStore? resume = null,
        TokenBucket? globalLimit = null)
    {
        Item = item;
        _transport = transport;
        _sinks = sinks;
        _clock = clock;
        _options = options ?? new DownloadOptions();
        _connector = connector ?? new TcpPeerConnector();
        _resume = resume;
        _globalLimit = globalLimit;
        _meter = new SpeedMeter(_options.Window);
        _uploadMeter = new SpeedMeter(_options.Window);
    }

    public DownloadItem Item { get; }

    /// <summary>The folder the torrent's files go into. Null until the metainfo is known.</summary>
    public string? TargetPath { get; private set; }

    /// <summary>The metainfo, once a .torrent has been read or a magnet has resolved.</summary>
    public TorrentMetainfo? Torrent { get; private set; }

    /// <summary>When to stop seeding. Set from the settings before the job starts.</summary>
    public SeedingLimits SeedingLimits { get; set; } = SeedingLimits.Forever;

    /// <summary>顺序下载, for making a partly-fetched video playable.</summary>
    public bool Sequential { get; set; }

    /// <summary>
    /// 选择文件: the paths inside the torrent that were asked for. Null or empty
    /// means all of them, which is the ordinary case.
    /// </summary>
    public IReadOnlyList<string>? WantedFiles { get; set; }

    /// <summary>
    /// 强制校验: hash what is on disk instead of trusting the resume sidecar.
    ///
    /// Asked for more often than it sounds. A sidecar can be stale, files can
    /// be moved in from elsewhere, and cross-seeding the same content under a
    /// second torrent begins with exactly this question. Cleared once done, so
    /// it costs one pass rather than every start.
    /// </summary>
    public bool Recheck { get; set; }

    /// <summary>
    /// A per-task cap, in bytes per second. A torrent's bytes come from many
    /// peers at once, so the cap is one budget for the whole swarm rather than
    /// one per connection -- and it can be moved while the transfer runs, from
    /// the inspector.
    /// </summary>
    public double SpeedLimit
    {
        get => _speedLimit;
        set
        {
            _speedLimit = value;
            if (_downloadGate is { } gate) gate.BytesPerSecond = value;
        }
    }

    /// <summary>
    /// A cap on what goes out, which HTTP transfers have no use for and a
    /// torrent very much does: an upload that saturates the line is what makes
    /// everything else on the machine feel broken.
    /// </summary>
    public double UploadLimit
    {
        get => _uploadLimit;
        set
        {
            _uploadLimit = value;
            if (_uploadGate is { } gate) gate.BytesPerSecond = value;
        }
    }

    public double EffectiveSpeedLimit => _downloadGate?.BytesPerSecond ?? SpeedLimit;

    public double BytesPerSecond => _meter.BytesPerSecond(_clock.UtcNow);

    /// <summary>
    /// One rate per connected peer, which for a torrent is what the 连接 tab is
    /// for: seven peers and one of them stalled is the thing you opened the tab
    /// to see.
    ///
    /// Down and up are added rather than shown apart, because the tab draws one
    /// bar per connection -- and while seeding, up is all there is.
    /// </summary>
    public double[] ConnectionSpeeds =>
        _swarm is { } swarm
            ? swarm.PeerRates.Select(rate => rate.Down + rate.Up).ToArray()
            : Array.Empty<double>();

    public void Pause()
    {
        CancellationTokenSource? cancellation;

        lock (_pauseGate)
        {
            _pauseRequested = true;
            cancellation = _cancellation;
        }

        cancellation?.Cancel();
    }

    public async Task<JobOutcome> RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        bool alreadyPaused;
        lock (_pauseGate)
        {
            _cancellation = linked;
            alreadyPaused = _pauseRequested;
        }

        if (alreadyPaused) linked.Cancel();

        try
        {
            return await TransferAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await PersistAsync().ConfigureAwait(false);
            Idle();

            bool paused;
            lock (_pauseGate) paused = _pauseRequested;

            if (!paused)
            {
                // Stopped from outside rather than paused. A torrent can sit
                // for a long time with nothing to talk to, so the row has to
                // say which of the two happened rather than going blank.
                Item.ErrorMessage = _swarm is { } swarm && swarm.Progress.ConnectedPeers == 0
                    ? "没有连接到任何 peer"
                    : "已停止";

                Item.Log.Add(new LogEntry(Stamp(), Item.ErrorMessage, IsError: true));
                return JobOutcome.Failed;
            }

            Item.Status = DownloadStatus.Paused;
            Item.Log.Add(new LogEntry(Stamp(), Progressed()));
            return JobOutcome.Paused;
        }
        catch (Exception exception)
        {
            await PersistAsync().ConfigureAwait(false);
            Idle();

            Item.ErrorMessage = DownloadJob.Describe(exception);
            Item.Status = DownloadStatus.Error;
            Item.Log.Add(new LogEntry(Stamp(), Item.ErrorMessage, IsError: true));
            return JobOutcome.Failed;
        }
        finally
        {
            lock (_pauseGate) _cancellation = null;
        }
    }

    /// <summary>
    /// Narrows the picker to the chosen files, if any were chosen.
    ///
    /// The size in the row becomes what will actually be fetched, which for a
    /// selection is more than the files add up to: a piece straddling a wanted
    /// and an unwanted file has to be fetched whole, because a piece is the
    /// smallest thing that can be verified.
    /// </summary>
    private void Select(TorrentMetainfo torrent, PiecePicker picker)
    {
        if (WantedFiles is not { Count: > 0 } wanted) return;

        var chosen = FileSelection.Choose(torrent, wanted);

        if (chosen.Count == 0 || chosen.Count == torrent.Files.Count) return;

        var pieces = FileSelection.WantedPieces(torrent, chosen);
        picker.WantOnly(pieces);

        long bytes = FileSelection.BytesFor(torrent, chosen);
        Item.Size = bytes;

        Item.Log.Add(new LogEntry(
            Stamp(),
            $"只下载 {chosen.Count}/{torrent.Files.Count} 个文件 · {pieces.Count} 个分片 · {FormatBytes(bytes)}"));
    }

    private async Task<JobOutcome> TransferAsync(CancellationToken cancellationToken)
    {
        Item.Status = DownloadStatus.Downloading;
        Item.ErrorMessage = null;

        var peerId = TrackerProtocol.NewPeerId();
        var trackers = new TrackerPool(new HttpTrackerClient(_transport), new UdpTrackerClient(new UdpChannel()));

        var (torrent, extraTrackers) = await ResolveAsync(peerId, trackers, cancellationToken).ConfigureAwait(false);

        Torrent = torrent;

        Item.Name = torrent.Name;
        Item.Size = torrent.TotalLength;
        Item.Checksum ??= FileHash.Pending;
        TargetPath = Path.Combine(Item.SavePath, torrent.Name);

        Item.Log.Add(new LogEntry(
            Stamp(),
            $"{torrent.Files.Count} 个文件 · {torrent.PieceCount} 个分片 · {FormatBytes(torrent.TotalLength)}"));

        if (torrent.IsPrivate) Item.Log.Add(new LogEntry(Stamp(), "私有种子：只使用 tracker，不启用 DHT / PEX"));

        var picker = new PiecePicker(torrent.PieceCount) { Sequential = Sequential };
        _picker = picker;

        Select(torrent, picker);

        await using var store = new PieceStore(torrent, _sinks, Item.SavePath);

        await RestoreAsync(torrent, picker, store, cancellationToken).ConfigureAwait(false);

        // The caps are the swarm's, not any one peer's: ten peers under a
        // 1 MB/s cap share the megabyte instead of taking one each.
        _downloadGate = new RateGate(SpeedLimit, _clock, _globalLimit);
        _uploadGate = new RateGate(UploadLimit, _clock);

        var swarm = new TorrentSwarm(torrent, _connector, trackers, store, picker, peerId)
        {
            MaxPeers = Math.Clamp(Item.RequestedConnections > 0 ? Item.RequestedConnections : 8, 1, 50),
            SeedingLimits = SeedingLimits,

            // 下完即停 means the peers are let go as soon as there is nothing
            // left to fetch, rather than being served until a limit is checked.
            Seed = !SeedingLimits.StopsImmediately,
            DownloadGate = _downloadGate,
            UploadGate = _uploadGate,
        };

        _swarm = swarm;

        swarm.Said += (_, message) => Item.Log.Add(new LogEntry(Stamp(), message));
        swarm.Progressed += (_, progress) => Record(progress);

        // Trackers the magnet named are worth keeping when the metainfo's own
        // list is thin or absent.
        if (extraTrackers.Count > 0) Item.Log.Add(new LogEntry(Stamp(), $"共 {torrent.Trackers.Count} 个 tracker"));

        try
        {
            await swarm.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await PersistAsync().ConfigureAwait(false);
        }

        if (!picker.IsComplete) throw new IOException("下载未完成。");

        Finish(store, picker);
        return JobOutcome.Completed;
    }

    /// <summary>
    /// Turns whatever the task holds into a metainfo: a .torrent read from
    /// disk or fetched over HTTP, or a magnet resolved from the swarm.
    /// </summary>
    private async Task<(TorrentMetainfo Torrent, IReadOnlyList<Uri> Extra)> ResolveAsync(
        byte[] peerId,
        TrackerPool trackers,
        CancellationToken cancellationToken)
    {
        if (MagnetLink.Parse(Item.Url) is { } magnet)
        {
            Item.Name = magnet.DisplayName ?? magnet.InfoHashHex;
            Item.Log.Add(new LogEntry(Stamp(), $"磁力链 {magnet.InfoHashHex[..12]}… 正在向 peer 索取元数据"));

            var resolved = await MagnetResolver
                .ResolveAsync(magnet, _connector, trackers, peerId, cancellationToken)
                .ConfigureAwait(false);

            return (resolved, magnet.Trackers);
        }

        var bytes = await ReadTorrentAsync(cancellationToken).ConfigureAwait(false);

        return (TorrentMetainfo.Parse(bytes), Array.Empty<Uri>());
    }

    private async Task<byte[]> ReadTorrentAsync(CancellationToken cancellationToken)
    {
        // A .torrent that is already on disk, which is what the 种子 sheet's
        // file picker produces.
        if (File.Exists(Item.Url)) return await File.ReadAllBytesAsync(Item.Url, cancellationToken).ConfigureAwait(false);

        if (!Uri.TryCreate(Item.Url, UriKind.Absolute, out var url) ||
            (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            throw new NotSupportedException("既不是磁力链，也不是可读取的种子文件。");
        }

        await using var stream = await _transport.OpenAsync(url, 0, null, cancellationToken).ConfigureAwait(false);

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        return buffer.ToArray();
    }

    /// <summary>
    /// Picks up where a previous run left off. The sidecar is trusted only as
    /// far as its info hash; anything else means these are not our files.
    /// </summary>
    private async Task RestoreAsync(
        TorrentMetainfo torrent,
        PiecePicker picker,
        PieceStore store,
        CancellationToken cancellationToken)
    {
        // 强制校验 is the whole point of the setting: the sidecar is exactly what
        // is not to be trusted this time.
        if (Recheck)
        {
            Recheck = false;

            Item.Log.Add(new LogEntry(Stamp(), "强制校验：忽略续传记录，重新校验磁盘上的文件"));
            await VerifyDiskAsync(torrent, picker, store, cancellationToken).ConfigureAwait(false);

            return;
        }

        // The sidecar is the cheap path and needs a store to have been given.
        if (_resume is not null && TargetPath is not null)
        {
            var saved = await _resume.LoadAsync(TargetPath, cancellationToken).ConfigureAwait(false);

            if (saved?.BitfieldFor(torrent) is { } bits)
            {
                picker.Restore(bits);

                Item.Log.Add(new LogEntry(Stamp(), $"续传：已有 {picker.CompletedCount} / {torrent.PieceCount} 个分片"));
                Record(new SwarmProgress(0, torrent.TotalLength, picker.CompletedCount, torrent.PieceCount, 0, 0));

                return;
            }
        }

        // Files already there with no usable sidecar: hash them rather than
        // fetch what is already on disk. This has nothing to do with the resume
        // store -- it is also how seeding an already-downloaded torrent starts,
        // and gating it on the sidecar meant re-downloading a complete folder.
        if (!torrent.Files.Any(file => _sinks.Exists(store.PathOf(file)))) return;

        Item.Log.Add(new LogEntry(Stamp(), "发现已有文件，正在校验…"));
        await VerifyDiskAsync(torrent, picker, store, cancellationToken).ConfigureAwait(false);
    }

    private async Task VerifyDiskAsync(
        TorrentMetainfo torrent,
        PiecePicker picker,
        PieceStore store,
        CancellationToken cancellationToken)
    {
        var verified = await TorrentVerifier
            .VerifyAsync(torrent, store, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        picker.Restore(verified);

        Item.Log.Add(new LogEntry(Stamp(), $"校验完成：{picker.CompletedCount} / {torrent.PieceCount} 个分片可用"));
        Record(new SwarmProgress(0, torrent.TotalLength, picker.CompletedCount, torrent.PieceCount, 0, 0));

        // What the sidecar said is now wrong either way; the truth is what was
        // just hashed.
        await PersistAsync().ConfigureAwait(false);
    }

    private void Record(SwarmProgress progress)
    {
        var now = _clock.UtcNow;

        // The swarm reports totals; the meter wants the delta since last time.
        long delta = Math.Max(0, progress.Downloaded - _lastDone);
        _lastDone = progress.Downloaded;

        if (delta > 0) _meter.Record((int)Math.Min(delta, int.MaxValue), now);

        // Upload is metered the same way, so 上传 is a rate rather than a
        // running total the eye cannot read.
        long sent = Math.Max(0, progress.Uploaded - _lastUploaded);
        _lastUploaded = progress.Uploaded;

        if (sent > 0) _uploadMeter.Record((int)Math.Min(sent, int.MaxValue), now);

        Item.Done = progress.Downloaded;
        Item.Size = progress.Total;
        Item.Connections = progress.ConnectedPeers;
        Item.Uploaded = progress.Uploaded;
        Item.Blocks = PlaylistJob.BlockMap(progress.TotalPieces, progress.Pieces, 96);
        Item.Speed = _meter.BytesPerSecond(now);
        Item.PeakSpeed = Math.Max(Item.PeakSpeed, Item.Speed);
        Item.UploadSpeed = _uploadMeter.BytesPerSecond(now);

        if (_swarm is { } swarm)
        {
            // Written even at 0.00: a torrent that has shared nothing yet is a
            // fact worth showing, where the dash means "not a torrent".
            Item.Ratio = swarm.Ratio;
            Item.Seeds = swarm.Seeders;
            Item.Peers = swarm.Leechers ?? progress.KnownPeers;
        }
    }

    private async Task PersistAsync()
    {
        if (_resume is null || TargetPath is null || Torrent is null || _picker is null) return;

        await _resume
            .SaveAsync(TargetPath, TorrentResumeState.From(Torrent, _picker), CancellationToken.None)
            .ConfigureAwait(false);
    }

    private string Progressed() =>
        _picker is { } picker && Torrent is { } torrent
            ? $"已暂停（{picker.CompletedCount} / {torrent.PieceCount} 个分片）"
            : "已暂停";

    private void Idle()
    {
        Item.Speed = 0;
        Item.UploadSpeed = 0;
        Item.Connections = 0;
        Item.ConnectionSpeeds = Array.Empty<double>();
        _meter.Reset();
        _uploadMeter.Reset();
    }

    private void Finish(PieceStore store, PiecePicker picker)
    {
        Item.Done = Item.Size;
        Item.Speed = 0;
        Item.UploadSpeed = 0;
        Item.Connections = 0;
        Item.ConnectionSpeeds = Array.Empty<double>();
        Item.Status = DownloadStatus.Completed;
        Item.Blocks = Enumerable.Repeat(1, 96).ToArray();

        // Every piece was checked against the torrent's own SHA-1 on the way
        // in, which is a stronger guarantee than a checksum after the fact.
        Item.Checksum = FileHash.Verified;

        Item.Log.Add(new LogEntry(Stamp(), $"下载完成，共 {picker.CompletedCount} 个分片，全部校验通过"));

        if (_swarm is { } swarm && swarm.Progress.Uploaded > 0)
        {
            Item.Log.Add(new LogEntry(Stamp(), $"已上传 {FormatBytes(swarm.Progress.Uploaded)}，分享率 {swarm.Ratio:0.00}"));
        }

        if (_resume is not null && TargetPath is not null) _resume.Delete(TargetPath);

        _ = store;
        _meter.Reset();
    }

    private static string FormatBytes(long bytes) => Services.FormatHelpers.Bytes(bytes);

    private string Stamp() => _clock.UtcNow.ToLocalTime().ToString("HH:mm");
}

/// <summary>
/// Turns a magnet link into a metainfo by asking the swarm for it.
///
/// A magnet has no file list, so the only way in is: announce with the info
/// hash, connect to whatever the tracker returns, and ask each peer for the
/// metadata until one of them supplies a copy that hashes correctly.
/// </summary>
public static class MagnetResolver
{
    /// <summary>How many peers to ask at once. Most will not answer.</summary>
    private const int Parallel = 4;

    public static async Task<TorrentMetainfo> ResolveAsync(
        MagnetLink magnet,
        IPeerConnector connector,
        TrackerPool trackers,
        byte[] peerId,
        CancellationToken cancellationToken)
    {
        if (magnet.Trackers.Count == 0)
        {
            // Without trackers the only way to find peers is the DHT, which is
            // not implemented. Saying so beats waiting forever.
            throw new NotSupportedException("这条磁力链没有 tracker，而 DHT 尚未实现，无法找到 peer。");
        }

        var announce = new AnnounceRequest(
            magnet.InfoHash,
            peerId,
            6881,
            Uploaded: 0,
            Downloaded: 0,
            Left: magnet.Length > 0 ? magnet.Length : 1,
            AnnounceEvent.Started);

        var response = await trackers
            .AnnounceAsync(magnet.Trackers, announce, cancellationToken)
            .ConfigureAwait(false);

        if (response.Peers.Count == 0) throw new NotSupportedException("tracker 没有返回任何 peer。");

        var buffer = new MetadataBuffer(magnet.InfoHash);

        foreach (var batch in response.Peers.Chunk(Parallel))
        {
            cancellationToken.ThrowIfCancellationRequested();

            await Task.WhenAll(batch.Select(peer => AskAsync(peer, buffer, magnet, connector, peerId, cancellationToken)))
                .ConfigureAwait(false);

            if (!buffer.IsComplete) continue;

            // Assembled from several peers, so a whole that does not hash means
            // one of them lied: throw it all away and try the next batch.
            if (buffer.Verifies()) return buffer.Build(magnet.Trackers)!;

            buffer.Reset();
        }

        throw new NotSupportedException("没有 peer 能提供这条磁力链的元数据。");
    }

    private static async Task AskAsync(
        System.Net.IPEndPoint peer,
        MetadataBuffer buffer,
        MagnetLink magnet,
        IPeerConnector connector,
        byte[] peerId,
        CancellationToken cancellationToken)
    {
        Stream? stream = null;

        try
        {
            stream = await connector.ConnectAsync(peer, cancellationToken).ConfigureAwait(false);

            var session = new MetadataSession(stream, buffer) { Address = peer };
            await session.RunAsync(magnet.InfoHash, peerId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Most peers will not answer, will not speak the extension, or will
            // have gone. That is the normal case, not an error.
        }
        finally
        {
            if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}
