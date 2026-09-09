using System.Text;
using NetTrans.Tests.Fakes;
using NetTrans.Verify;
using Xunit;

namespace NetTrans.Tests;

/// <summary>完成后校验: the task's own value, then 哈希库, then the server.</summary>
public class FileVerifierTests : IDisposable
{
    /// <summary>SHA-256 and MD5 of "abc", which is what every payload here is.</summary>
    private const string Sha = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    private const string Md5 = "900150983cd24fb0d6963f7d28e17f72";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nettrans-verify-" + Guid.NewGuid().ToString("N"));

    public FileVerifierTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Not worth failing a test over.
        }
    }

    private string Write(string name, string content)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
        return path;
    }

    [Fact]
    public async Task Records_a_file_nobody_publishes_a_digest_for()
    {
        var database = new HashDatabase();
        string path = Write("payload.bin", "abc");

        var report = await FileVerifier.VerifyAsync(path, url: "https://example.com/payload.bin", database: database);

        Assert.Equal(VerifyOutcome.Recorded, report.Outcome);
        Assert.Equal(Sha, report.Sha256);
        Assert.False(report.IsError);
        Assert.Equal(Sha, database.Lookup("payload.bin", 3)!.Sha256);
    }

    /// <summary>The second download of a file we already know needs no network at all.</summary>
    [Fact]
    public async Task Verifies_against_the_database_without_asking_anyone()
    {
        var database = new HashDatabase();
        database.Remember(Sha, 3, "payload.bin", HashOrigin.Published, source: "https://example.com/SHA256SUMS");

        var site = new FakeWebsite();
        string path = Write("payload.bin", "abc");

        var report = await FileVerifier.VerifyAsync(path, "https://example.com/payload.bin", database: database, transport: site);

        Assert.Equal(VerifyOutcome.Match, report.Outcome);
        Assert.Equal(HashOrigin.Published, report.Source);
        Assert.Empty(site.Fetched);
    }

    /// <summary>Same name, same length, different bytes: the mirror changed under us.</summary>
    [Fact]
    public async Task Notices_that_a_file_is_not_what_it_was_last_time()
    {
        var database = new HashDatabase();
        string path = Write("payload.bin", "abc");

        await FileVerifier.VerifyAsync(path, database: database);

        Write("payload.bin", "xyz");
        var report = await FileVerifier.VerifyAsync(path, database: database);

        Assert.Equal(VerifyOutcome.Changed, report.Outcome);
        Assert.True(report.IsError);
        Assert.Contains("哈希库", report.Detail);
    }

    [Fact]
    public async Task Checks_a_published_sidecar_and_remembers_it()
    {
        var database = new HashDatabase();
        var site = new FakeWebsite().Page("https://example.com/d/payload.bin.sha256", $"{Sha}  payload.bin\n");
        string path = Write("payload.bin", "abc");

        var report = await FileVerifier.VerifyAsync(path, "https://example.com/d/payload.bin", database: database, transport: site);

        Assert.Equal(VerifyOutcome.Match, report.Outcome);
        Assert.Equal(HashOrigin.Published, report.Source);

        var stored = database.Lookup("payload.bin", 3)!;
        Assert.Equal(HashOrigin.Published, stored.Origin);
        Assert.Equal("https://example.com/d/payload.bin.sha256", stored.Source);
    }

    /// <summary>An MD5 published in 2009 still says whether the file is right; the SHA-256 comes from the same pass.</summary>
    [Fact]
    public async Task Checks_a_published_md5_and_stores_the_sha256()
    {
        var database = new HashDatabase();
        var site = new FakeWebsite().Page("https://example.com/d/payload.bin.md5", $"{Md5}  payload.bin\n");
        string path = Write("payload.bin", "abc");

        var report = await FileVerifier.VerifyAsync(path, "https://example.com/d/payload.bin", database: database, transport: site);

        Assert.Equal(VerifyOutcome.Match, report.Outcome);
        Assert.Equal(HashKind.Md5, report.Kind);

        var stored = database.Lookup("payload.bin", 3)!;
        Assert.Equal(Sha, stored.Sha256);
        Assert.Equal(HashOrigin.Published, stored.Origin);
    }

    [Fact]
    public async Task Reports_a_file_that_does_not_match_what_was_published()
    {
        var database = new HashDatabase();
        var site = new FakeWebsite().Page("https://example.com/d/payload.bin.sha256", $"{Sha}  payload.bin\n");
        string path = Write("payload.bin", "xyz");

        var report = await FileVerifier.VerifyAsync(path, "https://example.com/d/payload.bin", database: database, transport: site);

        Assert.Equal(VerifyOutcome.Mismatch, report.Outcome);
        Assert.True(report.IsError);

        // What the publisher says the file should be is worth keeping even
        // though this copy is not it -- the retry gets checked offline.
        var stored = database.Lookup("payload.bin", 3)!;
        Assert.Equal(Sha, stored.Sha256);
        Assert.Equal(HashOrigin.Published, stored.Origin);
    }

    [Fact]
    public async Task Uses_the_value_the_task_was_created_with()
    {
        string path = Write("payload.bin", "abc");

        var report = await FileVerifier.VerifyAsync(path, expected: $"{Sha}  payload.bin");

        Assert.Equal(VerifyOutcome.Match, report.Outcome);
        Assert.Equal(HashOrigin.Given, report.Source);
    }

    [Fact]
    public async Task Says_so_when_the_file_is_gone()
    {
        var report = await FileVerifier.VerifyAsync(Path.Combine(_directory, "never-downloaded.bin"));

        Assert.Equal(VerifyOutcome.Missing, report.Outcome);
        Assert.Null(report.Sha256);
    }

    /// <summary>A checksum probe must never turn a finished download into a failure.</summary>
    [Fact]
    public async Task A_broken_server_only_means_there_is_nothing_to_check_against()
    {
        var site = new FakeWebsite().Broken("https://example.com/d/payload.bin.sha256");
        string path = Write("payload.bin", "abc");

        var report = await FileVerifier.VerifyAsync(path, "https://example.com/d/payload.bin", transport: site);

        Assert.Equal(VerifyOutcome.Recorded, report.Outcome);
        Assert.Equal(Sha, report.Sha256);
    }
}
