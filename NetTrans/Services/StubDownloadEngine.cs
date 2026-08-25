using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using NetTrans.Models;
using NetTrans.ViewModels;

namespace NetTrans.Services;

/// <summary>
/// Timer-driven fake engine, seeded with the handoff's own SEED array and
/// ticking on the same 900ms cadence and the same growth curve
/// (kbs = max(60, kbs * (0.93 + rand * 0.15))). Swap it for a real
/// multi-segment HTTP engine behind <see cref="IDownloadEngine"/>.
/// </summary>
public sealed class StubDownloadEngine : IDownloadEngine
{
    private const int BlockCount = 96;
    private const int IslandSamples = 26;
    private const int SessionSamples = 40;
    private const double TickSeconds = 0.9;
    private const double Mb = 1024 * 1024;
    private const double Kb = 1024;


    private readonly DispatcherTimer _timer;
    private readonly Random _random = new();
    private readonly double[] _speedHistory = new double[IslandSamples];
    private int _nextId = 100;

    public ObservableCollection<DownloadItemViewModel> Tasks { get; } = new();

    /// <summary>
    /// Real, even in demo mode: 批量下载 and 视频嗅探 read pages off the network,
    /// and there is nothing to fake about that.
    /// </summary>
    public NetTrans.Net.IHttpTransport Transport { get; } = new NetTrans.Net.HttpTransport(userAgent: "NetTrans/1.0");

    public double TotalSpeed { get; private set; }
    public double UploadSpeed { get; private set; }
    public IReadOnlyList<double> SpeedHistory => _speedHistory;
    public bool IsRunning => Tasks.Any(t => t.IsRunning);

    public event EventHandler? Ticked;
    public event EventHandler<DownloadItemViewModel>? Completed;

    public StubDownloadEngine()
    {
        foreach (var item in Seed()) Tasks.Add(new DownloadItemViewModel(item));

        for (int i = 0; i < _speedHistory.Length; i++)
        {
            _speedHistory[i] = (900 + _random.NextDouble() * 900) * Kb;
        }

        Recompute();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(TickSeconds) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private void Tick()
    {
        foreach (var task in Tasks.ToList())
        {
            if (!task.IsRunning) continue;
            var model = task.Model;

            model.Speed = ProgressSimulator.NextSpeed(model.Speed, _random.NextDouble());
            model.Done = ProgressSimulator.Advance(model.Done, model.Size, model.Speed, TickSeconds);
            model.PeakSpeed = Math.Max(model.PeakSpeed, model.Speed);
            model.SpeedHistory = ProgressSimulator.Push(model.SpeedHistory, model.Speed, SessionSamples);

            if (model.Done >= model.Size)
            {
                model.Done = model.Size;
                model.Speed = 0;
                model.Status = DownloadStatus.Completed;
                model.Connections = 0;
                model.Checksum = NetTrans.Download.FileHash.Verified;
                model.ConnectionSpeeds = System.Array.Empty<double>();
                model.Blocks = Enumerable.Repeat(1, BlockCount).ToArray();
                model.Log.Add(new LogEntry(Stamp(), "下载完成"));
                task.Refresh();
                Completed?.Invoke(this, task);
                continue;
            }

            model.Blocks = MakeBlocks(model.Done / (double)model.Size);
            model.ConnectionSpeeds = MakeConnections(model.Connections, model.Speed);
            task.Refresh();
        }

        Recompute();
        Ticked?.Invoke(this, EventArgs.Empty);
    }

    private void Recompute()
    {
        TotalSpeed = Tasks.Sum(t => t.Speed);
        UploadSpeed = Tasks.Sum(t => t.Model.UploadSpeed);

        System.Array.Copy(_speedHistory, 1, _speedHistory, 0, _speedHistory.Length - 1);
        _speedHistory[^1] = TotalSpeed;
    }

    public void Toggle(int id)
    {
        var task = Tasks.FirstOrDefault(t => t.Id == id);
        if (task is null || task.IsDone) return;
        var model = task.Model;

        if (task.IsRunning)
        {
            model.Status = DownloadStatus.Paused;
            model.Speed = 0;
            model.ConnectionSpeeds = System.Array.Empty<double>();
            model.Log.Add(new LogEntry(Stamp(), "已暂停"));
        }
        else
        {
            model.Status = DownloadStatus.Downloading;
            model.Speed = (300 + _random.NextDouble() * 700) * Kb;
            model.Connections = model.Connections > 0 ? model.Connections : 8;
            model.ErrorMessage = null;
            model.Log.Add(new LogEntry(Stamp(), $"已建立 {model.Connections} 个连接"));
        }

        task.Refresh();
        Recompute();
    }

    public void Remove(IEnumerable<int> ids)
    {
        foreach (int id in ids.ToList())
        {
            if (Tasks.FirstOrDefault(t => t.Id == id) is { } task) Tasks.Remove(task);
        }

        Recompute();
    }

    public void ToggleAll()
    {
        bool pausing = IsRunning;

        foreach (var task in Tasks)
        {
            if (task.IsDone) continue;
            var model = task.Model;
            model.Status = pausing ? DownloadStatus.Paused : DownloadStatus.Downloading;
            model.Speed = pausing ? 0 : (300 + _random.NextDouble() * 800) * Kb;
            if (!pausing && model.Connections == 0) model.Connections = 8;
            task.Refresh();
        }

        Recompute();
    }

    public void MoveToFront(int id)
    {
        int index = IndexOf(id);
        if (index > 0) Tasks.Move(index, 0);
    }

    public void MoveToBack(int id)
    {
        int index = IndexOf(id);
        if (index >= 0 && index < Tasks.Count - 1) Tasks.Move(index, Tasks.Count - 1);
    }

    public void Redownload(int id)
    {
        var task = Tasks.FirstOrDefault(t => t.Id == id);
        if (task is null) return;

        var model = task.Model;
        model.Done = 0;
        model.Status = DownloadStatus.Downloading;
        model.Speed = (420 + _random.NextDouble() * 600) * Kb;
        model.Connections = model.Connections > 0 ? model.Connections : 8;
        model.Checksum = null;
        model.Blocks = MakeBlocks(0);
        model.Log.Add(new LogEntry(Stamp(), "按新版本重新开始下载"));

        task.NewerVersion = null;
        task.Refresh();
        Recompute();
    }

    /// <summary>Nothing to apply: the stub ignores concurrency and rate limits.</summary>
    /// <summary>The demo engine has no torrents to configure.</summary>
    public void ApplyTorrentOptions(int id, bool sequential, NetTrans.Torrent.SeedingLimits limits, double uploadLimit = 0)
    {
    }

    public void RememberReferer(Uri url, Uri page)
    {
    }

    public void ApplySettings(AppSettings settings)
    {
    }

    public string? PathOf(int id) =>
        Tasks.FirstOrDefault(task => task.Id == id) is { } task
            ? System.IO.Path.Combine(task.SavePath, task.Name)
            : null;

    /// <summary>The demo has no files, so it reports the state the seed data already claims.</summary>
    public Task<string?> VerifyAsync(int id, CancellationToken cancellationToken = default)
    {
        if (Tasks.FirstOrDefault(task => task.Id == id) is not { } task) return Task.FromResult<string?>(null);

        task.Checksum = NetTrans.Download.FileHash.Verified;
        task.Refresh();
        return Task.FromResult<string?>(task.Checksum);
    }

    /// <summary>The demo's 新版本 flags come from the seed data, not from a server.</summary>
    public Task<bool> CheckForUpdateAsync(int id, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public bool Rename(int id, string newName)
    {
        if (Tasks.FirstOrDefault(task => task.Id == id) is not { } task) return false;

        task.Model.Name = newName;
        task.Refresh();
        return true;
    }

    public DownloadItemViewModel Add(NewDownloadRequest request)
    {
        string name = FileNameFrom(request.Url);
        var kind = KindFrom(name);

        var model = new DownloadItem
        {
            Id = _nextId++,
            Name = name,
            Host = HostFrom(request.Url),
            Kind = kind,
            Size = (long)((80 + _random.NextDouble() * 900) * Mb),
            Category = request.Category,
            Tint = TintFor(kind),
            Url = request.Url,
            SavePath = request.SaveTo,
            Priority = request.Priority,
            Connections = request.StartNow ? request.Connections : 0,
            Status = request.StartNow ? DownloadStatus.Downloading : DownloadStatus.Queued,
            Speed = request.StartNow ? (300 + _random.NextDouble() * 700) * Kb : 0,
            AddedAt = "今天 " + DateTime.Now.ToString("HH:mm"),
        };

        model.Blocks = MakeBlocks(0);
        model.Log.Add(new LogEntry(Stamp(), "任务已创建"));
        model.Log.Add(new LogEntry(Stamp(), request.StartNow ? $"已连接 {model.Host}" : "已加入队列，等待空闲通道"));

        var task = new DownloadItemViewModel(model);
        Tasks.Add(task);
        Recompute();
        return task;
    }

    private int IndexOf(int id)
    {
        for (int i = 0; i < Tasks.Count; i++)
        {
            if (Tasks[i].Id == id) return i;
        }

        return -1;
    }

    // The handoff's mkBlocks / mkConns, with the randomness injected so the
    // rules can be exercised deterministically in NetTrans.Core.Tests.
    private int[] MakeBlocks(double fraction) =>
        ProgressSimulator.MakeBlocks(fraction, BlockCount, _random.NextDouble);

    private double[] MakeConnections(int count, double speed) =>
        ProgressSimulator.MakeConnections(count, speed, _random.NextDouble);

    private static string Stamp() => DateTime.Now.ToString("HH:mm");

    private static string FileNameFrom(string url)
    {
        string trimmed = url.Split('?')[0].TrimEnd('/');
        int slash = trimmed.LastIndexOf('/');
        string name = slash >= 0 && slash < trimmed.Length - 1 ? trimmed[(slash + 1)..] : trimmed;
        return name.Length == 0 ? "未命名下载" : name;
    }

    private static string HostFrom(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : "未知来源";

    private static FileKind KindFrom(string name)
    {
        string ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".iso" or ".img" or ".dmg" or ".torrent" => FileKind.Disc,
            ".mp4" or ".mkv" or ".mov" or ".avi" or ".webm" => FileKind.Film,
            ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".zst" => FileKind.Zip,
            ".flac" or ".mp3" or ".m4a" or ".wav" => FileKind.Music,
            _ => FileKind.Doc,
        };
    }

    private static string TintFor(FileKind kind) => kind switch
    {
        FileKind.Disc => "#FF9500",
        FileKind.Film => "#AF52DE",
        FileKind.Zip => "#5856D6",
        FileKind.Music => "#FF2D55",
        _ => "#0A84FF",
    };

    // ── seed data: the handoff's SEED, MB converted to bytes ──────────────
    private IEnumerable<DownloadItem> Seed()
    {
        yield return SeedTask(1, "ubuntu-24.04.2-desktop.iso", FileKind.Disc, "#FF9500", "soft", 5940, 3742, 1180,
            DownloadStatus.Downloading, 8, "releases.ubuntu.com",
            "https://releases.ubuntu.com/24.04/ubuntu-24.04.2-desktop-amd64.iso",
            checksum: "SHA-256 待校验", priority: TaskPriority.High,
            newer: new NewVersionInfo("ubuntu-24.04.3-desktop.iso", (long)(6042 * Mb), "2 天前"));

        yield return SeedTask(3, "4k-timelapse-reel.mp4", FileKind.Film, "#AF52DE", "video", 1180, 486, 742,
            DownloadStatus.Downloading, 6, "cdn.video-host.net",
            "https://cdn.video-host.net/stream/8841/4k-timelapse-reel.mp4");

        yield return SeedTask(5, "source-sans-pack.zip", FileKind.Zip, "#5856D6", "doc", 86, 21, 214,
            DownloadStatus.Downloading, 4, "fonts.mirror.dev",
            "https://fonts.mirror.dev/packs/source-sans-3-complete.zip");

        yield return SeedTask(4, "arch-linux-2026.08.torrent", FileKind.Disc, "#8E8E93", "bt", 3210, 1104, 0,
            DownloadStatus.Paused, 0, "12 个节点", "magnet:?xt=urn:btih:9f2c1a…",
            peers: 12, seeds: 4, ratio: 0.42, upload: 86);

        yield return SeedTask(7, "logic-samples-vol3.flac", FileKind.Music, "#FF2D55", "music", 740, 96, 0,
            DownloadStatus.Error, 0, "cdn.audio-lib.net",
            "https://cdn.audio-lib.net/vol3/logic-samples-vol3.flac",
            retries: 2, error: "连接被服务器重置",
            log: new[]
            {
                new LogEntry("09:38", "任务已创建"),
                new LogEntry("09:39", "已连接 cdn.audio-lib.net"),
                new LogEntry("09:44", "连接被服务器重置", IsError: true),
                new LogEntry("09:44", "第 2 次重试失败", IsError: true),
            });

        yield return SeedTask(8, "design-system-handoff.pdf", FileKind.Doc, "#0A84FF", "doc", 62, 0, 0,
            DownloadStatus.Queued, 0, "docs.internal", "https://docs.internal/handoff/design-system.pdf",
            log: new[] { new LogEntry("09:45", "已加入队列，等待空闲通道") });

        yield return SeedTask(2, "blender-4.2-portable.7z", FileKind.Zip, "#34C759", "soft", 412, 412, 0,
            DownloadStatus.Completed, 0, "mirror.blender.org",
            "https://mirror.blender.org/release/blender-4.2.7z",
            checksum: "SHA-256 已校验",
            newer: new NewVersionInfo("blender-4.2.1-portable.7z", (long)(418 * Mb), "今天 08:12"));

        yield return SeedTask(6, "quarterly-report-q2.pdf", FileKind.Doc, "#FF3B30", "doc", 14, 14, 0,
            DownloadStatus.Completed, 0, "docs.internal", "https://docs.internal/reports/2026-q2.pdf");
    }

    private DownloadItem SeedTask(
        int id, string name, FileKind kind, string tint, string category,
        double sizeMb, double doneMb, double speedKb,
        DownloadStatus status, int connections, string host, string url,
        string? checksum = null, TaskPriority priority = TaskPriority.Normal,
        NewVersionInfo? newer = null, int retries = 0, string? error = null,
        int? peers = null, int? seeds = null, double? ratio = null, double upload = 0,
        LogEntry[]? log = null)
    {
        var item = new DownloadItem
        {
            Id = id,
            Name = name,
            Kind = kind,
            Tint = tint,
            Category = category,
            Size = (long)(sizeMb * Mb),
            Done = (long)(doneMb * Mb),
            Speed = speedKb * Kb,
            Status = status,
            Connections = connections,
            Host = host,
            Url = url,
            Checksum = checksum,
            Priority = priority,
            NewerVersion = newer,
            Retries = retries,
            ErrorMessage = error,
            Peers = peers,
            Seeds = seeds,
            Ratio = ratio,
            UploadSpeed = upload * Kb,
            AddedAt = "今天 09:41",
        };

        item.PeakSpeed = item.Speed * 1.24;
        item.Blocks = MakeBlocks(item.Size == 0 ? 0 : item.Done / (double)item.Size);
        item.ConnectionSpeeds = MakeConnections(connections, item.Speed);
        item.SpeedHistory = Enumerable.Range(0, SessionSamples)
            .Select(_ => item.Speed * (0.6 + _random.NextDouble() * 0.7))
            .ToArray();

        item.Log.AddRange(log ?? new[]
        {
            new LogEntry("09:41", "任务已创建"),
            new LogEntry("09:41", $"已连接 {host}"),
            new LogEntry("09:41", "服务器支持断点续传"),
            new LogEntry("09:42", $"已建立 {connections} 个连接"),
        });

        return item;
    }
}
