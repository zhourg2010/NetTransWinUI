using NetTrans.Models;
using NetTrans.Net;

namespace NetTrans.Download;

/// <summary>
/// One transfer: probe, plan, fetch every range in parallel, write them into a
/// pre-sized file, and keep a resume sidecar up to date so a pause or a crash
/// costs nothing.
///
/// Everything it touches -- HTTP, the file, the clock -- is injected, so the
/// whole loop runs in a unit test against a fake server and an in-memory file.
/// </summary>
public sealed class DownloadJob : ITransferJob
{
    private readonly IHttpTransport _transport;
    private readonly IFileSinkFactory _sinks;
    private readonly IClock _clock;
    private readonly ResumeStore? _resume;
    private readonly DownloadOptions _options;
    private readonly TokenBucket? _globalLimit;

    private readonly SpeedMeter _meter;
    private readonly List<SpeedMeter> _connectionMeters = new();
    private readonly object _gate = new();

    private readonly object _pauseGate = new();
    private CancellationTokenSource? _cancellation;
    private bool _pauseRequested;
    private DateTimeOffset _lastMapRefresh;

    private double _speedLimit;
    private TokenBucket? _perTaskLimit;

    public DownloadJob(
        DownloadItem item,
        IHttpTransport transport,
        IFileSinkFactory sinks,
        IClock clock,
        DownloadOptions? options = null,
        ResumeStore? resume = null,
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

    /// <summary>
    /// The cap the transfer is enforcing right now. Equal to
    /// <see cref="SpeedLimit"/> once a transfer is running; before one starts
    /// there is no bucket yet and the two are the same by definition. Worth
    /// asking separately because the two take effect at different moments.
    /// </summary>
    public double EffectiveSpeedLimit => _perTaskLimit?.BytesPerSecond ?? SpeedLimit;

    /// <summary>Null until the transfer has probed and planned.</summary>
    public SegmentPlan? Plan { get; private set; }

    /// <summary>Where the bytes are being written.</summary>
    public string? TargetPath { get; private set; }

    /// <summary>
    /// Per-task cap in bytes per second; zero or less means 不限. Settable while
    /// the transfer runs -- the inspector's 单任务限速 dropdown changes it under
    /// a live download -- so it writes through to the bucket in use.
    /// </summary>
    public double SpeedLimit
    {
        get => _speedLimit;
        set
        {
            _speedLimit = value;
            if (_perTaskLimit is { } bucket) bucket.BytesPerSecond = value;
        }
    }

    public double BytesPerSecond => _meter.BytesPerSecond(_clock.UtcNow);

    /// <summary>One rate per live connection, for the inspector's 连接 tab.</summary>
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

    /// <summary>
    /// Asks the transfer to stop at the next read boundary and keep its
    /// progress. Safe to call before <see cref="RunAsync"/> has got as far as
    /// creating its cancellation source -- the request is held and honoured
    /// when it does.
    /// </summary>
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

        // Paused between being queued and getting here: honour it now rather
        // than running to completion and ignoring the request.
        if (alreadyPaused) linked.Cancel();

        try
        {
            return await TransferAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await PersistAsync(CancellationToken.None).ConfigureAwait(false);
            Item.Speed = 0;
            Item.Connections = 0;
            _meter.Reset();

            bool paused;
            lock (_pauseGate) paused = _pauseRequested;

            if (!paused) return JobOutcome.Failed;

            Item.Status = DownloadStatus.Paused;
            Item.Log.Add(new LogEntry(Stamp(), "已暂停"));
            return JobOutcome.Paused;
        }
        catch (Exception exception)
        {
            await PersistAsync(CancellationToken.None).ConfigureAwait(false);
            Item.Speed = 0;
            Item.Connections = 0;
            Item.ErrorMessage = Describe(exception);
            Item.Status = DownloadStatus.Error;
            Item.Log.Add(new LogEntry(Stamp(), Item.ErrorMessage, IsError: true));
            _meter.Reset();
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

        var info = await _transport.ProbeAsync(url, cancellationToken).ConfigureAwait(false);

        // Kept so a later 新版本 check has something to compare against.
        Item.SourceETag = info.ETag;
        Item.SourceLastModified = info.LastModified;
        Item.Checksum ??= FileHash.Pending;

        Item.Log.Add(new LogEntry(Stamp(), $"已连接 {url.Host}"));
        Item.Log.Add(new LogEntry(Stamp(), info.CanSplit ? "服务器支持断点续传" : "服务器不支持断点续传，将单线程下载"));

        TargetPath = Path.Combine(Item.SavePath, Item.Name.Length > 0 ? Item.Name : info.FileName);

        var plan = await PlanAsync(url, info, cancellationToken).ConfigureAwait(false);
        Plan = plan;

        if (info.HasKnownLength) Item.Size = info.Length;
        Item.Done = plan.Downloaded;

        if (plan.IsComplete)
        {
            Finish();
            return JobOutcome.Completed;
        }

        await using var sink = await _sinks
            .OpenAsync(TargetPath, info.HasKnownLength ? info.Length : -1, cancellationToken)
            .ConfigureAwait(false);

        var pending = plan.Segments.Where(segment => !segment.IsComplete).ToList();

        lock (_gate)
        {
            _connectionMeters.Clear();
            for (int i = 0; i < pending.Count; i++) _connectionMeters.Add(new SpeedMeter(_options.Window));
        }

        Item.Connections = pending.Count;
        Item.Log.Add(new LogEntry(Stamp(), $"已建立 {pending.Count} 个连接"));

        // Held so a change to SpeedLimit mid-transfer reaches this bucket.
        var limit = _perTaskLimit = new TokenBucket(SpeedLimit, _clock.UtcNow);
        using var persistence = _resume is null ? null : new PeriodicPersister(this, _options.ResumeInterval);

        var transfers = pending
            .Select((segment, index) => FetchAsync(url, segment, index, sink, limit, info, cancellationToken))
            .ToArray();

        await Task.WhenAll(transfers).ConfigureAwait(false);

        await sink.FlushAsync(cancellationToken).ConfigureAwait(false);
        Finish();
        return JobOutcome.Completed;
    }

    private async Task<SegmentPlan> PlanAsync(Uri url, RemoteFileInfo info, CancellationToken cancellationToken)
    {
        if (!info.CanSplit)
        {
            // No ranges means no resume either: any partial file has to go.
            return info.HasKnownLength ? SegmentPlan.Create(info.Length, 1, 0) : SegmentPlan.Unbounded();
        }

        if (_resume is not null && TargetPath is not null)
        {
            var saved = await _resume.LoadAsync(TargetPath, cancellationToken).ConfigureAwait(false);

            if (saved is not null && saved.Matches(info) && _sinks.Exists(TargetPath))
            {
                Item.Log.Add(new LogEntry(Stamp(), $"从 {FormatBytes(saved.Segments.Sum(s => s.Position - s.Start))} 处继续"));
                return SegmentPlan.Restore(info.Length, saved.Segments);
            }

            if (saved is not null)
            {
                Item.Log.Add(new LogEntry(Stamp(), "服务器上的文件已变化，重新开始下载"));
                _resume.Delete(TargetPath);
            }
        }

        int connections = Item.RequestedConnections > 0 ? Item.RequestedConnections : _options.Connections;
        return SegmentPlan.Create(info.Length, Math.Max(1, connections), _options.MinimumSegmentLength);
    }

    private async Task FetchAsync(
        Uri url,
        Segment segment,
        int index,
        IFileSink sink,
        TokenBucket limit,
        RemoteFileInfo info,
        CancellationToken cancellationToken)
    {
        int attempt = 0;

        while (!segment.IsComplete)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                long? end = info.CanSplit ? segment.End : null;
                await using var stream = await _transport
                    .OpenAsync(url, segment.Position, end, cancellationToken)
                    .ConfigureAwait(false);

                var buffer = new byte[_options.BufferSize];

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int wanted = info.CanSplit
                        ? (int)Math.Min(buffer.Length, segment.Remaining)
                        : buffer.Length;

                    if (wanted <= 0) break;

                    int read = await stream.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);

                    if (read == 0)
                    {
                        // A short read on an unbounded transfer is the end of the
                        // file; on a ranged one it is a dropped connection.
                        if (!info.CanSplit)
                        {
                            segment.MarkComplete();
                            break;
                        }

                        throw new IOException($"连接在第 {segment.Position} 字节被中断");
                    }

                    await ThrottleAsync(limit, read, cancellationToken).ConfigureAwait(false);
                    await sink.WriteAsync(segment.Position, buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

                    segment.Position += read;
                    Record(index, read);
                }

                attempt = 0;
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
                    throw new IOException($"连接 #{index + 1} 失败：{Describe(exception)}", exception);
                }

                Item.Log.Add(new LogEntry(Stamp(), $"连接 #{index + 1} 第 {attempt} 次重试：{Describe(exception)}", IsError: true));

                // Exponential backoff, so a struggling server is not hammered.
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

    private void Record(int index, int bytes)
    {
        var now = _clock.UtcNow;
        _meter.Record(bytes, now);

        lock (_gate)
        {
            if (index < _connectionMeters.Count) _connectionMeters[index].Record(bytes, now);
        }

        if (Plan is { } plan)
        {
            Item.Done = plan.Downloaded;
            Item.Connections = plan.ActiveSegmentCount;

            // The chunk map is only ever looked at a few times a second, so it
            // is rebuilt on a timer rather than on every read.
            if (now - _lastMapRefresh >= TimeSpan.FromMilliseconds(200))
            {
                _lastMapRefresh = now;
                Item.Blocks = plan.BlockMap(96);
                Item.ConnectionSpeeds = ConnectionSpeeds;
            }
        }

        Item.Speed = _meter.BytesPerSecond(now);
        Item.PeakSpeed = Math.Max(Item.PeakSpeed, Item.Speed);
    }

    private void Finish()
    {
        Item.Done = Item.Size > 0 ? Item.Size : Plan?.Downloaded ?? Item.Done;
        Item.Speed = 0;
        Item.Connections = 0;
        Item.Status = DownloadStatus.Completed;
        Item.ConnectionSpeeds = Array.Empty<double>();
        Item.Blocks = Enumerable.Repeat(1, 96).ToArray();
        Item.Log.Add(new LogEntry(Stamp(), "下载完成"));

        if (_resume is not null && TargetPath is not null) _resume.Delete(TargetPath);

        _meter.Reset();
    }

    internal async Task PersistAsync(CancellationToken cancellationToken)
    {
        if (_resume is null || TargetPath is null || Plan is not { IsBounded: true } plan) return;

        var state = new ResumeState(Item.Url, plan.TotalLength, null, null, plan.Snapshot());
        await _resume.SaveAsync(TargetPath, state, cancellationToken).ConfigureAwait(false);
    }

    private string Stamp() => _clock.UtcNow.ToLocalTime().ToString("HH:mm");

    private static string FormatBytes(long bytes) => Services.FormatHelpers.Bytes(bytes);

    /// <summary>Turns an exception into the one line the row's error state shows.</summary>
    internal static string Describe(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: not null } http => $"服务器返回 {(int)http.StatusCode!.Value}",
        HttpRequestException => "无法连接到服务器",
        IOException io => io.Message,
        UnauthorizedAccessException => "没有写入权限",
        _ => exception.Message,
    };

    /// <summary>Rewrites the resume sidecar on a timer while the transfer runs.</summary>
    private sealed class PeriodicPersister : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();

        /// <summary>
        /// Deliberately on the real clock rather than the injected one: this is
        /// housekeeping, not transfer logic, and a fake clock that returns from
        /// every delay immediately would turn it into a busy loop.
        /// </summary>
        public PeriodicPersister(DownloadJob job, TimeSpan interval)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        await Task.Delay(interval, _stop.Token).ConfigureAwait(false);
                        await job.PersistAsync(_stop.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Stopped with the transfer.
                }
            });
        }

        public void Dispose()
        {
            _stop.Cancel();
            _stop.Dispose();
        }
    }
}
