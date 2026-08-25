using System.Text;
using NetTrans.Net;
using Xunit;

namespace NetTrans.Tests;

/// <summary>
/// thunder:// and its cousins, which are base64 rather than protocols.
/// </summary>
public class PrivateLinkTests
{
    private static string Thunder(string url) =>
        "thunder://" + Convert.ToBase64String(Encoding.UTF8.GetBytes("AA" + url + "ZZ"));

    [Fact]
    public void A_thunder_link_is_the_address_inside_it()
    {
        Assert.Equal(
            "https://mirror.test/ubuntu.iso",
            PrivateLinks.Unwrap(Thunder("https://mirror.test/ubuntu.iso")));
    }

    [Fact]
    public void A_flashget_link_unwraps_too()
    {
        string payload = Convert.ToBase64String(
            Encoding.UTF8.GetBytes("[FLASHGET]https://files.test/a.zip[FLASHGET]"));

        Assert.Equal("https://files.test/a.zip", PrivateLinks.Unwrap("flashget://" + payload + "&fs=0"));
    }

    [Fact]
    public void A_wrapped_magnet_comes_back_as_a_magnet()
    {
        const string magnet = "magnet:?xt=urn:btih:9f2c1a3b4d5e6f708192a3b4c5d6e7f809112233";

        Assert.Equal(magnet, PrivateLinks.Unwrap(Thunder(magnet)));
    }

    [Fact]
    public void Padding_a_site_stripped_is_put_back()
    {
        string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("AAhttps://a.test/bZZ")).TrimEnd('=');

        Assert.Equal("https://a.test/b", PrivateLinks.Unwrap("thunder://" + payload));
    }

    [Theory]
    [InlineData("https://plain.test/file.iso")]
    [InlineData("magnet:?xt=urn:btih:9f2c1a")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_that_is_not_wrapped_is_left_alone(string? text) =>
        Assert.Equal((text ?? "").Trim(), PrivateLinks.Unwrap(text));

    [Theory]
    [InlineData("thunder://not base64 at all")]
    [InlineData("thunder://QUFub3RhdXJsWlo=")]
    public void Something_that_does_not_decode_to_an_address_comes_back_untouched(string text) =>
        Assert.Equal(text, PrivateLinks.Unwrap(text));

    [Fact]
    public void The_wrapped_schemes_are_recognised_by_name()
    {
        Assert.True(PrivateLinks.IsWrapped("Thunder://abc"));
        Assert.True(PrivateLinks.IsWrapped("flashget://abc"));
        Assert.True(PrivateLinks.IsWrapped("qqdl://abc"));
        Assert.False(PrivateLinks.IsWrapped("https://site.test/a"));
    }
}
