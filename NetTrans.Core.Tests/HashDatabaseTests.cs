using NetTrans.Verify;
using Xunit;

namespace NetTrans.Tests;

/// <summary>哈希库: what it remembers, what it refuses to forget, and what it survives.</summary>
public class HashDatabaseTests : IDisposable
{
    private const string Sha = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    private const string Other = "3a985da74fe225b2045c172d6bd390bd855f086e3e9d525b46bfe24511431532";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nettrans-db-" + Guid.NewGuid().ToString("N"));

    public HashDatabaseTests() => Directory.CreateDirectory(_directory);

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

    [Fact]
    public void Remembers_a_file_by_name_and_length()
    {
        var database = new HashDatabase();
        database.Remember(Sha, 4096, @"D:\Downloads\ubuntu.iso", HashOrigin.Computed, "https://example.com/ubuntu.iso");

        var found = database.Lookup("ubuntu.iso", 4096);

        Assert.Equal(Sha, found!.Sha256);
        Assert.Equal("ubuntu.iso", found.Name);
        Assert.Null(database.Lookup("ubuntu.iso", 4097));
        Assert.Same(found, database.ByHash(Sha.ToUpperInvariant()));
    }

    /// <summary>Hashing a local copy must not be able to redefine a digest the publisher gave.</summary>
    [Fact]
    public void A_local_hash_never_overwrites_a_published_one()
    {
        var database = new HashDatabase();
        database.Remember(Sha, 10, "ubuntu.iso", HashOrigin.Published, source: "https://example.com/SHA256SUMS");
        database.Remember(Other, 10, "ubuntu.iso", HashOrigin.Computed);

        var found = database.Lookup("ubuntu.iso", 10)!;

        Assert.Equal(Sha, found.Sha256);
        Assert.Equal(HashOrigin.Published, found.Origin);
        Assert.Equal(2, found.Seen);
    }

    [Fact]
    public void A_published_hash_does_replace_a_local_one()
    {
        var database = new HashDatabase();
        database.Remember(Other, 10, "ubuntu.iso", HashOrigin.Computed);
        database.Remember(Sha, 10, "ubuntu.iso", HashOrigin.Published);

        Assert.Equal(Sha, database.Lookup("ubuntu.iso", 10)!.Sha256);

        // The row it replaced must not still be reachable by its old digest.
        Assert.Null(database.ByHash(Other));
    }

    [Fact]
    public void Keeps_the_first_time_a_file_was_seen()
    {
        var first = DateTimeOffset.Now.AddDays(-30);
        var database = new HashDatabase();

        database.Remember(Sha, 10, "ubuntu.iso", HashOrigin.Computed, now: first);
        var again = database.Remember(Sha, 10, "ubuntu.iso", HashOrigin.Computed, now: first.AddDays(30));

        Assert.Equal(first, again.FirstSeen);
        Assert.Equal(2, again.Seen);
    }

    [Fact]
    public void Survives_a_round_trip_through_disk()
    {
        string path = Path.Combine(_directory, "hashes.json");

        var database = new HashDatabase();
        database.Remember(Sha, 4096, "ubuntu.iso", HashOrigin.Published, "https://example.com/ubuntu.iso", "https://example.com/SHA256SUMS");
        Assert.True(database.Dirty);

        database.Save(path);
        Assert.False(database.Dirty);

        var reloaded = HashDatabase.Load(path);
        var found = reloaded.Lookup("ubuntu.iso", 4096)!;

        Assert.Equal(Sha, found.Sha256);
        Assert.Equal(HashOrigin.Published, found.Origin);
        Assert.Equal("https://example.com/SHA256SUMS", found.Source);
        Assert.False(reloaded.Dirty);
    }

    [Fact]
    public void A_corrupt_file_costs_the_history_and_nothing_else()
    {
        string path = Path.Combine(_directory, "hashes.json");
        File.WriteAllText(path, "{\"version\":1,\"records\":[{\"Sha256\":\"tru");

        var database = HashDatabase.Load(path);

        Assert.Equal(0, database.Count);
        Assert.Null(database.Lookup("ubuntu.iso", 10));
    }

    [Fact]
    public void Missing_file_loads_as_an_empty_database()
    {
        Assert.Equal(0, HashDatabase.Load(Path.Combine(_directory, "never-written.json")).Count);
    }

    /// <summary>Years of downloads must not turn into an unbounded file.</summary>
    [Fact]
    public void Drops_the_oldest_rows_once_it_is_full()
    {
        var database = new HashDatabase(capacity: 20);
        var start = DateTimeOffset.Now.AddDays(-100);

        for (int i = 0; i < 40; i++)
        {
            database.Remember(Sha[..60] + i.ToString("x4"), i, $"file{i}.bin", HashOrigin.Computed, now: start.AddDays(i));
        }

        Assert.True(database.Count <= 20);

        // The newest survived, the oldest did not.
        Assert.NotNull(database.Lookup("file39.bin", 39));
        Assert.Null(database.Lookup("file0.bin", 0));
    }
}
