using System.Security.Cryptography;
using System.Text;
using NetTrans.Torrent;

namespace NetTrans.Tests.Fakes;

/// <summary>
/// Builds a .torrent over content the test also holds, so a transfer can be
/// checked against the bytes it was supposed to produce.
/// </summary>
public sealed class TorrentBuilder
{
    private readonly List<(string Path, byte[] Content)> _files = new();

    public string Name { get; set; } = "wanted";

    public long PieceLength { get; set; } = 256;

    public List<string> Trackers { get; } = new();

    /// <summary>Tiers for announce-list, when a test needs the nested form.</summary>
    public List<List<string>> TrackerTiers { get; } = new();

    public bool IsPrivate { get; set; }

    /// <summary>Single-file when there is one entry and <see cref="Multi"/> is false.</summary>
    public bool Multi { get; set; }

    public TorrentBuilder Add(string path, byte[] content)
    {
        _files.Add((path, content));
        return this;
    }

    public TorrentBuilder Add(string path, int length, byte fill)
    {
        return Add(path, Enumerable.Repeat(fill, length).ToArray());
    }

    /// <summary>The content as the torrent addresses it: every file end to end.</summary>
    public byte[] Content() => _files.SelectMany(file => file.Content).ToArray();

    public byte[] Build()
    {
        var content = Content();

        var hashes = new List<byte>();

        for (long offset = 0; offset < content.Length; offset += PieceLength)
        {
            int length = (int)Math.Min(PieceLength, content.Length - offset);
            hashes.AddRange(SHA1.HashData(content.AsSpan((int)offset, length)));
        }

        var info = new Dictionary<string, BValue>(StringComparer.Ordinal)
        {
            ["name"] = Bencode.String(Name),
            ["piece length"] = Bencode.Number(PieceLength),
            ["pieces"] = Bencode.String(hashes.ToArray()),
        };

        if (IsPrivate) info["private"] = Bencode.Number(1);

        if (Multi || _files.Count > 1)
        {
            info["files"] = new BList(_files
                .Select(file => (BValue)Bencode.Dictionary(
                    ("length", Bencode.Number(file.Content.Length)),
                    ("path", new BList(file.Path
                        .Split('/', StringSplitOptions.RemoveEmptyEntries)
                        .Select(part => (BValue)Bencode.String(part))
                        .ToList()))))
                .ToList());
        }
        else
        {
            info["length"] = Bencode.Number(_files[0].Content.Length);
        }

        var root = new Dictionary<string, BValue>(StringComparer.Ordinal)
        {
            ["info"] = new BDictionary(info),
        };

        if (Trackers.Count > 0) root["announce"] = Bencode.String(Trackers[0]);

        if (TrackerTiers.Count > 0)
        {
            root["announce-list"] = new BList(TrackerTiers
                .Select(tier => (BValue)new BList(tier.Select(url => (BValue)Bencode.String(url)).ToList()))
                .ToList());
        }

        return Bencode.Encode(new BDictionary(root));
    }

    /// <summary>The info hash the built torrent will have.</summary>
    public byte[] InfoHash() => TorrentMetainfo.Parse(Build()).InfoHash;

    /// <summary>The info dictionary on its own, as a magnet's peers would send it.</summary>
    public byte[] InfoDictionary()
    {
        byte[] data = Build();
        var info = Bencode.DecodeDictionary(data).Dictionary("info")!;

        return data.AsSpan(info.Start, info.Length).ToArray();
    }

    public string Magnet()
    {
        var link = new StringBuilder("magnet:?xt=urn:btih:").Append(Convert.ToHexString(InfoHash()).ToLowerInvariant());

        link.Append("&dn=").Append(Uri.EscapeDataString(Name));
        foreach (string tracker in Trackers) link.Append("&tr=").Append(Uri.EscapeDataString(tracker));

        return link.ToString();
    }
}
