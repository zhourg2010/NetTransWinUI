using NetTrans.Media;
using NetTrans.Models;
using NetTrans.Net;

namespace NetTrans.Download;

/// <summary>
/// An HLS transfer: fetch every segment of a media playlist and write them into
/// one file, in order.
///
/// There is no remuxing here and there does not need to be. MPEG-TS segments
/// concatenate into a playable .ts by design -- that is what a TS stream is --
/// and fMP4 segments concatenate onto their #EXT-X-MAP init segment into a
/// playable .mp4. Anything beyond that (a real MP4 rebuild, SAMPLE-AES, a live
/// edge) is refused up front by <see cref="HlsPlaylistLoader"/> rather than
/// attempted badly.
///
/// The shape differs from a ranged download in one way that matters: the total
/// size is not known until the last segment lands, so progress is counted in
/// segments and the size shown is an estimate that sharpens as it goes.
/// </summary>
public sealed class PlaylistJob : ITransferJob
{
    private readonly IHttpTransport _transport;
    private readonly IFileSinkFactory _sinks;
    private readonly IClock _clock;
    private readonly DownloadOptions _options;
    private readonly PlaylistResumeStore? _resume;
    private readonly TokenBucket? _globalLimit;

    private readonly SpeedMeter _meter;
    private readonly List<SpeedMeter> _connectionMeters = new();
    private readonly object _gate = new();

    private readonly object _pauseGate = new();
    private CancellationTokenSource? _cancellation;
    private bool _pauseRequested;

    private double _speedLimit;
    private TokenBucket? _perTaskLimit;

    private HlsPlaylist? _playlist;
    private int _segmentsDone;
    private long _written;

    public PlaylistJob(
        DownloadItem item,
        IHttpTransport transport,
        IFileSinkFactory sinks,
        IClock clock,
        DownloadOptions? options = null,
        PlaylistResumeStore? resume = null,
        TokenBucket? globalLimit = null)
    {
        Item = item;
        _transport = transport;
        _sinks = sinks;
        _clock = clock;
        _options = options ?? new DownloadOptions();
        _resume = resume;
        _globalLimit = globalLimit;
        _meter = new SpeedMeter(_options.Window);
    }

    public DownloadItem Item { get; }

    public string? TargetPath { get; private set; }

    /// <summary>The rendition being fetched. Null until the playlist has been read.</summary>
    public HlsPlaylist? Playlist => _playlist;

    public double SpeedLimit
    {
        get => _speedLimit;
        set
        {
            _speedLimit = value;
            if (_perTaskLimit is { } bucket) bucket.BytesPerSecond = value;
        }
    }

    public double EffectiveSpeedLimit => _perTaskLimit?.BytesPerSecond ?? SpeedLimit;

    public double BytesPerSecond => _meter.BytesPerSecond(_clock.UtcNow);

    public double[] ConnectionSpeeds
    {
        get
        {
            lock (_gate)
            {
                var now = _clock.UtcNow;
                return _connectionMeters.Select(meter => meter.BytesPerSecond(now)).ToArray();
            }
        }
    }

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

            if (!paused) return JobOutcome.Failed;

            Item.Status = DownloadStatus.Paused;
            Item.Log.Add(new LogEntry(Stamp(), $"已暂停（已完成 {_segmentsDone} / {_playlist?.SegmentCount ?? 0} 分片）"));
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

    private async Task<JobOutcome> TransferAsync(CancellationToken cancellationToken)
    {
        var url = new Uri(Item.Url, UriKind.Absolute);

        Item.Status = DownloadStatus.Downloading;
        Item.ErrorMessage = null;

        // Recognised, but an MPD is a different format with its own segment
        // addressing; saying which one it is beats failing as "no segments".
        if (PlaylistUrl.IsDash(Item.Url))
        {
            throw new NotSupportedException("MPEG-DASH（.mpd）暂不支持，目前只能下载 HLS（.m3u8）。");
        }

        var playlist = await new HlsPlaylistLoader(_transport)
            .LoadAsync(url, preferred: null, cancellationToken)
            .ConfigureAwait(false);

        _playlist = playlist;

        Item.Log.Add(new LogEntry(Stamp(), $"已解析播放列表：{playlist.SegmentCount} 个分片 · {playlist.Quality}"));
        if (playlist.EstimatedBytes > 0) Item.Size = playlist.EstimatedBytes;

        TargetPath = Path.Combine(Item.SavePath, NameFor(playlist));
        Item.Name = Path.GetFileName(TargetPath);

        Item.Checksum ??= FileHash.Pending;

        // How far a previous run got, if the playlist is still the same one.
        var resumed = await ResumeAsync(playlist, cancellationToken).ConfigureAwait(false);
        _segmentsDone = resumed.SegmentsDone;
        _written = resumed.BytesWritten;

        if (_segmentsDone > 0)
        {
            Item.Log.Add(new LogEntry(Stamp(), $"从第 {_segmentsDone + 1} 个分片继续"));
        }

        if (_segmentsDone >= playlist.SegmentCount)
        {
            Finish();
            return JobOutcome.Completed;
        }

        // The length is not known ahead of time, so the sink is opened
        // unsized and grows as segments land.
        await using var sink = await _sinks.OpenAsync(TargetPath, -1, cancellationToken).ConfigureAwait(false);

        // Whatever the file held past this point belonged to a run we are not
        // continuing, and leaving it there would put an old tail behind the new
        // bytes rather than simply making the file too long.
        await sink.TruncateAsync(_written, cancellationToken).ConfigureAwait(false);

        var limit = _perTaskLimit = new TokenBucket(SpeedLimit, _clock.UtcNow);
        using var decryptor = new HlsDecryptor();

        int lanes = Math.Clamp(Item.RequestedConnections > 0 ? Item.RequestedConnections : _options.Connections, 1, 8);

        lock (_gate)
        {
            _connectionMeters.Clear();
            for (int i = 0; i < lanes; i++) _connectionMeters.Add(new SpeedMeter(_options.Window));
        }

        Item.Connections = lanes;

        // The init segment is part of the file, before everything else, and is
        // only written on a fresh start.
        if (playlist.Media.InitSegment is { } init && _segmentsDone == 0)
        {
            var bytes = await FetchAsync(init, null, null, limit, lane: 0, cancellationToken).ConfigureAwait(false);
            await sink.WriteAsync(_written, bytes, cancellationToken).ConfigureAwait(false);
            _written += bytes.Length;
        }

        await PumpSegmentsAsync(playlist, sink, decryptor, limit, lanes, cancellationToken).ConfigureAwait(false);

        // The last segment decides the length; anything beyond it is left over
        // from a longer earlier attempt.
        await sink.TruncateAsync(_written, cancellationToken).ConfigureAwait(false);
        await sink.FlushAsync(cancellationToken).ConfigureAwait(false);
        Finish();

        return JobOutcome.Completed;
    }

    /// <summary>
    /// Fetches up to <paramref name="lanes"/> segments at once but writes them
    /// strictly in order: a concatenated stream is only playable if the pieces
    /// arrive in the order the playlist listed them, and a lane that finishes
    /// early waits its turn rather than writing past its neighbour.
    /// </summary>
    private async Task PumpSegmentsAsync(
        HlsPlaylist playlist,
        IFileSink sink,
        HlsDecryptor decryptor,
        TokenBucket limit,
        int lanes,
        CancellationToken cancellationToken)
    {
        var segments = playlist.Media.Segments;
        var inFlight = new Dictionary<int, Task<byte[]>>();

        int next = _segmentsDone;

        try
        {
            while (_segmentsDone < segments.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Top up the window.
                while (inFlight.Count < lanes && next < segments.Count)
                {
                    int index = next++;
                    int lane = index % lanes;
                    inFlight[index] = FetchSegmentAsync(segments[index], decryptor, limit, lane, cancellationToken);
                }

                // Only the next one in order can be written, however early the
                // others finish.
                var bytes = await inFlight[_segmentsDone].ConfigureAwait(false);
                inFlight.Remove(_segmentsDone);

                await sink.WriteAsync(_written, bytes, cancellationToken).ConfigureAwait(false);

                _written += bytes.Length;
                _segmentsDone++;

                Record(playlist);

                // Persisting per segment would be a write per second; this is
                // the same cadence the ranged transfers use.
                if (_segmentsDone % 20 == 0) await PersistAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            // A pause or a failed segment leaves the rest of the window
            // running. Their results are no longer wanted, but an unawaited
            // faulted task is a loose end, so they are observed here.
            await Task.WhenAll(inFlight.Values)
                .ContinueWith(static _ => { }, TaskScheduler.Default)
                .ConfigureAwait(false);
        }
    }

    private async Task<byte[]> FetchSegmentAsync(
        HlsSegment segment,
        HlsDecryptor decryptor,
        TokenBucket limit,
        int lane,
        CancellationToken cancellationToken)
    {
        var bytes = await FetchAsync(
            segment.Url,
            segment.ByteRangeOffset,
            segment.ByteRangeLength,
            limit,
            lane,
            cancellationToken).ConfigureAwait(false);

        if (segment.Key is not { Method: HlsEncryption.Aes128, KeyUri: { } keyUri }) return bytes;

        var key = await decryptor.KeyAsync(keyUri, _transport, cancellationToken).ConfigureAwait(false);
        return HlsDecryptor.Decrypt(bytes, key, HlsDecryptor.InitialisationVector(segment));
    }

    /// <summary>
    /// One segment, whole, with the same retry budget a ranged connection gets.
    /// Segments are held in memory because AES-128 decrypts a segment at a time
    /// and a segment is seconds of video, not the whole file.
    /// </summary>
    private async Task<byte[]> FetchAsync(
        Uri url,
        long? rangeOffset,
        long? rangeLength,
        TokenBucket limit,
        int lane,
        CancellationToken cancellationToken)
    {
        int attempt = 0;

        while (true)
        {
            try
            {
                long from = rangeOffset ?? 0;
                long? to = rangeOffset is { } offset && rangeLength is { } length ? offset + length - 1 : null;

                await using var stream = await _transport.OpenAsync(url, from, to, cancellationToken).ConfigureAwait(false);

                using var buffer = new MemoryStream();
                var chunk = new byte[_options.BufferSize];

                while (true)
                {
                    int read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;

                    await ThrottleAsync(limit, read, cancellationToken).ConfigureAwait(false);
                    buffer.Write(chunk, 0, read);

                    _meter.Record(read, _clock.UtcNow);

                    lock (_gate)
                    {
                        if (lane < _connectionMeters.Count) _connectionMeters[lane].Record(read, _clock.UtcNow);
                    }
                }

                return buffer.ToArray();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                attempt++;
                Item.Retries++;

                if (attempt > _options.MaxRetries)
                {
                    throw new IOException($"分片下载失败：{DownloadJob.Describe(exception)}", exception);
                }

                Item.Log.Add(new LogEntry(
                    Stamp(),
                    $"分片第 {attempt} 次重试：{DownloadJob.Describe(exception)}",
                    IsError: true));

                var delay = TimeSpan.FromMilliseconds(_options.Backoff.TotalMilliseconds * Math.Pow(2, attempt - 1));
                await _clock.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ThrottleAsync(TokenBucket perTask, int bytes, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var wait = perTask.Take(bytes, now);

        if (_globalLimit is not null)
        {
            var globalWait = _globalLimit.Take(bytes, now);
            if (globalWait > wait) wait = globalWait;
        }

        if (wait > TimeSpan.Zero) await _clock.DelayAsync(wait, cancellationToken).ConfigureAwait(false);
    }

    private void Record(HlsPlaylist playlist)
    {
        var now = _clock.UtcNow;

        Item.Done = _written;

        // The playlist never says how big the file is, so the estimate is what
        // has arrived scaled by how much of the list is left. It is wrong early
        // on and right by the end, which is the honest shape for this.
        if (_segmentsDone > 0)
        {
            Item.Size = Math.Max(_written, (long)(_written / (double)_segmentsDone * playlist.SegmentCount));
        }

        // One block per segment, capped to the map the inspector draws.
        Item.Blocks = BlockMap(playlist.SegmentCount, _segmentsDone, 96);
        Item.ConnectionSpeeds = ConnectionSpeeds;

        Item.Speed = _meter.BytesPerSecond(now);
        Item.PeakSpeed = Math.Max(Item.PeakSpeed, Item.Speed);
    }

    /// <summary>Segments folded onto the inspector's fixed-width chunk map.</summary>
    internal static int[] BlockMap(int total, int done, int width)
    {
        var map = new int[width];
        if (total <= 0) return map;

        for (int i = 0; i < width; i++)
        {
            // Each cell stands for a span of segments; it is filled once the
            // whole span is written, so the bar never runs ahead of the file.
            int spanEnd = (int)Math.Ceiling((i + 1) / (double)width * total);
            map[i] = done >= spanEnd ? 1 : 0;
        }

        return map;
    }

    private async Task<PlaylistResumeState> ResumeAsync(HlsPlaylist playlist, CancellationToken cancellationToken)
    {
        var empty = new PlaylistResumeState(Item.Url, playlist.SegmentCount, 0, 0);

        if (_resume is null || TargetPath is null) return empty;
        if (!_sinks.Exists(TargetPath)) return empty;

        var saved = await _resume.LoadAsync(TargetPath, cancellationToken).ConfigureAwait(false);
        if (saved is null) return empty;

        // A playlist that has changed length is a different stream, or the same
        // one re-cut; either way the bytes already written no longer line up.
        if (saved.SegmentCount != playlist.SegmentCount || !string.Equals(saved.Url, Item.Url, StringComparison.Ordinal))
        {
            Item.Log.Add(new LogEntry(Stamp(), "播放列表已变化，重新开始"));
            return empty;
        }

        return saved;
    }

    internal async Task PersistAsync()
    {
        if (_resume is null || TargetPath is null || _playlist is null) return;

        await _resume.SaveAsync(
            TargetPath,
            new PlaylistResumeState(Item.Url, _playlist.SegmentCount, _segmentsDone, _written),
            CancellationToken.None).ConfigureAwait(false);
    }

    private void Idle()
    {
        Item.Speed = 0;
        Item.Connections = 0;
        _meter.Reset();
    }

    private void Finish()
    {
        Item.Done = _written;
        Item.Size = _written;
        Item.Speed = 0;
        Item.Connections = 0;
        Item.Status = DownloadStatus.Completed;
        Item.ConnectionSpeeds = Array.Empty<double>();
        Item.Blocks = Enumerable.Repeat(1, 96).ToArray();
        Item.Log.Add(new LogEntry(Stamp(), $"下载完成，共 {_segmentsDone} 个分片"));

        if (_resume is not null && TargetPath is not null) _resume.Delete(TargetPath);

        _meter.Reset();
    }

    /// <summary>
    /// The file name.
    ///
    /// A playlist URL ends in .m3u8, which is not what the file is, so the
    /// extension comes from the container and the stem from whatever the task
    /// was named. That means the queue could not have picked a non-colliding
    /// name when the task was added -- it did not know the extension yet -- so
    /// it is picked here instead, skipping anything that is already there and
    /// is not a transfer of ours to continue.
    /// </summary>
    private string NameFor(HlsPlaylist playlist)
    {
        string stem = Path.GetFileNameWithoutExtension(Item.Name);

        // "index", "master", "playlist" say nothing; the host does better.
        if (stem.Length == 0 || stem is "index" or "master" or "playlist" or "未命名下载")
        {
            stem = new Uri(Item.Url).Host.Replace('.', '-');
        }

        return Services.SavePathPlanner.UniqueName(Item.SavePath, $"{stem}.{playlist.Container}", Taken);
    }

    /// <summary>
    /// A path counts as free when we have a half-finished transfer of our own
    /// there: that is the file this run is continuing, not one to step around.
    /// </summary>
    private bool Taken(string path) =>
        _sinks.Exists(path) && (_resume is null || !File.Exists(PlaylistResumeStore.SidecarPath(path)));

    private string Stamp() => _clock.UtcNow.ToLocalTime().ToString("HH:mm");
}
