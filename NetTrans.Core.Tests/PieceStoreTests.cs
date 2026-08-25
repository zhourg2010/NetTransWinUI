using NetTrans.Tests.Fakes;
using NetTrans.Torrent;
using Xunit;

namespace NetTrans.Tests;

/// <summary>
/// Assembling and writing pieces. A peer sending bad bytes is a normal event,
/// not an exceptional one, so nothing reaches a file before it hashes right.
/// </summary>
public class PieceStoreTests
{
    [Fact]
    public async Task A_verified_piece_is_written_and_a_bad_one_is_not()
    {
        var builder = new TorrentBuilder { Name = "wanted.bin", PieceLength = 256 }.Add("wanted.bin", 512, 0x3C);
        var torrent = TorrentMetainfo.Parse(builder.Build());
        var sinks = new MemoryFileSinkFactory();

        await using var store = new PieceStore(torrent, sinks, "/downloads");

        var good = builder.Content()[..256];
        Assert.True(await store.WriteAsync(0, good, CancellationToken.None));

        var bad = (byte[])good.Clone();
        bad[7] ^= 0xFF;
        Assert.False(await store.WriteAsync(1, bad, CancellationToken.None));

        // Only the verified one landed.
        Assert.Equal(good, sinks.Files.Values.Single().ToArray()[..256]);
    }

    [Fact]
    public async Task A_piece_of_the_wrong_length_is_refused_before_it_is_hashed()
    {
        var builder = new TorrentBuilder { PieceLength = 256 }.Add("f", 512, 1);
        var torrent = TorrentMetainfo.Parse(builder.Build());

        await using var store = new PieceStore(torrent, new MemoryFileSinkFactory(), "/downloads");

        Assert.False(await store.WriteAsync(0, new byte[200], CancellationToken.None));
    }

    [Fact]
    public async Task A_piece_that_spans_two_files_is_written_into_both()
    {
        var builder = new TorrentBuilder { Name = "set", PieceLength = 256 }
            .Add("a.bin", 100, 0xA1)
            .Add("b.bin", 156, 0xB2);

        var torrent = TorrentMetainfo.Parse(builder.Build());
        var sinks = new MemoryFileSinkFactory();

        await using var store = new PieceStore(torrent, sinks, "/downloads");

        Assert.True(await store.WriteAsync(0, builder.Content(), CancellationToken.None));

        Assert.Equal(2, sinks.Files.Count);
        Assert.Equal(Enumerable.Repeat((byte)0xA1, 100), sinks.Files[store.PathOf(torrent.Files[0])].ToArray());
        Assert.Equal(Enumerable.Repeat((byte)0xB2, 156), sinks.Files[store.PathOf(torrent.Files[1])].ToArray());
    }

    [Fact]
    public async Task Each_file_is_opened_once_however_many_pieces_touch_it()
    {
        var builder = new TorrentBuilder { PieceLength = 256 }.Add("f", 1024, 7);
        var torrent = TorrentMetainfo.Parse(builder.Build());
        var sinks = new CountingSinkFactory();

        await using var store = new PieceStore(torrent, sinks, "/downloads");

        for (int piece = 0; piece < 4; piece++)
        {
            await store.WriteAsync(piece, builder.Content().AsSpan(piece * 256, 256).ToArray(), CancellationToken.None);
        }

        Assert.Equal(1, sinks.Opens);
    }

    [Fact]
    public async Task Files_are_pre_sized_so_the_disk_is_claimed_up_front()
    {
        var builder = new TorrentBuilder { PieceLength = 256 }.Add("f", 1000, 7);
        var torrent = TorrentMetainfo.Parse(builder.Build());
        var sinks = new CountingSinkFactory();

        await using var store = new PieceStore(torrent, sinks, "/downloads");
        await store.WriteAsync(0, builder.Content()[..256], CancellationToken.None);

        Assert.Equal(1000, sinks.LastLength);
    }

    // ── the block buffer ──────────────────────────────────────────────────

    [Fact]
    public void A_piece_is_assembled_out_of_sixteen_kilobyte_blocks()
    {
        var buffer = new PieceBuffer(0, PeerWire.BlockLength * 2 + 100);

        Assert.Equal(3, buffer.BlockCount);
        Assert.Equal((0, PeerWire.BlockLength), buffer.Block(0));
        Assert.Equal((PeerWire.BlockLength * 2, 100), buffer.Block(2));
    }

    [Fact]
    public void Blocks_may_arrive_in_any_order()
    {
        var buffer = new PieceBuffer(0, PeerWire.BlockLength + 10);

        Assert.True(buffer.Add(PeerWire.BlockLength, Enumerable.Repeat((byte)2, 10).ToArray()));
        Assert.False(buffer.IsComplete);

        Assert.True(buffer.Add(0, Enumerable.Repeat((byte)1, PeerWire.BlockLength).ToArray()));
        Assert.True(buffer.IsComplete);

        var assembled = buffer.ToArray();
        Assert.Equal(1, assembled[0]);
        Assert.Equal(2, assembled[^1]);
    }

    [Fact]
    public void A_duplicate_block_does_not_finish_a_piece_that_still_has_a_hole()
    {
        // Counting bytes rather than blocks would call this piece complete.
        var buffer = new PieceBuffer(0, PeerWire.BlockLength * 2);
        var block = new byte[PeerWire.BlockLength];

        buffer.Add(0, block);
        buffer.Add(0, block);

        Assert.False(buffer.IsComplete);
        Assert.Equal(new[] { 1 }, buffer.Missing());
    }

    [Theory]
    [InlineData(-1, 16)]
    [InlineData(5, 16)]                       // not on a block boundary
    [InlineData(0, PeerWire.BlockLength + 1)] // runs past the block
    public void A_block_that_does_not_line_up_is_refused(int offset, int length)
    {
        var buffer = new PieceBuffer(0, PeerWire.BlockLength * 2);

        Assert.False(buffer.Add(offset, new byte[Math.Max(0, length)]));
    }

    [Fact]
    public void A_block_that_runs_past_the_piece_is_refused()
    {
        var buffer = new PieceBuffer(0, 100);

        Assert.False(buffer.Add(0, new byte[200]));
    }

    [Fact]
    public void Missing_blocks_are_reported_in_order()
    {
        var buffer = new PieceBuffer(0, PeerWire.BlockLength * 3);

        Assert.Equal(new[] { 0, 1, 2 }, buffer.Missing());

        buffer.Add(PeerWire.BlockLength, new byte[PeerWire.BlockLength]);
        Assert.Equal(new[] { 0, 2 }, buffer.Missing());
    }

    /// <summary>Counts opens, so "each file once" can be asserted.</summary>
    private sealed class CountingSinkFactory : NetTrans.Download.IFileSinkFactory
    {
        private readonly MemoryFileSinkFactory _inner = new();

        public int Opens { get; private set; }

        public long LastLength { get; private set; }

        public ValueTask<NetTrans.Download.IFileSink> OpenAsync(string path, long length, CancellationToken cancellationToken)
        {
            Opens++;
            LastLength = length;

            return _inner.OpenAsync(path, length, cancellationToken);
        }

        public bool Exists(string path) => _inner.Exists(path);
    }
}
