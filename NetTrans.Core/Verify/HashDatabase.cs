namespace NetTrans.Verify;

/// <summary>
/// 哈希库: what we know about files we have seen, kept on disk so the next
/// download can be checked against it without asking anyone.
///
/// Two things go in. A digest published by the server, once 联网核对 has found
/// one -- that is a real claim about what the file should be, and re-downloading
/// the same release a year later is verified offline and instantly. And the
/// digest of every file we finish, which is a weaker claim but still answers
/// "the copy I got today is not the copy I got last month", which is exactly
/// how a swapped mirror shows itself.
///
/// Rows are keyed by name and length together: a byte-for-byte different file
/// almost never has both.
/// </summary>
public sealed class HashDatabase
{
    /// <summary>Rows are small; this is about a megabyte of JSON, and years of downloads.</summary>
    public const int DefaultCapacity = 4000;

    private readonly object _gate = new();

    /// <summary>name|size -> row. One row per file identity, not per download.</summary>
    private readonly Dictionary<string, HashRecord> _byKey = new(StringComparer.Ordinal);

    /// <summary>sha-256 -> row, for "have I seen this exact content before".</summary>
    private readonly Dictionary<string, HashRecord> _byHash = new(StringComparer.OrdinalIgnoreCase);

    public HashDatabase(int capacity = DefaultCapacity) => Capacity = Math.Max(1, capacity);

    public int Capacity { get; }

    /// <summary>True when something has been remembered since the last save.</summary>
    public bool Dirty { get; private set; }

    public int Count
    {
        get { lock (_gate) return _byKey.Count; }
    }

    /// <summary>What a file of this name and length should hash to, if we know.</summary>
    public HashRecord? Lookup(string name, long size)
    {
        lock (_gate) return _byKey.GetValueOrDefault(HashRecord.KeyFor(name, size));
    }

    /// <summary>Whether this exact content is already known, under any name.</summary>
    public HashRecord? ByHash(string sha256)
    {
        lock (_gate) return _byHash.GetValueOrDefault(sha256);
    }

    /// <summary>
    /// Records what a file should hash to.
    ///
    /// A weaker claim never overwrites a stronger one: once a published digest
    /// is in, hashing a local copy cannot quietly redefine what "correct" means
    /// -- which is the whole point of keeping the origin.
    /// </summary>
    public HashRecord Remember(
        string sha256,
        long size,
        string name,
        HashOrigin origin,
        string? url = null,
        string? source = null,
        DateTimeOffset? now = null)
    {
        var when = now ?? DateTimeOffset.Now;
        string key = HashRecord.KeyFor(name, size);

        lock (_gate)
        {
            var existing = _byKey.GetValueOrDefault(key);

            if (existing is not null && existing.Origin > origin)
            {
                var kept = existing with { LastSeen = when, Seen = existing.Seen + 1 };
                Store(key, kept);
                return kept;
            }

            var record = new HashRecord
            {
                Sha256 = sha256.ToLowerInvariant(),
                Size = size,
                Name = HashRecord.FileNameOf(name),
                Origin = origin,
                Url = url ?? existing?.Url,
                Source = source ?? existing?.Source,
                FirstSeen = existing?.FirstSeen ?? when,
                LastSeen = when,
                Seen = existing is null ? 1 : existing.Seen + 1,
            };

            Store(key, record);
            Evict();
            return record;
        }
    }

    /// <summary>Every row, newest first. The 哈希库 sheet's list, and what Save writes.</summary>
    public IReadOnlyList<HashRecord> Snapshot()
    {
        lock (_gate) return _byKey.Values.OrderByDescending(row => row.LastSeen).ToList();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _byKey.Clear();
            _byHash.Clear();
            Dirty = true;
        }
    }

    /// <summary>Reads the database, or hands back an empty one. A corrupt file is not worth refusing to start over.</summary>
    public static HashDatabase Load(string path, int capacity = DefaultCapacity)
    {
        var database = new HashDatabase(capacity);

        // A file truncated by a power cut, edited by hand, or written by some
        // future version reads as null; that costs the history, not the app.
        var file = JsonStore.Read<HashFile>(path);

        foreach (var row in file?.Records ?? Enumerable.Empty<HashRecord>())
        {
            if (string.IsNullOrWhiteSpace(row.Sha256) || string.IsNullOrWhiteSpace(row.Name)) continue;
            database.Store(HashRecord.KeyFor(row.Name, row.Size), row);
        }

        database.Evict();
        database.Dirty = false;

        return database;
    }

    /// <summary>Writes the database. Atomic, because downloads finish while the app is being closed.</summary>
    public void Save(string path)
    {
        JsonStore.Write(path, new HashFile { Version = 1, Records = Snapshot().ToList() });

        lock (_gate) Dirty = false;
    }

    private void Store(string key, HashRecord record)
    {
        // A row replaced under the same key may have carried a different
        // digest; the hash index must not keep pointing at the old one.
        if (_byKey.TryGetValue(key, out var previous) && !string.Equals(previous.Sha256, record.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            _byHash.Remove(previous.Sha256);
        }

        _byKey[key] = record;
        _byHash[record.Sha256] = record;
        Dirty = true;
    }

    /// <summary>
    /// Drops the least recently seen tenth once the cap is passed, rather than
    /// one row per insert -- trimming in blocks keeps this off the path of a
    /// download that just finished.
    /// </summary>
    private void Evict()
    {
        if (_byKey.Count <= Capacity) return;

        int drop = Math.Max(1, Capacity / 10) + (_byKey.Count - Capacity);

        foreach (var row in _byKey.OrderBy(pair => pair.Value.LastSeen).Take(drop).ToArray())
        {
            _byKey.Remove(row.Key);
            _byHash.Remove(row.Value.Sha256);
        }
    }

    private sealed class HashFile
    {
        public int Version { get; set; }
        public List<HashRecord> Records { get; set; } = new();
    }
}
