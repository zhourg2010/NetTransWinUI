using NetTrans.Download;
using NetTrans.Media;
using NetTrans.Models;
using NetTrans.Tests.Fakes;
using Xunit;

namespace NetTrans.Tests;

/// <summary>
/// The HLS transfer. The thing most worth pinning down is order: a concatenated
/// stream is only playable if the segments land in the order the playlist named
/// them, however early a later one arrives.
/// </summary>
public class PlaylistJobTests
{
    private const string Base = "https://cdn.test/hls/";
    private const string PlaylistUrl = Base + "index.m3u8";

    [Fact]
    public async Task Fetches_every_segment_and_writes_them_in_order()
    {
        var server = new FakeHlsServer();
        server.Add(PlaylistUrl, server.AddSegments(Base, count: 12));

        var (job, sinks) = Build(server, connections: 4);

        Assert.Equal(JobOutcome.Completed, await job.RunAsync(CancellationToken.None));
        Assert.Equal(FakeHlsServer.Expected(12), sinks.Files.Values.Single().ToArray());
    }

    [Fact]
    public async Task A_single_lane_and_many_lanes_produce_the_same_file()
    {
        var server = new FakeHlsServer();
        string playlist = server.AddSegments(Base, count: 9);
        server.Add(PlaylistUrl, playlist);

        var (slow, slowSinks) = Build(server, connections: 1);
        await slow.RunAsync(CancellationToken.None);

        var (fast, fastSinks) = Build(server, connections: 8);
        await fast.RunAsync(CancellationToken.None);

        Assert.Equal(slowSinks.Files.Values.Single().ToArray(), fastSinks.Files.Values.Single().ToArray());
    }

    [Fact]
    public async Task The_file_is_named_after_the_container_not_the_playlist()
    {
        var server = new FakeHlsServer();
        server.Add(PlaylistUrl, server.AddSegments(Base, count: 2));

        var (job, _) = Build(server);
        await job.RunAsync(CancellationToken.None);

        // "index.m3u8" says nothing about the file and is not what it is.
        Assert.EndsWith(".ts", job.TargetPath!);
        Assert.DoesNotContain("m3u8", job.TargetPath!);
    }

    [Fact]
    public async Task An_fmp4_stream_gets_its_init_segment_first_and_an_mp4_name()
    {
        var server = new FakeHlsServer();
        server.Add(Base + "init.mp4", new byte[] { 0xAA, 0xBB });
        server.Add(Base + "seg-0.m4s", new byte[] { 1, 1, 1 });
        server.Add(Base + "seg-1.m4s", new byte[] { 2, 2, 2 });
        server.Add(PlaylistUrl, """
            #EXTM3U
            #EXT-X-MAP:URI="init.mp4"
            #EXTINF:4.0,
            seg-0.m4s
            #EXTINF:4.0,
            seg-1.m4s
            #EXT-X-ENDLIST
            """);

        var (job, sinks) = Build(server);
        await job.RunAsync(CancellationToken.None);

        Assert.EndsWith(".mp4", job.TargetPath!);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 1, 1, 1, 2, 2, 2 }, sinks.Files.Values.Single().ToArray());
    }

    [Fact]
    public async Task Encrypted_segments_are_decrypted_with_the_playlists_key()
    {
        var key = Enumerable.Range(0, 16).Select(n => (byte)n).ToArray();

        var server = new FakeHlsServer();
        server.Add(PlaylistUrl, server.AddSegments(Base, count: 6, key: key));

        var (job, sinks) = Build(server);

        Assert.Equal(JobOutcome.Completed, await job.RunAsync(CancellationToken.None));
        Assert.Equal(FakeHlsServer.Expected(6), sinks.Files.Values.Single().ToArray());
    }

    [Fact]
    public async Task An_explicit_iv_is_honoured_over_the_sequence_number()
    {
        var key = Enumerable.Range(0, 16).Select(n => (byte)n).ToArray();
        var iv = Enumerable.Repeat((byte)0x5A, 16).ToArray();

        var server = new FakeHlsServer();
        server.Add(PlaylistUrl, server.AddSegments(Base, count: 4, key: key, iv: iv));

        var (job, sinks) = Build(server);

        // Decrypting with the sequence number instead would come back as noise
        // rather than as an error, so the content is what proves it.
        Assert.Equal(JobOutcome.Completed, await job.RunAsync(CancellationToken.None));
        Assert.Equal(FakeHlsServer.Expected(4), sinks.Files.Values.Single().ToArray());
    }

    [Fact]
    public async Task The_key_is_fetched_once_for_the_whole_playlist()
    {
        var key = Enumerable.Range(0, 16).Select(n => (byte)n).ToArray();

        var server = new FakeHlsServer();
        server.Add(PlaylistUrl, server.AddSegments(Base, count: 30, key: key));

        var (job, _) = Build(server, connections: 6);
        await job.RunAsync(CancellationToken.None);

        Assert.Equal(1, server.Requests.Count(url => url.EndsWith("key.bin", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_master_playlist_is_followed_to_its_best_rendition()
    {
        var server = new FakeHlsServer();
        server.Add(Base + "1080/index.m3u8", server.AddSegments(Base + "1080/", count: 3));
        server.Add(Base + "480/index.m3u8", server.AddSegments(Base + "480/", count: 3));
        server.Add(PlaylistUrl, """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=5000000,RESOLUTION=1920x1080
            1080/index.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=1000000,RESOLUTION=854x480
            480/index.m3u8
            """);

        var (job, _) = Build(server);
        await job.RunAsync(CancellationToken.None);

        Assert.Equal("1080p", job.Streams[0].Quality);
        Assert.Contains(server.Requests, url => url.Contains("/1080/", StringComparison.Ordinal));
        Assert.DoesNotContain(server.Requests, url => url.Contains("/480/seg", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_dropped_segment_is_retried_rather_than_failing_the_file()
    {
        var server = new FakeHlsServer();
        server.Add(PlaylistUrl, server.AddSegments(Base, count: 5));
        server.FailOnce.Add(Base + "seg-3.ts");

        var (job, sinks) = Build(server, retries: 2);

        Assert.Equal(JobOutcome.Completed, await job.RunAsync(CancellationToken.None));
        Assert.Equal(FakeHlsServer.Expected(5), sinks.Files.Values.Single().ToArray());
        Assert.True(job.Item.Retries > 0);
    }

    [Fact]
    public async Task A_live_playlist_is_refused_with_a_reason_rather_than_downloaded()
    {
        var server = new FakeHlsServer();
        server.Add(PlaylistUrl, server.AddSegments(Base, count: 3).Replace("#EXT-X-ENDLIST\n", ""));

        var (job, _) = Build(server);

        Assert.Equal(JobOutcome.Failed, await job.RunAsync(CancellationToken.None));
        Assert.Contains("直播", job.Item.ErrorMessage!);
    }

    [Fact]
    public async Task Sample_aes_is_refused_rather_than_fetched_and_mangled()
    {
        var server = new FakeHlsServer();
        server.Add(Base + "seg-0.ts", new byte[16]);
        server.Add(PlaylistUrl, """
            #EXTM3U
            #EXT-X-KEY:METHOD=SAMPLE-AES,URI="key.bin"
            #EXTINF:4.0,
            seg-0.ts
            #EXT-X-ENDLIST
            """);

        var (job, _) = Build(server);

        Assert.Equal(JobOutcome.Failed, await job.RunAsync(CancellationToken.None));
        Assert.Contains("SAMPLE-AES", job.Item.ErrorMessage!);
    }

    [Fact]
    public async Task A_manifest_that_is_neither_format_says_so()
    {
        var server = new FakeHlsServer();
        server.Add(Base + "manifest.mpd", "<html>not a manifest</html>");

        var (job, _) = Build(server, url: Base + "manifest.mpd");

        Assert.Equal(JobOutcome.Failed, await job.RunAsync(CancellationToken.None));
        Assert.Contains("DASH", job.Item.ErrorMessage!);
    }

    [Fact]
    public async Task A_dash_manifest_downloads_like_a_playlist_does()
    {
        var server = new FakeHlsServer();
        server.Add(Base + "init.mp4", new byte[] { 0xAA });

        for (int i = 1; i <= 4; i++) server.Add($"{Base}seg-{i}.m4s", Enumerable.Repeat((byte)i, 100).ToArray());

        server.Add(Base + "manifest.mpd", """
            <MPD type="static" mediaPresentationDuration="PT16S">
              <Period>
                <AdaptationSet mimeType="video/mp4" codecs="avc1.640028,mp4a.40.2">
                  <Representation id="v0" bandwidth="800000" width="1920" height="1080">
                    <SegmentTemplate media="seg-$Number$.m4s" initialization="init.mp4" startNumber="1" duration="4" timescale="1"/>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """);

        var (job, sinks) = Build(server, url: Base + "manifest.mpd");

        Assert.Equal(JobOutcome.Completed, await job.RunAsync(CancellationToken.None));
        Assert.EndsWith(".mp4", job.TargetPath!);

        var expected = new byte[] { 0xAA }
            .Concat(Enumerable.Range(1, 4).SelectMany(i => Enumerable.Repeat((byte)i, 100)))
            .ToArray();

        Assert.Equal(expected, sinks.Files.Values.Single().ToArray());
    }

    [Fact]
    public async Task A_split_dash_manifest_produces_a_file_per_track_and_says_so()
    {
        var server = new FakeHlsServer();
        server.Add(Base + "v-init.mp4", new byte[] { 0x11 });
        server.Add(Base + "a-init.mp4", new byte[] { 0x22 });
        server.Add(Base + "v-1.m4s", Enumerable.Repeat((byte)0xB1, 50).ToArray());
        server.Add(Base + "a-1.m4s", Enumerable.Repeat((byte)0xA1, 30).ToArray());

        server.Add(Base + "manifest.mpd", """
            <MPD type="static" mediaPresentationDuration="PT4S">
              <Period>
                <AdaptationSet mimeType="video/mp4" codecs="avc1.640028">
                  <Representation id="v0" bandwidth="800000" width="1920" height="1080">
                    <SegmentTemplate media="v-$Number$.m4s" initialization="v-init.mp4" startNumber="1" duration="4" timescale="1"/>
                  </Representation>
                </AdaptationSet>
                <AdaptationSet mimeType="audio/mp4" codecs="mp4a.40.2">
                  <Representation id="a0" bandwidth="128000">
                    <SegmentTemplate media="a-$Number$.m4s" initialization="a-init.mp4" startNumber="1" duration="4" timescale="1"/>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """);

        var (job, sinks) = Build(server, url: Base + "manifest.mpd");

        Assert.Equal(JobOutcome.Completed, await job.RunAsync(CancellationToken.None));

        // Two files, named apart, and the log says why: neither is the whole
        // thing on its own, and a silent file labelled as the video would lie.
        Assert.Equal(2, job.Files.Count);
        Assert.Equal(2, sinks.Files.Count);
        Assert.Contains("-视频", job.Files[0]);
        Assert.Contains("-音频", job.Files[1]);
        Assert.Contains(job.Item.Log, entry => entry.Message.Contains("音视频分离"));
    }

    [Fact]
    public async Task Pausing_keeps_the_segments_it_had_and_resuming_appends_the_rest()
    {
        var server = new FakeHlsServer();
        server.Add(PlaylistUrl, server.AddSegments(Base, count: 10));

        var directory = Path.Combine(Path.GetTempPath(), "nettrans-hls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var resume = new PlaylistResumeStore();
            var sinks = new MemoryFileSinkFactory();

            // Held at the fourth segment, then paused.
            var gate = new TaskCompletionSource();
            int opened = 0;
            server.BeforeOpen = () =>
            {
                if (Interlocked.Increment(ref opened) <= 4) return Task.CompletedTask;
                return gate.Task;
            };

            var first = Job(server, sinks, Item(directory), connections: 1, resume: resume);
            var running = first.RunAsync(CancellationToken.None);

            await Until(() => first.Item.Done > 0);
            first.Pause();
            gate.SetResult();

            Assert.Equal(JobOutcome.Paused, await running);
            Assert.True(first.Item.Done > 0, "the paused transfer kept nothing");

            // A second job over the same file picks up where it stopped.
            server.BeforeOpen = null;
            var second = Job(server, sinks, Item(directory), connections: 1, resume: resume);

            Assert.Equal(JobOutcome.Completed, await second.RunAsync(CancellationToken.None));
            Assert.Equal(FakeHlsServer.Expected(10), sinks.Files.Values.Single().ToArray());
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Not worth failing a test over.
            }
        }
    }

    [Fact]
    public async Task Progress_is_counted_in_segments_and_ends_at_the_real_size()
    {
        var server = new FakeHlsServer();
        server.Add(PlaylistUrl, server.AddSegments(Base, count: 8, bytesEach: 1000));

        var (job, _) = Build(server, connections: 2);
        await job.RunAsync(CancellationToken.None);

        // A playlist never states a byte count, so the size is an estimate until
        // the last segment lands and then it is the truth.
        Assert.Equal(8000, job.Item.Size);
        Assert.Equal(8000, job.Item.Done);
        Assert.All(job.Item.Blocks, block => Assert.Equal(1, block));
    }

    [Fact]
    public void The_block_map_never_runs_ahead_of_what_is_written()
    {
        // Half the segments written means at most half the map filled, whatever
        // the rounding: the bar claiming progress the file does not have is
        // worse than a bar that lags.
        var map = PlaylistJob.BlockMap(total: 10, done: 5, width: 96);

        Assert.Equal(48, map.Count(block => block == 1));
        Assert.All(map[48..], block => Assert.Equal(0, block));
    }

    [Fact]
    public void An_empty_playlist_has_an_empty_block_map() =>
        Assert.All(PlaylistJob.BlockMap(total: 0, done: 0, width: 96), block => Assert.Equal(0, block));

    private static (PlaylistJob Job, MemoryFileSinkFactory Sinks) Build(
        FakeHlsServer server,
        int connections = 2,
        int retries = 3,
        string url = PlaylistUrl)
    {
        var sinks = new MemoryFileSinkFactory();
        return (Job(server, sinks, Item(url: url), connections, retries), sinks);
    }

    private static PlaylistJob Job(
        FakeHlsServer server,
        MemoryFileSinkFactory sinks,
        DownloadItem item,
        int connections = 2,
        int retries = 3,
        PlaylistResumeStore? resume = null) =>
        new(
            item,
            server,
            sinks,
            new ManualClock(),
            new DownloadOptions(
                Connections: connections,
                BufferSize: 256,
                MaxRetries: retries,
                RetryDelay: TimeSpan.FromMilliseconds(1)),
            resume);

    private static DownloadItem Item(string? savePath = null, string url = PlaylistUrl) => new()
    {
        Id = 1,
        Name = "index.m3u8",
        Host = "cdn.test",
        Kind = FileKind.Film,
        // A playlist never states a byte count; the transfer works it out.
        Size = 0,
        Category = "video",
        Url = url,
        SavePath = savePath ?? Path.Combine(Path.GetTempPath(), "nettrans-hls"),
        RequestedConnections = 0,
    };

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
}
