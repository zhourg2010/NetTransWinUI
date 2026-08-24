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
