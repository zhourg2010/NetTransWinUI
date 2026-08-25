using System.Buffers.Binary;
using System.Text;

namespace NetTrans.Torrent;

/// <summary>The message ids of the peer wire protocol.</summary>
public enum PeerMessageKind
{
    /// <summary>A zero-length message, sent to keep an idle connection open.</summary>
    KeepAlive = -1,

    Choke = 0,
    Unchoke = 1,
    Interested = 2,
    NotInterested = 3,
    Have = 4,
    Bitfield = 5,
    Request = 6,
    Piece = 7,
    Cancel = 8,
    Port = 9,

    /// <summary>BEP 10, which is how metadata is fetched for a magnet link.</summary>
    Extended = 20,
}

/// <summary>One message, with whatever payload its kind carries.</summary>
public sealed record PeerMessage(PeerMessageKind Kind, byte[] Payload)
{
    public static PeerMessage KeepAlive { get; } = new(PeerMessageKind.KeepAlive, Array.Empty<byte>());

    public static PeerMessage Choke { get; } = new(PeerMessageKind.Choke, Array.Empty<byte>());

    public static PeerMessage Unchoke { get; } = new(PeerMessageKind.Unchoke, Array.Empty<byte>());

    public static PeerMessage Interested { get; } = new(PeerMessageKind.Interested, Array.Empty<byte>());

    public static PeerMessage NotInterested { get; } = new(PeerMessageKind.NotInterested, Array.Empty<byte>());

    public static PeerMessage Have(int piece)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(payload, piece);
        return new PeerMessage(PeerMessageKind.Have, payload);
    }

    public static PeerMessage Bitfield(byte[] bits) => new(PeerMessageKind.Bitfield, bits);

    public static PeerMessage Request(int piece, int offset, int length) =>
        new(PeerMessageKind.Request, BlockHeader(piece, offset, length));

    public static PeerMessage Cancel(int piece, int offset, int length) =>
        new(PeerMessageKind.Cancel, BlockHeader(piece, offset, length));

    public static PeerMessage Piece(int piece, int offset, ReadOnlySpan<byte> block)
    {
        var payload = new byte[8 + block.Length];
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0), piece);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4), offset);
        block.CopyTo(payload.AsSpan(8));

        return new PeerMessage(PeerMessageKind.Piece, payload);
    }

    public static PeerMessage Extended(byte id, byte[] body)
    {
        var payload = new byte[1 + body.Length];
        payload[0] = id;
        body.CopyTo(payload.AsSpan(1));

        return new PeerMessage(PeerMessageKind.Extended, payload);
    }

    /// <summary>The piece index a have / request / piece / cancel is about.</summary>
    public int PieceIndex => Payload.Length >= 4 ? BinaryPrimitives.ReadInt32BigEndian(Payload.AsSpan(0)) : -1;

    /// <summary>The offset within the piece, for a request / piece / cancel.</summary>
    public int BlockOffset => Payload.Length >= 8 ? BinaryPrimitives.ReadInt32BigEndian(Payload.AsSpan(4)) : -1;

    /// <summary>The requested length, for a request / cancel.</summary>
    public int BlockLength => Payload.Length >= 12 ? BinaryPrimitives.ReadInt32BigEndian(Payload.AsSpan(8)) : -1;

    /// <summary>The bytes of a piece message, which start after its two headers.</summary>
    public ReadOnlySpan<byte> Block => Payload.Length >= 8 ? Payload.AsSpan(8) : ReadOnlySpan<byte>.Empty;

    private static byte[] BlockHeader(int piece, int offset, int length)
    {
        var payload = new byte[12];
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0), piece);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4), offset);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(8), length);

        return payload;
    }
}

/// <summary>What a peer said in its handshake.</summary>
/// <param name="InfoHash">Which torrent it thinks this connection is about.</param>
/// <param name="PeerId">Its own id.</param>
/// <param name="SupportsExtended">BEP 10, which a magnet link needs to fetch metadata.</param>
public sealed record PeerHandshake(byte[] InfoHash, byte[] PeerId, bool SupportsExtended);

/// <summary>The peer refused, disconnected, or said something that is not the protocol.</summary>
public sealed class PeerException : Exception
{
    public PeerException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>
/// The framing of the peer wire protocol: a fixed handshake, then
/// length-prefixed messages until someone hangs up.
///
/// Kept apart from the connection that speaks it so the wire format can be
/// tested against bytes rather than against a socket.
/// </summary>
public static class PeerWire
{
    /// <summary>The protocol name every handshake opens with, length-prefixed.</summary>
    public const string ProtocolName = "BitTorrent protocol";

    public const int HandshakeLength = 68;

    /// <summary>Blocks are 16 KiB by convention, and peers refuse larger requests.</summary>
    public const int BlockLength = 16 * 1024;

    /// <summary>
    /// A peer that asks for more than this in one message is trying to make us
    /// allocate, not to download.
    /// </summary>
    public const int MaxMessageLength = 1024 * 1024;

    /// <summary>Bit 44 of the reserved field: BEP 10 extended messages.</summary>
    private const int ExtendedReservedByte = 5;

    private const byte ExtendedReservedBit = 0x10;

    public static byte[] BuildHandshake(byte[] infoHash, byte[] peerId, bool supportsExtended = true)
    {
        var packet = new byte[HandshakeLength];

        packet[0] = (byte)ProtocolName.Length;
        Encoding.ASCII.GetBytes(ProtocolName).CopyTo(packet.AsSpan(1));

        // Eight reserved bytes, all zero except the capabilities we claim.
        if (supportsExtended) packet[9 + ExtendedReservedByte] |= ExtendedReservedBit;

        infoHash.CopyTo(packet.AsSpan(28, 20));
        peerId.CopyTo(packet.AsSpan(48, 20));

        return packet;
    }

    public static PeerHandshake ParseHandshake(byte[] packet)
    {
        if (packet.Length < HandshakeLength) throw new PeerException("握手长度不足。");

        int nameLength = packet[0];

        if (nameLength != ProtocolName.Length ||
            Encoding.ASCII.GetString(packet, 1, nameLength) != ProtocolName)
        {
            throw new PeerException("对方不是 BitTorrent 客户端。");
        }

        bool extended = (packet[9 + ExtendedReservedByte] & ExtendedReservedBit) != 0;

        return new PeerHandshake(
            packet.AsSpan(28, 20).ToArray(),
            packet.AsSpan(48, 20).ToArray(),
            extended);
    }

    /// <summary>A message as it goes on the wire: four bytes of length, then id and payload.</summary>
    public static byte[] Encode(PeerMessage message)
    {
        if (message.Kind == PeerMessageKind.KeepAlive) return new byte[4];

        var packet = new byte[4 + 1 + message.Payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(0), 1 + message.Payload.Length);
        packet[4] = (byte)message.Kind;
        message.Payload.CopyTo(packet.AsSpan(5));

        return packet;
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes, or throws.</summary>
    public static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        int read = 0;

        while (read < count)
        {
            int got = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);

            // A peer that hangs up mid-message is a disconnection, not a
            // protocol violation, but either way this connection is over.
            if (got == 0) throw new PeerException($"对方在读取 {count} 字节时断开（已读 {read}）。");

            read += got;
        }

        return buffer;
    }

    /// <summary>Reads one message. Returns a keep-alive for a zero-length one.</summary>
    public static async Task<PeerMessage> ReadMessageAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = await ReadExactAsync(stream, 4, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32BigEndian(header);

        if (length == 0) return PeerMessage.KeepAlive;

        // The length is the peer's, so it is checked before it is trusted with
        // an allocation.
        if (length < 0 || length > MaxMessageLength)
        {
            throw new PeerException($"对方声明的消息长度不合理：{length} 字节。");
        }

        var body = await ReadExactAsync(stream, length, cancellationToken).ConfigureAwait(false);

        return new PeerMessage((PeerMessageKind)body[0], body.AsSpan(1).ToArray());
    }

    /// <summary>Whether a bitfield says the peer has a piece.</summary>
    public static bool HasPiece(byte[] bitfield, int index)
    {
        int position = index >> 3;
        if (position < 0 || position >= bitfield.Length) return false;

        // The high bit of the first byte is piece 0, which is the opposite of
        // how a bit index usually reads.
        return (bitfield[position] & (0x80 >> (index & 7))) != 0;
    }

    public static void SetPiece(byte[] bitfield, int index)
    {
        int position = index >> 3;
        if (position < 0 || position >= bitfield.Length) return;

        bitfield[position] |= (byte)(0x80 >> (index & 7));
    }

    /// <summary>How many bytes a bitfield for this many pieces takes.</summary>
    public static int BitfieldLength(int pieces) => (pieces + 7) / 8;

    /// <summary>
    /// Whether a bitfield is the right size and has no bits set past the last
    /// piece. A peer that sets the spare bits is broken, and trusting it means
    /// asking for pieces that do not exist.
    /// </summary>
    public static bool IsValidBitfield(byte[] bitfield, int pieces)
    {
        if (bitfield.Length != BitfieldLength(pieces)) return false;

        for (int index = pieces; index < bitfield.Length * 8; index++)
        {
            if (HasPiece(bitfield, index)) return false;
        }

        return true;
    }
}
