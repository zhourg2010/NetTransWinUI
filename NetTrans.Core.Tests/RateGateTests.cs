using System.Net;
using NetTrans.Download;
using NetTrans.Tests.Fakes;
using NetTrans.Torrent;
using Xunit;

namespace NetTrans.Tests;

/// <summary>
/// 限速 applied to BitTorrent.
///
/// The cap used to be stored on the torrent job and read by nothing: 全局限速
/// held every HTTP transfer to 1 MB/s while a torrent next to it took the whole
/// line, which is the one case where a cap matters most.
/// </summary>
public class RateGateTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task An_uncapped_gate_never_waits()
    {
        var clock = new ManualClock(Start);
        var gate = new RateGate(0, clock);

        for (int i = 0; i < 100; i++) await gate.PassAsync(64 * 1024, CancellationToken.None);

        Assert.Empty(clock.Delays);
    }

    [Fact]
    public async Task A_cap_waits_once_the_burst_is_spent()
    {
        var clock = new ManualClock(Start);
        var gate = new RateGate(1024, clock);

        // The first second's worth goes straight through; the second does not.
        await gate.PassAsync(1024, CancellationToken.None);
        await gate.PassAsync(1024, CancellationToken.None);

        Assert.NotEmpty(clock.Delays);
    }

    [Fact]
    public async Task The_stricter_of_the_two_caps_is_the_one_that_binds()
    {
        var clock = new ManualClock(Start);
        var global = new TokenBucket(1024, Start);
        var gate = new RateGate(1024 * 1024, clock, global);

        await gate.PassAsync(4096, CancellationToken.None);
        await gate.PassAsync(4096, CancellationToken.None);

        // The task's own cap is generous; the global one is not, and it is the
        // global one that has to be felt.
        Assert.NotEmpty(clock.Delays);
    }

    [Fact]
    public void A_shared_bucket_survives_being_taken_from_at_once()
    {
        var bucket = new TokenBucket(1024 * 1024, Start);

        Parallel.For(0, 64, _ =>
        {
            for (int i = 0; i < 200; i++) bucket.Take(512, Start + TimeSpan.FromMilliseconds(i));
        });

        // Nothing to assert beyond having got here without a torn read: the
        // global bucket is shared by every transfer and every peer of a
        // torrent, so it is taken from by many threads at once.
        Assert.True(bucket.BytesPerSecond > 0);
    }

    [Fact]
    public async Task A_swarm_under_a_cap_waits_for_its_bytes()
    {
        var world = new World(capBytesPerSecond: 2048);

        await world.RunAsync();

        // 8 KB fetched under a 2 KB/s cap cannot happen without waiting.
        Assert.NotEmpty(world.Clock.Delays);
    }

    [Fact]
    public async Task An_uncapped_swarm_waits_for_nothing()
    {
        var world = new World(capBytesPerSecond: 0);

        await world.RunAsync();

        Assert.Empty(world.Clock.Delays);
    }

    /// <summary>A seed with the whole torrent and a swarm fetching it under a cap.</summary>
    private sealed class World
    {
        private static readonly IPEndPoint SeedAddress = new(IPAddress.Parse("10.0.0.3"), 6881);

        private readonly MemoryFileSinkFactory _sinks = new();
        private readonly PieceStore _store;
        private readonly PiecePicker _picker;
        private readonly byte[] _content;
        private readonly double _cap;

        public World(double capBytesPerSecond)
        {
            _cap = capBytesPerSecond;

            var builder = new TorrentBuilder { Name = "capped.bin", PieceLength = 512 };

            _content = new byte[8192];
            for (int i = 0; i < _content.Length; i++) _content[i] = (byte)(i * 7 % 251);

            builder.Add("capped.bin", _content);
            builder.Trackers.Add("http://tracker.test/announce");

            Torrent = TorrentMetainfo.Parse(builder.Build());
            _picker = new PiecePicker(Torrent.PieceCount);
            _store = new PieceStore(Torrent, _sinks, "/downloads");
        }

        public TorrentMetainfo Torrent { get; }

        /// <summary>Waiting is recorded rather than slept, so the cap can be seen without the test taking that long.</summary>
        public ManualClock Clock { get; } = new();

        public async Task RunAsync(int timeoutMilliseconds = 3000)
        {
            var seed = new FakeSeed(Torrent, _content);
            var connector = new FakePeerConnector(Torrent.InfoHash).Add(SeedAddress, seed);
            var trackers = new TrackerPool(new OnePeerTracker(SeedAddress));

            var swarm = new TorrentSwarm(Torrent, connector, trackers, _store, _picker)
            {
                MaxPeers = 1,
                Seed = false,
                DownloadGate = new RateGate(_cap, Clock),
            };

            using var cancellation = new CancellationTokenSource(timeoutMilliseconds);

            try
            {
                await swarm.RunAsync(cancellation.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>Answers every announce with the one seed.</summary>
    private sealed class OnePeerTracker : ITrackerClient
    {
        private readonly IPEndPoint _peer;

        public OnePeerTracker(IPEndPoint peer) => _peer = peer;

        public bool CanAnnounceTo(Uri tracker) => tracker.Scheme is "http" or "https";

        public Task<AnnounceResponse> AnnounceAsync(Uri tracker, AnnounceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new AnnounceResponse(new[] { _peer }, TimeSpan.FromMinutes(1), 1, 0));
    }
}
