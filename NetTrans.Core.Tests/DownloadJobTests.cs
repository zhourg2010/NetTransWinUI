using NetTrans.Download;
using NetTrans.Models;
using NetTrans.Tests.Fakes;
using Xunit;

namespace NetTrans.Tests;

/// <summary>
/// The transfer loop, against a fake server and an in-memory file. These cover
/// the parts most likely to corrupt a download: how the file is split, what
/// happens when a connection drops, and whether a resumed transfer picks up
/// from the right offset.
/// </summary>
public class DownloadJobTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nettrans-tests-" + Guid.NewGuid().ToString("N"));

    public DownloadJobTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    [Fact]
    public async Task Downloads_the_whole_file()
    {
        var content = Payload(300_000);
        var (job, sinks, _, _) = Build(content);

        var outcome = await job.RunAsync(CancellationToken.None);

        Assert.Equal(JobOutcome.Completed, outcome);
        Assert.Equal(content, sinks.Files.Values.Single().ToArray());
        Assert.Equal(DownloadStatus.Completed, job.Item.Status);
        Assert.Equal(content.Length, job.Item.Done);
    }

    [Fact]
    public async Task Splits_the_file_across_the_requested_connections()
    {
        var content = Payload(8 * 1024 * 1024);
        var (job, _, transport, _) = Build(content, connections: 4);

        await job.RunAsync(CancellationToken.None);

        Assert.Equal(4, transport.Requests.Count);

        // The ranges must tile the file exactly, with no gap and no overlap.
        var ordered = transport.Requests.OrderBy(r => r.From).ToList();
        Assert.Equal(0, ordered[0].From);
        for (int i = 1; i < ordered.Count; i++)
        {
            Assert.Equal(ordered[i - 1].To!.Value + 1, ordered[i].From);
        }

        Assert.Equal(content.Length - 1, ordered[^1].To);
    }

    [Fact]
    public async Task Does_not_split_a_file_below_the_minimum_segment_size()
    {
        var content = Payload(100_000);
        var (job, _, transport, _) = Build(content, connections: 8);

        await job.RunAsync(CancellationToken.None);

        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task Uses_one_connection_when_the_server_refuses_ranges()
    {
        var content = Payload(4 * 1024 * 1024);
        var (job, sinks, transport, _) = Build(content, connections: 8, supportsRanges: false);

        var outcome = await job.RunAsync(CancellationToken.None);

        Assert.Equal(JobOutcome.Completed, outcome);
        Assert.Single(transport.Requests);
        Assert.Equal(content, sinks.Files.Values.Single().ToArray());
    }

    [Fact]
    public async Task Retries_a_dropped_connection_and_still_finishes()
    {
        var content = Payload(200_000);
        var (job, sinks, transport, _) = Build(content, connections: 1);
        transport.DropConnections = 2;
        transport.BytesBeforeDrop = 4096;

        var outcome = await job.RunAsync(CancellationToken.None);

        Assert.Equal(JobOutcome.Completed, outcome);
        Assert.Equal(content, sinks.Files.Values.Single().ToArray());
        Assert.True(job.Item.Retries >= 2, $"expected at least two retries, saw {job.Item.Retries}");

        // Each retry resumes from where the last one stopped rather than restarting.
        Assert.Equal(3, transport.Requests.Count);
        Assert.Equal(0, transport.Requests[0].From);
        Assert.True(transport.Requests[1].From > 0);
        Assert.True(transport.Requests[2].From > transport.Requests[1].From);
    }

    [Fact]
    public async Task Backs_off_further_after_each_retry()
    {
        var content = Payload(200_000);
        var (job, _, transport, clock) = Build(content, connections: 1);
        transport.DropConnections = 2;
        transport.BytesBeforeDrop = 4096;

        await job.RunAsync(CancellationToken.None);

        var backoffs = clock.Delays.Where(delay => delay >= TimeSpan.FromMilliseconds(1)).ToList();
        Assert.True(backoffs.Count >= 2, "expected a delay per retry");
        Assert.True(backoffs[1] > backoffs[0], "the second backoff should be longer than the first");
    }

    [Fact]
    public async Task Gives_up_after_the_retry_budget_and_reports_the_error()
    {
        var content = Payload(50_000);
        var (job, _, transport, _) = Build(content, connections: 1, retries: 1);
        transport.DropConnections = 99;
        transport.BytesBeforeDrop = 8;

        var outcome = await job.RunAsync(CancellationToken.None);

        Assert.Equal(JobOutcome.Failed, outcome);
        Assert.Equal(DownloadStatus.Error, job.Item.Status);
        Assert.False(string.IsNullOrWhiteSpace(job.Item.ErrorMessage));
        Assert.Contains(job.Item.Log, entry => entry.IsError);
    }

    [Fact]
    public async Task Reports_a_server_that_will_not_answer()
    {
        var content = Payload(1000);
        var (job, _, transport, _) = Build(content, connections: 1, retries: 0);
        transport.OpenFailure = new HttpRequestException("boom", null, System.Net.HttpStatusCode.NotFound);

        var outcome = await job.RunAsync(CancellationToken.None);

        Assert.Equal(JobOutcome.Failed, outcome);
        Assert.Contains("服务器返回 404", job.Item.ErrorMessage);
    }

    [Fact]
    public async Task Resumes_from_the_sidecar_instead_of_starting_over()
    {
        var content = Payload(400_000);
        var store = new ResumeStore();
        var (job, sinks, transport, _) = Build(content, connections: 1, resume: store);

        // Pretend an earlier run got a quarter of the way through.
        const long already = 100_000;
        var partial = new MemoryFileSink(content.Length);
        await partial.WriteAsync(0, content.AsMemory(0, (int)already), CancellationToken.None);
        sinks.Seed(job.TargetPathFor(), partial);

        await store.SaveAsync(
            job.TargetPathFor(),
            new ResumeState(job.Item.Url, content.Length, transport.ETag, null,
                new[] { new SegmentState(0, content.Length - 1, already) }),
            CancellationToken.None);

        var outcome = await job.RunAsync(CancellationToken.None);

        Assert.Equal(JobOutcome.Completed, outcome);
        Assert.Equal(already, transport.Requests.Single().From);
        Assert.Equal(content, sinks.Files.Values.Single().ToArray());
    }

    [Fact]
    public async Task Starts_over_when_the_remote_file_has_changed()
    {
        var content = Payload(400_000);
        var store = new ResumeStore();
        var (job, sinks, transport, _) = Build(content, connections: 1, resume: store);

        var partial = new MemoryFileSink(content.Length);
        sinks.Seed(job.TargetPathFor(), partial);

        // The sidecar remembers a different ETag, so the partial bytes are stale.
        await store.SaveAsync(
            job.TargetPathFor(),
            new ResumeState(job.Item.Url, content.Length, "\"v0\"", null,
                new[] { new SegmentState(0, content.Length - 1, 100_000) }),
            CancellationToken.None);

        await job.RunAsync(CancellationToken.None);

        Assert.Equal(0, transport.Requests.Single().From);
        Assert.Contains(job.Item.Log, entry => entry.Message.Contains("已变化"));
    }

    [Fact]
    public async Task Deletes_the_sidecar_once_the_transfer_finishes()
    {
        var content = Payload(50_000);
        var store = new ResumeStore();
        var (job, _, _, _) = Build(content, connections: 1, resume: store);

        await job.RunAsync(CancellationToken.None);

        Assert.False(File.Exists(ResumeStore.SidecarPath(job.TargetPathFor())));
    }

    [Fact]
    public async Task Pausing_keeps_the_progress_it_had()
    {
        var content = Payload(2 * 1024 * 1024);
        var (job, _, transport, _) = Build(content, connections: 1);

        // Pause once the first buffer has landed, so the outcome is not a race.
        transport.AfterFirstRead = () => job.Pause();

        var outcome = await job.RunAsync(CancellationToken.None);

        Assert.Equal(JobOutcome.Paused, outcome);
        Assert.Equal(DownloadStatus.Paused, job.Item.Status);
        Assert.Equal(0d, job.Item.Speed);
        Assert.InRange(job.Item.Done, 1, content.Length - 1);
    }

    [Fact]
    public async Task A_paused_transfer_can_be_resumed_from_where_it_stopped()
    {
        var content = Payload(2 * 1024 * 1024);
        var store = new ResumeStore();
        var (job, sinks, transport, _) = Build(content, connections: 1, resume: store);

        transport.AfterFirstRead = () => job.Pause();
        Assert.Equal(JobOutcome.Paused, await job.RunAsync(CancellationToken.None));

        long stopped = job.Item.Done;
        Assert.True(stopped > 0);

        transport.AfterFirstRead = null;
        transport.Requests.Clear();

        var resumed = new DownloadJob(job.Item, transport, sinks, new ManualClock(),
            new DownloadOptions(BufferSize: 8 * 1024, ResumeSaveInterval: TimeSpan.FromHours(1)), store);

        Assert.Equal(JobOutcome.Completed, await resumed.RunAsync(CancellationToken.None));
        Assert.Equal(stopped, transport.Requests[0].From);
        Assert.Equal(content, sinks.Files.Values.Single().ToArray());
    }

    [Fact]
    public async Task Fills_in_the_chunk_map_and_the_connection_count()
    {
        var content = Payload(8 * 1024 * 1024);
        var (job, _, _, _) = Build(content, connections: 4);

        await job.RunAsync(CancellationToken.None);

        Assert.Equal(96, job.Item.Blocks.Length);
        Assert.All(job.Item.Blocks, block => Assert.Equal(1, block));
        Assert.Equal(0, job.Item.Connections);
    }

    [Fact]
    public async Task Names_the_file_from_the_request_when_it_has_one()
    {
        var content = Payload(1000);
        var (job, sinks, _, _) = Build(content);

        await job.RunAsync(CancellationToken.None);

        Assert.Equal(Path.Combine(_directory, "wanted.bin"), sinks.Files.Keys.Single());
    }

    private static byte[] Payload(int length)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = (byte)(i * 31 % 251);
        return bytes;
    }

    [Fact]
    public async Task Changing_the_per_task_limit_reaches_a_running_transfer()
    {
        var (job, _, transport, _) = Build(Payload(50_000));
        job.SpeedLimit = 4096;

        // Held at the first open, so the transfer is live and holding its bucket.
        var gate = new TaskCompletionSource();
        transport.BeforeOpen = () => gate.Task;
        var running = job.RunAsync(CancellationToken.None);

        await Until(() => job.EffectiveSpeedLimit == 4096);

        // 单任务限速 changed from the inspector while it runs.
        job.SpeedLimit = 0;
        Assert.Equal(0, job.EffectiveSpeedLimit);

        gate.SetResult();
        Assert.Equal(JobOutcome.Completed, await running);
    }

    [Fact]
    public void A_limit_set_before_the_transfer_starts_is_the_one_in_force()
    {
        var (job, _, _, _) = Build(Payload(1000));
        job.SpeedLimit = 2048;

        Assert.Equal(2048, job.EffectiveSpeedLimit);
    }

    private static async Task Until(Func<bool> condition, int timeoutMilliseconds = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);

        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.Fail($"The job did not reach the expected state within {timeoutMilliseconds}ms.");
    }

    private (DownloadJob Job, MemoryFileSinkFactory Sinks, FakeHttpTransport Transport, ManualClock Clock) Build(
        byte[] content,
        int connections = 1,
        bool supportsRanges = true,
        int retries = 3,
        ResumeStore? resume = null)
    {
        var transport = new FakeHttpTransport(content, supportsRanges);
        var sinks = new MemoryFileSinkFactory();
        var clock = new ManualClock();

        var item = new DownloadItem
        {
            Id = 1,
            Name = "wanted.bin",
            Host = "example.test",
            Kind = FileKind.Doc,
            Size = content.Length,
            Category = "doc",
            Url = "https://example.test/wanted.bin",
            SavePath = _directory,
            RequestedConnections = connections,
        };

        var options = new DownloadOptions(
            Connections: connections,
            MinimumSegmentLength: 1024 * 1024,
            BufferSize: 8 * 1024,
            MaxRetries: retries,
            RetryDelay: TimeSpan.FromMilliseconds(10),
            ResumeSaveInterval: TimeSpan.FromHours(1));

        return (new DownloadJob(item, transport, sinks, clock, options, resume), sinks, transport, clock);
    }
}

internal static class DownloadJobTestExtensions
{
    /// <summary>The path a job will write to, which the transfer only computes once it runs.</summary>
    public static string TargetPathFor(this DownloadJob job) => Path.Combine(job.Item.SavePath, job.Item.Name);
}
