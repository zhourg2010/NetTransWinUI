using System.Xml.Linq;
using NetTrans.Media;
using Xunit;

namespace NetTrans.Tests;

/// <summary>
/// The DASH manifest reader. Segment URLs are templated rather than listed, so
/// the substitution rules are where a misread manifest turns into a file full
/// of 404s.
/// </summary>
public class MpdTests
{
    private static readonly Uri Manifest = new("https://cdn.test/dash/manifest.mpd");

    [Theory]
    [InlineData("$Number$", 7, "7")]
    [InlineData("$Number%05d$", 7, "00007")]
    [InlineData("$Number%02d$", 1234, "1234")]      // wider than the format: not truncated
    [InlineData("seg-$Number$.m4s", 3, "seg-3.m4s")]
    public void Number_is_substituted_with_its_padding(string template, long number, string expected) =>
        Assert.Equal(expected, Mpd.Substitute(template, "v0", 800000, number, time: null));

    [Fact]
    public void The_other_identifiers_are_substituted_too()
    {
        Assert.Equal("v0/800000/12.m4s", Mpd.Substitute(
            "$RepresentationID$/$Bandwidth$/$Time$.m4s", "v0", 800000, number: null, time: 12));
    }

    [Fact]
    public void A_doubled_dollar_is_a_literal_one() =>
        Assert.Equal("a$b", Mpd.Substitute("a$$b", "v0", 0, 1, 1));

    [Fact]
    public void An_identifier_with_no_value_here_is_left_alone()
    {
        // Blanking it would give a URL that looks plausible and fetches the
        // wrong thing; leaving it gives one that fails where it can be seen.
        Assert.Equal("seg-$Time$.m4s", Mpd.Substitute("seg-$Time$.m4s", "v0", 0, number: 1, time: null));
        Assert.Equal("$Unknown$", Mpd.Substitute("$Unknown$", "v0", 0, 1, 1));
    }

    [Theory]
    [InlineData("PT10S", 10)]
    [InlineData("PT1M30S", 90)]
    [InlineData("PT1H", 3600)]
    [InlineData("PT0H4M13.13S", 253.13)]
    [InlineData("P1DT1S", 86401)]
    public void Iso_durations_are_read(string text, double seconds) =>
        Assert.Equal(seconds, Mpd.Duration(text)!.Value.TotalSeconds, 2);

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("10s")]
    [InlineData("bogus")]
    public void An_unreadable_duration_is_no_duration(string? text) => Assert.Null(Mpd.Duration(text));

    [Fact]
    public void A_byte_range_becomes_an_offset_and_a_length()
    {
        // DASH writes "first-last" inclusive, not "offset+length".
        Assert.Equal(((long?)0, (long?)1000), Mpd.Range("0-999"));
        Assert.Equal(((long?)1000, (long?)1), Mpd.Range("1000-1000"));
    }

    [Theory]
    [InlineData("999-0")]
    [InlineData("0")]
    [InlineData("a-b")]
    [InlineData(null)]
    public void An_unreadable_range_is_no_range(string? text)
    {
        var (offset, length) = Mpd.Range(text);

        Assert.Null(offset);
        Assert.Null(length);
    }

    [Fact]
    public void A_template_with_a_duration_is_divided_into_the_period()
    {
        var representation = Parse(Template("PT16S", @"duration=""4"" timescale=""1""")).Single();

        Assert.Equal(4, representation.Segments.Count);
        Assert.Equal(new Uri("https://cdn.test/dash/seg-1.m4s"), representation.Segments[0].Url);
        Assert.Equal(new Uri("https://cdn.test/dash/seg-4.m4s"), representation.Segments[3].Url);
        Assert.Equal(new Uri("https://cdn.test/dash/init.mp4"), representation.InitSegment);
    }

    [Fact]
    public void A_partial_last_segment_still_counts()
    {
        // 17 seconds of 4-second segments is five, not four: the last one is
        // short, and dropping it would truncate the file.
        Assert.Equal(5, Parse(Template("PT17S", @"duration=""4"" timescale=""1""")).Single().Segments.Count);
    }

    [Fact]
    public void A_timescale_is_honoured()
    {
        var representation = Parse(Template("PT10S", @"duration=""90000"" timescale=""90000""")).Single();
        Assert.Equal(10, representation.Segments.Count);
    }

    [Fact]
    public void An_explicit_timeline_is_used_in_preference_to_a_duration()
    {
        string timeline = @"
                    <SegmentTemplate media=""seg-$Number$-$Time$.m4s"" initialization=""init.mp4"" startNumber=""1"" timescale=""1"">
                      <SegmentTimeline>
                        <S t=""0"" d=""4"" r=""2""/>
                        <S d=""3""/>
                      </SegmentTimeline>
                    </SegmentTemplate>";

        var segments = Parse(Wrap("PT15S", timeline)).Single().Segments;

        // r=2 means two *more* after the first, so three of four seconds, then
        // one of three.
        Assert.Equal(4, segments.Count);
        Assert.Equal(new Uri("https://cdn.test/dash/seg-1-0.m4s"), segments[0].Url);
        Assert.Equal(new Uri("https://cdn.test/dash/seg-2-4.m4s"), segments[1].Url);
        Assert.Equal(new Uri("https://cdn.test/dash/seg-3-8.m4s"), segments[2].Url);
        Assert.Equal(new Uri("https://cdn.test/dash/seg-4-12.m4s"), segments[3].Url);
        Assert.Equal(3, segments[3].Duration, 3);
    }

    [Fact]
    public void A_segment_list_is_read_with_its_ranges()
    {
        string list = @"
                    <SegmentList>
                      <Initialization sourceURL=""init.mp4""/>
                      <SegmentURL media=""all.m4s"" mediaRange=""0-99""/>
                      <SegmentURL media=""all.m4s"" mediaRange=""100-299""/>
                    </SegmentList>";

        var representation = Parse(Wrap("PT8S", list)).Single();

        Assert.Equal(new Uri("https://cdn.test/dash/init.mp4"), representation.InitSegment);
        Assert.Equal(((long?)0, (long?)100), (representation.Segments[0].RangeOffset, representation.Segments[0].RangeLength));
        Assert.Equal(((long?)100, (long?)200), (representation.Segments[1].RangeOffset, representation.Segments[1].RangeLength));
    }

    [Fact]
    public void A_base_url_chain_is_followed_down_the_tree()
    {
        string text = @"<?xml version=""1.0""?>
            <MPD type=""static"" mediaPresentationDuration=""PT8S"">
              <BaseURL>https://other.test/v/</BaseURL>
              <Period>
                <BaseURL>p1/</BaseURL>
                <AdaptationSet mimeType=""video/mp4"">
                  <BaseURL>hi/</BaseURL>
                  <Representation id=""v0"" bandwidth=""1"" width=""1920"" height=""1080"">
                    <SegmentTemplate media=""seg-$Number$.m4s"" startNumber=""1"" duration=""4"" timescale=""1""/>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>";

        var segments = Parse(text).Single().Segments;
        Assert.Equal(new Uri("https://other.test/v/p1/hi/seg-1.m4s"), segments[0].Url);
    }

    [Fact]
    public void A_namespaced_manifest_reads_the_same()
    {
        // Packagers differ on the prefix, and matching on it rather than on the
        // local name is how a reader ends up working with only one of them.
        string text = Template("PT8S", @"duration=""4"" timescale=""1""")
            .Replace("<MPD ", @"<MPD xmlns=""urn:mpeg:dash:schema:mpd:2011"" ");

        Assert.Equal(2, Parse(text).Single().Segments.Count);
    }

    [Fact]
    public void A_video_representation_whose_codecs_include_audio_is_muxed()
    {
        var muxed = Parse(Template("PT8S", @"duration=""4"" timescale=""1""", codecs: "avc1.640028,mp4a.40.2")).Single();
        Assert.Equal(TrackKind.Muxed, muxed.Track);

        var video = Parse(Template("PT8S", @"duration=""4"" timescale=""1""", codecs: "avc1.640028")).Single();
        Assert.Equal(TrackKind.Video, video.Track);
    }

    [Fact]
    public void Representations_come_back_best_first_with_audio_last()
    {
        string text = @"<?xml version=""1.0""?>
            <MPD type=""static"" mediaPresentationDuration=""PT8S"">
              <Period>
                <AdaptationSet mimeType=""audio/mp4"" codecs=""mp4a.40.2"">
                  <Representation id=""a0"" bandwidth=""128000"">
                    <SegmentTemplate media=""a-$Number$.m4s"" startNumber=""1"" duration=""4"" timescale=""1""/>
                  </Representation>
                </AdaptationSet>
                <AdaptationSet mimeType=""video/mp4"" codecs=""avc1.4d401f"">
                  <Representation id=""v1"" bandwidth=""1000000"" width=""854"" height=""480"">
                    <SegmentTemplate media=""s-$Number$.m4s"" startNumber=""1"" duration=""4"" timescale=""1""/>
                  </Representation>
                  <Representation id=""v0"" bandwidth=""5000000"" width=""1920"" height=""1080"">
                    <SegmentTemplate media=""h-$Number$.m4s"" startNumber=""1"" duration=""4"" timescale=""1""/>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>";

        Assert.Equal(new[] { "v0", "v1", "a0" }, Parse(text).Select(r => r.Id));
    }

    [Fact]
    public void A_live_manifest_is_refused_rather_than_downloaded()
    {
        string text = Template("PT8S", @"duration=""4"" timescale=""1""").Replace(@"type=""static""", @"type=""dynamic""");

        var error = Assert.Throws<NotSupportedException>(() => DashManifestLoader.Read(text, Manifest));
        Assert.Contains("直播", error.Message);
    }

    [Fact]
    public void Something_that_is_not_a_manifest_says_so()
    {
        var error = Assert.Throws<NotSupportedException>(() => DashManifestLoader.Read("<html/>", Manifest));
        Assert.Contains("DASH", error.Message);
    }

    [Fact]
    public void A_muxed_manifest_comes_back_as_one_stream()
    {
        var streams = DashManifestLoader.Read(
            Template("PT8S", @"duration=""4"" timescale=""1""", codecs: "avc1.640028,mp4a.40.2"),
            Manifest);

        Assert.Single(streams);
        Assert.Equal(TrackKind.Muxed, streams[0].Track);
        Assert.Equal("", streams[0].NameSuffix);
    }

    [Fact]
    public void A_split_manifest_comes_back_as_video_then_audio()
    {
        string text = @"<?xml version=""1.0""?>
            <MPD type=""static"" mediaPresentationDuration=""PT8S"">
              <Period>
                <AdaptationSet mimeType=""video/mp4"" codecs=""avc1.640028"">
                  <Representation id=""v0"" bandwidth=""5000000"" width=""1920"" height=""1080"">
                    <SegmentTemplate media=""v-$Number$.m4s"" startNumber=""1"" duration=""4"" timescale=""1""/>
                  </Representation>
                </AdaptationSet>
                <AdaptationSet mimeType=""audio/mp4"" codecs=""mp4a.40.2"">
                  <Representation id=""a0"" bandwidth=""128000"">
                    <SegmentTemplate media=""a-$Number$.m4s"" startNumber=""1"" duration=""4"" timescale=""1""/>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>";

        var streams = DashManifestLoader.Read(text, Manifest);

        Assert.Equal(2, streams.Count);
        Assert.Equal(TrackKind.Video, streams[0].Track);
        Assert.Equal(TrackKind.Audio, streams[1].Track);
        Assert.Equal("-视频", streams[0].NameSuffix);
        Assert.Equal("-音频", streams[1].NameSuffix);
    }

    private static IReadOnlyList<DashRepresentation> Parse(string text) =>
        Mpd.Parse(XDocument.Parse(text), Manifest);

    private static string Template(string duration, string attributes, string codecs = "avc1.640028") =>
        Wrap(duration, $@"<SegmentTemplate media=""seg-$Number$.m4s"" initialization=""init.mp4"" startNumber=""1"" {attributes}/>", codecs);

    private static string Wrap(string duration, string segmentInfo, string codecs = "avc1.640028") => $@"<?xml version=""1.0""?>
        <MPD type=""static"" mediaPresentationDuration=""{duration}"">
          <Period>
            <AdaptationSet mimeType=""video/mp4"" codecs=""{codecs}"">
              <Representation id=""v0"" bandwidth=""800000"" width=""1920"" height=""1080"">
                {segmentInfo}
              </Representation>
            </AdaptationSet>
          </Period>
        </MPD>";
}
