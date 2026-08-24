using NetTrans.Download;
using NetTrans.Models;
using NetTrans.Tests.Fakes;
using Xunit;

namespace NetTrans.Tests;

/// <summary>The queue: how many run at once, in what order, and what pausing does.</summary>
public class DownloadEngineTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nettrans-engine-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHttpTransport _transport = new(Payload(50_000));
    private readonly MemoryFileSinkFactory _sinks = new();
    private DownloadEngine _engine = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_engine is not null) await _engine.DisposeAsync();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Not worth failing a test over.
        }
    }

    [Fact]
    public async Task Runs_a_task_to_completion()
    {
        _engine = Engine();
        var item = Item(1);

        DownloadItem? completed = null;
        _engine.Completed += (_, finished) => completed = finished;

        _engine.Add(item);

        await Until(() => item.Status == DownloadStatus.Completed);
        Assert.Same(item, completed);
        Assert.Equal(item.Size, item.Done);
    }

    [Fact]
    public async Task Holds_extra_tasks_in_the_queue()
    {
        var gate = new TaskCompletionSource();
        _transport.BeforeOpen = () => gate.Task;
        _engine = Engine(maxConcurrent: 2);

        var items = new[] { Item(1), Item(2), Item(3), Item(4) };
        foreach (var item in items) _engine.Add(item);

        await Until(() => items.Count(i => i.Status == DownloadStatus.Downloading) == 2);

        Assert.Equal(2, items.Count(i => i.Status == DownloadStatus.Downloading));
        Assert.Equal(2, items.Count(i => i.Status == DownloadStatus.Queued));

        gate.SetResult();
        await Until(() => items.All(i => i.Status == DownloadStatus.Completed));
    }

    [Fact]
    public async Task Starts_the_highest_priority_task_first()
    {
        var gate = new TaskCompletionSource();
        _transport.BeforeOpen = () => gate.Task;
        _engine = Engine(maxConcurrent: 1);

        var started = new List<int>();
        _engine.StatusChanged += (_, item) =>
        {
            if (item.Status != DownloadStatus.Downloading) return;
            lock (started) started.Add(item.Id);
        };

        // The first one takes the only slot; the rest queue behind it.
        var blocker = Item(1);
        _engine.Add(blocker);
        await Until(() => blocker.Status == DownloadStatus.Downloading);

        var low = Item(2, TaskPriority.Low);
        var normal = Item(3);
        var high = Item(4, TaskPriority.High);
        foreach (var item in new[] { low, normal, high }) _engine.Add(item);

        gate.SetResult();
        await Until(() => new[] { blocker, low, normal, high }.All(i => i.Status == DownloadStatus.Completed));

        // Slot freed by the blocker goes to 高, then 普通, then 低.
        lock (started) Assert.Equal(new[] { blocker.Id, high.Id, normal.Id, low.Id }, started);
    }

    [Fact]
    public async Task Toggling_pauses_a_running_task()
    {
        var gate = new TaskCompletionSource();
        _transport.BeforeOpen = () => gate.Task;
        _engine = Engine();

        var item = Item(1);
        _engine.Add(item);
        await Until(() => item.Status == DownloadStatus.Downloading);

        _engine.Toggle(item.Id);
        gate.SetResult();

        await Until(() => item.Status == DownloadStatus.Paused);
        Assert.Equal(0d, item.Speed);
    }

    [Fact]
    public async Task Toggling_a_queued_task_stands_it_down_without_starting_it()
    {
        var gate = new TaskCompletionSource();
        _transport.BeforeOpen = () => gate.Task;
        _engine = Engine(maxConcurrent: 1);

        var running = Item(1);
        var queued = Item(2);
        _engine.Add(running);
        await Until(() => running.Status == DownloadStatus.Downloading);
        _engine.Add(queued);

        _engine.Toggle(queued.Id);

        Assert.Equal(DownloadStatus.Paused, queued.Status);
        gate.SetResult();
    }

    [Fact]
    public async Task A_paused_task_starts_again_when_resumed()
    {
        _engine = Engine();
        var item = Item(1);
        _engine.Add(item, startNow: false);

        Assert.Equal(DownloadStatus.Paused, item.Status);

        _engine.Resume(item.Id);
        await Until(() => item.Status == DownloadStatus.Completed);
    }

    [Fact]
    public async Task Pausing_everything_leaves_nothing_running()
    {
        var gate = new TaskCompletionSource();
        _transport.BeforeOpen = () => gate.Task;
        _engine = Engine(maxConcurrent: 3);

        var items = new[] { Item(1), Item(2), Item(3) };
        foreach (var item in items) _engine.Add(item);
        await Until(() => items.All(i => i.Status == DownloadStatus.Downloading));

        _engine.PauseAll();
        gate.SetResult();

        await Until(() => items.All(i => i.Status == DownloadStatus.Paused));
    }

    [Fact]
    public async Task A_failure_is_reported_and_frees_the_slot()
    {
        _transport.OpenFailure = new HttpRequestException("nope", null, System.Net.HttpStatusCode.ServiceUnavailable);
        _engine = Engine(maxConcurrent: 1, retries: 0);

        DownloadItem? failed = null;
        _engine.Failed += (_, item) => failed = item;

        var broken = Item(1);
        _engine.Add(broken);
        await Until(() => broken.Status == DownloadStatus.Error);

        Assert.Same(broken, failed);
        Assert.Contains("服务器返回 503", broken.ErrorMessage);

        // The queue must keep moving after a failure.
        _transport.OpenFailure = null;
        var next = Item(2);
        _engine.Add(next);
        await Until(() => next.Status == DownloadStatus.Completed);
    }

    [Fact]
    public async Task Removing_a_task_drops_it_from_the_queue()
    {
        var gate = new TaskCompletionSource();
        _transport.BeforeOpen = () => gate.Task;
        _engine = Engine();

        var item = Item(1);
        _engine.Add(item);
        await Until(() => item.Status == DownloadStatus.Downloading);

        _engine.Remove(item.Id);
        gate.SetResult();

        Assert.DoesNotContain(_engine.Items, i => i.Id == item.Id);
    }

    [Fact]
    public async Task Queue_moves_reorder_the_list()
    {
        _engine = Engine();
        var items = new[] { Item(1), Item(2), Item(3) };
        foreach (var item in items) _engine.Add(item, startNow: false);

        _engine.MoveToFront(3);
        Assert.Equal(new[] { 3, 1, 2 }, _engine.Items.Select(i => i.Id));

        _engine.MoveToBack(3);
        Assert.Equal(new[] { 1, 2, 3 }, _engine.Items.Select(i => i.Id));

        await Task.CompletedTask;
    }

    [Fact]
    public async Task Raising_the_concurrency_starts_queued_work()
    {
        var gate = new TaskCompletionSource();
        _transport.BeforeOpen = () => gate.Task;
        _engine = Engine(maxConcurrent: 1);

        var items = new[] { Item(1), Item(2), Item(3) };
        foreach (var item in items) _engine.Add(item);
        await Until(() => items.Count(i => i.Status == DownloadStatus.Downloading) == 1);

        _engine.MaxConcurrent = 3;

        await Until(() => items.Count(i => i.Status == DownloadStatus.Downloading) == 3);
        gate.SetResult();
    }

    [Fact]
    public void The_global_limit_is_readable_and_writable()
    {
        _engine = Engine();
        _engine.GlobalSpeedLimit = 4 * 1024 * 1024;
        Assert.Equal(4 * 1024 * 1024, _engine.GlobalSpeedLimit);
    }

    private DownloadEngine Engine(int maxConcurrent = 4, int retries = 0) => new(
        _transport,
        _sinks,
        new ManualClock(),
        resume: null,
        options: new DownloadOptions(
            Connections: 1,
            BufferSize: 8 * 1024,
            MaxRetries: retries,
            RetryDelay: TimeSpan.FromMilliseconds(1),
            ResumeSaveInterval: TimeSpan.FromHours(1)),
        maxConcurrent: maxConcurrent);

    private DownloadItem Item(int id, TaskPriority priority = TaskPriority.Normal) => new()
    {
        Id = id,
        Name = $"file-{id}.bin",
        Host = "example.test",
        Kind = FileKind.Doc,
        Size = 50_000,
        Category = "doc",
        Url = $"https://example.test/file-{id}.bin",
        SavePath = _directory,
        RequestedConnections = 1,
        Priority = priority,
    };

    private static byte[] Payload(int length)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = (byte)(i % 251);
        return bytes;
    }

    /// <summary>Waits for the engine's background work to reach a state, rather than sleeping a fixed amount.</summary>
    private static async Task Until(Func<bool> condition, int timeoutMilliseconds = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);

        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.Fail($"The engine did not reach the expected state within {timeoutMilliseconds}ms.");
    }
}
