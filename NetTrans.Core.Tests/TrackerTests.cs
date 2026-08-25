using System.Buffers.Binary;
using System.Net;
using System.Text;
using NetTrans.Torrent;
using Xunit;

namespace NetTrans.Tests;

/// <summary>
/// Announcing. The two protocols disagree about everything except what they are
/// for, and both have a detail that silently corrupts a request if it is got
/// wrong: raw-byte escaping for HTTP, event numbering for UDP.
/// </summary>
public class TrackerTests
{
    private static readonly byte[] InfoHash = Enumerable.Range(0, 20).Select(n => (byte)(n * 13)).ToArray();

    // ── the shared, pure parts ────────────────────────────────────────────

    [Fact]
    public void Raw_bytes_are_escaped_one_at_a_time()
    {
        // An info hash is twenty arbitrary bytes, not text. A string encoder
        // would mangle whatever is not valid UTF-8, which most of them are not.
        var bytes = new byte[] { 0x00, 0xFF, (byte)'A', (byte)'-', 0x7F, 0x20 };

        Assert.Equal("%00%FFA-%7F%20", TrackerProtocol.Escape(bytes));
    }

    [Fact]
    public void Only_the_unreserved_characters_survive_unescaped()
    {
        // RFC 3986 unreserved: letters, digits, and those four. Everything else
        // is escaped, including bytes a text encoder would have mangled.
        for (int n = 0; n < 256; n++)
        {
            char c = (char)n;
            bool unreserved = char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~';

            Assert.Equal(
                unreserved ? c.ToString() : $"%{n:X2}",
                TrackerProtocol.Escape(new[] { (byte)n }));
        }
    }

    [Fact]
    public void The_query_keeps_parameters_the_tracker_url_already_had()
    {
        // A private tracker's passkey lives in the URL; dropping it is how it
        // says no.
        var url = TrackerProtocol.BuildQuery(
            new Uri("https://tracker.test/announce?passkey=abc123"),
            Request());

        Assert.Contains("passkey=abc123", url.Query);
        Assert.Contains("&info_hash=", url.Query);
        Assert.Contains("compact=1", url.Query);
    }

    [Fact]
    public void The_query_starts_one_when_the_url_had_none()
    {
        var url = TrackerProtocol.BuildQuery(new Uri("https://tracker.test/announce"), Request());

        Assert.StartsWith("?info_hash=", url.Query);
    }

    [Fact]
    public void An_event_is_named_only_when_there_is_one()
    {
        Assert.Contains("event=started", TrackerProtocol.BuildQuery(Announce, Request()).Query);
        Assert.DoesNotContain("event=", TrackerProtocol.BuildQuery(Announce, Request(AnnounceEvent.None)).Query);
    }

    [Fact]
    public void Compact_peers_are_four_bytes_of_address_and_two_of_port()
    {
        var data = new byte[] { 10, 0, 0, 7, 0x1A, 0xE1, 192, 168, 1, 1, 0x00, 0x50 };

        var peers = TrackerProtocol.ParseCompact(data, TrackerProtocol.CompactPeerLength).ToList();

        Assert.Equal(2, peers.Count);
        Assert.Equal("10.0.0.7:6881", peers[0].ToString());
        Assert.Equal("192.168.1.1:80", peers[1].ToString());
    }

    [Fact]
    public void A_peer_on_port_zero_cannot_be_connected_to_and_is_dropped()
    {
        var data = new byte[] { 10, 0, 0, 7, 0, 0 };

        Assert.Empty(TrackerProtocol.ParseCompact(data, TrackerProtocol.CompactPeerLength));
    }

    [Fact]
    public void A_trailing_partial_record_is_ignored_rather_than_read_past()
    {
        var data = new byte[] { 10, 0, 0, 7, 0x1A, 0xE1, 192, 168 };

        Assert.Single(TrackerProtocol.ParseCompact(data, TrackerProtocol.CompactPeerLength));
    }

    [Fact]
    public void The_dictionary_form_of_peers_is_read_too()
    {
        var body = Bencode.Encode(Bencode.Dictionary(
            ("interval", Bencode.Number(900)),
            ("peers", Bencode.List(
                Bencode.Dictionary(("ip", Bencode.String("10.0.0.1")), ("port", Bencode.Number(6881))),
                Bencode.Dictionary(("ip", Bencode.String("bogus")), ("port", Bencode.Number(1)))))));

        var response = TrackerProtocol.ParseResponse(body);

        Assert.Equal("10.0.0.1:6881", response.Peers.Single().ToString());
    }

    [Fact]
    public void A_refusal_is_reported_with_the_trackers_own_words()
    {
        var body = Bencode.Encode(Bencode.Dictionary(("failure reason", Bencode.String("torrent not registered"))));

        var error = Assert.Throws<TrackerException>(() => TrackerProtocol.ParseResponse(body));
        Assert.Contains("torrent not registered", error.Message);
    }

    [Fact]
    public void An_absurd_interval_is_clamped_rather_than_obeyed()
    {
        // A tracker asking to be re-announced every second is broken or hostile.
        Assert.Equal(60, Interval(1).TotalSeconds);
        Assert.Equal(3600, Interval(999999).TotalSeconds);
        Assert.Equal(900, Interval(900).TotalSeconds);
    }

    [Fact]
    public void The_same_peer_from_two_trackers_is_only_kept_once()
    {
        var peers = new[]
        {
            new IPEndPoint(IPAddress.Parse("10.0.0.1"), 6881),
            new IPEndPoint(IPAddress.Parse("10.0.0.1"), 6881),
            new IPEndPoint(IPAddress.Parse("10.0.0.1"), 6882),
        };

        Assert.Equal(2, TrackerProtocol.Distinct(peers).Count);
    }

    [Fact]
    public void A_peer_id_looks_like_a_client_that_identifies_itself()
    {
        var id = TrackerProtocol.NewPeerId();

        Assert.Equal(20, id.Length);
        Assert.Equal("-NT0001-", Encoding.ASCII.GetString(id, 0, 8));
        Assert.NotEqual(id, TrackerProtocol.NewPeerId());
    }

    // ── HTTP ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_http_tracker_is_asked_and_its_peers_read()
    {
        var server = new Fakes.FakeHlsServer();

        var body = Bencode.Encode(Bencode.Dictionary(
            ("interval", Bencode.Number(1800)),
            ("complete", Bencode.Number(9)),
            ("incomplete", Bencode.Number(3)),
            ("peers", Bencode.String(new byte[] { 10, 0, 0, 7, 0x1A, 0xE1 }))));

        var url = TrackerProtocol.BuildQuery(Announce, Request());
        server.Add(url.AbsoluteUri, body);

        var response = await new HttpTrackerClient(server)
            .AnnounceAsync(Announce, Request(), CancellationToken.None);

        Assert.Equal("10.0.0.7:6881", response.Peers.Single().ToString());
        Assert.Equal(9, response.Seeders);
        Assert.Equal(3, response.Leechers);
    }

    [Fact]
    public async Task An_http_tracker_that_will_not_answer_is_reported_as_such()
    {
        var server = new Fakes.FakeHlsServer();

        var error = await Assert.ThrowsAsync<TrackerException>(
            () => new HttpTrackerClient(server).AnnounceAsync(Announce, Request(), CancellationToken.None));

        Assert.Contains("tracker.test", error.Message);
    }

    // ── UDP ───────────────────────────────────────────────────────────────

    [Fact]
    public void The_udp_event_numbers_are_not_the_enum_order()
    {
        // BEP 15: 1 is completed and 2 is started. Casting the enum would
        // announce a completion every time a torrent began.
        Assert.Equal(0, UdpTrackerClient.UdpEvent(AnnounceEvent.None));
        Assert.Equal(1, UdpTrackerClient.UdpEvent(AnnounceEvent.Completed));
        Assert.Equal(2, UdpTrackerClient.UdpEvent(AnnounceEvent.Started));
        Assert.Equal(3, UdpTrackerClient.UdpEvent(AnnounceEvent.Stopped));
    }

    [Fact]
    public async Task A_udp_tracker_connects_then_announces()
    {
        var channel = new FakeUdpTracker { Peers = { new IPEndPoint(IPAddress.Parse("10.0.0.7"), 6881) } };
        var client = new UdpTrackerClient(channel);

        var response = await client.AnnounceAsync(Udp, Request(), CancellationToken.None);

        Assert.Equal(2, channel.Requests.Count);
        Assert.Equal("10.0.0.7:6881", response.Peers.Single().ToString());

        // The connect quotes the protocol's fixed magic; a tracker that does
        // not see it drops the packet.
        Assert.Equal(0x41727101980L, BinaryPrimitives.ReadInt64BigEndian(channel.Requests[0]));
    }

    [Fact]
    public async Task The_announce_carries_the_info_hash_and_peer_id_where_bep15_says()
    {
        var channel = new FakeUdpTracker();
        var request = Request();

        await new UdpTrackerClient(channel).AnnounceAsync(Udp, request, CancellationToken.None);

        byte[] announce = channel.Requests[1];

        Assert.Equal(98, announce.Length);
        Assert.Equal(request.InfoHash, announce.AsSpan(16, 20).ToArray());
        Assert.Equal(request.PeerId, announce.AsSpan(36, 20).ToArray());
        Assert.Equal(6881, BinaryPrimitives.ReadUInt16BigEndian(announce.AsSpan(96)));
    }

    [Fact]
    public async Task The_connection_id_is_reused_rather_than_renegotiated()
    {
        var channel = new FakeUdpTracker();
        var client = new UdpTrackerClient(channel);

        await client.AnnounceAsync(Udp, Request(), CancellationToken.None);
        await client.AnnounceAsync(Udp, Request(AnnounceEvent.None), CancellationToken.None);

        // One connect, two announces -- not two of each.
        Assert.Equal(3, channel.Requests.Count);
    }

    [Fact]
    public async Task A_reply_that_does_not_echo_the_transaction_id_is_refused()
    {
        // UDP has no connection: a reply that does not echo it came from
        // somewhere else, or from a request already given up on.
        var channel = new FakeUdpTracker { CorruptTransactionId = true };

        await Assert.ThrowsAsync<TrackerException>(
            () => new UdpTrackerClient(channel).AnnounceAsync(Udp, Request(), CancellationToken.None));
    }

    [Fact]
    public async Task A_tracker_that_does_not_answer_at_all_is_reported()
    {
        var channel = new FakeUdpTracker { Silent = true };

        var error = await Assert.ThrowsAsync<TrackerException>(
            () => new UdpTrackerClient(channel).AnnounceAsync(Udp, Request(), CancellationToken.None));

        Assert.Contains("没有响应", error.Message);
    }

    [Fact]
    public async Task A_udp_error_reply_is_reported_with_its_text()
    {
        var channel = new FakeUdpTracker { ErrorText = "torrent not registered" };

        var error = await Assert.ThrowsAsync<TrackerException>(
            () => new UdpTrackerClient(channel).AnnounceAsync(Udp, Request(), CancellationToken.None));

        Assert.Contains("torrent not registered", error.Message);
    }

    // ── the pool ──────────────────────────────────────────────────────────

    [Fact]
    public async Task One_dead_tracker_out_of_several_does_not_stop_the_announce()
    {
        var pool = new TrackerPool(
            new ScriptedTracker("http", new Dictionary<string, object>
            {
                ["http://good.test/announce"] = Response("10.0.0.1"),
                ["http://dead.test/announce"] = new TrackerException("timed out"),
            }));

        var response = await pool.AnnounceAsync(
            new[] { new Uri("http://good.test/announce"), new Uri("http://dead.test/announce") },
            Request(),
            CancellationToken.None);

        Assert.Equal("10.0.0.1:6881", response.Peers.Single().ToString());
        Assert.Contains("http://dead.test/announce", pool.Failures.Keys);
    }

    [Fact]
    public async Task Peers_from_several_trackers_are_pooled_without_duplicates()
    {
        var pool = new TrackerPool(
            new ScriptedTracker("http", new Dictionary<string, object>
            {
                ["http://a.test/announce"] = Response("10.0.0.1", "10.0.0.2"),
                ["http://b.test/announce"] = Response("10.0.0.2", "10.0.0.3"),
            }));

        var response = await pool.AnnounceAsync(
            new[] { new Uri("http://a.test/announce"), new Uri("http://b.test/announce") },
            Request(),
            CancellationToken.None);

        Assert.Equal(3, response.Peers.Count);
    }

    [Fact]
    public async Task Every_tracker_failing_is_the_one_case_that_fails_the_announce()
    {
        var pool = new TrackerPool(
            new ScriptedTracker("http", new Dictionary<string, object>
            {
                ["http://a.test/announce"] = new TrackerException("timed out"),
            }));

        var error = await Assert.ThrowsAsync<TrackerException>(() => pool.AnnounceAsync(
            new[] { new Uri("http://a.test/announce") },
            Request(),
            CancellationToken.None));

        Assert.Contains("timed out", error.Message);
    }

    [Fact]
    public async Task A_tracker_nobody_can_announce_to_is_not_counted_as_a_failure()
    {
        var pool = new TrackerPool(new ScriptedTracker("http", new Dictionary<string, object>()));

        await Assert.ThrowsAsync<TrackerException>(() => pool.AnnounceAsync(
            new[] { new Uri("udp://only.test:6969") },
            Request(),
            CancellationToken.None));

        Assert.Empty(pool.Failures);
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static readonly Uri Announce = new("https://tracker.test/announce");
    private static readonly Uri Udp = new("udp://tracker.test:6969/announce");

    private static AnnounceRequest Request(AnnounceEvent what = AnnounceEvent.Started) =>
        new(InfoHash, Enumerable.Repeat((byte)'P', 20).ToArray(), 6881, 0, 0, 1000, what);

    private static TimeSpan Interval(long seconds) => TrackerProtocol
        .ParseResponse(Bencode.Encode(Bencode.Dictionary(("interval", Bencode.Number(seconds)))))
        .Interval;

    private static AnnounceResponse Response(params string[] addresses) => new(
        addresses.Select(address => new IPEndPoint(IPAddress.Parse(address), 6881)).ToList(),
        TimeSpan.FromMinutes(15));

    /// <summary>Answers whatever the test scripted, by tracker URL.</summary>
    private sealed class ScriptedTracker : ITrackerClient
    {
        private readonly string _scheme;
        private readonly Dictionary<string, object> _answers;

        public ScriptedTracker(string scheme, Dictionary<string, object> answers)
        {
            _scheme = scheme;
            _answers = answers;
        }

        public bool CanAnnounceTo(Uri tracker) => tracker.Scheme == _scheme;

        public Task<AnnounceResponse> AnnounceAsync(Uri tracker, AnnounceRequest request, CancellationToken cancellationToken) =>
            _answers.TryGetValue(tracker.AbsoluteUri, out var answer)
                ? answer is Exception error ? Task.FromException<AnnounceResponse>(error) : Task.FromResult((AnnounceResponse)answer)
                : Task.FromException<AnnounceResponse>(new TrackerException("no answer scripted"));
    }

    /// <summary>A BEP 15 tracker in a box.</summary>
    private sealed class FakeUdpTracker : IUdpChannel
    {
        private const long ConnectionId = 0x0123456789ABCDEFL;

        public List<byte[]> Requests { get; } = new();

        public List<IPEndPoint> Peers { get; } = new();

        public bool Silent { get; set; }

        public bool CorruptTransactionId { get; set; }

        public string? ErrorText { get; set; }

        public Task<byte[]?> ExchangeAsync(Uri tracker, byte[] request, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Requests.Add(request);

            if (Silent) return Task.FromResult<byte[]?>(null);

            int transaction = BinaryPrimitives.ReadInt32BigEndian(request.AsSpan(12));
            if (CorruptTransactionId) transaction ^= 0x5A5A5A5A;

            if (ErrorText is { } text)
            {
                var error = new byte[8 + text.Length];
                BinaryPrimitives.WriteInt32BigEndian(error.AsSpan(0), 3);
                BinaryPrimitives.WriteInt32BigEndian(error.AsSpan(4), transaction);
                Encoding.UTF8.GetBytes(text).CopyTo(error.AsSpan(8));
                return Task.FromResult<byte[]?>(error);
            }

            // A connect is sixteen bytes; anything longer is an announce.
            if (request.Length == 16)
            {
                var reply = new byte[16];
                BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(0), 0);
                BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(4), transaction);
                BinaryPrimitives.WriteInt64BigEndian(reply.AsSpan(8), ConnectionId);
                return Task.FromResult<byte[]?>(reply);
            }

            var announce = new byte[20 + Peers.Count * 6];
            BinaryPrimitives.WriteInt32BigEndian(announce.AsSpan(0), 1);
            BinaryPrimitives.WriteInt32BigEndian(announce.AsSpan(4), transaction);
            BinaryPrimitives.WriteInt32BigEndian(announce.AsSpan(8), 1800);
            BinaryPrimitives.WriteInt32BigEndian(announce.AsSpan(12), 4);
            BinaryPrimitives.WriteInt32BigEndian(announce.AsSpan(16), 11);

            for (int i = 0; i < Peers.Count; i++)
            {
                Peers[i].Address.GetAddressBytes().CopyTo(announce.AsSpan(20 + i * 6, 4));
                BinaryPrimitives.WriteUInt16BigEndian(announce.AsSpan(24 + i * 6), (ushort)Peers[i].Port);
            }

            return Task.FromResult<byte[]?>(announce);
        }

        public void Dispose()
        {
        }
    }
}
