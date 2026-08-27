using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using NetTrans.Download;
using NetTrans.Torrent;
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
public sealed partial class HttpDownloadEngine : IDownloadEngine, IAsyncDisposable
{
    private const int IslandSamples = 26;

    private readonly CoreEngine _engine;
    private readonly SchemeTransport _transport;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly double[] _speedHistory = new double[IslandSamples];
    private readonly AppSettings _settings;

    /// <summary>Per-site Referer / Cookie / 账号密码, and the 代理 dropdown.</summary>
    private readonly RequestProfiles _profiles = new();
    private readonly LiveProxy _proxy = new();

    private readonly Dictionary<int, TorrentTaskOptions> _torrentOptions = new();

    /// <summary>Tasks that asked for 强制校验 and have not had it yet.</summary>
    private readonly HashSet<int> _rechecking = new();

    private int _nextId = 1;

    public HttpDownloadEngine(AppSettings settings)
    {
        _settings = settings;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _proxy.Set(settings.Proxy);

        // ftp:// and ftps:// go to their own transport; everything above this
        // is written against the one interface and never learns the difference.
        _transport = new SchemeTransport(
            new HttpTransport(userAgent: "NetTrans/1.0", profiles: _profiles, proxy: _proxy),
            new NetTrans.Net.Ftp.FtpTransport(profiles: _profiles));

        _engine = new CoreEngine(
            _transport,
            FileSinkFactory.Instance,
            SystemClock.Instance,
            ResumeStore.Instance,
            // Per-task connection counts come from the 新建下载 sheet; this is
            // only the fallback for a task that did not specify one.
            new DownloadOptions(Connections: 8, MaxRetries: SettingsRules.Retries(settings.RetryPolicy)),
            Math.Max(1, settings.MaxSimultaneousDownloads),
            SettingsRules.SpeedLimitAt(settings, DateTimeOffset.Now),
            PlaylistResumeStore.Instance,
            TorrentResumeStore.Instance);

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

    /// <summary>
    /// The task whose detail window is open, or 0 for none.
    ///
    /// Per-connection rates are built for this one alone: they are what the
    /// inspector's 连接 tab draws, and nothing else reads them.
    /// </summary>
    public int Inspected { get; set; }

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

            // A task deleted before it ever ran leaves its BT settings behind,
            // and nothing would ever come to collect them.
            _torrentOptions.Remove(id);
            _rechecking.Remove(id);

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
        // thunder:// / flashget:// / qqdl:// carry an ordinary address inside a
        // base64 wrapper. Unwrapping here rather than in each sheet means every
        // way in -- typed, pasted, dropped, sniffed -- gets it.
        request = request with { Url = PrivateLinks.Unwrap(request.Url) };

        // https://user:pass@host/file.iso is a normal thing to paste. The
        // credentials are lifted out and remembered for the host, and the URL
        // that gets stored and shown is the one without them -- a password does
        // not belong in a list row or a resume file.
        string url = request.Url;

        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute) &&
            RequestProfile.FromUserInfo(absolute) is { } credentials)
        {
            _profiles.Set(absolute, credentials);
            url = RequestProfile.WithoutUserInfo(absolute).AbsoluteUri;
            request = request with { Url = url };
        }

        // 按分类建子文件夹, then a name that is not already spoken for -- by a
        // file on disk or by another task in this queue heading for the same
        // place, which the disk cannot tell us about yet.
        string directory = SavePathPlanner.Directory(request.SaveTo, request.Category, _settings.FoldersByCategory);
        string name = SavePathPlanner.UniqueName(directory, FileNameFrom(request.Url), IsTaken);

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
            SavePath = directory,
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

    /// <summary>
    /// Whether anything is already headed for this path: a file that exists, or
    /// a task that has not written its first byte yet.
    /// </summary>
    private bool IsTaken(string path) =>
        System.IO.File.Exists(path) ||
        Tasks.Any(task => string.Equals(
            System.IO.Path.Combine(task.SavePath, task.Name), path, StringComparison.OrdinalIgnoreCase));

    public void ApplyTorrentOptions(int id, TorrentTaskOptions options)
    {
        // Held until the job starts: a queued task has no job yet, and a
        // running one has already read them.
        _torrentOptions[id] = options;

        if (_engine.JobFor(id) is TorrentJob job) Apply(job, options);
    }

    private static void Apply(TorrentJob job, TorrentTaskOptions options)
    {
        job.Sequential = options.Sequential;
        job.SeedingLimits = options.Limits;
        job.UploadLimit = options.UploadLimit;
        job.WantedFiles = options.Files;
    }

    public void RememberReferer(Uri url, Uri page) => _profiles.SetReferer(url, page);

    public void Recheck(int id)
    {
        if (Tasks.FirstOrDefault(task => task.Id == id) is not { } task) return;
        if (!NetTrans.Torrent.TorrentUrl.IsTorrent(task.Model.Url)) return;

        _rechecking.Add(id);

        task.Model.Log.Add(new LogEntry(DateTime.Now.ToString("HH:mm"), "已安排强制校验"));
        task.Refresh();

        // A transfer has read its resume record by the time it is running, so
        // rechecking means running it again -- which for a finished torrent is
        // the whole point, and is why this is Restart rather than Resume.
        _engine.Restart(id);
    }

    public void ApplySettings(AppSettings settings)
    {
        _proxy.Set(settings.Proxy);
        _engine.MaxConcurrent = Math.Max(1, settings.MaxSimultaneousDownloads);
        _engine.Options = _engine.Options with { MaxRetries = SettingsRules.Retries(settings.RetryPolicy) };
        ApplySpeedLimit(settings);
    }

    /// <summary>
    /// The cap in force right now, which 夜间不限速 makes a function of the
    /// clock rather than of the dropdown alone.
    /// </summary>
    private void ApplySpeedLimit(AppSettings settings)
    {
        double limit = SettingsRules.SpeedLimitAt(settings, DateTimeOffset.Now);
        if (Math.Abs(limit - _engine.GlobalSpeedLimit) < 0.5) return;

        _engine.GlobalSpeedLimit = limit;
    }

    public string? PathOf(int id) =>
        Tasks.FirstOrDefault(task => task.Id == id) is { } task
            ? System.IO.Path.Combine(task.SavePath, task.Name)
            : null;

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
        int inspected = Inspected;

        foreach (var task in Tasks)
        {
            // Per-connection rates only exist while a job is live.
            if (_engine.JobFor(task.Id) is { } job)
            {
                // A torrent job reads these once, when it starts, so they are
                // pushed on the first tick that finds it running.
                if (job is TorrentJob torrent)
                {
                    if (_torrentOptions.Remove(task.Id, out var options)) Apply(torrent, options);

                    // The job reads this when it starts and clears it once it
                    // has hashed the files.
                    if (_rechecking.Remove(task.Id)) torrent.Recheck = true;
                }

                // Only the inspector draws these, and it draws one task. Asking
                // every running job for a fresh array twice a second built a
                // rate per peer for rows nobody was looking at.
                if (task.Id == inspected) task.Model.ConnectionSpeeds = job.ConnectionSpeeds;

                // 单任务限速 can be changed from the inspector while the transfer
                // is running, and the job holds its own bucket.
                double limit = SpeedLimits.Parse(task.Model.SpeedLimit);
                if (Math.Abs(job.SpeedLimit - limit) > 0.5) job.SpeedLimit = limit;
            }

            task.Refresh();
        }

        // 夜间不限速 changes what the cap is without anyone touching a setting,
        // so the tick that already runs is where the window is noticed.
        ApplySpeedLimit(_settings);

        TotalSpeed = Tasks.Sum(task => task.Speed);

        Array.Copy(_speedHistory, 1, _speedHistory, 0, _speedHistory.Length - 1);
        _speedHistory[^1] = TotalSpeed;

        Ticked?.Invoke(this, EventArgs.Empty);
    }

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
        // A playlist is the video, as far as the row is concerned: what lands
        // on disk is a .ts or an .mp4, not the index.
        ".mp4" or ".mkv" or ".mov" or ".avi" or ".webm" or ".ts" or ".m3u8" or ".m3u" or ".mpd" => FileKind.Film,
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
