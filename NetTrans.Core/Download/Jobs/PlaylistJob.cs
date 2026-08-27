using NetTrans.Media;
using NetTrans.Models;
using NetTrans.Net;

namespace NetTrans.Download;

/// <summary>
/// A segmented transfer: fetch every segment a manifest lists and write them
/// into one file, in order. HLS and DASH both arrive here, as
/// <see cref="SegmentedStream"/>s.
///
/// There is no remuxing here and there does not need to be. MPEG-TS segments
/// concatenate into a playable .ts by design -- that is what a TS stream is --
/// and fMP4 segments concatenate onto their init segment into a playable .mp4.
/// Anything beyond that (a real MP4 rebuild, SAMPLE-AES, a live edge) is
/// refused up front by the loaders rather than attempted badly.
///
/// One task can produce more than one file. DASH usually keeps video and audio
/// in separate Representations, and interleaving those into a single MP4 is a
/// muxer, not a downloader. So both are fetched, side by side, and the log says
/// what landed -- which is honest, where a silent file labelled as the video
/// would not be.
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

    private IReadOnlyList<SegmentedStream> _streams = Array.Empty<SegmentedStream>();
    private int _segmentsDone;
    private int _segmentsTotal;
    private long _bytesTotal;

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

    /// <summary>The first file written, which is the one the row is named after.</summary>
    public string? TargetPath { get; private set; }

    /// <summary>Every file this task produced. More than one only when a DASH manifest split its tracks.</summary>
    public IReadOnlyList<string> Files { get; private set; } = Array.Empty<string>();

    /// <summary>The streams being fetched. Empty until the manifest has been read.</summary>
    public IReadOnlyList<SegmentedStream> Streams => _streams;

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
            Idle();

            bool paused;
            lock (_pauseGate) paused = _pauseRequested;

            if (!paused) return JobOutcome.Failed;

            Item.Status = DownloadStatus.Paused;
            Item.Log.Add(new LogEntry(Stamp(), $"已暂停（已完成 {_segmentsDone} / {_segmentsTotal} 分片）"));
            return JobOutcome.Paused;
        }
        catch (Exception exception)
        {
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

        _streams = await new StreamLoader(_transport).LoadAsync(url, cancellationToken).ConfigureAwait(false);

        _segmentsTotal = _streams.Sum(stream => stream.SegmentCount);
        _bytesTotal = 0;
        _segmentsDone = 0;

        Item.Log.Add(new LogEntry(Stamp(), $"已解析清单：{_segmentsTotal} 个分片 · {_streams[0].Quality}"));

        if (_streams.Count > 1)
        {
            // Worth saying plainly and early: the row is about to produce more
            // than one file, and neither is the whole thing on its own.
            Item.Log.Add(new LogEntry(
                Stamp(),
                $"该清单音视频分离，将分别下载 {_streams.Count} 个文件（需自行合并）"));
        }

        long estimate = _streams.Sum(stream => stream.EstimatedBytes);
        if (estimate > 0) Item.Size = estimate;

        var names = NamesFor(_streams);
        TargetPath = names[0];
        Files = names;
        Item.Name = Path.GetFileName(TargetPath);
        Item.Checksum ??= FileHash.Pending;

        _perTaskLimit = new TokenBucket(SpeedLimit, _clock.UtcNow);

        int lanes = Math.Clamp(
            Item.RequestedConnections > 0 ? Item.RequestedConnections : _options.Connections,
            1,
            8);

        lock (_gate)
        {
            _connectionMeters.Clear();
            for (int i = 0; i < lanes; i++) _connectionMeters.Add(new SpeedMeter(_options.Window));
        }

        Item.Connections = lanes;

        using var decryptor = new HlsDecryptor();

        for (int i = 0; i < _streams.Count; i++)
        {
            await FetchStreamAsync(_streams[i], names[i], decryptor, lanes, cancellationToken).ConfigureAwait(false);
        }

        Finish(names);
        return JobOutcome.Completed;
    }

    /// <summary>One stream into one file.</summary>
    private async Task FetchStreamAsync(
        SegmentedStream stream,
        string path,
        HlsDecryptor decryptor,
        int lanes,
        CancellationToken cancellationToken)
    {
        var resumed = await ResumeAsync(stream, path, cancellationToken).ConfigureAwait(false);

        int done = resumed.SegmentsDone;
        long written = resumed.BytesWritten;

        if (done > 0) Item.Log.Add(new LogEntry(Stamp(), $"{Path.GetFileName(path)} 从第 {done + 1} 个分片继续"));

        // Segments finished in an earlier run still count towards the bar.
        _segmentsDone += done;
        _bytesTotal += written;

        if (done >= stream.SegmentCount) return;

        // The length is not known ahead of time, so the sink is opened unsized
        // and grows as segments land.
        await using var sink = await _sinks.OpenAsync(path, -1, cancellationToken).ConfigureAwait(false);

        // Whatever the file held past this point belonged to a run we are not
        // continuing; leaving it would put an old tail behind the new bytes.
        await sink.TruncateAsync(written, cancellationToken).ConfigureAwait(false);

        if (stream.InitSegment is { } init && done == 0)
        {
            var bytes = await FetchAsync(init, null, null, lane: 0, cancellationToken).ConfigureAwait(false);
            await sink.WriteAsync(written, bytes, cancellationToken).ConfigureAwait(false);
            written += bytes.Length;
            _bytesTotal += bytes.Length;
        }

        var inFlight = new Dictionary<int, Task<byte[]>>();
        int next = done;

        try
        {
            while (done < stream.SegmentCount)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Top up the window.
                while (inFlight.Count < lanes && next < stream.SegmentCount)
                {
                    int index = next++;
                    int lane = index % lanes;
                    inFlight[index] = FetchSegmentAsync(stream.Segments[index], decryptor, lane, cancellationToken);
                }

                // Only the next one in order can be written, however early the
                // others finish: a concatenated stream is only playable if the
                // pieces land in the order the manifest listed them.
                var bytes = await inFlight[done].ConfigureAwait(false);
                inFlight.Remove(done);

                await sink.WriteAsync(written, bytes, cancellationToken).ConfigureAwait(false);

                written += bytes.Length;
                _bytesTotal += bytes.Length;
                done++;
                _segmentsDone++;

                Record();

                // Persisting per segment would be a write per second; this is
                // the same cadence the ranged transfers use.
                if (done % 20 == 0) await PersistAsync(stream, path, done, written).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Whatever went wrong, what is on disk is whole segments, and the
            // next run should start after them rather than at the beginning.
            await PersistAsync(stream, path, done, written).ConfigureAwait(false);
            throw;
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

        // The last segment decides the length; anything beyond it is left over
        // from a longer earlier attempt.
        await sink.TruncateAsync(written, cancellationToken).ConfigureAwait(false);
        await sink.FlushAsync(cancellationToken).ConfigureAwait(false);

        _resume?.Delete(path);
    }

    private async Task<byte[]> FetchSegmentAsync(
        StreamSegment segment,
        HlsDecryptor decryptor,
        int lane,
        CancellationToken cancellationToken)
    {
        var bytes = await FetchAsync(
            segment.Url,
            segment.RangeOffset,
            segment.RangeLength,
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

                    await ThrottleAsync(read, cancellationToken).ConfigureAwait(false);
                    buffer.Write(chunk, 0, read);

                    var now = _clock.UtcNow;
                    _meter.Record(read, now);

                    lock (_gate)
                    {
                        if (lane < _connectionMeters.Count) _connectionMeters[lane].Record(read, now);
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

    private async Task ThrottleAsync(int bytes, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var wait = _perTaskLimit?.Take(bytes, now) ?? TimeSpan.Zero;

        if (_globalLimit is not null)
        {
            var globalWait = _globalLimit.Take(bytes, now);
            if (globalWait > wait) wait = globalWait;
        }

        if (wait > TimeSpan.Zero) await _clock.DelayAsync(wait, cancellationToken).ConfigureAwait(false);
    }

    private void Record()
    {
        var now = _clock.UtcNow;

        Item.Done = _bytesTotal;

        // The manifest never says how big the file is, so the estimate is what
        // has arrived scaled by how much of the list is left. It is wrong early
        // on and right by the end, which is the honest shape for this.
        if (_segmentsDone > 0)
        {
            Item.Size = Math.Max(_bytesTotal, (long)(_bytesTotal / (double)_segmentsDone * _segmentsTotal));
        }

        Item.Blocks = BlockMap(_segmentsTotal, _segmentsDone, 96);
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

    private async Task<PlaylistResumeState> ResumeAsync(SegmentedStream stream, string path, CancellationToken cancellationToken)
    {
        var empty = new PlaylistResumeState(Item.Url, stream.SegmentCount, 0, 0);

        if (_resume is null) return empty;
        if (!_sinks.Exists(path)) return empty;

        var saved = await _resume.LoadAsync(path, cancellationToken).ConfigureAwait(false);
        if (saved is null) return empty;

        // A manifest that has changed length is a different stream, or the same
        // one re-cut; either way the bytes already written no longer line up.
        if (saved.SegmentCount != stream.SegmentCount || !string.Equals(saved.Url, Item.Url, StringComparison.Ordinal))
        {
            Item.Log.Add(new LogEntry(Stamp(), "清单已变化，重新开始"));
            return empty;
        }

        return saved;
    }

    private async Task PersistAsync(SegmentedStream stream, string path, int done, long written)
    {
        if (_resume is null) return;

        await _resume.SaveAsync(
            path,
            new PlaylistResumeState(Item.Url, stream.SegmentCount, done, written),
            CancellationToken.None).ConfigureAwait(false);
    }

    private void Idle()
    {
        Item.Speed = 0;
        Item.Connections = 0;
        _meter.Reset();
    }

    private void Finish(IReadOnlyList<string> files)
    {
        Item.Done = _bytesTotal;
        Item.Size = _bytesTotal;
        Item.Speed = 0;
        Item.Connections = 0;
        Item.Status = DownloadStatus.Completed;
        Item.ConnectionSpeeds = Array.Empty<double>();
        Item.Blocks = Enumerable.Repeat(1, 96).ToArray();

        Item.Log.Add(new LogEntry(Stamp(), $"下载完成，共 {_segmentsDone} 个分片"));

        if (files.Count > 1)
        {
            foreach (string file in files) Item.Log.Add(new LogEntry(Stamp(), $"已保存 {Path.GetFileName(file)}"));
        }

        _meter.Reset();
    }

    /// <summary>
    /// One file name per stream.
    ///
    /// A manifest URL ends in .m3u8 or .mpd, which is not what the file is, so
    /// the extension comes from the container and the stem from whatever the
    /// task was named. That means the queue could not have picked a
    /// non-colliding name when the task was added -- it did not know the
    /// extension yet -- so it is picked here, skipping anything already there
    /// that is not a transfer of ours to continue.
    /// </summary>
    private IReadOnlyList<string> NamesFor(IReadOnlyList<SegmentedStream> streams)
    {
        string stem = Path.GetFileNameWithoutExtension(Item.Name);

        // "index", "master", "playlist", "manifest" say nothing; the host does better.
        if (stem.Length == 0 || stem is "index" or "master" or "playlist" or "manifest" or "未命名下载")
        {
            stem = new Uri(Item.Url).Host.Replace('.', '-');
        }

        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>(streams.Count);

        foreach (var stream in streams)
        {
            // The suffix is what keeps a split manifest's two tracks apart; it
            // is empty for the usual single muxed stream.
            string wanted = $"{stem}{(streams.Count > 1 ? stream.NameSuffix : "")}.{stream.Container}";

            string name = Services.SavePathPlanner.UniqueName(
                Item.SavePath,
                wanted,
                path => claimed.Contains(path) || Taken(path));

            string full = Path.Combine(Item.SavePath, name);
            claimed.Add(full);
            names.Add(full);
        }

        return names;
    }

    /// <summary>
    /// A path counts as free when we have a half-finished transfer of our own
    /// there: that is the file this run is continuing, not one to step around.
    /// </summary>
    private bool Taken(string path) =>
        _sinks.Exists(path) && (_resume is null || !File.Exists(PlaylistResumeStore.SidecarPath(path)));

    private string Stamp() => _clock.UtcNow.ToLocalTime().ToString("HH:mm");
}
