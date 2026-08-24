using NetTrans.Download;
using NetTrans.Net;
using Xunit;

namespace NetTrans.Tests;

/// <summary>The sidecar that makes 断点续传 survive a restart.</summary>
public class ResumeStateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nettrans-resume-" + Guid.NewGuid().ToString("N"));

    public ResumeStateTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
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
    public void Matches_a_file_with_the_same_etag() =>
        Assert.True(State(etag: "\"v1\"").Matches(Info(etag: "\"v1\"")));

    [Fact]
    public void Rejects_a_file_whose_etag_moved_on() =>
        Assert.False(State(etag: "\"v1\"").Matches(Info(etag: "\"v2\"")));

    [Fact]
    public void Rejects_a_file_that_changed_length() =>
        Assert.False(State().Matches(Info(length: 999)));

    [Fact]
    public void Falls_back_to_last_modified_when_there_is_no_etag()
    {
        var state = new ResumeState("https://example.test/f", 1000, null, "Mon, 01 Jan 2026 00:00:00 GMT", Array.Empty<SegmentState>());

        Assert.True(state.Matches(Info(etag: null, lastModified: "Mon, 01 Jan 2026 00:00:00 GMT")));
        Assert.False(state.Matches(Info(etag: null, lastModified: "Tue, 02 Jan 2026 00:00:00 GMT")));
    }

    [Fact]
    public void Accepts_a_bare_server_on_length_alone()
    {
        var state = new ResumeState("https://example.test/f", 1000, null, null, Array.Empty<SegmentState>());
        Assert.True(state.Matches(Info(etag: null, lastModified: null)));
    }

    [Fact]
    public async Task Round_trips_through_the_sidecar()
    {
        var store = new ResumeStore();
        string target = Path.Combine(_directory, "payload.bin");

        var state = new ResumeState("https://example.test/payload.bin", 4096, "\"abc\"", null, new[]
        {
            new SegmentState(0, 2047, 1024),
            new SegmentState(2048, 4095, 4096),
        });

        await store.SaveAsync(target, state, CancellationToken.None);
        var loaded = await store.LoadAsync(target, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(state.Url, loaded!.Url);
        Assert.Equal(state.TotalLength, loaded.TotalLength);
        Assert.Equal(state.ETag, loaded.ETag);
        Assert.Equal(state.Segments, loaded.Segments);
    }

    [Fact]
    public async Task Reads_back_nothing_when_there_is_no_sidecar() =>
        Assert.Null(await new ResumeStore().LoadAsync(Path.Combine(_directory, "missing.bin"), CancellationToken.None));

    [Fact]
    public async Task Survives_a_corrupt_sidecar()
    {
        var store = new ResumeStore();
        string target = Path.Combine(_directory, "corrupt.bin");
        await File.WriteAllTextAsync(ResumeStore.SidecarPath(target), "{ this is not json");

        Assert.Null(await store.LoadAsync(target, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_removes_the_sidecar()
    {
        var store = new ResumeStore();
        string target = Path.Combine(_directory, "done.bin");
        await store.SaveAsync(target, State(), CancellationToken.None);

        store.Delete(target);

        Assert.False(File.Exists(ResumeStore.SidecarPath(target)));
    }

    [Fact]
    public void Deleting_a_sidecar_that_is_not_there_is_fine() =>
        new ResumeStore().Delete(Path.Combine(_directory, "never-existed.bin"));

    [Fact]
    public void The_sidecar_sits_beside_the_file() =>
        Assert.Equal(@"C:\Downloads\a.iso.nettrans", ResumeStore.SidecarPath(@"C:\Downloads\a.iso"));

    private static ResumeState State(string? etag = "\"v1\"") =>
        new("https://example.test/f", 1000, etag, null, new[] { new SegmentState(0, 999, 100) });

    private static RemoteFileInfo Info(long length = 1000, string? etag = "\"v1\"", string? lastModified = null) =>
        new(length, SupportsRanges: true, etag, lastModified, "f");
}
