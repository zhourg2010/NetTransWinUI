using System.Security.Cryptography;
using NetTrans.Tests.Fakes;
using NetTrans.Torrent;
using Xunit;

namespace NetTrans.Tests;

/// <summary>
/// BEP 9. A magnet names a torrent and nothing else, so until this works there
/// is no file list, no piece count and nothing that can be verified -- which
/// makes it the one part of BitTorrent a magnet link cannot do without.
/// </summary>
public class MetadataExchangeTests
{
    [Fact]
    public void Our_handshake_offers_ut_metadata_under_a_number()
    {
        var message = MetadataExchange.Handshake();

        Assert.Equal(PeerMessageKind.Extended, message.Kind);
        Assert.Equal(MetadataExchange.HandshakeId, message.Payload[0]);

        var body = Bencode.DecodeDictionary(message.Payload.AsSpan(1).ToArray());
        Assert.Equal(MetadataExchange.OurMetadataId, body.Dictionary("m")!.Number("ut_metadata"));
    }

    [Fact]
    public void A_peers_handshake_gives_up_its_number_and_the_size()
    {
        var theirs = PeerMessage.Extended(0, Bencode.Encode(Bencode.Dictionary(
            ("m", Bencode.Dictionary(("ut_metadata", Bencode.Number(3)))),
            ("metadata_size", Bencode.Number(40_000)))));

        var capabilities = MetadataExchange.ReadHandshake(theirs.Payload);

        Assert.True(capabilities.SupportsMetadata);
        Assert.Equal(3, capabilities.MetadataId);
        Assert.Equal(40_000, capabilities.MetadataSize);
    }

    [Fact]
    public void A_peer_that_does_not_speak_it_says_so_by_omission()
    {
        var theirs = PeerMessage.Extended(0, Bencode.Encode(Bencode.Dictionary(
            ("m", Bencode.Dictionary(("ut_pex", Bencode.Number(2)))))));

        Assert.False(MetadataExchange.ReadHandshake(theirs.Payload).SupportsMetadata);
    }

    [Fact]
    public void A_handshake_that_is_not_bencode_is_survivable()
    {
        // Not a reason to drop the connection: the peer is still fine for
        // blocks, it just cannot be talked to about extensions.
        var nonsense = PeerMessage.Extended(0, new byte[] { 0xFF, 0xFE, 0xFD });

        Assert.False(MetadataExchange.ReadHandshake(nonsense.Payload).SupportsMetadata);
        Assert.False(MetadataExchange.ReadHandshake(Array.Empty<byte>()).SupportsMetadata);
    }

    [Fact]
    public void A_size_a_peer_made_up_is_refused_before_it_is_allocated()
    {
        var absurd = PeerMessage.Extended(0, Bencode.Encode(Bencode.Dictionary(
            ("m", Bencode.Dictionary(("ut_metadata", Bencode.Number(1)))),
            ("metadata_size", Bencode.Number(int.MaxValue)))));

        Assert.Equal(0, MetadataExchange.ReadHandshake(absurd.Payload).MetadataSize);
    }

    [Fact]
    public void A_data_message_carries_raw_bytes_straight_after_its_dictionary()
    {
        // The awkward part of BEP 9: there is no length between the bencoded
        // header and the payload, so the only way to know where the bytes
        // start is to note where the dictionary ended.
        var bytes = Enumerable.Range(0, 300).Select(n => (byte)n).ToArray();
        var message = MetadataExchange.SendPiece(1, piece: 2, totalSize: 40_000, bytes);

        var incoming = MetadataExchange.Read(message.Payload)!;

        Assert.True(incoming.IsData);
        Assert.Equal(2, incoming.Piece);
        Assert.Equal(40_000, incoming.TotalSize);
        Assert.Equal(bytes, incoming.Bytes);
    }

    [Fact]
    public void Requests_and_rejects_round_trip()
    {
        var request = MetadataExchange.Read(MetadataExchange.RequestPiece(1, 7).Payload)!;
        Assert.True(request.IsRequest);
        Assert.Equal(7, request.Piece);

        var reject = MetadataExchange.Read(MetadataExchange.RejectPiece(1, 4).Payload)!;
        Assert.True(reject.IsReject);
        Assert.Equal(4, reject.Piece);
    }

    [Fact]
    public void A_message_that_is_not_ut_metadata_is_ignored_rather_than_guessed_at()
    {
        Assert.Null(MetadataExchange.Read(new byte[] { 1, 0xFF }));
        Assert.Null(MetadataExchange.Read(Array.Empty<byte>()));

        // Bencode, but not a ut_metadata header.
        var wrong = PeerMessage.Extended(1, Bencode.Encode(Bencode.Dictionary(("hello", Bencode.Number(1)))));
        Assert.Null(MetadataExchange.Read(wrong.Payload));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(16384, 1)]
    [InlineData(16385, 2)]
    [InlineData(40000, 3)]
    public void Metadata_is_cut_into_sixteen_kilobyte_pieces(int size, int pieces) =>
        Assert.Equal(pieces, MetadataExchange.PieceCount(size));

    // ── the buffer ────────────────────────────────────────────────────────

    [Fact]
    public void The_buffer_assembles_pieces_and_hands_back_the_metainfo()
    {
        var builder = new TorrentBuilder { Name = "wanted.bin" }.Add("wanted.bin", 4096, 0x7A);
        byte[] info = builder.InfoDictionary();

        var buffer = new MetadataBuffer(builder.InfoHash());
        Assert.True(buffer.Size(info.Length));

        foreach (int piece in buffer.Missing().ToList())
        {
            int offset = piece * MetadataExchange.PieceLength;
            int length = Math.Min(MetadataExchange.PieceLength, info.Length - offset);

            Assert.True(buffer.Add(piece, info.AsSpan(offset, length)));
        }

        Assert.True(buffer.IsComplete);
        Assert.True(buffer.Verifies());

        var torrent = buffer.Build(Array.Empty<Uri>())!;
        Assert.Equal("wanted.bin", torrent.Name);
        Assert.Equal(4096, torrent.TotalLength);
    }

    [Fact]
    public void Metadata_that_does_not_hash_is_refused_rather_than_used()
    {
        var builder = new TorrentBuilder { Name = "wanted.bin" }.Add("wanted.bin", 2048, 3);
        byte[] info = builder.InfoDictionary();

        // Pieces can come from several peers, so a whole that does not hash
        // means one of them lied and there is no way to tell which.
        var wrong = (byte[])builder.InfoHash().Clone();
        wrong[5] ^= 0xFF;

        var buffer = new MetadataBuffer(wrong);
        buffer.Size(info.Length);
        buffer.Add(0, info);

        Assert.True(buffer.IsComplete);
        Assert.False(buffer.Verifies());
        Assert.Throws<NotSupportedException>(() => buffer.Build(Array.Empty<Uri>()));
    }

    [Fact]
    public void A_piece_of_the_wrong_length_is_refused()
    {
        var buffer = new MetadataBuffer(new byte[20]);
        buffer.Size(MetadataExchange.PieceLength * 2);

        // Every piece is full except the last.
        Assert.False(buffer.Add(0, new byte[100]));
        Assert.True(buffer.Add(0, new byte[MetadataExchange.PieceLength]));
        Assert.False(buffer.Add(5, new byte[MetadataExchange.PieceLength]));
    }

    [Fact]
    public void The_last_piece_is_short_and_that_is_the_only_one_that_may_be()
    {
        var buffer = new MetadataBuffer(new byte[20]);
        buffer.Size(MetadataExchange.PieceLength + 500);

        Assert.True(buffer.Add(1, new byte[500]));
        Assert.False(buffer.Add(0, new byte[500]));
    }

    [Fact]
    public void A_size_offered_twice_has_to_agree()
    {
        var buffer = new MetadataBuffer(new byte[20]);

        Assert.True(buffer.Size(1000));
        Assert.True(buffer.Size(1000));
        Assert.False(buffer.Size(2000));
    }

    [Fact]
    public void An_absurd_size_is_refused()
    {
        var buffer = new MetadataBuffer(new byte[20]);

        Assert.False(buffer.Size(0));
        Assert.False(buffer.Size(-1));
        Assert.False(buffer.Size(MetadataExchange.MaxMetadataBytes + 1));
        Assert.False(buffer.IsSized);
    }

    [Fact]
    public void Nothing_is_built_before_every_piece_has_arrived()
    {
        var buffer = new MetadataBuffer(new byte[20]);
        buffer.Size(MetadataExchange.PieceLength * 2);
        buffer.Add(0, new byte[MetadataExchange.PieceLength]);

        Assert.False(buffer.IsComplete);
        Assert.Null(buffer.Build(Array.Empty<Uri>()));
        Assert.Equal(new[] { 1 }, buffer.Missing());
    }

    [Fact]
    public void Resetting_throws_away_everything_collected()
    {
        var buffer = new MetadataBuffer(new byte[20]);
        buffer.Size(1000);
        buffer.Add(0, new byte[1000]);

        buffer.Reset();

        Assert.False(buffer.IsSized);
        Assert.False(buffer.IsComplete);
        Assert.Empty(buffer.Missing());
    }

    // ── the whole conversation ────────────────────────────────────────────

    [Fact]
    public async Task A_magnet_gets_its_metainfo_from_a_peer()
    {
        var builder = new TorrentBuilder { Name = "film.mkv" }
            .Add("film.mkv", 100_000, 0x22);

        // Big enough to need several metadata pieces, which is the case worth
        // exercising: one piece would never show a reassembly bug.
        var torrent = await FetchAsync(builder);

        Assert.Equal("film.mkv", torrent.Name);
        Assert.Equal(100_000, torrent.TotalLength);
        Assert.Equal(builder.InfoHash(), torrent.InfoHash);
    }

    [Fact]
    public async Task A_multi_piece_metadata_is_reassembled_in_order()
    {
        // A torrent with enough files that its info dictionary spans several
        // 16 KiB metadata pieces.
        var builder = new TorrentBuilder { Name = "collection", PieceLength = 1024 };

        for (int i = 0; i < 400; i++) builder.Add($"disc{i / 20}/track-{i:D3}.flac", 1024, (byte)i);

        byte[] info = builder.InfoDictionary();
        Assert.True(info.Length > MetadataExchange.PieceLength, $"metadata is only {info.Length} bytes");

        var torrent = await FetchAsync(builder);

        Assert.Equal(400, torrent.Files.Count);
        Assert.Equal(builder.InfoHash(), torrent.InfoHash);
    }

    [Fact]
    public async Task A_peer_offering_metadata_for_a_different_torrent_is_refused()
    {
        var builder = new TorrentBuilder { Name = "wanted.bin" }.Add("wanted.bin", 2048, 1);
        var other = new TorrentBuilder { Name = "other.bin" }.Add("other.bin", 4096, 2);

        // The peer handshakes with our hash but serves someone else's info.
        var error = await Assert.ThrowsAsync<NotSupportedException>(
            () => FetchAsync(builder, serve: other));

        Assert.Contains("哈希", error.Message);
    }

    [Fact]
    public async Task A_peer_without_the_extension_is_no_use_for_a_magnet()
    {
        var builder = new TorrentBuilder { Name = "wanted.bin" }.Add("wanted.bin", 2048, 1);

        var error = await Assert.ThrowsAsync<PeerException>(
            () => FetchAsync(builder, supportsExtended: false));

        Assert.Contains("扩展协议", error.Message);
    }

    /// <summary>Runs a metadata fetch against a peer that has the torrent.</summary>
    private static async Task<TorrentMetainfo> FetchAsync(
        TorrentBuilder builder,
        TorrentBuilder? serve = null,
        bool supportsExtended = true)
    {
        byte[] infoHash = builder.InfoHash();
        byte[] info = (serve ?? builder).InfoDictionary();

        var (ours, theirs) = DuplexPipe.Create();
        using var cancellation = new CancellationTokenSource(5000);

        var peer = Task.Run(
            () => ServeMetadataAsync(theirs, infoHash, info, supportsExtended, cancellation.Token),
            CancellationToken.None);

        var buffer = new MetadataBuffer(infoHash);
        var session = new MetadataSession(ours, buffer);

        try
        {
            await session.RunAsync(infoHash, TrackerProtocol.NewPeerId(), cancellation.Token);
        }
        finally
        {
            cancellation.Cancel();
            await ours.DisposeAsync();
            await peer;
        }

        return buffer.Build(Array.Empty<Uri>())!;
    }

    /// <summary>A peer that has the metainfo and will hand it over.</summary>
    private static async Task ServeMetadataAsync(
        Stream stream,
        byte[] infoHash,
        byte[] info,
        bool supportsExtended,
        CancellationToken cancellationToken)
    {
        try
        {
            await PeerWire.ReadExactAsync(stream, PeerWire.HandshakeLength, cancellationToken).ConfigureAwait(false);

            await stream.WriteAsync(
                PeerWire.BuildHandshake(infoHash, Enumerable.Repeat((byte)'M', 20).ToArray(), supportsExtended),
                cancellationToken).ConfigureAwait(false);

            if (!supportsExtended) return;

            const byte ourId = 42;

            await Send(stream, PeerMessage.Extended(0, Bencode.Encode(Bencode.Dictionary(
                ("m", Bencode.Dictionary(("ut_metadata", Bencode.Number(ourId)))),
                ("metadata_size", Bencode.Number(info.Length))))), cancellationToken).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await PeerWire.ReadMessageAsync(stream, cancellationToken).ConfigureAwait(false);
                if (message.Kind != PeerMessageKind.Extended) continue;

                var incoming = MetadataExchange.Read(message.Payload);
                if (incoming is null || !incoming.IsRequest) continue;

                int offset = incoming.Piece * MetadataExchange.PieceLength;
                if (offset >= info.Length) continue;

                int length = Math.Min(MetadataExchange.PieceLength, info.Length - offset);

                await Send(
                    stream,
                    MetadataExchange.SendPiece(ourId, incoming.Piece, info.Length, info.AsSpan(offset, length)),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // The client hung up, which is how every one of these ends.
        }
    }

    private static async Task Send(Stream stream, PeerMessage message, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(PeerWire.Encode(message), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
