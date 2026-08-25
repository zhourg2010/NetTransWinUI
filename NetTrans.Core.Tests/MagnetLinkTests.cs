using System.Text;
using NetTrans.Torrent;
using Xunit;

namespace NetTrans.Tests;

/// <summary>Magnet links. A link names a torrent; it is not one.</summary>
public class MagnetLinkTests
{
    private const string Hex = "9f2c1a3b4d5e6f708192a3b4c5d6e7f809112233";

    [Fact]
    public void A_plain_link_gives_up_its_hash()
    {
        var magnet = MagnetLink.Parse($"magnet:?xt=urn:btih:{Hex}")!;

        Assert.Equal(20, magnet.InfoHash.Length);
        Assert.Equal(Hex, magnet.InfoHashHex);
    }

    [Fact]
    public void The_hash_may_be_written_in_base32()
    {
        // Both spellings are in wide use and mean the same twenty bytes. This
        // is RFC 4648 base32 of the ASCII "abcdefghijklmnopqrst".
        const string base32 = "MFRGGZDFMZTWQ2LKNNWG23TPOBYXE43U";

        var magnet = MagnetLink.Parse($"magnet:?xt=urn:btih:{base32}")!;

        Assert.Equal(20, magnet.InfoHash.Length);
        Assert.Equal(Encoding.ASCII.GetBytes("abcdefghijklmnopqrst"), magnet.InfoHash);
    }

    [Fact]
    public void Case_does_not_matter_in_the_hash_or_the_scheme()
    {
        var upper = MagnetLink.Parse($"MAGNET:?xt=URN:BTIH:{Hex.ToUpperInvariant()}")!;

        Assert.Equal(Hex, upper.InfoHashHex);
    }

    [Fact]
    public void The_display_name_and_trackers_are_read()
    {
        string link =
            $"magnet:?xt=urn:btih:{Hex}" +
            "&dn=Some%20Release%20Name" +
            "&tr=udp%3A%2F%2Ftracker.test%3A6969%2Fannounce" +
            "&tr=http%3A%2F%2Fother.test%2Fannounce";

        var magnet = MagnetLink.Parse(link)!;

        Assert.Equal("Some Release Name", magnet.DisplayName);
        Assert.Equal(2, magnet.Trackers.Count);
        Assert.Equal("udp", magnet.Trackers[0].Scheme);
        Assert.Equal("http://other.test/announce", magnet.Trackers[1].AbsoluteUri);
    }

    [Fact]
    public void A_tracker_of_a_scheme_we_cannot_announce_to_is_dropped()
    {
        var magnet = MagnetLink.Parse($"magnet:?xt=urn:btih:{Hex}&tr=ftp%3A%2F%2Fnope.test%2Fx&tr=not-a-url")!;

        Assert.Empty(magnet.Trackers);
    }

    [Fact]
    public void A_repeated_tracker_is_only_kept_once()
    {
        string tracker = "udp%3A%2F%2Ftracker.test%3A6969%2Fannounce";
        var magnet = MagnetLink.Parse($"magnet:?xt=urn:btih:{Hex}&tr={tracker}&tr={tracker}")!;

        Assert.Single(magnet.Trackers);
    }

    [Fact]
    public void An_exact_length_is_read_when_the_link_states_one()
    {
        Assert.Equal(1234567, MagnetLink.Parse($"magnet:?xt=urn:btih:{Hex}&xl=1234567")!.Length);
        Assert.Equal(0, MagnetLink.Parse($"magnet:?xt=urn:btih:{Hex}")!.Length);
    }

    [Fact]
    public void A_second_xt_does_not_hide_the_v1_hash()
    {
        // A v1 and v2 hash on the same link is normal; the v1 one is the one
        // this can act on, wherever it sits.
        var magnet = MagnetLink.Parse(
            $"magnet:?xt=urn:btmh:1220deadbeef&xt=urn:btih:{Hex}&dn=x")!;

        Assert.Equal(Hex, magnet.InfoHashHex);
    }

    [Theory]
    [InlineData("https://example.test/file.torrent")]
    [InlineData("magnet:")]
    [InlineData("magnet:?dn=no-hash-here")]
    [InlineData("magnet:?xt=urn:btih:tooshort")]
    [InlineData("magnet:?xt=urn:btih:zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("magnet:?xt=urn:sha1:9f2c1a3b4d5e6f708192a3b4c5d6e7f809112233")]
    [InlineData("")]
    [InlineData(null)]
    public void Something_that_is_not_a_usable_magnet_is_refused(string? text) =>
        Assert.Null(MagnetLink.Parse(text));

    [Fact]
    public void Only_a_magnet_is_recognised_as_one()
    {
        Assert.True(MagnetLink.IsMagnet("magnet:?xt=urn:btih:" + Hex));
        Assert.True(MagnetLink.IsMagnet("  MAGNET:?xt=x"));
        Assert.False(MagnetLink.IsMagnet("https://example.test/x.torrent"));
        Assert.False(MagnetLink.IsMagnet(null));
    }
}
