using NetTrans.Torrent;

namespace NetTrans.Diagnostics;

/// <summary>
/// A real .torrent on disk, built from nothing, so the screenshot walk can
/// reach 种子内容.
///
/// That screen only exists once a torrent is loaded, so the walk was capturing
/// the sheet's other half and the file list went one more round with no picture
/// of it -- which is how a stock CheckBox sat in a list the design draws as
/// .frow rows for as long as it did.
///
/// It is bencode written by our own encoder and read back by our own parser,
/// so if either end drifts this stops producing a screen and says so.
/// </summary>
public static class SampleTorrent
{
    /// <summary>The handoff's own example, down to the file names it lists.</summary>
    public static string Write(string directory)
    {
        var files = new (string Name, long Length)[]
        {
            ("archlinux-2026.08-x86_64.iso", 3_140_000_000),
            ("archlinux-bootstrap.tar.zst", 182_000_000),
            ("sha256sums.txt", 1024),
            ("sha256sums.txt.sig", 1024),
            ("magnet-mirrors.txt", 2048),
        };

        var entries = files
            .Select(file => (BValue)new BDictionary(new Dictionary<string, BValue>
            {
                ["length"] = Bencode.Number(file.Length),
                ["path"] = Bencode.List(Bencode.String(file.Name)),
            }))
            .ToArray();

        // One 20-byte SHA-1 per piece, and the count has to match the content:
        // the parser checks it, and rightly so -- every offset after a mismatch
        // would be wrong. The first cut of this fixture shipped a single hash
        // for 3.3 GB, so the parse threw and the walk quietly photographed the
        // sheet's other half again.
        const long pieceLength = 262_144;

        long total = files.Sum(file => file.Length);
        long pieceCount = (total + pieceLength - 1) / pieceLength;

        var pieces = new byte[pieceCount * 20];

        var metainfo = new BDictionary(new Dictionary<string, BValue>
        {
            ["announce"] = Bencode.String("http://tracker.invalid/announce"),
            ["info"] = new BDictionary(new Dictionary<string, BValue>
            {
                ["name"] = Bencode.String("archlinux-2026.08"),
                ["piece length"] = Bencode.Number(pieceLength),
                ["pieces"] = Bencode.String(pieces),
                ["files"] = new BList(entries),
            }),
        });

        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "sample.torrent");
        File.WriteAllBytes(path, Bencode.Encode(metainfo));
        return path;
    }
}
