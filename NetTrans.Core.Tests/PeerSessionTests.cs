using System.Net;
using NetTrans.Tests.Fakes;
using NetTrans.Torrent;
using Xunit;

namespace NetTrans.Tests;

/// <summary>
/// A whole conversation with a peer, over an in-memory pipe. This is where the
/// wire format, the picker and the store are checked against each other rather
/// than one at a time.
/// </summary>
public class PeerSessionTests
{
    [Fact]
    public async Task A_session_downloads_the_whole_torrent_from_one_seed()
    {
        var world = new World(pieces: 6);

        await world.RunAsync();

        Assert.True(world.Picker.IsComplete);
        Assert.Equal(world.Content, world.Written());
    }

    [Fact]
    public async Task A_piece_larger_than_one_block_is_fetched_in_blocks()
    {
        // 40 KiB pieces are three blocks each, which is what the pipeline is for.
        var world = new World(pieces: 3, pieceLength: PeerWire.BlockLength * 2 + 1000);

        await world.RunAsync();

        Assert.Equal(world.Content, world.Written());
        Assert.True(world.Seed.BlocksServed >= 9, $"expected at least 9 blocks, served {world.Seed.BlocksServed}");
    }

    [Fact]
    public async Task A_short_last_piece_is_fetched_at_its_real_length()
    {
        // The last piece is short unless the total divides evenly; asking for a
        // full one would hang waiting for bytes that do not exist.
        var world = new World(pieces: 3, pieceLength: 256, tail: 100);

        await world.RunAsync();

        Assert.True(world.Picker.IsComplete);
        Assert.Equal(world.Content, world.Written());
    }

    [Fact]
    public async Task A_peer_that_only_has_some_pieces_is_asked_only_for_those()
    {
        var world = new World(pieces: 6);
        world.Seed.Has = new[] { 0, 1, 2 };

        await world.RunAsync();

        // It finishes what it can and then stops, rather than waiting forever.
        Assert.Equal(3, world.Picker.CompletedCount);
        Assert.True(world.Picker.IsDone(0));
        Assert.False(world.Picker.IsDone(3));
    }

    [Fact]
    public async Task A_peer_announcing_by_have_messages_works_the_same()
    {
        var world = new World(pieces: 4);
        world.Seed.UseHaveMessages = true;

        await world.RunAsync();

        Assert.True(world.Picker.IsComplete);
    }

    [Fact]
    public async Task A_piece_that_does_not_hash_is_never_written()
    {
        var world = new World(pieces: 4);
        world.Seed.Corrupt.Add(3);

        // With every other piece done, the only one left is the bad one, so the
        // peer runs out of chances and is dropped. That is the point.
        await world.RunAsync(timeoutMilliseconds: 2000, expectFailure: true);

        // The good pieces landed; the corrupt one never verified, so it was
        // never written and never counted.
        Assert.True(world.Picker.IsDone(0));
        Assert.True(world.Picker.IsDone(1));
        Assert.True(world.Picker.IsDone(2));
        Assert.False(world.Picker.IsDone(3));
        Assert.True(world.Session.BadPieces > 0);
    }

    [Fact]
    public async Task A_peer_that_keeps_sending_bad_pieces_is_dropped()
    {
        // Retrying from the same peer forever would stall the torrent on one
        // piece while every other peer waits for it.
        var world = new World(pieces: 6);
        world.Seed.Corrupt.Add(2);
        world.Seed.Corrupt.Add(3);

        await world.RunAsync(timeoutMilliseconds: 2000, expectFailure: true);

        Assert.Equal(2, world.Session.BadPieces);

        // And the pieces it botched went back on offer for someone else.
        Assert.False(world.Picker.IsDone(2));
        Assert.Equal(2, world.Picker.Take(BitfieldOf(6, 2)));
    }

    [Fact]
    public async Task A_peer_on_the_wrong_torrent_is_refused()
    {
        var world = new World(pieces: 2);

        var wrong = (byte[])world.Torrent.InfoHash.Clone();
        wrong[0] ^= 0xFF;

        var error = await Assert.ThrowsAsync<PeerException>(() => world.RunAsync(infoHash: wrong));
        Assert.Contains("info_hash", error.Message);
    }

    [Fact]
    public async Task Pieces_a_previous_run_finished_are_not_asked_for_again()
    {
        var world = new World(pieces: 4);
        world.Picker.Restore(BitfieldOf(4, 0, 1));

        await world.RunAsync();

        Assert.True(world.Picker.IsComplete);

        // Only the two that were missing were fetched.
        Assert.Equal(2, world.Seed.BlocksServed);
    }

    [Fact]
    public async Task A_reserved_piece_goes_back_when_the_session_ends()
    {
        var world = new World(pieces: 4);

        // The seed never unchokes and never answers, so the session is
        // cancelled holding a piece.
        world.Seed.Has = Array.Empty<int>();

        await world.RunAsync(timeoutMilliseconds: 500);

        // Nothing was reserved and left dangling: every piece is still on offer.
        var all = BitfieldOf(4, 0, 1, 2, 3);
        var offered = new List<int>();

        for (int piece = world.Picker.Take(all); piece >= 0; piece = world.Picker.Take(all)) offered.Add(piece);

        Assert.Equal(new[] { 0, 1, 2, 3 }, offered);
    }

    private static byte[] BitfieldOf(int pieces, params int[] have)
    {
        var bits = new byte[PeerWire.BitfieldLength(pieces)];
        foreach (int piece in have) PeerWire.SetPiece(bits, piece);

        return bits;
    }

    /// <summary>A torrent, a seed that has it, and a client wired to each other.</summary>
    private sealed class World
    {
        private readonly MemoryFileSinkFactory _sinks = new();
        private readonly TorrentBuilder _builder;

        public World(int pieces, long pieceLength = 256, int tail = 0)
        {
            _builder = new TorrentBuilder { Name = "wanted.bin", PieceLength = pieceLength };

            int length = (int)(pieceLength * (pieces - 1)) + (tail > 0 ? tail : (int)pieceLength);

            // Content that differs per byte, so a piece written to the wrong
            // offset shows up as a mismatch rather than as identical filler.
            var content = new byte[length];
            for (int i = 0; i < length; i++) content[i] = (byte)(i * 31 % 251);

            _builder.Add("wanted.bin", content);

            Torrent = TorrentMetainfo.Parse(_builder.Build());
            Picker = new PiecePicker(Torrent.PieceCount);
            Store = new PieceStore(Torrent, _sinks, "/downloads");
            Seed = new FakeSeed(Torrent, content);
        }

        public TorrentMetainfo Torrent { get; }

        public PiecePicker Picker { get; }

        public PieceStore Store { get; }

        public FakeSeed Seed { get; }

        public PeerSession Session { get; private set; } = null!;

        public byte[] Content => _builder.Content();

        public byte[] Written() => _sinks.Files.Values.Single().ToArray();

        public async Task RunAsync(int timeoutMilliseconds = 5000, byte[]? infoHash = null, bool expectFailure = false)
        {
            var (ours, theirs) = DuplexPipe.Create();

            using var cancellation = new CancellationTokenSource(timeoutMilliseconds);

            var serving = Task.Run(
                () => Seed.ServeAsync(theirs, Torrent.InfoHash, cancellation.Token),
                CancellationToken.None);

            Session = new PeerSession(ours, Torrent, Picker, Store, new IPEndPoint(IPAddress.Loopback, 6881));

            try
            {
                await Session.RunAsync(
                    infoHash ?? Torrent.InfoHash,
                    TrackerProtocol.NewPeerId(),
                    cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                // A seed that has nothing: the deadline is how that test ends.
            }
            catch (PeerException) when (expectFailure)
            {
                // A peer dropped for sending bad pieces, which is the point.
            }
            finally
            {
                cancellation.Cancel();
                await ours.DisposeAsync();
                await serving;
                await Store.FlushAsync(CancellationToken.None);
            }
        }
    }
}
