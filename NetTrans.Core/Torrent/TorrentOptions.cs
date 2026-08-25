namespace NetTrans.Torrent;

/// <summary>
/// When to stop seeding, borrowed from qBittorrent because it is the setting a
/// private tracker's rules are actually written in.
/// </summary>
/// <param name="MaxRatio">Stop once uploaded / downloaded reaches this. Null means never.</param>
/// <param name="MaxSeedingTime">Stop after this long seeding. Null means never.</param>
public sealed record SeedingLimits(double? MaxRatio = null, TimeSpan? MaxSeedingTime = null)
{
    /// <summary>Seed until told otherwise, which is what a public swarm wants.</summary>
    public static SeedingLimits Forever { get; } = new();

    /// <summary>The usual private-tracker floor.</summary>
    public static SeedingLimits Ratio(double ratio) => new(MaxRatio: ratio);

    public bool IsUnlimited => MaxRatio is null && MaxSeedingTime is null;

    /// <summary>
    /// The share ratio. A torrent downloaded from nothing -- one seeded from
    /// files already on disk -- has no denominator, so its ratio is reported as
    /// infinite rather than as a division by zero.
    /// </summary>
    public static double RatioOf(long uploaded, long downloaded) =>
        downloaded > 0 ? uploaded / (double)downloaded : uploaded > 0 ? double.PositiveInfinity : 0;

    /// <summary>Whether seeding has met its limit and should stop.</summary>
    public bool Reached(long uploaded, long downloaded, TimeSpan seedingFor)
    {
        if (MaxRatio is { } ratio && RatioOf(uploaded, downloaded) >= ratio) return true;
        if (MaxSeedingTime is { } time && seedingFor >= time) return true;

        return false;
    }

    /// <summary>What the inspector shows for the limit.</summary>
    public string Describe() =>
        MaxRatio is { } ratio && MaxSeedingTime is { } time ? $"分享率 {ratio:0.##} 或 {Minutes(time)} 分钟"
        : MaxRatio is { } only ? $"分享率 {only:0.##}"
        : MaxSeedingTime is { } span ? $"{Minutes(span)} 分钟"
        : "一直做种";

    private static int Minutes(TimeSpan time) => (int)Math.Round(time.TotalMinutes);
}

/// <summary>
/// Which files of a multi-file torrent to actually fetch -- qBittorrent's
/// 选择文件, and the reason nobody has to download the sample clip and the
/// screenshots to get the film.
/// </summary>
public static class FileSelection
{
    /// <summary>The pieces a file's bytes fall in, inclusive of both ends.</summary>
    public static IEnumerable<int> PiecesOf(TorrentMetainfo torrent, TorrentEntry file)
    {
        if (file.Length <= 0) yield break;

        int first = (int)(file.Offset / torrent.PieceLength);
        int last = (int)((file.Offset + file.Length - 1) / torrent.PieceLength);

        for (int piece = first; piece <= last && piece < torrent.PieceCount; piece++) yield return piece;
    }

    /// <summary>
    /// The pieces needed for a selection.
    ///
    /// A piece straddling a selected and a deselected file is included: a piece
    /// is the smallest thing that can be verified, so it cannot be had in
    /// halves. That is also why deselecting a small file next to a wanted one
    /// often saves nothing.
    /// </summary>
    public static IReadOnlyList<int> WantedPieces(TorrentMetainfo torrent, IEnumerable<TorrentEntry> selected)
    {
        var wanted = new SortedSet<int>();

        foreach (var file in selected)
        {
            foreach (int piece in PiecesOf(torrent, file)) wanted.Add(piece);
        }

        return wanted.ToList();
    }

    /// <summary>
    /// How many bytes a selection actually costs, counting a straddling piece
    /// once. This is the number worth showing next to the checkboxes, because
    /// it is what the disk and the tracker will see -- not the sum of the file
    /// sizes.
    /// </summary>
    public static long BytesFor(TorrentMetainfo torrent, IEnumerable<TorrentEntry> selected) =>
        WantedPieces(torrent, selected).Sum(torrent.LengthOfPiece);
}

/// <summary>
/// 强制校验: rebuild what we have by hashing the files on disk.
///
/// Needed more often than it sounds. The resume sidecar can be lost or stale,
/// files can be moved in from elsewhere, and cross-seeding the same content
/// under a second torrent starts with exactly this question -- which pieces do
/// these bytes already satisfy?
/// </summary>
public static class TorrentVerifier
{
    /// <summary>Hashes every piece and returns the bitfield of the ones that check out.</summary>
    /// <param name="progress">Reports pieces checked, for a bar that would otherwise sit still for minutes.</param>
    public static async Task<byte[]> VerifyAsync(
        TorrentMetainfo torrent,
        PieceStore store,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var bits = new byte[PeerWire.BitfieldLength(torrent.PieceCount)];

        for (int piece = 0; piece < torrent.PieceCount; piece++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int length = (int)torrent.LengthOfPiece(piece);

            // Read through the store so a piece spanning two files is
            // reassembled the same way it would be to serve it.
            var bytes = await store.ReadAsync(piece, 0, length, cancellationToken).ConfigureAwait(false);

            if (bytes is not null && torrent.Verify(piece, bytes)) PeerWire.SetPiece(bits, piece);

            progress?.Report(piece + 1);
        }

        return bits;
    }
}
