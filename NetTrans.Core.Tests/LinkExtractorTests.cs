using NetTrans.Net;
using Xunit;

namespace NetTrans.Tests;

/// <summary>What 批量下载 finds on a page.</summary>
public class LinkExtractorTests
{
    private static readonly Uri Page = new("https://docs.internal/handbook/index.html");

    [Fact]
    public void Finds_hrefs_and_srcs_and_makes_them_absolute()
    {
        const string html = """
            <a href="chapter-01.pdf">one</a>
            <a href='/assets/bundle.zip'>two</a>
            <img src="cover.png">
            <a href=notes.pdf>unquoted</a>
            """;

        var links = LinkExtractor.Extract(html, Page);

        Assert.Equal(
            new[]
            {
                "https://docs.internal/handbook/chapter-01.pdf",
                "https://docs.internal/assets/bundle.zip",
                "https://docs.internal/handbook/cover.png",
                "https://docs.internal/handbook/notes.pdf",
            },
            links.Select(link => link.Url.AbsoluteUri));
    }

    [Fact]
    public void Skips_anchors_scripts_and_data_urls()
    {
        const string html = """
            <a href="#top">top</a>
            <a href="javascript:void(0)">js</a>
            <a href="mailto:a@b.c">mail</a>
            <img src="data:image/png;base64,AAAA">
            <a href="real.pdf">real</a>
            """;

        var link = Assert.Single(LinkExtractor.Extract(html, Page));
        Assert.Equal("real.pdf", link.Name);
    }

    [Fact]
    public void Deduplicates_the_same_target()
    {
        const string html = """<a href="a.pdf">1</a><a href="a.pdf">2</a><a href="a.pdf#page2">3</a>""";
        Assert.Single(LinkExtractor.Extract(html, Page));
    }

    [Fact]
    public void Reads_the_name_and_extension_off_the_path()
    {
        var link = LinkExtractor.Describe(new Uri("https://x.test/a/b/My%20Report.PDF?v=2"));

        Assert.Equal("My Report.PDF", link.Name);
        Assert.Equal("pdf", link.Extension);
        Assert.False(link.IsPage);
    }

    [Theory]
    [InlineData("https://x.test/page", true)]
    [InlineData("https://x.test/page.html", true)]
    [InlineData("https://x.test/page.php", true)]
    [InlineData("https://x.test/file.pdf", false)]
    [InlineData("https://x.test/file.zip", false)]
    public void Tells_pages_from_files(string url, bool isPage) =>
        Assert.Equal(isPage, LinkExtractor.Describe(new Uri(url)).IsPage);

    [Theory]
    [InlineData("pdf; zip; png", new[] { "pdf", "zip", "png" })]
    [InlineData("pdf,zip", new[] { "pdf", "zip" })]
    [InlineData(".pdf .zip", new[] { "pdf", "zip" })]
    [InlineData("PDF; pdf", new[] { "pdf" })]
    [InlineData("", new string[0])]
    [InlineData(null, new string[0])]
    public void Reads_the_extension_filter(string? filter, string[] expected) =>
        Assert.Equal(expected, LinkExtractor.ParseExtensions(filter));
}
