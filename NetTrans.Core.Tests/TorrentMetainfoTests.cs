using System.Security.Cryptography;
using System.Text;
using NetTrans.Tests.Fakes;
using NetTrans.Torrent;
using Xunit;

namespace NetTrans.Tests;

/// <summary>
/// Reading a .torrent. The info hash is the torrent's identity on every tracker
/// and to every peer, so most of what matters here is getting it right.
/// </summary>
public class TorrentMetainfoTests
{
    [Fact]
    public void A_single_file_torrent_is_read()
    {
        var torrent = Parse(new TorrentBuilder { Name = "wanted.bin" }.Add("wanted.bin", 700, 0xAB));

        Assert.Equal("wanted.bin", torrent.Name);
        Assert.True(torrent.IsSingleFile);
        Assert.Equal(700, torrent.TotalLength);
        Assert.Equal(256, torrent.PieceLength);
        Assert.Equal(3, torrent.PieceCount);
        Assert.Equal(0, torrent.Files.Single().Offset);
    }

    [Fact]
    public void A_multi_file_torrent_lays_its_files_end_to_end()
    {
        var torrent = Parse(new TorrentBuilder { Name = "album" }
            .Add("cover.jpg", 100, 1)
            .Add("disc/track.flac", 300, 2));

        Assert.False(torrent.IsSingleFile);
        Assert.Equal(400, torrent.TotalLength);

        // The offsets are what make a piece that straddles two files writable.
        Assert.Equal(0, torrent.Files[0].Offset);
        Assert.Equal(100, torrent.Files[1].Offset);

        Assert.Equal(Path.Combine("album", "cover.jpg"), torrent.Files[0].Path);
        Assert.Equal(Path.Combine("album", "disc", "track.flac"), torrent.Files[1].Path);
    }

    [Fact]
    public void The_info_hash_is_taken_over_the_bytes_as_written()
    {
        byte[] data = new TorrentBuilder { Name = "wanted.bin" }.Add("wanted.bin", 300, 7).Build();

        var info = Bencode.DecodeDictionary(data).Dictionary("info")!;
        var expected = SHA1.HashData(data.AsSpan(info.Start, info.Length).ToArray());

        Assert.Equal(expected, TorrentMetainfo.Parse(data).InfoHash);
    }

    [Fact]
    public void The_info_hash_survives_a_torrent_a_re_encoding_would_reorder()
    {
        // A key out of canonical order: re-encoding would sort it and produce a
        // hash that matches nothing on any tracker.
        byte[] canonical = new TorrentBuilder { Name = "x" }.Add("x", 256, 3).Build();
        var info = Bencode.DecodeDictionary(canonical).Dictionary("info")!;

        byte[] raw = canonical.AsSpan(info.Start, info.Length).ToArray();

        // Move "name" to the front, which bencode says is wrong and clients
        // accept anyway.
        byte[] reordered = Reorder(raw);

        byte[] rebuilt = Splice(canonical, info.Start, info.Length, reordered);

        Assert.Equal(SHA1.HashData(reordered), TorrentMetainfo.Parse(rebuilt).InfoHash);
        Assert.NotEqual(TorrentMetainfo.Parse(canonical).InfoHash, TorrentMetainfo.Parse(rebuilt).InfoHash);
    }

    [Fact]
    public void The_last_piece_is_short_unless_it_divides_evenly()
    {
        var torrent = Parse(new TorrentBuilder { PieceLength = 256 }.Add("f", 700, 9));

        Assert.Equal(256, torrent.LengthOfPiece(0));
        Assert.Equal(256, torrent.LengthOfPiece(1));
        Assert.Equal(188, torrent.LengthOfPiece(2));
    }

    [Fact]
    public void A_piece_verifies_against_its_own_hash_and_not_another()
    {
        var builder = new TorrentBuilder { PieceLength = 256 }.Add("f", 512, 0x5A);
        var torrent = Parse(builder);
        var content = builder.Content();

        Assert.True(torrent.Verify(0, content.AsSpan(0, 256)));

        var tampered = content.AsSpan(0, 256).ToArray();
        tampered[100] ^= 0xFF;
        Assert.False(torrent.Verify(0, tampered));
    }

    [Fact]
    public void A_piece_that_straddles_two_files_locates_into_both()
    {
        var torrent = Parse(new TorrentBuilder { Name = "set", PieceLength = 256 }
            .Add("a.bin", 100, 1)
            .Add("b.bin", 300, 2));

        // Piece 0 covers bytes 0..255: all of a.bin, then the first 156 of b.bin.
        var places = torrent.Locate(0).ToList();

        Assert.Equal(2, places.Count);
        Assert.Equal((0L, 0L, 100L), (places[0].FileOffset, places[0].PieceOffset, places[0].Length));
        Assert.Equal((0L, 100L, 156L), (places[1].FileOffset, places[1].PieceOffset, places[1].Length));
    }

    [Fact]
    public void The_last_piece_locates_only_as_far_as_the_content_goes()
    {
        var torrent = Parse(new TorrentBuilder { PieceLength = 256 }.Add("f", 300, 4));

        var last = torrent.Locate(1).Single();
        Assert.Equal(44, last.Length);
    }

    [Fact]
    public void Trackers_come_from_announce_and_announce_list_without_duplicates()
    {
        var builder = new TorrentBuilder();
        builder.Add("f", 256, 1);
        builder.Trackers.Add("http://tracker.test/announce");
        builder.TrackerTiers.Add(new List<string> { "http://tracker.test/announce", "udp://udp.test:6969" });
        builder.TrackerTiers.Add(new List<string> { "not a url", "ftp://nope.test/x" });

        var trackers = Parse(builder).Trackers;

        Assert.Equal(2, trackers.Count);
        Assert.Equal("http://tracker.test/announce", trackers[0].AbsoluteUri);
        Assert.Equal("udp", trackers[1].Scheme);
    }

    [Fact]
    public void The_private_flag_is_read()
    {
        Assert.True(Parse(new TorrentBuilder { IsPrivate = true }.Add("f", 256, 1)).IsPrivate);
        Assert.False(Parse(new TorrentBuilder().Add("f", 256, 1)).IsPrivate);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../../etc/passwd")]
    [InlineData("a\\b")]
    public void A_path_that_could_climb_out_of_the_folder_is_neutralised(string path)
    {
        // The path list is attacker-controlled; only its own elements may nest.
        var torrent = Parse(new TorrentBuilder { Name = "set", Multi = true }.Add(path, 256, 1).Add("ok", 256, 2));

        string relative = torrent.Files[0].Path;

        Assert.StartsWith("set" + Path.DirectorySeparatorChar, relative);
        Assert.DoesNotContain("..", relative);
        Assert.DoesNotContain("passwd", relative.Split(Path.DirectorySeparatorChar)[1]);
    }

    [Fact]
    public void A_name_that_tries_to_escape_is_neutralised_too()
    {
        // Name is the stem every path is built on, so it is sanitised at the
        // same point the path elements are.
        var torrent = Parse(new TorrentBuilder { Name = "../evil" }.Add("f", 256, 1));

        Assert.DoesNotContain('/', torrent.Name);
        Assert.DoesNotContain('\\', torrent.Name);
        Assert.Equal(torrent.Name, torrent.Files[0].Path);
        Assert.False(Path.IsPathRooted(torrent.Files[0].Path));
    }

    [Fact]
    public void A_torrent_whose_piece_count_does_not_match_its_content_is_refused()
    {
        // Every offset after a mismatch would be wrong, which is a corrupt file
        // rather than an error the user could act on. 700 bytes at 256 needs
        // three hashes; this gives two.
        byte[] broken = Bencode.Encode(Bencode.Dictionary(("info", Bencode.Dictionary(
            ("name", Bencode.String("f")),
            ("length", Bencode.Number(700)),
            ("piece length", Bencode.Number(256)),
            ("pieces", Bencode.String(new byte[40]))))));

        var error = Assert.Throws<NotSupportedException>(() => TorrentMetainfo.Parse(broken));
        Assert.Contains("分片数不符", error.Message);
    }

    [Fact]
    public void A_torrent_whose_hash_block_is_not_a_multiple_of_twenty_is_refused()
    {
        byte[] broken = Bencode.Encode(Bencode.Dictionary(("info", Bencode.Dictionary(
            ("name", Bencode.String("f")),
            ("length", Bencode.Number(256)),
            ("piece length", Bencode.Number(256)),
            ("pieces", Bencode.String(new byte[19]))))));

        Assert.Throws<NotSupportedException>(() => TorrentMetainfo.Parse(broken));
    }

    [Fact]
    public void Something_that_is_not_a_torrent_is_refused()
    {
        Assert.Throws<NotSupportedException>(() => TorrentMetainfo.Parse(Bencode.Encode(Bencode.Dictionary(
            ("announce", Bencode.String("http://tracker.test/announce"))))));
    }

    [Fact]
    public void An_info_dictionary_from_a_peer_is_accepted_only_when_it_hashes_right()
    {
        var builder = new TorrentBuilder { Name = "wanted.bin" }.Add("wanted.bin", 512, 3);
        byte[] infoBytes = builder.InfoDictionary();
        byte[] hash = builder.InfoHash();

        var torrent = TorrentMetainfo.FromInfoDictionary(infoBytes, hash, Array.Empty<Uri>());
        Assert.Equal("wanted.bin", torrent.Name);

        var wrong = (byte[])hash.Clone();
        wrong[0] ^= 0xFF;

        // A peer that sends metadata for a different torrent is either broken
        // or lying; either way its bytes are not ours.
        Assert.Throws<NotSupportedException>(
            () => TorrentMetainfo.FromInfoDictionary(infoBytes, wrong, Array.Empty<Uri>()));
    }

    private static TorrentMetainfo Parse(TorrentBuilder builder) => TorrentMetainfo.Parse(builder.Build());

    /// <summary>Rewrites an info dictionary with its first key moved to the end.</summary>
    private static byte[] Reorder(byte[] raw)
    {
        var info = Bencode.DecodeDictionary(raw);
        var entries = info.Entries.OrderBy(entry => entry.Key, StringComparer.Ordinal).ToList();

        var output = new MemoryStream();
        output.WriteByte((byte)'d');

        foreach (var entry in entries.Skip(1).Concat(entries.Take(1)))
        {
            byte[] key = Encoding.UTF8.GetBytes(entry.Key);
            output.Write(Encoding.ASCII.GetBytes($"{key.Length}:"));
            output.Write(key);
            output.Write(raw, entry.Value.Start, entry.Value.Length);
        }

        output.WriteByte((byte)'e');
        return output.ToArray();
    }

    private static byte[] Splice(byte[] data, int start, int length, byte[] replacement)
    {
        var output = new MemoryStream();
        output.Write(data, 0, start);
        output.Write(replacement);
        output.Write(data, start + length, data.Length - start - length);
        return output.ToArray();
    }
}
