using NetTrans.Models;
using NetTrans.Net;

namespace NetTrans.Download;

/// <summary>
/// The queue: how many transfers run at once, in what order, and under what
/// global speed cap. Everything it drives is injectable, so the queueing rules
/// can be tested without a network or a UI.
/// </summary>
public sealed class DownloadEngine : IAsyncDisposable
{
    private readonly IHttpTransport _transport;
    private readonly IFileSinkFactory _sinks;
    private readonly IClock _clock;
    private readonly ResumeStore? _resume;
    private readonly DownloadOptions _options;
    private readonly TokenBucket _globalLimit;

    private readonly List<DownloadItem> _items = new();
    private readonly Dictionary<int, Running> _running = new();
    private readonly object _gate = new();

    private int _maxConcurrent;
    private bool _disposed;

    public DownloadEngine(
        IHttpTransport transport,
        IFileSinkFactory sinks,
        IClock? clock = null,
        ResumeStore? resume = null,
        DownloadOptions? options = null,
        int maxConcurrent = 3,
        double globalSpeedLimit = 0)
    {
        _transport = transport;
        _sinks = sinks;
        _clock = clock ?? SystemClock.Instance;
        _resume = resume;
        _options = options ?? new DownloadOptions();
        _maxConcurrent = Math.Max(1, maxConcurrent);
        _globalLimit = new TokenBucket(globalSpeedLimit, _clock.UtcNow);
    }

    /// <summary>Every task, in queue order.</summary>
    public IReadOnlyList<DownloadItem> Items
    {
        get
        {
            lock (_gate) return _items.ToList();
        }
    }

    public int MaxConcurrent
    {
        get => _maxConcurrent;
        set
        {
            _maxConcurrent = Math.Max(1, value);
            Pump();
        }
    }

    /// <summary>The 全局限速 dropdown, in bytes per second. Zero means 不限.</summary>
    public double GlobalSpeedLimit
    {
        get => _globalLimit.BytesPerSecond;
        set => _globalLimit.BytesPerSecond = value;
    }

    public double TotalSpeed
    {
        get
        {
            lock (_gate) return _items.Sum(item => item.Speed);
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate) return _items.Any(item => item.Status == DownloadStatus.Downloading);
        }
    }

    public event EventHandler<DownloadItem>? Completed;

    public event EventHandler<DownloadItem>? Failed;

    /// <summary>Raised whenever a task's status changes, so a shell can refresh.</summary>
    public event EventHandler<DownloadItem>? StatusChanged;

    /// <summary>Adds a task and starts it if a slot is free, or queues it if not.</summary>
    public void Add(DownloadItem item, bool startNow = true)
    {
        lock (_gate)
        {
            _items.Add(item);
            item.Status = startNow ? DownloadStatus.Queued : DownloadStatus.Paused;
        }

        Pump();
    }

    public void Remove(int id)
    {
        Running? running;

        lock (_gate)
        {
            _items.RemoveAll(item => item.Id == id);
            _running.Remove(id, out running);
        }

        running?.Cancel();
        Pump();
    }

    /// <summary>
    /// The row's 暂停 / 继续 / 重试, which is the same button in three states.
    ///
    /// A queued task counts as running here: it is going to start on its own,
    /// so the action the user has is to stand it down. The prototype starts it
    /// instead, but it has no concurrency limit, so "queued" never lasts there.
    /// </summary>
    public void Toggle(int id)
    {
        var item = Find(id);
        if (item is null || item.Status == DownloadStatus.Completed) return;

        if (item.Status is DownloadStatus.Downloading or DownloadStatus.Queued) Pause(id);
        else Resume(id);
    }

    public void Pause(int id)
    {
        Running? running;
        lock (_gate) _running.TryGetValue(id, out running);

        if (running is not null)
        {
            running.Job.Pause();
            return;
        }

        // Queued but not yet started: it never has to stop, just stand down.
        if (Find(id) is { Status: DownloadStatus.Queued } item) SetStatus(item, DownloadStatus.Paused);
    }

    public void Resume(int id)
    {
        if (Find(id) is not { } item || item.Status == DownloadStatus.Completed) return;

        item.ErrorMessage = null;
        SetStatus(item, DownloadStatus.Queued);
        Pump();
    }

    public void PauseAll()
    {
        foreach (var item in Items.Where(item => item.Status is DownloadStatus.Downloading or DownloadStatus.Queued))
        {
            Pause(item.Id);
        }
    }

    public void ResumeAll()
    {
        foreach (var item in Items.Where(item => item.Status is DownloadStatus.Paused or DownloadStatus.Error))
        {
            Resume(item.Id);
        }
    }

    public void MoveToFront(int id)
    {
        lock (_gate)
        {
            int index = _items.FindIndex(item => item.Id == id);
            if (index <= 0) return;

            var item = _items[index];
            _items.RemoveAt(index);
            _items.Insert(0, item);
        }
    }

    public void MoveToBack(int id)
    {
        lock (_gate)
        {
            int index = _items.FindIndex(item => item.Id == id);
            if (index < 0 || index == _items.Count - 1) return;

            var item = _items[index];
            _items.RemoveAt(index);
            _items.Add(item);
        }
    }

    /// <summary>The live job for a task, when it has one -- the source of per-connection rates.</summary>
    public DownloadJob? JobFor(int id)
    {
        lock (_gate) return _running.TryGetValue(id, out var running) ? running.Job : null;
    }

    /// <summary>Starts queued tasks while there are free slots, highest priority first.</summary>
    private void Pump()
    {
        if (_disposed) return;

        while (true)
        {
            DownloadItem next;
            Running running;

            lock (_gate)
            {
                if (_running.Count >= _maxConcurrent) return;

                var candidate = _items
                    // A task that is still winding down holds its slot: it can
                    // be re-queued while its old job is finishing, and starting
                    // a second job for it would leave two writers on one file.
                    .Where(item => item.Status == DownloadStatus.Queued && !_running.ContainsKey(item.Id))
                    .OrderBy(item => item.Priority switch
                    {
                        TaskPriority.High => 0,
                        TaskPriority.Normal => 1,
                        _ => 2,
                    })
                    .ThenBy(item => _items.IndexOf(item))
                    .FirstOrDefault();

                if (candidate is null) return;
                next = candidate;

                var job = new DownloadJob(next, _transport, _sinks, _clock, _options, _resume, _globalLimit)
                {
                    SpeedLimit = SpeedLimits.Parse(next.SpeedLimit),
                };

                running = new Running(job);
                _running[next.Id] = running;

                // Marked Downloading before the job can run, not after: a short
                // transfer can otherwise finish and set Completed while we are
                // still on our way to this line, and we would overwrite it.
                next.Status = DownloadStatus.Downloading;
            }

            StatusChanged?.Invoke(this, next);
            running.Start(this);
        }
    }

    private async Task RunAsync(Running running)
    {
        var item = running.Job.Item;

        JobOutcome outcome;
        try
        {
            outcome = await running.Job.RunAsync(running.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // The message only; the status is set below with everything else,
            // so the change still reaches StatusChanged.
            item.ErrorMessage = DownloadJob.Describe(exception);
            outcome = JobOutcome.Failed;
        }

        bool stillQueued;
        lock (_gate)
        {
            // Only our own registration, never a successor's.
            if (_running.TryGetValue(item.Id, out var current) && ReferenceEquals(current, running))
            {
                _running.Remove(item.Id);
            }

            stillQueued = _items.Any(existing => existing.Id == item.Id);
        }

        // Removed while it was running: nobody is listening for it any more.
        if (!stillQueued)
        {
            Pump();
            return;
        }

        switch (outcome)
        {
            case JobOutcome.Completed:
                SetStatus(item, DownloadStatus.Completed);
                Completed?.Invoke(this, item);
                break;

            case JobOutcome.Failed:
                SetStatus(item, DownloadStatus.Error);
                Failed?.Invoke(this, item);
                break;

            default:
                // Re-queued while this job was stopping: the user's newer
                // intent wins, and the Pump below acts on it.
                if (item.Status == DownloadStatus.Downloading) SetStatus(item, DownloadStatus.Paused);
                break;
        }

        Pump();
    }

    private DownloadItem? Find(int id)
    {
        lock (_gate) return _items.FirstOrDefault(item => item.Id == id);
    }

    private void SetStatus(DownloadItem item, DownloadStatus status)
    {
        if (item.Status == status) return;
        item.Status = status;
        StatusChanged?.Invoke(this, item);
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;

        List<Running> running;
        lock (_gate)
        {
            running = _running.Values.ToList();
            _running.Clear();
        }

        foreach (var entry in running) entry.Cancel();

        // Give the transfers a moment to persist their resume state.
        await Task.WhenAll(running.Select(entry => entry.Completion)).ConfigureAwait(false);
    }

    private sealed class Running
    {
        private readonly CancellationTokenSource _cancellation = new();

        // Completes only when the transfer has actually finished, and exists
        // from construction: a task is registered as running slightly before
        // it is started, and a dispose landing in that gap still has to wait
        // for it rather than seeing an already-finished placeholder.
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Running(DownloadJob job) => Job = job;

        public DownloadJob Job { get; }

        public Task Completion => _completion.Task;

        public CancellationToken Token => _cancellation.Token;

        public void Start(DownloadEngine engine) => _ = Task.Run(async () =>
        {
            try
            {
                await engine.RunAsync(this).ConfigureAwait(false);
            }
            finally
            {
                _completion.TrySetResult();
            }
        });

        public void Cancel()
        {
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already torn down.
            }
        }
    }
}
