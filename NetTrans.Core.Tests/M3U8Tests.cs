using NetTrans.Media;
using Xunit;

namespace NetTrans.Tests;

/// <summary>
/// The M3U8 reader. A segment fetched from a misread URI is a corrupt file
/// rather than an error, so these are exact about resolution and ordering.
/// </summary>
public class M3U8Tests
{
    private static readonly Uri Playlist = new("https://cdn.test/hls/720/index.m3u8");

    [Fact]
    public void A_master_playlist_is_told_apart_from_a_media_one()
    {
        Assert.True(M3U8.IsMaster(Master));
        Assert.False(M3U8.IsMaster(Media));
    }

    [Fact]
    public void Variants_come_back_best_first()
    {
        var variants = M3U8.ParseMaster(Master, new Uri("https://cdn.test/hls/master.m3u8"));

        Assert.Equal(new[] { "1080p", "720p", "480p" }, variants.Select(v => v.Quality));
        Assert.Equal(5_000_000, variants[0].Bandwidth);
        Assert.Equal(1920, variants[0].Width);
        Assert.Equal(1080, variants[0].Height);
    }

    [Fact]
    public void A_codecs_attribute_survives_its_own_commas()
    {
        var variants = M3U8.ParseMaster(Master, new Uri("https://cdn.test/hls/master.m3u8"));

        // "avc1.640028,mp4a.40.2" would be torn in half by a plain comma split,
        // taking BANDWIDTH or RESOLUTION with it.
        Assert.Equal("avc1.640028,mp4a.40.2", variants[0].Codecs);
    }

    [Fact]
    public void Variant_uris_resolve_against_the_playlist_they_came_from()
    {
        var variants = M3U8.ParseMaster(Master, new Uri("https://cdn.test/hls/master.m3u8"));

        Assert.Equal(new Uri("https://cdn.test/hls/1080/index.m3u8"), variants[0].Url);
        Assert.Equal(new Uri("https://other.test/480.m3u8"), variants[2].Url);
    }

    [Fact]
    public void A_variant_with_no_resolution_falls_back_to_its_bitrate()
    {
        const string text = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=800000
            audio-only.m3u8
            """;

        var variants = M3U8.ParseMaster(text, Playlist);
        Assert.Equal("800 kbps", variants.Single().Quality);
    }

    [Fact]
    public void Segments_come_back_in_play_order_with_their_durations()
    {
        var media = M3U8.ParseMedia(Media, Playlist);

        Assert.Equal(3, media.Segments.Count);
        Assert.Equal(new Uri("https://cdn.test/hls/720/seg-1.ts"), media.Segments[0].Url);
        Assert.Equal(new Uri("https://cdn.test/hls/720/seg-3.ts"), media.Segments[2].Url);
        Assert.Equal(9.009, media.Segments[0].Duration, 3);
        Assert.Equal(9.009 + 9.009 + 3.003, media.TotalDuration, 3);
    }

    [Fact]
    public void A_finished_playlist_is_not_live_and_a_growing_one_is()
    {
        Assert.False(M3U8.ParseMedia(Media, Playlist).IsLive);
        Assert.True(M3U8.ParseMedia(Media.Replace("#EXT-X-ENDLIST", ""), Playlist).IsLive);
    }

    [Fact]
    public void Sequence_numbers_start_where_the_playlist_says()
    {
        var media = M3U8.ParseMedia(Media, Playlist);

        // #EXT-X-MEDIA-SEQUENCE:10, so the first segment is 10 -- which is what
        // an implicit AES IV is derived from.
        Assert.Equal(new long[] { 10, 11, 12 }, media.Segments.Select(s => s.SequenceNumber));
    }

    [Fact]
    public void An_fmp4_stream_reports_its_init_segment()
    {
        const string text = """
            #EXTM3U
            #EXT-X-MAP:URI="init.mp4"
            #EXTINF:4.0,
            seg-1.m4s
            #EXT-X-ENDLIST
            """;

        var media = M3U8.ParseMedia(text, Playlist);
        Assert.Equal(new Uri("https://cdn.test/hls/720/init.mp4"), media.InitSegment);
    }

    [Fact]
    public void A_transport_stream_has_no_init_segment() =>
        Assert.Null(M3U8.ParseMedia(Media, Playlist).InitSegment);

    [Fact]
    public void An_aes_key_is_read_with_its_uri_and_iv()
    {
        const string text = """
            #EXTM3U
            #EXT-X-KEY:METHOD=AES-128,URI="key.bin",IV=0x00000000000000000000000000000001
            #EXTINF:4.0,
            seg-1.ts
            #EXT-X-ENDLIST
            """;

        var key = M3U8.ParseMedia(text, Playlist).Segments.Single().Key!;

        Assert.Equal(HlsEncryption.Aes128, key.Method);
        Assert.Equal(new Uri("https://cdn.test/hls/720/key.bin"), key.KeyUri);
        Assert.Equal(16, key.Iv!.Length);
        Assert.Equal(1, key.Iv[15]);
    }

    [Fact]
    public void A_key_with_no_iv_leaves_it_to_the_sequence_number()
    {
        const string text = """
            #EXTM3U
            #EXT-X-KEY:METHOD=AES-128,URI="key.bin"
            #EXTINF:4.0,
            seg-1.ts
            #EXT-X-ENDLIST
            """;

        Assert.Null(M3U8.ParseMedia(text, Playlist).Segments.Single().Key!.Iv);
    }

    [Fact]
    public void Method_none_ends_an_encrypted_run_rather_than_describing_one()
    {
        const string text = """
            #EXTM3U
            #EXT-X-KEY:METHOD=AES-128,URI="key.bin"
            #EXTINF:4.0,
            secret.ts
            #EXT-X-KEY:METHOD=NONE
            #EXTINF:4.0,
            clear.ts
            #EXT-X-ENDLIST
            """;

        var segments = M3U8.ParseMedia(text, Playlist).Segments;

        Assert.Equal(HlsEncryption.Aes128, segments[0].Key!.Method);
        Assert.Null(segments[1].Key);
    }

    [Fact]
    public void An_unknown_encryption_method_is_treated_as_one_we_cannot_do()
    {
        const string text = """
            #EXTM3U
            #EXT-X-KEY:METHOD=SAMPLE-AES,URI="key.bin"
            #EXTINF:4.0,
            seg-1.ts
            #EXT-X-ENDLIST
            """;

        // Refused rather than fetched and mangled.
        Assert.Equal(HlsEncryption.SampleAes, M3U8.ParseMedia(text, Playlist).Segments.Single().Key!.Method);
    }

    [Fact]
    public void Byte_ranges_are_read_and_an_implicit_offset_continues_the_last_one()
    {
        const string text = """
            #EXTM3U
            #EXT-X-BYTERANGE:1000@0
            #EXTINF:4.0,
            all.ts
            #EXT-X-BYTERANGE:2000
            #EXTINF:4.0,
            all.ts
            #EXT-X-BYTERANGE:500@9000
            #EXTINF:4.0,
            all.ts
            #EXT-X-ENDLIST
            """;

        var segments = M3U8.ParseMedia(text, Playlist).Segments;

        Assert.Equal((0L, 1000L), (segments[0].ByteRangeOffset, segments[0].ByteRangeLength));
        Assert.Equal((1000L, 2000L), (segments[1].ByteRangeOffset, segments[1].ByteRangeLength));
        Assert.Equal((9000L, 500L), (segments[2].ByteRangeOffset, segments[2].ByteRangeLength));
    }

    [Fact]
    public void A_segment_that_is_not_a_web_url_is_dropped_rather_than_fetched()
    {
        const string text = """
            #EXTM3U
            #EXTINF:4.0,
            file:///C:/local.ts
            #EXTINF:4.0,
            good.ts
            #EXT-X-ENDLIST
            """;

        Assert.Equal(new Uri("https://cdn.test/hls/720/good.ts"), M3U8.ParseMedia(text, Playlist).Segments.Single().Url);
    }

    [Fact]
    public void Windows_line_endings_and_stray_blank_lines_do_not_matter()
    {
        string text = Media.Replace("\n", "\r\n\r\n");

        Assert.Equal(3, M3U8.ParseMedia(text, Playlist).Segments.Count);
    }

    [Fact]
    public void A_media_playlist_asked_for_variants_is_empty_rather_than_a_throw() =>
        Assert.Empty(M3U8.ParseMaster(Media, Playlist));

    private const string Master = """
        #EXTM3U
        #EXT-X-STREAM-INF:BANDWIDTH=5000000,RESOLUTION=1920x1080,CODECS="avc1.640028,mp4a.40.2"
        1080/index.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=2500000,RESOLUTION=1280x720,CODECS="avc1.4d401f"
        720/index.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=1000000,RESOLUTION=854x480
        https://other.test/480.m3u8
        """;

    private const string Media = """
        #EXTM3U
        #EXT-X-VERSION:3
        #EXT-X-TARGETDURATION:10
        #EXT-X-MEDIA-SEQUENCE:10
        #EXTINF:9.009,
        seg-1.ts
        #EXTINF:9.009,title with, a comma
        seg-2.ts
        #EXTINF:3.003,
        seg-3.ts
        #EXT-X-ENDLIST
        """;
}
