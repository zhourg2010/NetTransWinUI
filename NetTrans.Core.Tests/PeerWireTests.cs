using System.Buffers.Binary;
using System.Text;
using NetTrans.Torrent;
using Xunit;

namespace NetTrans.Tests;

/// <summary>
/// The peer wire format. Two things here are easy to get subtly wrong and hard
/// to notice: which way round the bits of a bitfield read, and trusting a
/// length a peer sent us.
/// </summary>
public class PeerWireTests
{
    private static readonly byte[] InfoHash = Enumerable.Range(0, 20).Select(n => (byte)n).ToArray();
    private static readonly byte[] PeerId = Enumerable.Repeat((byte)'P', 20).ToArray();

    [Fact]
    public void A_handshake_is_sixty_eight_bytes_in_the_order_the_protocol_says()
    {
        var packet = PeerWire.BuildHandshake(InfoHash, PeerId);

        Assert.Equal(68, packet.Length);
        Assert.Equal(19, packet[0]);
        Assert.Equal("BitTorrent protocol", Encoding.ASCII.GetString(packet, 1, 19));
        Assert.Equal(InfoHash, packet.AsSpan(28, 20).ToArray());
        Assert.Equal(PeerId, packet.AsSpan(48, 20).ToArray());
    }

    [Fact]
    public void A_handshake_round_trips()
    {
        var parsed = PeerWire.ParseHandshake(PeerWire.BuildHandshake(InfoHash, PeerId));

        Assert.Equal(InfoHash, parsed.InfoHash);
        Assert.Equal(PeerId, parsed.PeerId);
        Assert.True(parsed.SupportsExtended);
    }

    [Fact]
    public void The_extended_capability_is_one_reserved_bit_and_is_optional()
    {
        Assert.False(PeerWire.ParseHandshake(PeerWire.BuildHandshake(InfoHash, PeerId, supportsExtended: false)).SupportsExtended);

        // BEP 10 is bit 44: 0x10 of reserved byte 5. The reserved block starts
        // at 20, after the length byte and the nineteen of the protocol name --
        // putting it at 9 lands inside the name and corrupts the handshake.
        var packet = PeerWire.BuildHandshake(InfoHash, PeerId);

        Assert.Equal(0x10, packet[20 + 5]);
        Assert.Equal("BitTorrent protocol", Encoding.ASCII.GetString(packet, 1, 19));
    }

    [Fact]
    public void Something_that_is_not_a_bittorrent_handshake_is_refused()
    {
        var packet = PeerWire.BuildHandshake(InfoHash, PeerId);
        packet[1] = (byte)'X';

        Assert.Throws<PeerException>(() => PeerWire.ParseHandshake(packet));
        Assert.Throws<PeerException>(() => PeerWire.ParseHandshake(new byte[10]));
    }

    [Fact]
    public void A_message_goes_out_as_length_then_id_then_payload()
    {
        var packet = PeerWire.Encode(PeerMessage.Have(258));

        Assert.Equal(9, packet.Length);
        Assert.Equal(5, BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(0)));
        Assert.Equal((byte)PeerMessageKind.Have, packet[4]);
        Assert.Equal(258, BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(5)));
    }

    [Fact]
    public void A_keep_alive_is_four_zero_bytes_and_nothing_else()
    {
        Assert.Equal(new byte[4], PeerWire.Encode(PeerMessage.KeepAlive));
    }

    [Fact]
    public async Task Messages_round_trip_through_a_stream()
    {
        var sent = new PeerMessage[]
        {
            PeerMessage.KeepAlive,
            PeerMessage.Unchoke,
            PeerMessage.Interested,
            PeerMessage.Have(7),
            PeerMessage.Request(3, PeerWire.BlockLength, 1024),
            PeerMessage.Piece(3, 0, new byte[] { 1, 2, 3 }),
        };

        var wire = new MemoryStream();
        foreach (var message in sent) wire.Write(PeerWire.Encode(message));
        wire.Position = 0;

        foreach (var expected in sent)
        {
            var actual = await PeerWire.ReadMessageAsync(wire, CancellationToken.None);

            Assert.Equal(expected.Kind, actual.Kind);
            Assert.Equal(expected.Payload, actual.Payload);
        }
    }

    [Fact]
    public void A_request_carries_its_three_numbers_where_they_are_read_from()
    {
        var request = PeerMessage.Request(5, 32768, 16384);

        Assert.Equal(5, request.PieceIndex);
        Assert.Equal(32768, request.BlockOffset);
        Assert.Equal(16384, request.BlockLength);
    }

    [Fact]
    public void A_piece_message_carries_its_block_after_the_two_headers()
    {
        var block = new byte[] { 9, 8, 7, 6 };
        var message = PeerMessage.Piece(2, 16384, block);

        Assert.Equal(2, message.PieceIndex);
        Assert.Equal(16384, message.BlockOffset);
        Assert.Equal(block, message.Block.ToArray());
    }

    [Fact]
    public async Task A_length_a_peer_made_up_is_refused_before_it_is_allocated()
    {
        // The length is the peer's. A client that trusts it can be made to
        // allocate two gigabytes by a six-byte message.
        var wire = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, int.MaxValue);
        wire.Write(header);
        wire.Position = 0;

        await Assert.ThrowsAsync<PeerException>(() => PeerWire.ReadMessageAsync(wire, CancellationToken.None));
    }

    [Fact]
    public async Task A_peer_that_hangs_up_mid_message_ends_the_connection()
    {
        var wire = new MemoryStream(new byte[] { 0, 0, 0, 20, 4 });

        await Assert.ThrowsAsync<PeerException>(() => PeerWire.ReadMessageAsync(wire, CancellationToken.None));
    }

    [Fact]
    public void The_high_bit_of_the_first_byte_is_piece_zero()
    {
        // The opposite of how a bit index usually reads, and getting it
        // backwards means asking every peer for pieces it does not have.
        var bitfield = new byte[] { 0b1000_0001, 0b0100_0000 };

        Assert.True(PeerWire.HasPiece(bitfield, 0));
        Assert.False(PeerWire.HasPiece(bitfield, 1));
        Assert.True(PeerWire.HasPiece(bitfield, 7));
        Assert.True(PeerWire.HasPiece(bitfield, 9));
    }

    [Fact]
    public void Setting_and_reading_a_bit_agree()
    {
        var bitfield = new byte[PeerWire.BitfieldLength(20)];

        PeerWire.SetPiece(bitfield, 0);
        PeerWire.SetPiece(bitfield, 13);
        PeerWire.SetPiece(bitfield, 19);

        for (int i = 0; i < 20; i++)
        {
            Assert.Equal(i is 0 or 13 or 19, PeerWire.HasPiece(bitfield, i));
        }
    }

    [Fact]
    public void An_index_outside_the_bitfield_is_simply_absent()
    {
        var bitfield = new byte[] { 0xFF };

        Assert.False(PeerWire.HasPiece(bitfield, 8));
        Assert.False(PeerWire.HasPiece(bitfield, -1));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(8, 1)]
    [InlineData(9, 2)]
    [InlineData(16, 2)]
    [InlineData(17, 3)]
    public void A_bitfield_is_one_bit_per_piece_rounded_up(int pieces, int bytes) =>
        Assert.Equal(bytes, PeerWire.BitfieldLength(pieces));

    [Fact]
    public void A_bitfield_with_bits_set_past_the_last_piece_is_refused()
    {
        // A peer that sets the spare bits is broken, and believing it means
        // asking for pieces that do not exist.
        Assert.True(PeerWire.IsValidBitfield(new byte[] { 0b1111_1000 }, pieces: 5));
        Assert.False(PeerWire.IsValidBitfield(new byte[] { 0b1111_1100 }, pieces: 5));
        Assert.False(PeerWire.IsValidBitfield(new byte[] { 0xFF, 0xFF }, pieces: 5));
        Assert.False(PeerWire.IsValidBitfield(Array.Empty<byte>(), pieces: 5));
    }
}
