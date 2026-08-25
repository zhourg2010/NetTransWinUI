using System.Net;
using NetTrans.Tests.Fakes;
using NetTrans.Torrent;
using Xunit;

namespace NetTrans.Tests;

/// <summary>
/// A rate per peer, which is what the inspector's 连接 tab draws.
///
/// The tab was showing nothing at all for torrents: the job returned an empty
/// array with a comment claiming a per-peer rate was not meaningful. It is the
/// only thing that tab is for -- one stalled peer among seven is exactly what
/// you open it to find.
/// </summary>
public class PeerRateTests
{
    [Fact]
    public async Task A_connected_peer_shows_up_with_a_rate_of_its_own()
    {
        var world = new World();

        await world.RunAsync();

        // Sampled while the transfer was running, since a peer that has hung up
        // is rightly gone from the list.
        Assert.Contains(world.Sampled, rates => rates.Count == 1 && rates[0].Down > 0);
        Assert.Contains(world.Sampled, rates => rates.Count == 1 && Equals(rates[0].Peer, World.SeedAddress));
    }

    [Fact]
    public async Task A_peer_that_has_gone_is_not_still_listed()
    {
        var world = new World();

        await world.RunAsync();

        Assert.Empty(world.Swarm.PeerRates);
    }

    /// <summary>One seed with the whole torrent, and a swarm fetching it.</summary>
    private sealed class World
    {
        public static readonly IPEndPoint SeedAddress = new(IPAddress.Parse("10.0.0.5"), 6881);

        private readonly MemoryFileSinkFactory _sinks = new();
        private readonly PieceStore _store;
        private readonly PiecePicker _picker;

        public World()
        {
            var builder = new TorrentBuilder { Name = "rated.bin", PieceLength = 256 };

            Content = new byte[4096];
            for (int i = 0; i < Content.Length; i++) Content[i] = (byte)(i * 13 % 251);

            builder.Add("rated.bin", Content);
            builder.Trackers.Add("http://tracker.test/announce");

            Torrent = TorrentMetainfo.Parse(builder.Build());
            _picker = new PiecePicker(Torrent.PieceCount);
            _store = new PieceStore(Torrent, _sinks, "/downloads");
        }

        public TorrentMetainfo Torrent { get; }

        public byte[] Content { get; }

        public TorrentSwarm Swarm { get; private set; } = null!;

        /// <summary>Every non-empty snapshot taken while the swarm was running.</summary>
        public List<IReadOnlyList<PeerRate>> Sampled { get; } = new();

        public async Task RunAsync(int timeoutMilliseconds = 1500)
        {
            var seed = new FakeSeed(Torrent, Content);
            var connector = new FakePeerConnector(Torrent.InfoHash).Add(SeedAddress, seed);
            var trackers = new TrackerPool(new OnePeerTracker(SeedAddress));

            Swarm = new TorrentSwarm(Torrent, connector, trackers, _store, _picker)
            {
                MaxPeers = 1,

                // Short, so a rate is measured over the handful of blocks this
                // torrent has rather than averaged away to nothing.
                RateWindow = TimeSpan.FromSeconds(1),
            };

            using var cancellation = new CancellationTokenSource(timeoutMilliseconds);
            using var sampling = new CancellationTokenSource();

            var sampler = Task.Run(async () =>
            {
                while (!sampling.IsCancellationRequested)
                {
                    var rates = Swarm.PeerRates;
                    if (rates.Count > 0) lock (Sampled) Sampled.Add(rates);

                    await Task.Delay(5, CancellationToken.None);
                }
            });

            try
            {
                await Swarm.RunAsync(cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                // Seeding after the download has no natural end.
            }
            finally
            {
                sampling.Cancel();
                await sampler;
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
