using System.Security.Cryptography;
using System.Text;

namespace NetTrans.Torrent;

/// <summary>One file inside a torrent.</summary>
/// <param name="Path">Relative, with the torrent's own name as the first element for a multi-file torrent.</param>
/// <param name="Length">Bytes.</param>
/// <param name="Offset">Where this file starts in the torrent's single flat byte stream.</param>
public sealed record TorrentEntry(string Path, long Length, long Offset);

/// <summary>
/// A torrent's metainfo: what the files are, how they are cut into pieces, and
/// the hash of each piece.
///
/// A torrent addresses its content as one flat byte stream, with the files laid
/// end to end in the order the metainfo lists them; a piece can straddle a file
/// boundary. That is why <see cref="TorrentEntry.Offset"/> exists, and why
/// writing a piece can touch two files.
/// </summary>
public sealed class TorrentMetainfo
{
    /// <summary>Every hash in a torrent is a SHA-1, which is 20 bytes.</summary>
    public const int HashLength = 20;

    private TorrentMetainfo(
        byte[] infoHash,
        string name,
        long pieceLength,
        IReadOnlyList<byte[]> pieceHashes,
        IReadOnlyList<TorrentEntry> files,
        long totalLength,
        bool isPrivate,
        IReadOnlyList<Uri> trackers)
    {
        InfoHash = infoHash;
        Name = name;
        PieceLength = pieceLength;
        PieceHashes = pieceHashes;
        Files = files;
        TotalLength = totalLength;
        IsPrivate = isPrivate;
        Trackers = trackers;
    }

    /// <summary>SHA-1 of the info dictionary as written in the file. The torrent's identity everywhere.</summary>
    public byte[] InfoHash { get; }

    /// <summary>The suggested file name, or directory name for a multi-file torrent.</summary>
    public string Name { get; }

    public long PieceLength { get; }

    public IReadOnlyList<byte[]> PieceHashes { get; }

    public IReadOnlyList<TorrentEntry> Files { get; }

    public long TotalLength { get; }

    /// <summary>The private flag: no DHT and no peer exchange, tracker peers only.</summary>
    public bool IsPrivate { get; }

    /// <summary>Announce URLs, the announce-list flattened with the plain announce first.</summary>
    public IReadOnlyList<Uri> Trackers { get; }

    public int PieceCount => PieceHashes.Count;

    public bool IsSingleFile => Files.Count == 1;

    /// <summary>The length of one piece; the last is short unless the total divides evenly.</summary>
    public long LengthOfPiece(int index)
    {
        if (index < 0 || index >= PieceCount) throw new ArgumentOutOfRangeException(nameof(index));

        long start = index * PieceLength;
        return Math.Min(PieceLength, TotalLength - start);
    }

    /// <summary>Whether a piece's bytes hash to what the metainfo said they would.</summary>
    public bool Verify(int index, ReadOnlySpan<byte> piece)
    {
        if (index < 0 || index >= PieceCount) return false;

        Span<byte> actual = stackalloc byte[HashLength];
        SHA1.HashData(piece, actual);

        return actual.SequenceEqual(PieceHashes[index]);
    }

    /// <summary>
    /// Which files a piece's bytes belong to, and where in each.
    ///
    /// A piece is a slice of the flat stream and knows nothing about files, so
    /// this is what turns "piece 41 verified" into writes on disk.
    /// </summary>
    public IEnumerable<(TorrentEntry File, long FileOffset, long PieceOffset, long Length)> Locate(int index)
    {
        long start = index * PieceLength;
        long end = start + LengthOfPiece(index);

        foreach (var file in Files)
        {
            long fileEnd = file.Offset + file.Length;
            if (fileEnd <= start || file.Offset >= end) continue;

            long from = Math.Max(start, file.Offset);
            long to = Math.Min(end, fileEnd);

            yield return (file, from - file.Offset, from - start, to - from);
        }
    }

    /// <summary>Reads a .torrent.</summary>
    /// <exception cref="BencodeException">The bytes are not bencode.</exception>
    /// <exception cref="NotSupportedException">They are bencode, but not a torrent this can use.</exception>
    public static TorrentMetainfo Parse(byte[] data)
    {
        var root = Bencode.DecodeDictionary(data);

        var info = root.Dictionary("info")
            ?? throw new NotSupportedException("种子里没有 info 字典。");

        // Hashed over the original bytes, not a re-encoding: a torrent that is
        // not canonically ordered would otherwise get an info hash no tracker
        // and no peer recognises.
        var infoHash = SHA1.HashData(data.AsSpan(info.Start, info.Length));

        return FromInfo(info, infoHash, ReadTrackers(root));
    }

    /// <summary>
    /// Reads an info dictionary on its own, which is what a magnet link's peers
    /// send back (BEP 9). The hash is checked against the one the magnet asked
    /// for, since a peer that sends the wrong metadata is either broken or
    /// lying.
    /// </summary>
    public static TorrentMetainfo FromInfoDictionary(byte[] infoBytes, byte[] expectedInfoHash, IReadOnlyList<Uri> trackers)
    {
        var actual = SHA1.HashData(infoBytes);

        if (!actual.AsSpan().SequenceEqual(expectedInfoHash))
        {
            throw new NotSupportedException("对方发来的元数据与磁力链的哈希不符。");
        }

        var info = Bencode.DecodeDictionary(infoBytes);
        return FromInfo(info, actual, trackers);
    }

    private static TorrentMetainfo FromInfo(BDictionary info, byte[] infoHash, IReadOnlyList<Uri> trackers)
    {
        // Sanitised here, once: Name is the stem every file path is built on,
        // and a torrent that names itself "../evil" must not reach the
        // filesystem with that intact.
        string name = Sanitise(info.Text("name") ?? "未命名种子");
        long pieceLength = info.Number("piece length") ?? 0;

        if (pieceLength <= 0) throw new NotSupportedException("种子没有给出分片大小。");

        var pieces = info.Bytes("pieces") ?? throw new NotSupportedException("种子没有分片哈希。");

        if (pieces.Length == 0 || pieces.Length % HashLength != 0)
        {
            throw new NotSupportedException($"分片哈希长度 {pieces.Length} 不是 {HashLength} 的整数倍。");
        }

        var hashes = new List<byte[]>(pieces.Length / HashLength);

        for (int i = 0; i < pieces.Length; i += HashLength)
        {
            var hash = new byte[HashLength];
            Array.Copy(pieces, i, hash, 0, HashLength);
            hashes.Add(hash);
        }

        var files = ReadFiles(info, name);
        long total = files.Sum(file => file.Length);

        if (total <= 0) throw new NotSupportedException("种子里没有内容。");

        // The piece count has to match the content, or every offset after the
        // first mismatch is wrong.
        long expected = (total + pieceLength - 1) / pieceLength;

        if (expected != hashes.Count)
        {
            throw new NotSupportedException($"分片数不符：内容需要 {expected} 个，种子给了 {hashes.Count} 个。");
        }

        return new TorrentMetainfo(
            infoHash,
            name,
            pieceLength,
            hashes,
            files,
            total,
            info.Number("private") == 1,
            trackers);
    }

    private static IReadOnlyList<TorrentEntry> ReadFiles(BDictionary info, string name)
    {
        // Single-file: the info dictionary carries the length directly and the
        // name is the file's.
        if (info.List("files") is not { } list)
        {
            long length = info.Number("length")
                ?? throw new NotSupportedException("单文件种子没有给出长度。");

            return new[] { new TorrentEntry(name, length, 0) };
        }

        var files = new List<TorrentEntry>(list.Items.Count);
        long offset = 0;

        foreach (var item in list.Items)
        {
            if (item is not BDictionary entry) continue;

            long length = entry.Number("length") ?? 0;
            if (length < 0) throw new NotSupportedException("文件长度为负。");

            var pathParts = (entry.List("path")?.Items ?? Array.Empty<BValue>())
                .OfType<BString>()
                .Select(part => Sanitise(part.Text))
                .Where(part => part.Length > 0)
                .ToList();

            // A file with no usable path still occupies its bytes in the flat
            // stream, so it is kept under a made-up name rather than dropped --
            // dropping it would shift every offset after it.
            string relative = pathParts.Count > 0
                ? Path.Combine(pathParts.ToArray())
                : $"未命名文件 {files.Count + 1}";

            files.Add(new TorrentEntry(Path.Combine(name, relative), length, offset));
            offset += length;
        }

        if (files.Count == 0) throw new NotSupportedException("多文件种子的文件列表是空的。");

        return files;
    }

    private static IReadOnlyList<Uri> ReadTrackers(BDictionary root)
    {
        var trackers = new List<Uri>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? url)
        {
            if (url is null) return;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed)) return;
            if (!IsAnnounceScheme(parsed)) return;
            if (!seen.Add(parsed.AbsoluteUri)) return;

            trackers.Add(parsed);
        }

        Add(root.Text("announce"));

        // announce-list is a list of tiers, each a list of URLs. Flattened:
        // this does not implement tier failover, it just tries all of them.
        foreach (var tier in root.List("announce-list")?.Items ?? Array.Empty<BValue>())
        {
            if (tier is BList urls)
            {
                foreach (var url in urls.Items.OfType<BString>()) Add(url.Text);
            }
            else if (tier is BString single)
            {
                Add(single.Text);
            }
        }

        return trackers;
    }

    internal static bool IsAnnounceScheme(Uri url) =>
        url.Scheme is "http" or "https" or "udp";

    /// <summary>
    /// A path element from a torrent is attacker-controlled. Anything that
    /// could climb out of the download folder is neutralised here rather than
    /// trusted to the filesystem.
    /// </summary>
    internal static string Sanitise(string part)
    {
        if (part is "." or "..") return "_";

        var cleaned = new StringBuilder(part.Length);

        foreach (char c in part)
        {
            // Separators included: a torrent naming a file "a/b" must not
            // become a directory here, since its own path list is the only
            // thing allowed to nest.
            cleaned.Append(c is '/' or '\\' || c < ' ' || Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        }

        string result = cleaned.ToString().Trim().TrimEnd('.');
        return result.Length > 0 ? result : "_";
    }
}
