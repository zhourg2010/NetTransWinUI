using NetTrans.Net;
using NetTrans.Tests.Fakes;
using Xunit;

namespace NetTrans.Tests;

/// <summary>视频嗅探: finding playable sources in a page's markup.</summary>
public class MediaProbeTests
{
    private static readonly Uri Page = new("https://video-host.net/watch/8841");

    [Fact]
    public void Finds_source_elements_and_keeps_their_labels()
    {
        const string html = """
            <video poster="thumb.jpg">
              <source src="/stream/8841/2160.mp4" type="video/mp4" label="2160p">
              <source src="/stream/8841/1080.mp4" type="video/mp4" label="1080p">
            </video>
            """;

        var found = MediaProbe.Find(html, Page);

        Assert.Equal(new[] { "2160p", "1080p" }, found.Select(source => source.Quality));
        Assert.All(found, source => Assert.Equal("MP4", source.Format));
        Assert.Equal("https://video-host.net/stream/8841/2160.mp4", found[0].Url.AbsoluteUri);
    }

    [Fact]
    public void Finds_media_urls_buried_in_script_blobs()
    {
        const string html = """
            <script>var player = {"sources":[{"file":"https://cdn.video-host.net/8841/720p.mp4"}]};</script>
            """;

        var source = Assert.Single(MediaProbe.Find(html, Page));

        Assert.Equal("https://cdn.video-host.net/8841/720p.mp4", source.Url.AbsoluteUri);
        Assert.Equal("720p", source.Quality);
    }

    [Fact]
    public void Sorts_the_best_quality_first_and_audio_last()
    {
        const string html = """
            <source src="/a/audio.m4a">
            <source src="/a/720p.mp4">
            <source src="/a/2160p.mp4">
            <source src="/a/1080p.mp4">
            """;

        Assert.Equal(
            new[] { "2160p", "1080p", "720p", "音轨" },
            MediaProbe.Find(html, Page).Select(source => source.Quality));
    }

    [Fact]
    public void Labels_audio_only_sources()
    {
        var source = Assert.Single(MediaProbe.Find("""<source src="/a/track.m4a">""", Page));

        Assert.Equal("音轨", source.Quality);
        Assert.Equal("M4A", source.Format);
    }

    [Fact]
    public void Recognises_a_playlist_as_something_it_cannot_size()
    {
        var source = Assert.Single(MediaProbe.Find("""<source src="/a/master.m3u8">""", Page));

        Assert.True(source.IsPlaylist);
        Assert.Equal("M3U8", source.Format);
    }

    [Fact]
    public void Ignores_everything_that_is_not_media() =>
        Assert.Empty(MediaProbe.Find("""<a href="/a/notes.pdf">x</a><img src="/a/cover.png">""", Page));

    [Fact]
    public void Does_not_list_the_same_source_twice()
    {
        const string html = """
            <source src="https://cdn.x.test/a/1080p.mp4">
            <script>{"file":"https://cdn.x.test/a/1080p.mp4"}</script>
            """;

        Assert.Single(MediaProbe.Find(html, Page));
    }

    [Fact]
    public void Falls_back_to_a_plain_label_when_the_url_says_nothing() =>
        Assert.Equal("视频", Assert.Single(MediaProbe.Find("""<source src="/a/stream.mp4">""", Page)).Quality);

    [Fact]
    public async Task The_sniffer_asks_the_server_how_big_each_source_is()
    {
        var site = new FakeWebsite()
            .Page(Page.AbsoluteUri, """
                <source src="https://cdn.x.test/a/1080p.mp4">
                <source src="https://cdn.x.test/a/master.m3u8">
                """)
            .File("https://cdn.x.test/a/1080p.mp4", 412 * 1024 * 1024);

        var found = await new VideoSniffer(site).SniffAsync(Page);

        var video = found.Single(source => source.Format == "MP4");
        var playlist = found.Single(source => source.Format == "M3U8");

        Assert.Equal(412L * 1024 * 1024, video.SizeBytes);

        // A playlist has no single length, so it is not even probed.
        Assert.Null(playlist.SizeBytes);
        Assert.DoesNotContain("https://cdn.x.test/a/master.m3u8", site.Probed);
    }

    [Fact]
    public async Task The_sniffer_still_offers_a_source_whose_size_is_unknown()
    {
        var site = new FakeWebsite()
            .Page(Page.AbsoluteUri, """<source src="https://cdn.x.test/a/1080p.mp4">""")
            .Broken("https://cdn.x.test/a/1080p.mp4");

        var source = Assert.Single(await new VideoSniffer(site).SniffAsync(Page));
        Assert.Null(source.SizeBytes);
    }
}
