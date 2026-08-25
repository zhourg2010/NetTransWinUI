using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace NetTrans.Torrent;

/// <summary>The event a tracker is told about, which decides what it does with the peer.</summary>
public enum AnnounceEvent
{
    /// <summary>A routine re-announce partway through.</summary>
    None,

    /// <summary>The first announce for this torrent, which is how a tracker knows to add us.</summary>
    Started,

    /// <summary>Leaving, so the tracker can drop us rather than wait out the interval.</summary>
    Stopped,

    /// <summary>Everything is downloaded, which is what a tracker counts as a completion.</summary>
    Completed,
}

/// <summary>What a tracker is asked.</summary>
public sealed record AnnounceRequest(
    byte[] InfoHash,
    byte[] PeerId,
    int Port,
    long Uploaded,
    long Downloaded,
    long Left,
    AnnounceEvent Event = AnnounceEvent.Started,
    int NumWant = 50);

/// <summary>What a tracker answers.</summary>
/// <param name="Peers">Where to connect.</param>
/// <param name="Interval">How long to wait before asking again.</param>
/// <param name="Seeders">complete, when the tracker says.</param>
/// <param name="Leechers">incomplete, when the tracker says.</param>
public sealed record AnnounceResponse(
    IReadOnlyList<IPEndPoint> Peers,
    TimeSpan Interval,
    int Seeders = 0,
    int Leechers = 0)
{
    public static AnnounceResponse Empty { get; } =
        new(Array.Empty<IPEndPoint>(), TimeSpan.FromMinutes(30));
}

/// <summary>A tracker refused, or answered with something unusable.</summary>
public sealed class TrackerException : Exception
{
    public TrackerException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>Announces to one tracker. HTTP and UDP each implement it.</summary>
public interface ITrackerClient
{
    /// <summary>Whether this client speaks the tracker's scheme.</summary>
    bool CanAnnounceTo(Uri tracker);

    Task<AnnounceResponse> AnnounceAsync(Uri tracker, AnnounceRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The pieces of the tracker protocols that are pure: how bytes are spelled in
/// a query string, and how a peer list is unpacked.
/// </summary>
public static class TrackerProtocol
{
    /// <summary>Compact peers are six bytes each: four of address, two of port.</summary>
    public const int CompactPeerLength = 6;

    /// <summary>And eighteen for IPv6.</summary>
    public const int CompactPeer6Length = 18;

    /// <summary>
    /// A peer id: "-NT0001-" and twelve random bytes, which is the convention
    /// every client follows so a peer can tell who it is talking to.
    /// </summary>
    public static byte[] NewPeerId()
    {
        var id = new byte[20];
        Encoding.ASCII.GetBytes("-NT0001-").CopyTo(id, 0);
        RandomNumberGenerator.Fill(id.AsSpan(8));

        return id;
    }

    /// <summary>
    /// Percent-encodes raw bytes for a query string.
    ///
    /// This cannot go through the usual URL encoders: an info hash is twenty
    /// arbitrary bytes, not text, and running it through a string encoder
    /// mangles anything that is not valid UTF-8 -- which most info hashes are
    /// not. Every byte outside the unreserved set is escaped, one at a time.
    /// </summary>
    public static string Escape(ReadOnlySpan<byte> bytes)
    {
        var text = new StringBuilder(bytes.Length * 3);

        foreach (byte b in bytes)
        {
            bool unreserved =
                (b >= 'A' && b <= 'Z') ||
                (b >= 'a' && b <= 'z') ||
                (b >= '0' && b <= '9') ||
                b is (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~';

            if (unreserved) text.Append((char)b);
            else text.Append('%').Append(b.ToString("X2"));
        }

        return text.ToString();
    }

    /// <summary>The announce URL with the request's parameters appended.</summary>
    public static Uri BuildQuery(Uri tracker, AnnounceRequest request)
    {
        var query = new StringBuilder();

        query.Append("info_hash=").Append(Escape(request.InfoHash));
        query.Append("&peer_id=").Append(Escape(request.PeerId));
        query.Append("&port=").Append(request.Port);
        query.Append("&uploaded=").Append(request.Uploaded);
        query.Append("&downloaded=").Append(request.Downloaded);
        query.Append("&left=").Append(request.Left);
        query.Append("&numwant=").Append(request.NumWant);

        // compact=1 asks for the six-bytes-per-peer form. Every tracker worth
        // announcing to supports it, and the dictionary form is handled anyway.
        query.Append("&compact=1");

        if (request.Event != AnnounceEvent.None)
        {
            query.Append("&event=").Append(request.Event.ToString().ToLowerInvariant());
        }

        // A tracker URL may already carry parameters of its own -- a passkey,
        // usually -- and dropping them is how a private tracker says no.
        string separator = string.IsNullOrEmpty(tracker.Query) ? "?" : "&";

        return new Uri(tracker.GetLeftPart(UriPartial.Query) + separator + query);
    }

    /// <summary>Reads a tracker's bencoded reply.</summary>
    /// <exception cref="TrackerException">The tracker said no, or said something unusable.</exception>
    public static AnnounceResponse ParseResponse(byte[] body)
    {
        BDictionary root;

        try
        {
            root = Bencode.DecodeDictionary(body);
        }
        catch (BencodeException exception)
        {
            throw new TrackerException("Tracker 的回复不是 bencode。", exception);
        }

        // A tracker that refuses says so in a key rather than in a status code.
        if (root.Text("failure reason") is { Length: > 0 } failure)
        {
            throw new TrackerException($"Tracker 拒绝：{failure}");
        }

        var peers = new List<IPEndPoint>();

        switch (root["peers"])
        {
            case BString compact:
                peers.AddRange(ParseCompact(compact.Bytes, CompactPeerLength));
                break;

            case BList list:
                peers.AddRange(ParseDictionaryPeers(list));
                break;
        }

        if (root["peers6"] is BString compact6)
        {
            peers.AddRange(ParseCompact(compact6.Bytes, CompactPeer6Length));
        }

        long interval = root.Number("interval") ?? 1800;

        // A tracker asking to be re-announced every second is broken or
        // hostile; the floor is what stops us hammering it.
        interval = Math.Clamp(interval, 60, 3600);

        return new AnnounceResponse(
            Distinct(peers),
            TimeSpan.FromSeconds(interval),
            (int)(root.Number("complete") ?? 0),
            (int)(root.Number("incomplete") ?? 0));
    }

    /// <summary>The compact form: fixed-width records of address then big-endian port.</summary>
    public static IEnumerable<IPEndPoint> ParseCompact(byte[] data, int recordLength)
    {
        int addressLength = recordLength - 2;

        for (int i = 0; i + recordLength <= data.Length; i += recordLength)
        {
            var address = new IPAddress(data.AsSpan(i, addressLength));
            int port = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(i + addressLength, 2));

            // Port 0 is a peer that cannot be connected to.
            if (port == 0) continue;

            yield return new IPEndPoint(address, port);
        }
    }

    private static IEnumerable<IPEndPoint> ParseDictionaryPeers(BList list)
    {
        foreach (var item in list.Items.OfType<BDictionary>())
        {
            string? ip = item.Text("ip");
            long? port = item.Number("port");

            if (ip is null || port is not { } number || number is <= 0 or > 65535) continue;
            if (!IPAddress.TryParse(ip, out var address)) continue;

            yield return new IPEndPoint(address, (int)number);
        }
    }

    /// <summary>
    /// Trackers routinely return the same peer twice, and a tier of trackers
    /// returns overlapping sets; connecting twice to one peer wastes a slot.
    /// </summary>
    public static IReadOnlyList<IPEndPoint> Distinct(IEnumerable<IPEndPoint> peers)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<IPEndPoint>();

        foreach (var peer in peers)
        {
            if (seen.Add(peer.ToString())) unique.Add(peer);
        }

        return unique;
    }
}
