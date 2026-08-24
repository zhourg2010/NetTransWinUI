using NetTrans.Net;
using NetTrans.Tests.Fakes;
using Xunit;

namespace NetTrans.Tests;

/// <summary>批量下载: crawling a page for downloadable links.</summary>
public class PageCrawlerTests
{
    private const string Root = "https://docs.internal/handbook/";

    private static FakeWebsite Site() => new FakeWebsite()
        .Page(Root, """
            <a href="chapter-01.pdf">1</a>
            <a href="chapter-02.pdf">2</a>
            <a href="assets/bundle.zip">bundle</a>
            <a href="deeper/index.html">deeper</a>
            <a href="https://cdn.elsewhere.net/offsite.pdf">offsite</a>
            """)
        .Page(Root + "deeper/index.html", """
            <a href="chapter-03.pdf">3</a>
            <a href="cover.png">cover</a>
            <a href="../index.html">back</a>
            """);

    [Fact]
    public async Task Takes_the_files_off_the_first_page()
    {
        var found = await new PageCrawler(Site())
            .CrawlAsync(new Uri(Root), new CrawlOptions(Depth: 0), CancellationToken.None);

        Assert.Equal(
            new[] { "chapter-01.pdf", "chapter-02.pdf", "bundle.zip" },
            found.Select(link => link.Name));
    }

    [Fact]
    public async Task Follows_pages_one_level_deep()
    {
        var found = await new PageCrawler(Site())
            .CrawlAsync(new Uri(Root), new CrawlOptions(Depth: 1), CancellationToken.None);

        Assert.Contains(found, link => link.Name == "chapter-03.pdf");
        Assert.Contains(found, link => link.Name == "cover.png");
    }

    [Fact]
    public async Task Stays_on_the_site_when_asked()
    {
        var found = await new PageCrawler(Site())
            .CrawlAsync(new Uri(Root), new CrawlOptions(Depth: 1, SameSiteOnly: true), CancellationToken.None);

        Assert.DoesNotContain(found, link => link.Url.Host == "cdn.elsewhere.net");
    }

    [Fact]
    public async Task Leaves_the_site_when_allowed()
    {
        var found = await new PageCrawler(Site())
            .CrawlAsync(new Uri(Root), new CrawlOptions(Depth: 0, SameSiteOnly: false), CancellationToken.None);

        Assert.Contains(found, link => link.Url.Host == "cdn.elsewhere.net");
    }

    [Fact]
    public async Task Filters_by_extension()
    {
        var found = await new PageCrawler(Site()).CrawlAsync(
            new Uri(Root),
            new CrawlOptions(Depth: 1, Extensions: new[] { "pdf" }),
            CancellationToken.None);

        Assert.All(found, link => Assert.Equal("pdf", link.Extension));
        Assert.Equal(3, found.Count);
    }

    [Fact]
    public async Task Does_not_fetch_the_same_page_twice()
    {
        var site = Site();
        await new PageCrawler(site).CrawlAsync(new Uri(Root), new CrawlOptions(Depth: 3), CancellationToken.None);

        // deeper/index.html links back to the root; it must not loop.
        Assert.Equal(site.Fetched.Distinct().Count(), site.Fetched.Count);
    }

    [Fact]
    public async Task Stops_at_the_result_cap()
    {
        var found = await new PageCrawler(Site()).CrawlAsync(
            new Uri(Root),
            new CrawlOptions(Depth: 1, MaxResults: 2),
            CancellationToken.None);

        Assert.Equal(2, found.Count);
    }

    [Fact]
    public async Task Records_a_page_it_could_not_read_instead_of_throwing()
    {
        var site = Site().Broken(Root + "deeper/index.html");
        var crawler = new PageCrawler(site);

        var found = await crawler.CrawlAsync(new Uri(Root), new CrawlOptions(Depth: 1), CancellationToken.None);

        Assert.NotEmpty(found);
        Assert.Single(crawler.Failures);
        Assert.Contains("deeper/index.html", crawler.Failures[0]);
    }

    [Fact]
    public async Task An_unreadable_start_page_yields_nothing_and_is_reported()
    {
        var crawler = new PageCrawler(new FakeWebsite().Broken(Root));

        var found = await crawler.CrawlAsync(new Uri(Root), new CrawlOptions(), CancellationToken.None);

        Assert.Empty(found);
        Assert.Single(crawler.Failures);
    }
}

/// <summary>Sizing crawled links, for the sheet's size column and 最小文件.</summary>
public class LinkSizerTests
{
    [Fact]
    public async Task Asks_the_server_for_every_size()
    {
        var site = new FakeWebsite()
            .File("https://x.test/a.pdf", 4_200_000)
            .File("https://x.test/b.zip", 128_000_000);

        var sized = await LinkSizer.MeasureAsync(site, Links("https://x.test/a.pdf", "https://x.test/b.zip"));

        Assert.Equal(new long?[] { 4_200_000, 128_000_000 }, sized.Select(link => link.SizeBytes));
        Assert.Equal(2, site.Probed.Count);
    }

    [Fact]
    public async Task Keeps_the_original_order()
    {
        var site = new FakeWebsite().File("https://x.test/a.pdf", 1).File("https://x.test/b.pdf", 2);

        var sized = await LinkSizer.MeasureAsync(site, Links("https://x.test/a.pdf", "https://x.test/b.pdf"));

        Assert.Equal(new[] { "a.pdf", "b.pdf" }, sized.Select(link => link.Name));
    }

    [Fact]
    public async Task A_link_that_will_not_answer_keeps_an_unknown_size()
    {
        var site = new FakeWebsite().Broken("https://x.test/a.pdf");

        var link = Assert.Single(await LinkSizer.MeasureAsync(site, Links("https://x.test/a.pdf")));
        Assert.Null(link.SizeBytes);
    }

    [Fact]
    public async Task Measuring_nothing_is_not_an_error() =>
        Assert.Empty(await LinkSizer.MeasureAsync(new FakeWebsite(), Array.Empty<DiscoveredLink>()));

    [Fact]
    public void Filters_below_the_minimum_but_keeps_unknown_sizes()
    {
        var links = new[]
        {
            LinkExtractor.Describe(new Uri("https://x.test/small.pdf")) with { SizeBytes = 1000 },
            LinkExtractor.Describe(new Uri("https://x.test/big.pdf")) with { SizeBytes = 5_000_000 },
            LinkExtractor.Describe(new Uri("https://x.test/unknown.pdf")),
        };

        var kept = LinkSizer.AtLeast(links, 1024 * 1024);

        Assert.Equal(new[] { "big.pdf", "unknown.pdf" }, kept.Select(link => link.Name));
    }

    [Fact]
    public void No_minimum_keeps_everything()
    {
        var links = Links("https://x.test/a.pdf");
        Assert.Same(links, LinkSizer.AtLeast(links, 0));
    }

    [Theory]
    [InlineData("不限", 0L)]
    [InlineData("100 KB", 102400L)]
    [InlineData("1 MB", 1048576L)]
    [InlineData("10 MB", 10485760L)]
    public void Reads_the_minimum_size_labels(string label, long expected) =>
        Assert.Equal(expected, LinkSizer.ParseMinimum(label));

    private static IReadOnlyList<DiscoveredLink> Links(params string[] urls) =>
        urls.Select(url => LinkExtractor.Describe(new Uri(url))).ToList();
}
