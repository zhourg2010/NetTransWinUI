namespace NetTrans.Verify;

/// <summary>How much a stored digest is worth.</summary>
public enum HashOrigin
{
    /// <summary>We hashed the file ourselves and nobody confirmed it. Good for "did this change since last time", not for "is this the real thing".</summary>
    Computed = 0,

    /// <summary>The digest came with the task -- pasted into 新建下载, or carried by the link.</summary>
    Given = 1,

    /// <summary>The digest was published by the server the file came from.</summary>
    Published = 2,
}

/// <summary>
/// One row of the local hash database: what a file of this name and length
/// should hash to, where that claim came from, and when we last saw it.
/// </summary>
public sealed record HashRecord
{
    public required string Sha256 { get; init; }

    public required long Size { get; init; }

    /// <summary>File name only, no directory. The database is about files, not about where a copy of one happens to sit.</summary>
    public required string Name { get; init; }

    public HashOrigin Origin { get; init; }

    /// <summary>The download this was learned from, for the inspector to show.</summary>
    public string? Url { get; init; }

    /// <summary>The checksum file a published digest was read out of.</summary>
    public string? Source { get; init; }

    public DateTimeOffset FirstSeen { get; init; }

    public DateTimeOffset LastSeen { get; init; }

    /// <summary>How many times a file has matched this row. Only ever a hint for eviction and for the inspector.</summary>
    public int Seen { get; init; } = 1;

    /// <summary>The key a finished download is looked up by: same name, same length.</summary>
    public static string KeyFor(string name, long size) =>
        $"{System.IO.Path.GetFileName(name).ToLowerInvariant()}|{size}";
}
