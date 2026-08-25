using System.Net;
using NetTrans.Tests.Fakes;
using NetTrans.Torrent;
using Xunit;

namespace NetTrans.Tests;

/// <summary>
/// Uploading.
///
/// A client that only downloads is one a public swarm chokes and a private
/// tracker bans, so these check the thing that actually gets measured: that
/// bytes leave, that they are the right bytes, and that the number the tracker
/// is told is real.
/// </summary>
public class SeedingTests
{
    [Fact]
    public async Task A_finished_client_serves_the_pieces_it_has()
    {
        var world = new World(pieces: 4);
        world.CompleteEverything();

        world.Leech.Wants.AddRange(new[] { 0, 2 });

        await world.RunAsync();

        Assert.Equal(2, world.Leech.Received.Count);
        Assert.Equal(world.PieceBytes(0), world.Leech.Received[0]);
        Assert.Equal(world.PieceBytes(2), world.Leech.Received[2]);
    }

    [Fact]
    public async Task What_was_uploaded_is_counted_and_is_what_the_tracker_is_told()
    {
        var world = new World(pieces: 4);
        world.CompleteEverything();
        world.Leech.Wants.Add(1);

        await world.RunAsync();

        // The count is bytes actually served, not pieces claimed.
        Assert.Equal(world.Torrent.LengthOfPiece(1), world.Session.Uploaded);
    }

    [Fact]
    public async Task A_piece_we_do_not_have_is_not_served()
    {
        var world = new World(pieces: 4);
        world.Complete(0);

        // Asks for one we have and one we do not.
        world.Leech.Wants.AddRange(new[] { 0, 3 });

        await world.RunAsync(timeoutMilliseconds: 1000);

        Assert.True(world.Leech.Received.ContainsKey(0));
        Assert.False(world.Leech.Received.ContainsKey(3));
    }

    [Fact]
    public async Task A_peer_that_wants_something_is_unchoked()
    {
        var world = new World(pieces: 2);
        world.CompleteEverything();
        world.Leech.Wants.Add(0);

        await world.RunAsync();

        // The leech only sends requests after being unchoked, so receiving
        // anything at all proves the unchoke happened.
        Assert.True(world.Session.PeerIsInterested);
        Assert.Single(world.Leech.Received);
    }

    [Fact]
    public async Task A_short_last_piece_is_served_at_its_real_length()
    {
        var world = new World(pieces: 3, tail: 100);
        world.CompleteEverything();
        world.Leech.Wants.Add(2);

        await world.RunAsync();

        Assert.Equal(100, world.Leech.Received[2].Length);
        Assert.Equal(world.PieceBytes(2), world.Leech.Received[2]);
    }

    [Fact]
    public async Task A_piece_spanning_two_files_is_served_from_both()
    {
        var world = new World(multiFile: true);
        world.CompleteEverything();
        world.Leech.Wants.Add(0);

        await world.RunAsync();

        // Reassembled across the file boundary, byte for byte.
        Assert.Equal(world.PieceBytes(0), world.Leech.Received[0]);
    }

    [Fact]
    public async Task A_request_for_bytes_past_the_end_is_ignored_rather_than_answered()
    {
        var world = new World(pieces: 2);
        world.CompleteEverything();

        await world.RunRawAsync(async (stream, cancellation) =>
        {
            await stream.WriteAsync(PeerWire.Encode(PeerMessage.Interested), cancellation);

            // A length nobody could legitimately want, and an offset past the
            // piece. Both are the peer's numbers, so neither is trusted.
            await stream.WriteAsync(PeerWire.Encode(PeerMessage.Request(0, 0, 1 << 20)), cancellation);
            await stream.WriteAsync(PeerWire.Encode(PeerMessage.Request(0, 1 << 20, 16)), cancellation);
            await stream.WriteAsync(PeerWire.Encode(PeerMessage.Request(99, 0, 16)), cancellation);
        });

        // Zero because all three were refused, not because the client was
        // unwilling to serve: it unchoked us, which is the willing part.
        Assert.Equal(0, world.Session.Uploaded);
        Assert.True(world.Session.PeerIsInterested);
    }

    /// <summary>A client that has the torrent, and a peer that wants some of it.</summary>
    private sealed class World
    {
        private readonly MemoryFileSinkFactory _sinks = new();
        private readonly TorrentBuilder _builder;
        private readonly byte[] _content;

        public World(int pieces = 4, long pieceLength = 256, int tail = 0, bool multiFile = false)
        {
            _builder = new TorrentBuilder { Name = "shared", PieceLength = pieceLength };

            int length = (int)(pieceLength * (pieces - 1)) + (tail > 0 ? tail : (int)pieceLength);

            _content = new byte[length];
            for (int i = 0; i < length; i++) _content[i] = (byte)(i * 37 % 251);

            if (multiFile)
            {
                // A piece that straddles the boundary, so serving has to read
                // from two files the way writing had to write to two.
                _builder.Add("a.bin", _content[..100]);
                _builder.Add("b.bin", _content[100..]);
            }
            else
            {
                _builder.Add("shared.bin", _content);
            }

            Torrent = TorrentMetainfo.Parse(_builder.Build());
            Picker = new PiecePicker(Torrent.PieceCount);
            Store = new PieceStore(Torrent, _sinks, "/downloads");
            Leech = new FakeLeech(Torrent);
        }

        public TorrentMetainfo Torrent { get; }

        public PiecePicker Picker { get; }

        public PieceStore Store { get; }

        public FakeLeech Leech { get; }

        public PeerSession Session { get; private set; } = null!;

        public byte[] PieceBytes(int index) =>
            _content.AsSpan((int)(index * Torrent.PieceLength), (int)Torrent.LengthOfPiece(index)).ToArray();

        /// <summary>Puts the whole torrent on disk, as a finished download would have.</summary>
        public void CompleteEverything()
        {
            for (int piece = 0; piece < Torrent.PieceCount; piece++) Complete(piece);
        }

        /// <summary>
        /// Writes a piece and marks it done. Both halves matter: marking it done
        /// without writing it would have the client serve a pre-sized file full
        /// of zeros, and the test would pass on bytes that are not the content.
        /// </summary>
        public void Complete(int piece)
        {
            Assert.True(Store.WriteAsync(piece, PieceBytes(piece), CancellationToken.None).GetAwaiter().GetResult());
            Picker.Complete(piece);
        }

        public Task RunAsync(int timeoutMilliseconds = 3000) =>
            RunWithAsync((stream, cancellation) => Leech.LeechAsync(stream, Torrent.InfoHash, cancellation), timeoutMilliseconds);

        /// <summary>Drives the far end by hand, for the cases a well-behaved peer would not produce.</summary>
        public Task RunRawAsync(Func<Stream, CancellationToken, Task> peer, int timeoutMilliseconds = 1000) =>
            RunWithAsync(async (stream, cancellation) =>
            {
                await PeerWire.ReadExactAsync(stream, PeerWire.HandshakeLength, cancellation);

                await stream.WriteAsync(
                    PeerWire.BuildHandshake(Torrent.InfoHash, Enumerable.Repeat((byte)'R', 20).ToArray()),
                    cancellation);

                await stream.WriteAsync(
                    PeerWire.Encode(PeerMessage.Bitfield(new byte[PeerWire.BitfieldLength(Torrent.PieceCount)])),
                    cancellation);

                await peer(stream, cancellation);

                // Hold the connection open long enough for the client to have
                // answered, if it was going to.
                await Task.Delay(300, cancellation);
            }, timeoutMilliseconds);

        private async Task RunWithAsync(Func<Stream, CancellationToken, Task> peer, int timeoutMilliseconds)
        {
            var (ours, theirs) = DuplexPipe.Create();

            using var cancellation = new CancellationTokenSource(timeoutMilliseconds);

            var far = Task.Run(async () =>
            {
                try
                {
                    await peer(theirs, cancellation.Token);
                }
                catch (Exception)
                {
                    // The client hung up or the deadline passed.
                }
                finally
                {
                    // Seeding has no natural end, so the peer leaving is what
                    // stops the session -- otherwise every test here would wait
                    // out its whole deadline.
                    await theirs.DisposeAsync();
                }
            }, CancellationToken.None);

            Session = new PeerSession(ours, Torrent, Picker, Store, new IPEndPoint(IPAddress.Loopback, 6881));

            try
            {
                await Session.RunAsync(Torrent.InfoHash, TrackerProtocol.NewPeerId(), cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                // Seeding has no natural end; the deadline is how it stops.
            }
            catch (PeerException)
            {
                // The far end closed first.
            }
            finally
            {
                await far;
                cancellation.Cancel();
                await ours.DisposeAsync();
            }
        }
    }
}
