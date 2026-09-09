using NetTrans.Tests.Fakes;
using NetTrans.Verify;
using Xunit;

namespace NetTrans.Tests;

/// <summary>Reading the checksum files projects publish, and finding them.</summary>
public class ChecksumFileTests
{
    private const string Sha = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    private const string Md5 = "900150983cd24fb0d6963f7d28e17f72";

    [Fact]
    public void Reads_coreutils_lines()
    {
        var entries = ChecksumFile.Parse($"{Sha}  ubuntu.iso\n{Md5} *ubuntu.iso\n");

        Assert.Equal(2, entries.Count);
        Assert.Equal(new ChecksumEntry(HashKind.Sha256, Sha, "ubuntu.iso"), entries[0]);
        Assert.Equal(new ChecksumEntry(HashKind.Md5, Md5, "ubuntu.iso"), entries[1]);
    }

    [Fact]
    public void Reads_bsd_lines()
    {
        var entries = ChecksumFile.Parse($"SHA256 (dist/ubuntu.iso) = {Sha.ToUpperInvariant()}\n");

        Assert.Equal(new ChecksumEntry(HashKind.Sha256, Sha, "ubuntu.iso"), Assert.Single(entries));
    }

    [Fact]
    public void Reads_a_sidecar_that_is_nothing_but_a_hash()
    {
        var entries = ChecksumFile.Parse($"  {Sha}\n");

        Assert.Equal(new ChecksumEntry(HashKind.Sha256, Sha, null), Assert.Single(entries));
    }

    /// <summary>Signed SHA256SUMS files are the common case for big releases.</summary>
    [Fact]
    public void Skips_everything_that_is_not_a_digest()
    {
        string armoured = string.Join('\n',
            "-----BEGIN PGP SIGNED MESSAGE-----",
            "Hash: SHA512",
            "",
            "# 发布说明见 release notes",
            $"{Sha}  ubuntu.iso",
            "-----BEGIN PGP SIGNATURE-----",
            "iQIzBAEBCgAdFiEE…",
            "-----END PGP SIGNATURE-----");

        var entry = Assert.Single(ChecksumFile.Parse(armoured));
        Assert.Equal("ubuntu.iso", entry.Name);
    }

    [Fact]
    public void Ignores_a_page_that_is_not_a_checksum_file_at_all()
    {
        Assert.Empty(ChecksumFile.Parse("<html><body>404 Not Found</body></html>"));
    }

    [Fact]
    public void Takes_the_strongest_digest_published_for_the_file()
    {
        var entries = ChecksumFile.Parse($"{Md5}  ubuntu.iso\n{Sha}  ubuntu.iso\n");

        var found = ChecksumFile.Find(entries, "ubuntu.iso", singleEntryWins: false);
        Assert.Equal(HashKind.Sha256, found!.Kind);
    }

    [Fact]
    public void Matches_by_name_only_in_a_shared_list()
    {
        var entries = ChecksumFile.Parse($"{Sha}  ubuntu.iso\n{Md5}  debian.iso\n");

        Assert.Null(ChecksumFile.Find(entries, "fedora.iso", singleEntryWins: false));
        Assert.Equal(Sha, ChecksumFile.Find(entries, "ubuntu.iso", singleEntryWins: false)!.Hash);
    }

    /// <summary>A .sha256 sidecar belongs to the file it sits next to, whatever name it was published under.</summary>
    [Fact]
    public void A_sidecar_wins_even_when_the_name_inside_differs()
    {
        var entries = ChecksumFile.Parse($"{Sha}  ubuntu-24.04-desktop-amd64.iso\n");

        Assert.Null(ChecksumFile.Find(entries, "ubuntu.iso", singleEntryWins: false));
        Assert.Equal(Sha, ChecksumFile.Find(entries, "ubuntu.iso", singleEntryWins: true)!.Hash);
    }

    [Fact]
    public void Looks_for_sidecars_before_shared_lists_and_ignores_the_query_string()
    {
        var candidates = ChecksumSources.For(new Uri("https://cdn.example.com/dist/ubuntu.iso?token=abc"), "ubuntu.iso");

        Assert.Equal("https://cdn.example.com/dist/ubuntu.iso.sha256", candidates[0].Url.AbsoluteUri);
        Assert.True(candidates[0].SingleEntryWins);

        Assert.Contains(candidates, c => c.Url.AbsoluteUri == "https://cdn.example.com/dist/SHA256SUMS" && !c.SingleEntryWins);
        Assert.True(candidates.Count <= ChecksumSources.MaxProbes);
    }

    [Fact]
    public void Has_nowhere_to_look_for_a_magnet_link()
    {
        Assert.Empty(ChecksumSources.For(new Uri("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567"), "movie.mkv"));
    }

    [Fact]
    public async Task Finds_a_published_sidecar()
    {
        var site = new FakeWebsite().Page("https://example.com/d/ubuntu.iso.sha256", $"{Sha}  ubuntu.iso\n");

        var found = await OnlineChecksums.LookupAsync(site, new Uri("https://example.com/d/ubuntu.iso"), "ubuntu.iso");

        Assert.NotNull(found);
        Assert.Equal(Sha, found!.Hash);
        Assert.Equal(HashKind.Sha256, found.Kind);
    }

    [Fact]
    public async Task Falls_back_to_the_directory_list_when_there_is_no_sidecar()
    {
        var site = new FakeWebsite().Page("https://example.com/d/SHA256SUMS", $"{Sha}  ubuntu.iso\n{Md5}  other.bin\n");

        var found = await OnlineChecksums.LookupAsync(site, new Uri("https://example.com/d/ubuntu.iso"), "ubuntu.iso");

        Assert.Equal(Sha, found!.Hash);
        Assert.Equal("https://example.com/d/SHA256SUMS", found.Source.AbsoluteUri);
    }

    /// <summary>Most files have no published digest; every probe 404s and that is not an error.</summary>
    [Fact]
    public async Task Says_nothing_when_the_server_publishes_nothing()
    {
        var site = new FakeWebsite();

        Assert.Null(await OnlineChecksums.LookupAsync(site, new Uri("https://example.com/d/ubuntu.iso"), "ubuntu.iso"));
        Assert.True(site.Fetched.Count <= ChecksumSources.MaxProbes);
    }
}
