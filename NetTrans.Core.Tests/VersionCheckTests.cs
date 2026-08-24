using NetTrans.Models;
using NetTrans.Net;
using NetTrans.Tests.Fakes;
using Xunit;

namespace NetTrans.Tests;

/// <summary>
/// The 新版本 notice. The handoff leaves the source of truth as "a server-side
/// version query"; with plain HTTP the answerable question is whether the file
/// at the same URL has changed since we fetched it.
/// </summary>
public class VersionCheckTests
{
    [Fact]
    public async Task Reports_a_new_version_when_the_etag_moved_on()
    {
        var probe = new ScriptedProbe(Info(etag: "\"v2\"", length: 500));

        var newer = await VersionCheck.CheckAsync(Item(etag: "\"v1\"", size: 400), probe);

        Assert.NotNull(newer);
        Assert.Equal(500, newer!.Size);
    }

    [Fact]
    public async Task Says_nothing_when_the_etag_is_unchanged() =>
        Assert.Null(await VersionCheck.CheckAsync(Item(etag: "\"v1\""), new ScriptedProbe(Info(etag: "\"v1\""))));

    [Fact]
    public async Task Trusts_the_etag_over_a_changed_length()
    {
        // Same validator, different length: a truncated or ranged response, not
        // a new file.
        var probe = new ScriptedProbe(Info(etag: "\"v1\"", length: 999));
        Assert.Null(await VersionCheck.CheckAsync(Item(etag: "\"v1\"", size: 400), probe));
    }

    [Fact]
    public async Task Falls_back_to_last_modified_when_there_is_no_etag()
    {
        var item = Item(etag: null, lastModified: "Mon, 01 Jan 2026 00:00:00 GMT");
        var probe = new ScriptedProbe(Info(etag: null, lastModified: "Tue, 02 Jan 2026 00:00:00 GMT"));

        Assert.NotNull(await VersionCheck.CheckAsync(item, probe));
    }

    [Fact]
    public async Task Falls_back_to_length_when_there_are_no_validators()
    {
        var item = Item(etag: null, lastModified: null, size: 400);

        Assert.NotNull(await VersionCheck.CheckAsync(item, new ScriptedProbe(Info(etag: null, lastModified: null, length: 500))));
        Assert.Null(await VersionCheck.CheckAsync(item, new ScriptedProbe(Info(etag: null, lastModified: null, length: 400))));
    }

    [Fact]
    public async Task A_server_that_will_not_answer_is_not_a_new_version()
    {
        var probe = new ScriptedProbe(new HttpRequestException("down"));

        Assert.Null(await VersionCheck.CheckAsync(Item(), probe));
        Assert.Equal(1, probe.Probes);
    }

    [Theory]
    [InlineData("magnet:?xt=urn:btih:9f2c1a")]   // a valid absolute URI, but not one HEAD can answer
    [InlineData("ftp://example.test/file.bin")]
    [InlineData("file:///C:/downloads/file.bin")]
    [InlineData("not a url at all")]
    [InlineData("")]
    public async Task A_task_without_a_usable_url_is_skipped(string url)
    {
        var probe = new ScriptedProbe(Info());
        var item = Item();
        item.Url = url;

        Assert.Null(await VersionCheck.CheckAsync(item, probe));
        Assert.Equal(0, probe.Probes);
    }

    [Fact]
    public async Task Describes_how_long_ago_the_new_version_was_published()
    {
        var published = DateTimeOffset.UtcNow.AddDays(-3).ToString("R");
        var probe = new ScriptedProbe(Info(etag: "\"v2\"", lastModified: published));

        var newer = await VersionCheck.CheckAsync(Item(etag: "\"v1\""), probe);

        Assert.Equal("3 天前", newer!.Published);
    }

    [Fact]
    public async Task Says_the_time_is_unknown_when_the_server_does_not_give_one()
    {
        var newer = await VersionCheck.CheckAsync(Item(etag: "\"v1\""), new ScriptedProbe(Info(etag: "\"v2\"")));
        Assert.Equal("未知时间", newer!.Published);
    }

    private static DownloadItem Item(string? etag = "\"v1\"", string? lastModified = null, long size = 400) => new()
    {
        Id = 1,
        Name = "payload.iso",
        Host = "example.test",
        Kind = FileKind.Disc,
        Size = size,
        Category = "soft",
        Url = "https://example.test/payload.iso",
        SourceETag = etag,
        SourceLastModified = lastModified,
    };

    private static RemoteFileInfo Info(string? etag = "\"v1\"", string? lastModified = null, long length = 400) =>
        new(length, SupportsRanges: true, etag, lastModified, "payload.iso");
}
