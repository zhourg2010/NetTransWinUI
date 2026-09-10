using System.Net;
using NetTrans.Net;
using Xunit;

namespace NetTrans.Tests;

/// <summary>
/// 源地址失效了，同目录里找一下: which sibling counts as the same file in a
/// newer edition, and which only looks like it.
/// </summary>
public class ReplacementLinkTests
{
    private static readonly Uri Directory = new("https://releases.ubuntu.com/24.04/");

    [Fact]
    public void Finds_the_point_release_that_replaced_a_deleted_one()
    {
        var found = ReplacementLink.Pick(
            new Uri(Directory, "ubuntu-24.04.2-desktop-amd64.iso"),
            Links(
                "ubuntu-24.04.3-desktop-amd64.iso",
                "ubuntu-24.04.4-desktop-amd64.iso",
                "ubuntu-24.04.4-live-server-amd64.iso",
                "SHA256SUMS"));

        Assert.NotNull(found);
        Assert.Equal("ubuntu-24.04.4-desktop-amd64.iso", found!.Name);
    }

    /// <summary>
    /// The edition has to differ only in its digits. live-server is a different
    /// image, not a newer desktop, and offering it would be worse than saying
    /// nothing.
    /// </summary>
    [Fact]
    public void Will_not_offer_a_different_edition()
    {
        var found = ReplacementLink.Pick(
            new Uri(Directory, "ubuntu-24.04.2-desktop-amd64.iso"),
            Links("ubuntu-24.04.4-live-server-amd64.iso", "ubuntu-24.04.4-wsl-amd64.wsl"));

        Assert.Null(found);
    }

    [Fact]
    public void Will_not_offer_a_different_file_type()
    {
        var found = ReplacementLink.Pick(
            new Uri(Directory, "ubuntu-24.04.2-desktop-amd64.iso"),
            Links("ubuntu-24.04.4-desktop-amd64.iso.torrent", "ubuntu-24.04.4-desktop-amd64.iso.zsync"));

        Assert.Null(found);
    }

    /// <summary>An older neighbour is not a replacement.</summary>
    [Fact]
    public void Will_not_go_backwards()
    {
        var found = ReplacementLink.Pick(
            new Uri(Directory, "ubuntu-24.04.4-desktop-amd64.iso"),
            Links("ubuntu-24.04.1-desktop-amd64.iso", "ubuntu-24.04.3-desktop-amd64.iso"));

        Assert.Null(found);
    }

    /// <summary>Version fields are numbers, not text: 10 is after 9.</summary>
    [Fact]
    public void Orders_versions_as_numbers()
    {
        var found = ReplacementLink.Pick(
            new Uri(Directory, "app-1.2.3.zip"),
            Links("app-1.2.9.zip", "app-1.2.10.zip"));

        Assert.Equal("app-1.2.10.zip", found!.Name);
    }

    /// <summary>A name with no digits has no edition, so any "similar" name is a guess.</summary>
    [Fact]
    public void Says_nothing_about_a_name_with_no_version_in_it()
    {
        var found = ReplacementLink.Pick(
            new Uri(Directory, "installer.exe"),
            Links("installer-new.exe", "installer2.exe"));

        Assert.Null(found);
    }

    [Fact]
    public void Ignores_the_dead_link_itself()
    {
        var found = ReplacementLink.Pick(
            new Uri(Directory, "ubuntu-24.04.2-desktop-amd64.iso"),
            Links("ubuntu-24.04.2-desktop-amd64.iso"));

        Assert.Null(found);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, true)]
    [InlineData(HttpStatusCode.Gone, true)]
    [InlineData(HttpStatusCode.Forbidden, true)]     // what releases.ubuntu.com actually answers for a retired point release
    [InlineData(HttpStatusCode.Unauthorized, false)] // a login, not a missing file
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    public void Only_looks_when_the_status_means_not_here(HttpStatusCode status, bool expected) =>
        Assert.Equal(expected, ReplacementLink.WorthLooking(status));

    [Fact]
    public void Ignores_an_exception_that_carries_no_status()
    {
        Assert.False(ReplacementLink.WorthLooking(new HttpRequestException("connection reset")));
        Assert.False(ReplacementLink.WorthLooking(new IOException("disk full")));
    }

    /// <summary>
    /// End to end through the job: a 404 on the file, a directory that lists
    /// its successor, and the suggestion arriving in the message the row shows.
    /// This is the whole point of the feature -- the pure Pick above could be
    /// perfect and still never be reached.
    /// </summary>
    [Fact]
    public async Task The_suggestion_reaches_the_error_message()
    {
        var job = new NetTrans.Download.DownloadJob(
            new NetTrans.Models.DownloadItem
            {
                Id = 1,
                Name = "ubuntu-24.04.2-desktop-amd64.iso",
                Host = "releases.ubuntu.com",
                Kind = NetTrans.Models.FileKind.Disc,
                Size = 0,
                Category = "soft",
                Url = new Uri(Directory, "ubuntu-24.04.2-desktop-amd64.iso").AbsoluteUri,
                SavePath = "/downloads",
            },
            new RetiredReleaseServer(),
            new Fakes.MemoryFileSinkFactory(),
            new Fakes.ManualClock());

        var outcome = await job.RunAsync(CancellationToken.None);

        Assert.Equal(NetTrans.Download.JobOutcome.Failed, outcome);
        Assert.Equal(
            "服务器返回 404；同目录里有 ubuntu-24.04.4-desktop-amd64.iso，可能是它的新版本",
            job.Item.ErrorMessage);
    }

    /// <summary>
    /// A publisher that has deleted the point release but still indexes the
    /// directory -- which is exactly what releases.ubuntu.com does.
    /// </summary>
    private sealed class RetiredReleaseServer : IHttpTransport
    {
        private const string Listing = """
            <html><body>
            <a href="SHA256SUMS">SHA256SUMS</a>
            <a href="ubuntu-24.04.4-desktop-amd64.iso">ubuntu-24.04.4-desktop-amd64.iso</a>
            <a href="ubuntu-24.04.4-desktop-amd64.iso.torrent">torrent</a>
            <a href="ubuntu-24.04.4-live-server-amd64.iso">server</a>
            </body></html>
            """;

        public Task<RemoteFileInfo> ProbeAsync(Uri url, CancellationToken cancellationToken) =>
            throw new HttpRequestException("gone", null, HttpStatusCode.NotFound);

        public Task<Stream> OpenAsync(Uri url, long from, long? to, CancellationToken cancellationToken)
        {
            if (url.AbsolutePath.EndsWith('/'))
            {
                return Task.FromResult<Stream>(
                    new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Listing)));
            }

            throw new HttpRequestException("gone", null, HttpStatusCode.NotFound);
        }
    }

    private static IEnumerable<DiscoveredLink> Links(params string[] names) =>
        names.Select(name => LinkExtractor.Describe(new Uri(Directory, name)));
}
