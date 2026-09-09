using System.Text;
using NetTrans.Net;
using NetTrans.Tests.Fakes;
using NetTrans.Verify;
using Xunit;

namespace NetTrans.Tests;

/// <summary>大文件核对: finding them, hashing them once, and keeping the ledger apart from 哈希库.</summary>
public class BigFileTests : IDisposable
{
    /// <summary>SHA-256 of "abc", which is what the payloads here are.</summary>
    private const string Sha = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "nettrans-big-" + Guid.NewGuid().ToString("N"));

    /// <summary>Small numbers, same rules: the floor is what is being tested, not the gigabyte.</summary>
    private static readonly ScanOptions Options = new() { MinimumSize = 16 };

    public BigFileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Not worth failing a test over.
        }
    }

    private string Write(string relative, string content = "abc", int pad = 0)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content + new string('.', pad)));
        return path;
    }

    private IReadOnlyList<BigFile> Scan(ScanOptions? options = null) =>
        BigFileScan.Walk(new[] { _root }, options ?? Options).OrderBy(file => file.Name).ToList();

    [Fact]
    public void Finds_files_over_the_floor_and_ignores_the_rest()
    {
        Write("big.iso", pad: 64);
        Write("small.txt");

        var found = Scan();

        Assert.Equal("big.iso", Assert.Single(found).Name);
    }

    /// <summary>
    /// A page file is a gigabyte of live RAM: hashing it costs an hour and
    /// produces a number that is already wrong.
    /// </summary>
    [Fact]
    public void Skips_the_machine_own_working_files()
    {
        Write("pagefile.sys", pad: 64);
        Write("hiberfil.sys", pad: 64);
        Write("MEMORY.DMP", pad: 64);
        Write("crash.dmp", pad: 64);
        Write("movie.mkv.part", pad: 64);
        Write("keep.iso", pad: 64);

        Assert.Equal(new[] { "keep.iso" }, Scan().Select(file => file.Name));
    }

    [Fact]
    public void Skips_system_and_scratch_directories_wherever_they_are()
    {
        Write(Path.Combine("Windows", "install.wim"), pad: 64);
        Write(Path.Combine("Users", "me", "AppData", "Local", "Temp", "huge.bin"), pad: 64);
        Write(Path.Combine("$Recycle.Bin", "deleted.iso"), pad: 64);
        Write(Path.Combine("Users", "me", "Downloads", "ubuntu.iso"), pad: 64);

        Assert.Equal(new[] { "ubuntu.iso" }, Scan().Select(file => file.Name));
    }

    [Theory]
    [InlineData("1GB", 1073741824L)]
    [InlineData("1 GB", 1073741824L)]
    [InlineData("512m", 536870912L)]
    [InlineData("2.5GB", 2684354560L)]
    [InlineData("4K", 4096L)]
    [InlineData("1048576", 1048576L)]
    public void Reads_a_size_off_the_command_line(string text, long expected) =>
        Assert.Equal(expected, BigFileScan.ParseSize(text));

    [Theory]
    [InlineData("")]
    [InlineData("大一点")]
    [InlineData("-1GB")]
    public void Refuses_a_size_it_cannot_read(string text) =>
        Assert.Null(BigFileScan.ParseSize(text));

    [Fact]
    public void Reads_the_mark_of_the_web_back_out()
    {
        Assert.Equal(
            "https://releases.ubuntu.com/24.04/ubuntu.iso",
            ZoneIdentifier.SourceIn(ZoneIdentifier.Build("https://releases.ubuntu.com/24.04/ubuntu.iso")));

        // A mark with only a referrer still says which site it came from.
        Assert.Equal("https://example.com/", ZoneIdentifier.SourceIn("[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=https://example.com/\r\n"));
        Assert.Null(ZoneIdentifier.SourceIn("[ZoneTransfer]\r\nZoneId=3\r\n"));
    }

    [Fact]
    public async Task Records_a_file_nobody_published_a_digest_for()
    {
        Write("ubuntu.iso", pad: 64);
        var ledger = new BigFileLedger();

        var outcomes = await BigFileAudit.RunAsync(Scan(), ledger, new ManualClock());

        var outcome = Assert.Single(outcomes);
        Assert.Equal(VerifyOutcome.Recorded, outcome.Verdict);
        Assert.Null(outcome.Expected);

        var row = ledger.Find(outcome.File.Path)!;
        Assert.Equal(outcome.Sha256, row.Sha256);
        Assert.Null(row.Expected);
    }

    /// <summary>Hashing a terabyte again next week because nothing checked is the difference between a tool and a chore.</summary>
    [Fact]
    public async Task Does_not_hash_a_file_that_has_not_changed()
    {
        Write("ubuntu.iso", pad: 64);
        var ledger = new BigFileLedger();
        var clock = new ManualClock();

        await BigFileAudit.RunAsync(Scan(), ledger, clock);
        var again = await BigFileAudit.RunAsync(Scan(), ledger, clock);

        Assert.True(Assert.Single(again).Skipped);

        // ...unless asked to.
        var forced = await BigFileAudit.RunAsync(Scan(), ledger, clock, options: new AuditOptions { Rehash = true });
        Assert.False(Assert.Single(forced).Skipped);
    }

    /// <summary>Same name, same length, different bytes: bit rot, or somebody swapped it.</summary>
    [Fact]
    public async Task Says_when_a_file_is_not_what_it_was_last_time()
    {
        string path = Write("archive.bin", "abc", pad: 64);
        var ledger = new BigFileLedger();
        var clock = new ManualClock();

        var first = await BigFileAudit.RunAsync(Scan(), ledger, clock);

        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("xyz" + new string('.', 64)));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        var outcome = Assert.Single(await BigFileAudit.RunAsync(Scan(), ledger, clock));

        Assert.Equal(VerifyOutcome.Changed, outcome.Verdict);
        Assert.Equal(Assert.Single(first).Sha256, outcome.Expected);
        Assert.Equal("上次扫描", outcome.From);
    }

    /// <summary>哈希库 is read for a published digest -- and only read.</summary>
    [Fact]
    public async Task Checks_against_a_published_digest_the_main_database_already_has()
    {
        Write("payload.bin");
        var files = Scan(new ScanOptions { MinimumSize = 1 });

        var published = new HashDatabase();
        published.Remember(Sha, 3, "payload.bin", HashOrigin.Published, source: "https://example.com/SHA256SUMS");
        var before = published.Lookup("payload.bin", 3);

        var ledger = new BigFileLedger();
        var outcome = Assert.Single(await BigFileAudit.RunAsync(files, ledger, new ManualClock(), published));

        Assert.Equal(VerifyOutcome.Match, outcome.Verdict);
        Assert.Contains("哈希库", outcome.From);

        // The scan must not have written anything back into 哈希库: the row is
        // identical, down to the "seen" count only Remember touches.
        Assert.Equal(before, published.Lookup("payload.bin", 3));
        Assert.Equal(1, published.Count);
    }

    /// <summary>The mark of the web is the only thing on the machine that remembers where a file came from.</summary>
    [Fact]
    public async Task Asks_the_site_the_file_came_from()
    {
        string path = Write("payload.bin");
        File.WriteAllText(ZoneIdentifier.StreamPath(path), ZoneIdentifier.Build("https://example.com/d/payload.bin"));

        var site = new FakeWebsite().Page("https://example.com/d/payload.bin.sha256", $"{Sha}  payload.bin\n");

        var files = Scan(new ScanOptions { MinimumSize = 1 }).Where(file => file.Name == "payload.bin").ToList();
        var ledger = new BigFileLedger();

        var outcome = Assert.Single(await BigFileAudit.RunAsync(files, ledger, new ManualClock(), transport: site));

        Assert.Equal(VerifyOutcome.Match, outcome.Verdict);
        Assert.Equal("https://example.com/d/payload.bin.sha256", outcome.From);
        Assert.Equal("https://example.com/d/payload.bin", ledger.Find(path)!.Source);
    }

    [Fact]
    public async Task The_ledger_survives_a_round_trip_and_forgets_deleted_files()
    {
        string path = Write("ubuntu.iso", pad: 64);
        var ledger = new BigFileLedger();

        await BigFileAudit.RunAsync(Scan(), ledger, new ManualClock());
        Assert.True(ledger.Dirty);

        string file = Path.Combine(_root, "ledger.json");
        ledger.Save(file);
        Assert.False(ledger.Dirty);

        var reloaded = BigFileLedger.Load(file);
        Assert.Equal(1, reloaded.Count);
        Assert.Equal(ledger.TotalBytes, reloaded.TotalBytes);
        Assert.NotNull(reloaded.Find(path));

        File.Delete(path);
        Assert.Equal(1, BigFileAudit.Prune(reloaded));
        Assert.Equal(0, reloaded.Count);
    }

    [Fact]
    public async Task Reports_what_it_found()
    {
        Write("ubuntu.iso", pad: 64);
        Write(Path.Combine("vm", "disk.vhdx"), pad: 128);

        var outcomes = await BigFileAudit.RunAsync(Scan(), new BigFileLedger(), new ManualClock());
        string report = BigFileReport.Full(outcomes, new[] { _root }, Options.MinimumSize);

        Assert.Contains("大文件核对", report);
        Assert.Contains("ubuntu.iso", report);
        Assert.Contains("disk.vhdx", report);
        Assert.Contains("没有对不上的", report);
    }
}
