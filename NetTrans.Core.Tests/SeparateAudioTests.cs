using System.Xml.Linq;
using NetTrans.Media;
using NetTrans.Tests.Fakes;
using Xunit;

namespace NetTrans.Tests;

/// <summary>
/// The case both manifest formats share and both get wrong quietly: the video
/// and the audio are separate streams, so fetching the video alone gives a
/// silent film and says nothing about it.
/// </summary>
public class SeparateAudioTests
{
    private static readonly Uri Master = new("https://cdn.test/hls/master.m3u8");

    // ── HLS: #EXT-X-MEDIA ─────────────────────────────────────────────────

    [Fact]
    public void An_audio_rendition_with_a_uri_is_a_separate_track()
    {
        var renditions = M3U8.ParseRenditions(MasterWithAudio, Master);
        var audio = Assert.Single(renditions);

        Assert.True(audio.IsAudio);
        Assert.True(audio.IsSeparate);
        Assert.Equal("aac", audio.GroupId);
        Assert.Equal(new Uri("https://cdn.test/hls/audio/en.m3u8"), audio.Url);
    }

    [Fact]
    public void An_audio_rendition_without_a_uri_is_already_inside_the_variant()
    {
        const string text = """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aac",NAME="English",DEFAULT=YES
            #EXT-X-STREAM-INF:BANDWIDTH=5000000,RESOLUTION=1920x1080,AUDIO="aac"
            1080/index.m3u8
            """;

        var renditions = M3U8.ParseRenditions(text, Master);

        Assert.False(renditions.Single().IsSeparate);
        Assert.Null(M3U8.AudioFor(M3U8.ParseMaster(text, Master)[0], renditions));
    }

    [Fact]
    public void A_variant_is_matched_to_its_own_audio_group()
    {
        const string text = """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="lo",NAME="Low",DEFAULT=YES,URI="lo.m3u8"
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="hi",NAME="High",DEFAULT=YES,URI="hi.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=5000000,RESOLUTION=1920x1080,AUDIO="hi"
            1080/index.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360,AUDIO="lo"
            360/index.m3u8
            """;

        var variants = M3U8.ParseMaster(text, Master);
        var renditions = M3U8.ParseRenditions(text, Master);

        Assert.Equal("hi.m3u8", M3U8.AudioFor(variants[0], renditions)!.Url!.Segments[^1]);
        Assert.Equal("lo.m3u8", M3U8.AudioFor(variants[1], renditions)!.Url!.Segments[^1]);
    }

    [Fact]
    public void The_default_rendition_of_a_group_is_the_one_taken()
    {
        const string text = """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aac",NAME="French",URI="fr.m3u8"
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aac",NAME="English",DEFAULT=YES,URI="en.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=5000000,AUDIO="aac"
            v.m3u8
            """;

        var audio = M3U8.AudioFor(M3U8.ParseMaster(text, Master)[0], M3U8.ParseRenditions(text, Master))!;

        Assert.Equal("English", audio.Name);
    }

    [Fact]
    public void Subtitles_are_not_mistaken_for_audio()
    {
        const string text = """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="subs",NAME="English",URI="en.vtt.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=5000000,SUBTITLES="subs"
            v.m3u8
            """;

        Assert.False(M3U8.ParseRenditions(text, Master).Single().IsAudio);
        Assert.Null(M3U8.AudioFor(M3U8.ParseMaster(text, Master)[0], M3U8.ParseRenditions(text, Master)));
    }

    [Fact]
    public async Task An_hls_stream_with_separate_audio_comes_back_as_two_streams()
    {
        var server = new FakeHlsServer();
        server.Add("https://cdn.test/hls/1080/index.m3u8", server.AddSegments("https://cdn.test/hls/1080/", 3));
        server.Add("https://cdn.test/hls/audio/en.m3u8", server.AddSegments("https://cdn.test/hls/audio/", 3));
        server.Add(Master.AbsoluteUri, MasterWithAudio);

        var streams = await new StreamLoader(server).LoadAsync(Master);

        // The whole point: two files, not one silent one.
        Assert.Equal(2, streams.Count);
        Assert.Equal(TrackKind.Video, streams[0].Track);
        Assert.Equal(TrackKind.Audio, streams[1].Track);
        Assert.Equal("-视频", streams[0].NameSuffix);
        Assert.Equal("-音频", streams[1].NameSuffix);
    }

    [Fact]
    public async Task An_hls_stream_whose_variant_carries_its_own_audio_stays_one_stream()
    {
        var server = new FakeHlsServer();
        server.Add("https://cdn.test/hls/1080/index.m3u8", server.AddSegments("https://cdn.test/hls/1080/", 3));
        server.Add(Master.AbsoluteUri, """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=5000000,RESOLUTION=1920x1080,CODECS="avc1.640028,mp4a.40.2"
            1080/index.m3u8
            """);

        var streams = await new StreamLoader(server).LoadAsync(Master);

        Assert.Single(streams);
        Assert.Equal(TrackKind.Muxed, streams[0].Track);
        Assert.Equal("", streams[0].NameSuffix);
    }

    [Fact]
    public async Task A_bare_media_playlist_is_still_one_stream()
    {
        var server = new FakeHlsServer();
        server.Add("https://cdn.test/hls/only.m3u8", server.AddSegments("https://cdn.test/hls/", 2));

        var streams = await new StreamLoader(server).LoadAsync(new Uri("https://cdn.test/hls/only.m3u8"));

        Assert.Single(streams);
    }

    // ── DASH: multiple Periods ────────────────────────────────────────────

    [Fact]
    public void Periods_of_the_same_encode_are_stitched_end_to_end()
    {
        // Same Representation id and the same init segment: two stretches of
        // one encode, which genuinely concatenate.
        var streams = DashManifestLoader.Read(TwoPeriods(sameInit: true), Manifest);
        var video = streams[0];

        // Four segments from the first Period, four from the second.
        Assert.Equal(8, video.SegmentCount);
        Assert.EndsWith("p1-1.m4s", video.Segments[0].Url.AbsoluteUri);
        Assert.EndsWith("p2-1.m4s", video.Segments[4].Url.AbsoluteUri);
    }

    [Fact]
    public void Periods_that_do_not_line_up_are_refused_rather_than_truncated()
    {
        // Different init segments: ad insertion. Concatenating these gives a
        // file that is not playable, and taking only the first gives one that
        // is a fraction of the running time.
        var error = Assert.Throws<NotSupportedException>(
            () => DashManifestLoader.Read(TwoPeriods(sameInit: false), Manifest));

        Assert.Contains("Period", error.Message);
        Assert.Contains("广告", error.Message);
    }

    [Fact]
    public void A_single_period_manifest_is_unaffected()
    {
        var streams = DashManifestLoader.Read(TwoPeriods(sameInit: true, periods: 1), Manifest);

        Assert.Equal(4, streams[0].SegmentCount);
    }

    [Fact]
    public void A_representation_carries_the_period_it_came_from()
    {
        var parsed = Mpd.Parse(XDocument.Parse(TwoPeriods(sameInit: true)), Manifest);

        Assert.Equal(new[] { 0, 1 }, parsed.Select(r => r.PeriodIndex));
    }

    private static readonly Uri Manifest = new("https://cdn.test/dash/manifest.mpd");

    private const string MasterWithAudio = """
        #EXTM3U
        #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aac",NAME="English",DEFAULT=YES,URI="audio/en.m3u8"
        #EXT-X-STREAM-INF:BANDWIDTH=5000000,RESOLUTION=1920x1080,CODECS="avc1.640028",AUDIO="aac"
        1080/index.m3u8
        """;

    private static string TwoPeriods(bool sameInit, int periods = 2)
    {
        string second = periods < 2 ? "" : $"""
              <Period duration="PT16S">
                <AdaptationSet mimeType="video/mp4" codecs="avc1.640028,mp4a.40.2">
                  <Representation id="v0" bandwidth="800000" width="1920" height="1080">
                    <SegmentTemplate media="p2-$Number$.m4s" initialization="{(sameInit ? "init.mp4" : "ad-init.mp4")}" startNumber="1" duration="4" timescale="1"/>
                  </Representation>
                </AdaptationSet>
              </Period>
            """;

        return $"""
            <?xml version="1.0"?>
            <MPD type="static" mediaPresentationDuration="PT32S">
              <Period duration="PT16S">
                <AdaptationSet mimeType="video/mp4" codecs="avc1.640028,mp4a.40.2">
                  <Representation id="v0" bandwidth="800000" width="1920" height="1080">
                    <SegmentTemplate media="p1-$Number$.m4s" initialization="init.mp4" startNumber="1" duration="4" timescale="1"/>
                  </Representation>
                </AdaptationSet>
              </Period>
            {second}
            </MPD>
            """;
    }
}
