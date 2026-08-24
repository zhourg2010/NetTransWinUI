using NetTrans.Net;
using Xunit;

namespace NetTrans.Tests;

/// <summary>The mark of the web 完成后扫描 leaves behind.</summary>
public class ZoneIdentifierTests
{
    [Fact]
    public void It_marks_the_file_as_coming_from_the_internet()
    {
        string stream = ZoneIdentifier.Build("https://example.test/downloads/setup.exe");

        Assert.StartsWith("[ZoneTransfer]\r\nZoneId=3\r\n", stream);
        Assert.Contains("HostUrl=https://example.test/downloads/setup.exe\r\n", stream);
        Assert.Contains("ReferrerUrl=https://example.test/\r\n", stream);
    }

    [Fact]
    public void Every_line_ends_the_way_an_ini_windows_reads_has_to()
    {
        string stream = ZoneIdentifier.Build("https://example.test/a.bin");

        Assert.EndsWith("\r\n", stream);
        Assert.DoesNotContain("\n\n", stream);
        Assert.All(stream.Split("\r\n", StringSplitOptions.RemoveEmptyEntries), line => Assert.DoesNotContain("\n", line));
    }

    [Theory]
    [InlineData("magnet:?xt=urn:btih:9f2c1a")]
    [InlineData("file:///C:/x/setup.exe")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public void A_source_that_is_not_a_web_url_still_gets_the_zone_but_no_urls(string? url)
    {
        string stream = ZoneIdentifier.Build(url);

        // The zone is the part that matters, and it is true regardless.
        Assert.Equal("[ZoneTransfer]\r\nZoneId=3\r\n", stream);
    }

    [Fact]
    public void The_stream_hangs_off_the_file_it_describes() =>
        Assert.Equal(@"D:\x\setup.exe:Zone.Identifier", ZoneIdentifier.StreamPath(@"D:\x\setup.exe"));
}
