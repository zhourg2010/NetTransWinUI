using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace NetTrans.Torrent;

/// <summary>
/// One request/reply over UDP. Abstracted so the tracker protocol can be tested
/// without a socket, and so the app has one place that opens one.
/// </summary>
public interface IUdpChannel : IDisposable
{
    /// <summary>
    /// Sends and waits for the first reply. Returns null on timeout -- which is
    /// the normal outcome for a dead UDP tracker, not an error worth throwing.
    /// </summary>
    Task<byte[]?> ExchangeAsync(Uri tracker, byte[] request, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>The real socket.</summary>
public sealed class UdpChannel : IUdpChannel
{
    private readonly UdpClient _client = new(AddressFamily.InterNetwork);

    public async Task<byte[]?> ExchangeAsync(
        Uri tracker,
        byte[] request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            await _client.SendAsync(request, tracker.Host, tracker.Port, deadline.Token).ConfigureAwait(false);

            var reply = await _client.ReceiveAsync(deadline.Token).ConfigureAwait(false);
            return reply.Buffer;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own deadline, not the caller's: a tracker that did not answer.
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>
/// BEP 15, the UDP tracker protocol. Most public trackers are UDP now: it is
/// two datagrams instead of a TCP handshake plus a GET, which matters when a
/// tracker is answering millions of clients.
///
/// The exchange is two steps. A connect gets a connection id, which is then
/// quoted in the announce; the id is what stops a forged source address from
/// being used to flood a third party, so it cannot be skipped.
/// </summary>
public sealed class UdpTrackerClient : ITrackerClient
{
    /// <summary>The protocol's fixed opening magic, quoted verbatim from BEP 15.</summary>
    private const long ProtocolId = 0x41727101980L;

    private const int ActionConnect = 0;
    private const int ActionAnnounce = 1;
    private const int ActionError = 3;

    /// <summary>A connection id is good for a minute; re-connecting is cheaper than guessing.</summary>
    private static readonly TimeSpan ConnectionLifetime = TimeSpan.FromSeconds(55);

    private readonly IUdpChannel _channel;
    private readonly IClockNow _now;

    private readonly Dictionary<string, (long Id, DateTimeOffset Expires)> _connections = new(StringComparer.Ordinal);

    public UdpTrackerClient(IUdpChannel channel, IClockNow? now = null)
    {
        _channel = channel;
        _now = now ?? SystemNow.Instance;
    }

    /// <summary>How long to wait for each datagram.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(8);

    public bool CanAnnounceTo(Uri tracker) => tracker.Scheme == "udp";

    public async Task<AnnounceResponse> AnnounceAsync(
        Uri tracker,
        AnnounceRequest request,
        CancellationToken cancellationToken)
    {
        long connectionId = await ConnectAsync(tracker, cancellationToken).ConfigureAwait(false);

        int transaction = NewTransactionId();
        var packet = new byte[98];

        BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(0), connectionId);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(8), ActionAnnounce);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(12), transaction);
        request.InfoHash.CopyTo(packet.AsSpan(16, 20));
        request.PeerId.CopyTo(packet.AsSpan(36, 20));
        BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(56), request.Downloaded);
        BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(64), request.Left);
        BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(72), request.Uploaded);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(80), UdpEvent(request.Event));
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(84), 0);              // IP: 0 means "the one you see"
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(88), NewTransactionId()); // key
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(92), request.NumWant);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(96), (ushort)request.Port);

        var reply = await _channel.ExchangeAsync(tracker, packet, Timeout, cancellationToken).ConfigureAwait(false)
            ?? throw new TrackerException($"Tracker {tracker.Host} 没有响应。");

        // Twenty bytes of header before the first peer.
        if (reply.Length < 20) throw Malformed(tracker, reply, transaction);

        int action = BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(0));
        int echoed = BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(4));

        if (action == ActionError) throw new TrackerException($"Tracker 拒绝：{ErrorText(reply)}");
        if (action != ActionAnnounce || echoed != transaction) throw Malformed(tracker, reply, transaction);

        long interval = BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(8));
        int leechers = BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(12));
        int seeders = BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(16));

        var peers = TrackerProtocol
            .ParseCompact(reply.AsSpan(20).ToArray(), TrackerProtocol.CompactPeerLength)
            .ToList();

        return new AnnounceResponse(
            TrackerProtocol.Distinct(peers),
            TimeSpan.FromSeconds(Math.Clamp(interval, 60, 3600)),
            seeders,
            leechers);
    }

    private async Task<long> ConnectAsync(Uri tracker, CancellationToken cancellationToken)
    {
        string key = tracker.AbsoluteUri;
        var now = _now.UtcNow;

        lock (_connections)
        {
            if (_connections.TryGetValue(key, out var cached) && cached.Expires > now) return cached.Id;
        }

        int transaction = NewTransactionId();
        var packet = new byte[16];

        BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(0), ProtocolId);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(8), ActionConnect);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(12), transaction);

        var reply = await _channel.ExchangeAsync(tracker, packet, Timeout, cancellationToken).ConfigureAwait(false)
            ?? throw new TrackerException($"Tracker {tracker.Host} 没有响应。");

        if (reply.Length < 16) throw Malformed(tracker, reply, transaction);

        int action = BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(0));
        int echoed = BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(4));

        if (action == ActionError) throw new TrackerException($"Tracker 拒绝：{ErrorText(reply)}");

        // The transaction id is checked because UDP has no connection: a reply
        // that does not echo it came from somewhere else, or from a request we
        // have already given up on.
        if (action != ActionConnect || echoed != transaction) throw Malformed(tracker, reply, transaction);

        long connectionId = BinaryPrimitives.ReadInt64BigEndian(reply.AsSpan(8));

        lock (_connections) _connections[key] = (connectionId, now + ConnectionLifetime);

        return connectionId;
    }

    /// <summary>
    /// BEP 15 numbers the events differently from the order they read in: 1 is
    /// completed and 2 is started, not the other way round. Casting the enum
    /// would announce a completion every time a torrent began.
    /// </summary>
    internal static int UdpEvent(AnnounceEvent value) => value switch
    {
        AnnounceEvent.Completed => 1,
        AnnounceEvent.Started => 2,
        AnnounceEvent.Stopped => 3,
        _ => 0,
    };

    private static TrackerException Malformed(Uri tracker, byte[] reply, int transaction) =>
        new($"Tracker {tracker.Host} 的回复无法解析（{reply.Length} 字节，事务 {transaction}）。");

    private static string ErrorText(byte[] reply) =>
        reply.Length > 8
            ? System.Text.Encoding.UTF8.GetString(reply, 8, reply.Length - 8).Trim()
            : "未说明原因";

    private static int NewTransactionId() => RandomNumberGenerator.GetInt32(int.MaxValue);
}

/// <summary>
/// Just the current time.
///
/// Separate from the transfer clock: this is used for a cache expiry that must
/// follow the wall clock even in a test that drives transfers by hand.
/// </summary>
public interface IClockNow
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemNow : IClockNow
{
    public static SystemNow Instance { get; } = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
