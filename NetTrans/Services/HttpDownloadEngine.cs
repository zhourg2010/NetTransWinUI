using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using NetTrans.Download;
using NetTrans.Models;
using NetTrans.Net;
using NetTrans.ViewModels;
using CoreEngine = NetTrans.Download.DownloadEngine;

namespace NetTrans.Services;

/// <summary>
/// The real engine: <see cref="CoreEngine"/> doing the transfers, this class
/// keeping the view models in step with it.
///
/// The transfers run on the thread pool and mutate their models there; the UI
/// only ever reads those models on a timer tick, on the UI thread, which is
/// also when the observable properties are raised. Nothing else crosses the
/// boundary.
/// </summary>
public sealed class HttpDownloadEngine : IDownloadEngine, IAsyncDisposable
{
    private const int IslandSamples = 26;

    private readonly CoreEngine _engine;
    private readonly HttpTransport _transport;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly double[] _speedHistory = new double[IslandSamples];
    private readonly AppSettings _settings;

    private int _nextId = 1;

    public HttpDownloadEngine(AppSettings settings)
    {
        _settings = settings;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _transport = new HttpTransport(userAgent: "NetTrans/1.0");

        _engine = new CoreEngine(
            _transport,
            FileSinkFactory.Instance,
            SystemClock.Instance,
            ResumeStore.Instance,
            // Per-task connection counts come from the 新建下载 sheet; this is
            // only the fallback for a task that did not specify one.
            new DownloadOptions(Connections: 8),
            Math.Max(1, settings.MaxSimultaneousDownloads),
            SpeedLimits.Parse(settings.GlobalSpeedLimit));

        _engine.Completed += OnCoreCompleted;
        _engine.Failed += OnCoreStatusChanged;
        _engine.StatusChanged += OnCoreStatusChanged;

        // The transfers move bytes continuously; the UI only needs to see it a
        // few times a second, which is also the only place view models change.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public ObservableCollection<DownloadItemViewModel> Tasks { get; } = new();

    public IHttpTransport Transport => _transport;

    public double TotalSpeed { get; private set; }

    /// <summary>Always zero: an HTTP downloader has nothing to upload. BT would.</summary>
    public double UploadSpeed => 0;

    public IReadOnlyList<double> SpeedHistory => _speedHistory;

    public bool IsRunning => _engine.IsRunning;

    public event EventHandler? Ticked;

    public event EventHandler<DownloadItemViewModel>? Completed;

    public void Toggle(int id) => _engine.Toggle(id);

    public void Remove(IEnumerable<int> ids)
    {
        foreach (int id in ids.ToList())
        {
            _engine.Remove(id);

            if (Tasks.FirstOrDefault(task => task.Id == id) is { } removed) Tasks.Remove(removed);
        }
    }

    public void ToggleAll()
    {
        if (IsRunning) _engine.PauseAll();
        else _engine.ResumeAll();
    }

    public void MoveToFront(int id)
    {
        _engine.MoveToFront(id);
        Reorder();
    }

    public void MoveToBack(int id)
    {
        _engine.MoveToBack(id);
        Reorder();
    }

    public void Redownload(int id)
    {
        if (Tasks.FirstOrDefault(task => task.Id == id) is not { } task) return;

        var model = task.Model;

        // A newer build is a different file: throw away everything from the old one.
        if (model.NewerVersion is { } newer)
        {
            model.Name = newer.Version;
            model.Size = newer.Size;
        }

        model.Done = 0;
        model.Checksum = null;
        model.ErrorMessage = null;
        model.Blocks = Array.Empty<int>();
        model.ConnectionSpeeds = Array.Empty<double>();
        model.Log.Add(new LogEntry(DateTime.Now.ToString("HH:mm"), "按新版本重新开始下载"));

        ResumeStore.Instance.Delete(System.IO.Path.Combine(model.SavePath, model.Name));

        task.NewerVersion = null;
        task.Refresh();

        _engine.Resume(id);
    }

    public DownloadItemViewModel Add(NewDownloadRequest request)
    {
        string name = FileNameFrom(request.Url);

        var model = new DownloadItem
        {
            Id = _nextId++,
            Name = name,
            Host = HostFrom(request.Url),
            Kind = KindFrom(name),
            Size = 0, // filled in by the probe
            Category = request.Category,
            Tint = TintFor(KindFrom(name)),
            Url = request.Url,
            SavePath = request.SaveTo,
            Priority = request.Priority,
            RequestedConnections = Math.Max(1, request.Connections),
            AddedAt = "今天 " + DateTime.Now.ToString("HH:mm"),
        };

        model.Log.Add(new LogEntry(DateTime.Now.ToString("HH:mm"), "任务已创建"));

        var task = new DownloadItemViewModel(model);
        Tasks.Add(task);
        _engine.Add(model, request.StartNow);

        return task;
    }

    public void ApplySettings(AppSettings settings)
    {
        _engine.MaxConcurrent = Math.Max(1, settings.MaxSimultaneousDownloads);
        _engine.GlobalSpeedLimit = SpeedLimits.Parse(settings.GlobalSpeedLimit);
    }

    public string? PathOf(int id) =>
        Tasks.FirstOrDefault(task => task.Id == id) is { } task
            ? System.IO.Path.Combine(task.SavePath, task.Name)
            : null;

    public async Task<string?> VerifyAsync(int id, CancellationToken cancellationToken = default)
    {
        if (Tasks.FirstOrDefault(task => task.Id == id) is not { } task) return null;

        string path = System.IO.Path.Combine(task.SavePath, task.Name);
        if (!System.IO.File.Exists(path)) return null;

        string hash;
        try
        {
            // Hashing a several-gigabyte file is not something to do on the UI
            // thread, and ComputeFileAsync opens the file for async reads.
            hash = await FileHash.ComputeFileAsync(path, cancellationToken: cancellationToken).ConfigureAwait(true);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        task.Model.Sha256 = hash;
        task.Checksum = FileHash.Describe(hash, expected: null);
        task.Model.Log.Add(new LogEntry(DateTime.Now.ToString("HH:mm"), $"SHA-256 {hash}"));
        task.Refresh();

        return task.Checksum;
    }

    public async Task<bool> CheckForUpdateAsync(int id, CancellationToken cancellationToken = default)
    {
        if (Tasks.FirstOrDefault(task => task.Id == id) is not { } task) return false;

        var newer = await VersionCheck.CheckAsync(task.Model, _transport, cancellationToken).ConfigureAwait(true);
        if (newer is null) return false;

        task.NewerVersion = newer;
        task.Refresh();
        return true;
    }

    public bool Rename(int id, string newName)
    {
        if (Tasks.FirstOrDefault(task => task.Id == id) is not { } task) return false;

        newName = FileActions.Sanitise(newName);
        if (newName.Length == 0 || newName == task.Name) return false;

        // Renaming under a live transfer would leave the writer pointed at the
        // old handle and the resume sidecar pointed at neither.
        if (task.IsRunning) return false;

        string path = System.IO.Path.Combine(task.SavePath, task.Name);
        if (!FileActions.Rename(path, newName, out _)) return false;

        ResumeStore.Instance.Delete(path);
        task.Model.Name = newName;
        task.Refresh();
        return true;
    }

    /// <summary>The one place view models are refreshed, on the UI thread.</summary>
    private void Tick()
    {
        foreach (var task in Tasks)
        {
            // Per-connection rates only exist while a job is live.
            if (_engine.JobFor(task.Id) is { } job) task.Model.ConnectionSpeeds = job.ConnectionSpeeds;

            task.Refresh();
        }

        TotalSpeed = Tasks.Sum(task => task.Speed);

        Array.Copy(_speedHistory, 1, _speedHistory, 0, _speedHistory.Length - 1);
        _speedHistory[^1] = TotalSpeed;

        Ticked?.Invoke(this, EventArgs.Empty);
    }

    private void OnCoreCompleted(object? sender, DownloadItem item) =>
        _dispatcher.TryEnqueue(async () =>
        {
            if (Tasks.FirstOrDefault(task => task.Id == item.Id) is not { } task) return;

            task.Refresh();
            Completed?.Invoke(this, task);

            // 完成后校验 SHA-256, then see whether the server has moved on. Both
            // are after the fact, so a failure in either must not disturb a
            // transfer that already succeeded.
            try
            {
                if (_settings.VerifyChecksums) await VerifyAsync(item.Id);
                await CheckForUpdateAsync(item.Id);
            }
            catch (Exception)
            {
                // Housekeeping; the download itself is done.
            }
        });

    private void OnCoreStatusChanged(object? sender, DownloadItem item) =>
        _dispatcher.TryEnqueue(() => Tasks.FirstOrDefault(task => task.Id == item.Id)?.Refresh());

    /// <summary>Mirrors the core engine's queue order back onto the observable collection.</summary>
    private void Reorder()
    {
        var order = _engine.Items.Select(item => item.Id).ToList();

        for (int target = 0; target < order.Count; target++)
        {
            int current = IndexOf(order[target]);
            if (current < 0 || current == target) continue;
            Tasks.Move(current, target);
        }
    }

    private int IndexOf(int id)
    {
        for (int i = 0; i < Tasks.Count; i++)
        {
            if (Tasks[i].Id == id) return i;
        }

        return -1;
    }

    private static string FileNameFrom(string url)
    {
        string trimmed = url.Split('?')[0].TrimEnd('/');
        int slash = trimmed.LastIndexOf('/');
        string name = slash >= 0 && slash < trimmed.Length - 1 ? trimmed[(slash + 1)..] : trimmed;
        return name.Length == 0 ? "未命名下载" : Uri.UnescapeDataString(name);
    }

    private static string HostFrom(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : "未知来源";

    private static FileKind KindFrom(string name) => System.IO.Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".iso" or ".img" or ".dmg" or ".torrent" => FileKind.Disc,
        ".mp4" or ".mkv" or ".mov" or ".avi" or ".webm" => FileKind.Film,
        ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".zst" => FileKind.Zip,
        ".flac" or ".mp3" or ".m4a" or ".wav" => FileKind.Music,
        _ => FileKind.Doc,
    };

    private static string TintFor(FileKind kind) => kind switch
    {
        FileKind.Disc => "#FF9500",
        FileKind.Film => "#AF52DE",
        FileKind.Zip => "#5856D6",
        FileKind.Music => "#FF2D55",
        _ => "#0A84FF",
    };

    public async ValueTask DisposeAsync()
    {
        _timer.Stop();
        await _engine.DisposeAsync();
        _transport.Dispose();
    }
}
