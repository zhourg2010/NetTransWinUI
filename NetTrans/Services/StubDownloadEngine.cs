using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using NetTrans.Models;
using NetTrans.ViewModels;

namespace NetTrans.Services;

/// <summary>
/// Timer-driven fake download engine: seeds the same sample data as the
/// design reference's app.jsx (ACTIVE/COMPLETED/SOURCES) and advances
/// progress every 250ms with light noise so the UI has something live to
/// bind to. Swap this out for a real multi-segment HTTP engine later —
/// IDownloadEngine is the seam.
/// </summary>
public sealed class StubDownloadEngine : IDownloadEngine, IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;
    private readonly Random _rng = new();
    private readonly Dictionary<int, DownloadItemViewModel> _byId = new();
    private int _nextId = 100;
    private double _elapsedSeconds;
    private bool _erroredOne;

    public ObservableCollection<DownloadItemViewModel> ActiveDownloads { get; } = new();
    public ObservableCollection<DownloadItemViewModel> CompletedDownloads { get; } = new();

    public bool IsThrottled { get; set; }
    public double TotalSpeed { get; private set; }
    public long BytesTransferredThisMonth { get; private set; } = 412_000_000_000L;

    public event EventHandler? Ticked;

    private static readonly MirrorSource[] SampleMirrors =
    [
        new() { Region = "Frankfurt, DE", Host = "fra1.cdn.akamai.net", Speed = 6_400_000, Share = 36 },
        new() { Region = "Amsterdam, NL", Host = "ams2.cdn.akamai.net", Speed = 5_100_000, Share = 28 },
        new() { Region = "London, UK", Host = "lon3.cdn.akamai.net", Speed = 3_900_000, Share = 22 },
        new() { Region = "Stockholm, SE", Host = "arn1.cdn.akamai.net", Speed = 1_800_000, Share = 10 },
        new() { Region = "Madrid, ES", Host = "mad1.cdn.akamai.net", Speed = 600_000, Share = 4 },
    ];

    public StubDownloadEngine()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("StubDownloadEngine must be constructed on a UI thread with a DispatcherQueue.");

        SeedSampleData();

        _timer = _dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private void SeedSampleData()
    {
        DownloadItem[] active =
        [
            new() { Id = 1, Name = "Windows11_24H2_x64_en-us.iso", Host = "software-download.microsoft.com", Kind = FileKind.Iso, Category = "apps", Size = 5_840_000_000, Done = 2_104_000_000, Speed = 18_400_000, Status = DownloadStatus.Downloading, StartedAt = "10:24", SegmentCount = 8, Mirrors = SampleMirrors },
            new() { Id = 2, Name = "Anomaly Detection in Time Series.pdf", Host = "arxiv.org", Kind = FileKind.Doc, Category = "docs", Size = 12_400_000, Done = 9_300_000, Speed = 1_120_000, Status = DownloadStatus.Downloading, StartedAt = "10:31", SegmentCount = 4 },
            new() { Id = 3, Name = "Cinematic — Dunes (4K HDR).mkv", Host = "vimeo-cdn.akamai.net", Kind = FileKind.Video, Category = "video", Size = 2_840_000_000, Done = 0, Speed = 0, Status = DownloadStatus.Queued, StartedAt = "queued", SegmentCount = 8 },
            new() { Id = 4, Name = "ableton-live-12-suite.dmg", Host = "ableton.com", Kind = FileKind.App, Category = "apps", Size = 3_200_000_000, Done = 1_120_000_000, Speed = 0, Status = DownloadStatus.Paused, StartedAt = "09:58", SegmentCount = 8 },
            new() { Id = 5, Name = "research-corpus-2026-Q1.tar.zst", Host = "huggingface.co", Kind = FileKind.Zip, Category = "archives", Size = 48_700_000_000, Done = 47_900_000, Speed = 0, Status = DownloadStatus.Error, ErrorMessage = "Connection reset by peer", StartedAt = "09:42", SegmentCount = 16 },
        ];

        foreach (var item in active)
        {
            item.SegmentProgress = MakeInitialSegments(item);
            var vm = new DownloadItemViewModel(item, this);
            _byId[item.Id] = vm;
            ActiveDownloads.Add(vm);
        }
        _nextId = active.Max(a => a.Id) + 1;

        DownloadItem[] completed =
        [
            new() { Id = 11, Name = "Cyberpunk OST — Disc 2.flac.zip", Host = "bandcamp.com", Kind = FileKind.Zip, Category = "music", Size = 612_000_000, Done = 612_000_000, Status = DownloadStatus.Completed, CompletedWhen = "Today, 09:14", Duration = "00:01:42" },
            new() { Id = 12, Name = "blender-4.3.0-macos-arm64.dmg", Host = "blender.org", Kind = FileKind.App, Category = "apps", Size = 348_000_000, Done = 348_000_000, Status = DownloadStatus.Completed, CompletedWhen = "Today, 08:51", Duration = "00:00:38" },
            new() { Id = 13, Name = "Annual Report 2025.pdf", Host = "ir.contoso.com", Kind = FileKind.Doc, Category = "docs", Size = 18_400_000, Done = 18_400_000, Status = DownloadStatus.Completed, CompletedWhen = "Yesterday", Duration = "00:00:04" },
            new() { Id = 14, Name = "wallpapers-4k-pack-vol3.zip", Host = "unsplash.com", Kind = FileKind.Zip, Category = "archives", Size = 1_240_000_000, Done = 1_240_000_000, Status = DownloadStatus.Completed, CompletedWhen = "Yesterday", Duration = "00:02:18" },
            new() { Id = 15, Name = "Lecture 7 — Distributed Systems.mp4", Host = "stanford.edu", Kind = FileKind.Video, Category = "video", Size = 880_000_000, Done = 880_000_000, Status = DownloadStatus.Completed, CompletedWhen = "Mon", Duration = "00:01:11" },
        ];

        foreach (var item in completed)
        {
            CompletedDownloads.Add(new DownloadItemViewModel(item, this));
        }
    }

    private double[] MakeInitialSegments(DownloadItem item)
    {
        var segs = new double[item.SegmentCount];
        double basePct = item.Size > 0 ? item.Done / (double)item.Size * 100 : 0;
        for (int i = 0; i < segs.Length; i++)
        {
            segs[i] = Math.Clamp(basePct + _rng.NextDouble() * 20 - 10, 0, 100);
        }
        return segs;
    }

    private void Tick()
    {
        _elapsedSeconds += 0.25;
        double total = 0;

        foreach (var vm in ActiveDownloads.ToArray())
        {
            var item = vm.Model;

            if (item.Status != DownloadStatus.Downloading)
            {
                if (vm.Status != item.Status) vm.ApplyTick(item.Done, item.Speed, item.Status, item.ErrorMessage);
                continue;
            }

            double throttleFactor = IsThrottled ? 0.35 : 1.0;
            double noise = 0.85 + _rng.NextDouble() * 0.3;
            double speed = item.Speed <= 0 ? item.Size * 0.002 : item.Speed;
            speed = Math.Max(200_000, speed * noise) * throttleFactor;

            long done = Math.Min(item.Size, item.Done + (long)(speed * 0.25));
            item.Done = done;
            item.Speed = speed;
            item.DownloadElapsedSeconds += 0.25;
            total += speed;

            // Reassign (not mutate in place) so the SegmentMapControl DependencyProperty's
            // reference-equality change check actually fires and repaints.
            var segments = new double[item.SegmentProgress.Length];
            for (int i = 0; i < segments.Length; i++)
            {
                segments[i] = Math.Clamp(item.SegmentProgress[i] + _rng.NextDouble() * 6, 0, 100);
            }
            item.SegmentProgress = segments;
            item.SpeedHistory = AppendHistory(item.SpeedHistory, speed);

            if (done >= item.Size)
            {
                item.Status = DownloadStatus.Completed;
                item.Speed = 0;
                item.CompletedWhen = "Just now";
                item.Duration = FormatHelpers.Eta(TimeSpan.FromSeconds(_elapsedSeconds));
                vm.ApplyTick(item.Done, 0, DownloadStatus.Completed, null);
                ActiveDownloads.Remove(vm);
                CompletedDownloads.Insert(0, vm);
                continue;
            }

            if (!_erroredOne && item.DownloadElapsedSeconds >= 30)
            {
                item.Status = DownloadStatus.Error;
                item.ErrorMessage = "Connection timed out";
                item.Speed = 0;
                _erroredOne = true;
            }

            vm.ApplyTick(item.Done, item.Speed, item.Status, item.ErrorMessage);
        }

        TotalSpeed = total;
        BytesTransferredThisMonth += (long)(total * 0.25);
        Ticked?.Invoke(this, EventArgs.Empty);
    }

    private static double[] AppendHistory(double[] history, double speed)
    {
        var next = history.Length >= 60 ? history[1..] : history;
        var result = new double[next.Length + 1];
        Array.Copy(next, result, next.Length);
        result[^1] = speed;
        return result;
    }

    public void Pause(int id)
    {
        if (_byId.TryGetValue(id, out var vm) && vm.Model.Status == DownloadStatus.Downloading)
        {
            vm.Model.Status = DownloadStatus.Paused;
            vm.Model.Speed = 0;
            vm.ApplyTick(vm.Model.Done, 0, DownloadStatus.Paused, null);
        }
    }

    public void Resume(int id)
    {
        if (_byId.TryGetValue(id, out var vm) && vm.CanResume)
        {
            vm.Model.Status = DownloadStatus.Downloading;
            vm.Model.ErrorMessage = null;
            vm.ApplyTick(vm.Model.Done, vm.Model.Speed, DownloadStatus.Downloading, null);
        }
    }

    public void Retry(int id) => Resume(id);

    public void Remove(int id)
    {
        if (_byId.TryGetValue(id, out var vm))
        {
            ActiveDownloads.Remove(vm);
            CompletedDownloads.Remove(vm);
            _byId.Remove(id);
        }
    }

    public void PauseAll()
    {
        foreach (var vm in ActiveDownloads.ToArray()) Pause(vm.Id);
    }

    public void ResumeAll()
    {
        foreach (var vm in ActiveDownloads.ToArray())
        {
            if (vm.CanResume) Resume(vm.Id);
        }
    }

    public DownloadItemViewModel AddDownload(NewDownloadRequest request)
    {
        var host = Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) ? uri.Host : "unknown";
        var item = new DownloadItem
        {
            Id = _nextId++,
            Name = string.IsNullOrWhiteSpace(request.SaveAs) ? Path.GetFileName(request.Url) : request.SaveAs,
            Host = host,
            Kind = GuessKind(request.SaveAs),
            Category = request.Category,
            SavePath = request.SaveTo,
            Size = 1_000_000_000,
            Status = DownloadStatus.Queued,
            StartedAt = "queued",
            SegmentCount = request.Connections,
        };
        item.SegmentProgress = new double[item.SegmentCount];

        var vm = new DownloadItemViewModel(item, this);
        _byId[item.Id] = vm;
        ActiveDownloads.Insert(0, vm);

        item.Status = DownloadStatus.Downloading;
        vm.ApplyTick(0, 0, DownloadStatus.Downloading, null);
        return vm;
    }

    private static FileKind GuessKind(string fileName)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "iso" => FileKind.Iso,
            "pdf" or "doc" or "docx" or "txt" => FileKind.Doc,
            "mp4" or "mkv" or "mov" or "avi" => FileKind.Video,
            "exe" or "msi" or "dmg" or "app" => FileKind.App,
            "mp3" or "flac" or "wav" or "aac" => FileKind.Music,
            "png" or "jpg" or "jpeg" or "gif" or "webp" => FileKind.Image,
            _ => FileKind.Zip,
        };
    }

    public void Dispose() => _timer.Stop();
}
