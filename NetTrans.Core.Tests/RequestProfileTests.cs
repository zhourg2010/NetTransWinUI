using NetTrans.Net;
using Xunit;

namespace NetTrans.Tests;

/// <summary>
/// Referer, Cookie and 账号密码 -- the three reasons a link that works in the
/// browser used to 403 here.
/// </summary>
public class RequestProfileTests
{
    [Fact]
    public void A_profile_is_found_by_host_rather_than_by_exact_url()
    {
        var profiles = new RequestProfiles();
        profiles.Set(new Uri("https://site.test/page"), new RequestProfile(Cookie: "session=abc"));

        // By the time the bytes move, the URL is a different path -- often a
        // signed one on the same host.
        Assert.Equal("session=abc", profiles.For(new Uri("https://site.test/files/a.mp4?sig=1"))?.Cookie);
    }

    [Fact]
    public void Another_site_gets_nothing()
    {
        var profiles = new RequestProfiles();
        profiles.Set(new Uri("https://site.test/page"), new RequestProfile(Cookie: "session=abc"));

        Assert.Null(profiles.For(new Uri("https://cdn.other.test/a.mp4")));
    }

    [Fact]
    public void A_remembered_page_becomes_the_referer_without_losing_the_cookie()
    {
        var profiles = new RequestProfiles();
        profiles.Set(new Uri("https://site.test/"), new RequestProfile(Cookie: "session=abc"));
        profiles.SetReferer(new Uri("https://site.test/video.m3u8"), new Uri("https://site.test/watch/1"));

        var profile = profiles.For(new Uri("https://site.test/video.m3u8"));

        Assert.Equal("https://site.test/watch/1", profile?.Referer);
        Assert.Equal("session=abc", profile?.Cookie);
    }

    [Theory]
    [InlineData("https://user:pass@site.test/a.iso", "user", "pass")]
    [InlineData("https://user@site.test/a.iso", "user", "")]
    [InlineData("https://us%40er:p%3Ass@site.test/a.iso", "us@er", "p:ss")]
    public void Credentials_in_the_url_are_read_rather_than_dropped(string url, string user, string password)
    {
        var profile = RequestProfile.FromUserInfo(new Uri(url));

        Assert.Equal(user, profile?.User);
        Assert.Equal(password, profile?.Password);
    }

    [Fact]
    public void A_url_with_credentials_still_shows_without_them()
    {
        var clean = RequestProfile.WithoutUserInfo(new Uri("https://user:pass@site.test/a.iso"));

        Assert.Equal("https://site.test/a.iso", clean.AbsoluteUri);
    }

    [Fact]
    public void The_urls_own_credentials_win_over_the_stored_ones()
    {
        var profiles = new RequestProfiles();
        profiles.Set(new Uri("https://site.test/"), new RequestProfile(Cookie: "c", User: "old", Password: "old"));

        var profile = profiles.For(new Uri("https://fresh:pass@site.test/a.iso"));

        Assert.Equal("fresh", profile?.User);
        Assert.Equal("c", profile?.Cookie);
    }

    [Fact]
    public void Basic_auth_is_encoded_the_way_the_header_wants_it()
    {
        // "Aladdin:open sesame" from the RFC's own example.
        var profile = new RequestProfile(User: "Aladdin", Password: "open sesame");

        Assert.Equal("Basic QWxhZGRpbjpvcGVuIHNlc2FtZQ==", profile.BasicHeader());
    }

    [Fact]
    public void Nothing_to_authenticate_with_means_no_header_at_all() =>
        Assert.Null(new RequestProfile(Cookie: "c").BasicHeader());

    [Theory]
    [InlineData("不使用代理")]
    public void Direct_means_direct(string label)
    {
        var proxy = new LiveProxy();
        proxy.Set(label);

        Assert.True(proxy.IsDirect);
        Assert.True(proxy.IsBypassed(new Uri("https://site.test/")));
        Assert.Null(proxy.GetProxy(new Uri("https://site.test/")));
    }

    [Theory]
    [InlineData("127.0.0.1:8888", "http://127.0.0.1:8888/")]
    [InlineData("http://proxy.test:3128", "http://proxy.test:3128/")]
    public void A_named_proxy_is_used_for_every_host(string label, string expected)
    {
        var proxy = new LiveProxy();
        proxy.Set(label);

        Assert.False(proxy.IsDirect);
        Assert.Equal(expected, proxy.GetProxy(new Uri("https://site.test/a.iso"))?.AbsoluteUri);
    }

    [Fact]
    public void The_dropdown_can_be_changed_while_the_app_runs()
    {
        var proxy = new LiveProxy();

        proxy.Set("127.0.0.1:8888");
        Assert.False(proxy.IsDirect);

        // The handler holds this one instance for its whole life, so the
        // setting has to be readable through it rather than baked in.
        proxy.Set("不使用代理");
        Assert.True(proxy.IsDirect);

        proxy.Set("系统代理");
        Assert.False(proxy.IsDirect);
        Assert.Equal("系统代理", proxy.Label);
    }
}
