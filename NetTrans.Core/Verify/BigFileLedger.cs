namespace NetTrans.Verify;

/// <summary>What is known about one oversized file on this machine.</summary>
public sealed record BigFileRecord
{
    /// <summary>Full path. This ledger is about copies on disk, so the path is the identity -- unlike 哈希库, which is about files.</summary>
    public required string Path { get; init; }

    public required long Size { get; init; }

    /// <summary>Last write time. With the size, this is what says a rehash is unnecessary.</summary>
    public required DateTimeOffset Modified { get; init; }

    public required string Sha256 { get; init; }

    public DateTimeOffset HashedAt { get; init; }

    /// <summary>How long the hash took, so the next run can predict the wait.</summary>
    public TimeSpan Took { get; init; }

    /// <summary>What it was supposed to be, when anybody said. Null is the normal case and is left null on purpose.</summary>
    public string? Expected { get; init; }

    /// <summary>Where <see cref="Expected"/> came from: a checksum file's URL, or 哈希库.</summary>
    public string? ExpectedFrom { get; init; }

    /// <summary>The URL the file was downloaded from, when the mark of the web still remembers it.</summary>
    public string? Source { get; init; }

    public VerifyOutcome Verdict { get; init; } = VerifyOutcome.Recorded;

    /// <summary>Paths are compared the way the filesystem does on Windows, which is where this runs.</summary>
    public static string KeyFor(string path) =>
        System.IO.Path.TrimEndingDirectorySeparator(path).ToLowerInvariant();
}

/// <summary>
/// 大文件账本: its own file, deliberately not 哈希库.
///
/// The two answer different questions and must not be allowed to answer each
/// other's. 哈希库 is about files as things that can be downloaded -- keyed by
/// name and length, and its published digests are claims about what a file
/// should be anywhere. This is an inventory of one machine's disks: keyed by
/// path, carrying a modification time, and full of ISOs and VM images nobody
/// ever published a digest for. Mixing them would let a local scan of a
/// half-written file quietly become "what this release hashes to".
///
/// It may still read 哈希库 when looking for an expectation. It never writes
/// to it.
/// </summary>
public sealed class BigFileLedger
{
    private readonly object _gate = new();
    private readonly Dictionary<string, BigFileRecord> _rows = new(StringComparer.Ordinal);

    public bool Dirty { get; private set; }

    public int Count
    {
        get { lock (_gate) return _rows.Count; }
    }

    /// <summary>Total bytes accounted for. What the 大文件 report leads with.</summary>
    public long TotalBytes
    {
        get { lock (_gate) return _rows.Values.Sum(row => row.Size); }
    }

    public BigFileRecord? Find(string path)
    {
        lock (_gate) return _rows.GetValueOrDefault(BigFileRecord.KeyFor(path));
    }

    /// <summary>
    /// Whether the file at this path is already hashed and unchanged since.
    /// Hashing a terabyte of ISOs takes an hour; doing it again next week
    /// because nothing checked is the difference between a tool and a chore.
    /// </summary>
    public bool IsCurrent(BigFile file) =>
        Find(file.Path) is { } row && row.Size == file.Size && row.Modified == file.Modified;

    public BigFileRecord Record(BigFileRecord record)
    {
        lock (_gate)
        {
            _rows[BigFileRecord.KeyFor(record.Path)] = record;
            Dirty = true;
            return record;
        }
    }

    /// <summary>Drops rows for files that are no longer there. Called after a scan of the same roots.</summary>
    public int Forget(Func<BigFileRecord, bool> gone)
    {
        lock (_gate)
        {
            var dead = _rows.Where(row => gone(row.Value)).Select(row => row.Key).ToArray();
            foreach (var key in dead) _rows.Remove(key);

            if (dead.Length > 0) Dirty = true;
            return dead.Length;
        }
    }

    /// <summary>Biggest first, which is the order anyone reads an inventory of large files in.</summary>
    public IReadOnlyList<BigFileRecord> Snapshot()
    {
        lock (_gate) return _rows.Values.OrderByDescending(row => row.Size).ToList();
    }

    public static BigFileLedger Load(string path)
    {
        var ledger = new BigFileLedger();

        foreach (var row in JsonStore.Read<LedgerFile>(path)?.Files ?? Enumerable.Empty<BigFileRecord>())
        {
            if (string.IsNullOrWhiteSpace(row.Path) || string.IsNullOrWhiteSpace(row.Sha256)) continue;
            ledger._rows[BigFileRecord.KeyFor(row.Path)] = row;
        }

        ledger.Dirty = false;
        return ledger;
    }

    public void Save(string path)
    {
        JsonStore.Write(path, new LedgerFile { Version = 1, Files = Snapshot().ToList() });

        lock (_gate) Dirty = false;
    }

    private sealed class LedgerFile
    {
        public int Version { get; set; }
        public List<BigFileRecord> Files { get; set; } = new();
    }
}
