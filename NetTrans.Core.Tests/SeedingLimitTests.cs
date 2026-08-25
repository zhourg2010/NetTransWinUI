using System.Net;
using NetTrans.Tests.Fakes;
using NetTrans.Torrent;
using Xunit;

namespace NetTrans.Tests;

/// <summary>
/// 做种限制, enforced rather than merely stored.
///
/// The sheet offers a share ratio or a seeding time, and until this was wired
/// up the swarm kept the setting and never looked at it again -- which on a
/// private tracker is a promise about an account's ratio that nothing keeps.
/// </summary>
public class SeedingLimitTests
{
    [Fact]
    public async Task Stopping_the_moment_it_finishes_serves_nobody()
    {
        // "下完即停" is a real choice, and it has to be honoured before a single
        // block goes out rather than after the first peer is served.
        var world = new World { Limits = new SeedingLimits(MaxSeedingTime: TimeSpan.Zero) };

        await world.RunAsync();

        Assert.Equal(0, world.Swarm.Progress.Uploaded);
        Assert.Contains(world.Said, line => line.Contains("已达到做种限制"));
    }

    [Fact]
    public async Task Seeding_stops_once_the_ratio_is_met()
    {
        // The leech takes the whole torrent, so once it has, uploaded equals
        // downloaded and the ratio is 1.0 -- past the limit.
        var world = new World { Limits = SeedingLimits.Ratio(0.5) };

        await world.RunAsync();

        Assert.Contains(world.Said, line => line.Contains("停止做种"));
    }

    [Fact]
    public async Task A_peer_that_never_hangs_up_does_not_hold_the_limit_open()
    {
        // The limit used to be looked at only when a session ended, so one
        // leech that stayed connected -- which on a private tracker is most of
        // them -- could take as much as it liked.
        var world = new World { Limits = SeedingLimits.Ratio(0.5), Linger = true };

        await world.RunAsync();

        Assert.Contains(world.Said, line => line.Contains("停止做种"));
    }

    [Fact]
    public async Task Seeding_stops_once_the_time_is_up()
    {
        var clock = new SteppingClock(TimeSpan.FromMinutes(45));
        var world = new World { Limits = new SeedingLimits(MaxSeedingTime: TimeSpan.FromHours(1)), Now = clock };

        await world.RunAsync();

        // The clock jumps 45 minutes per reading, so the limit is met on the
        // second check rather than immediately.
        Assert.Contains(world.Said, line => line.Contains("开始做种"));
        Assert.Contains(world.Said, line => line.Contains("停止做种"));
    }

    [Fact]
    public async Task An_unlimited_swarm_says_so_and_keeps_going()
    {
        var world = new World { Limits = SeedingLimits.Forever };

        await world.RunAsync(timeoutMilliseconds: 700);

        Assert.Contains(world.Said, line => line.Contains("开始做种"));
        Assert.DoesNotContain(world.Said, line => line.Contains("停止做种"));
    }

    [Fact]
    public async Task Seeding_can_be_turned_off_entirely()
    {
        var world = new World { Seed = false };

        await world.RunAsync();

        Assert.DoesNotContain(world.Said, line => line.Contains("开始做种"));
    }

    /// <summary>A clock that moves on every reading, so a time limit can be reached.</summary>
    private sealed class SteppingClock : IClockNow
    {
        private readonly TimeSpan _step;
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public SteppingClock(TimeSpan step) => _step = step;

        public DateTimeOffset UtcNow
        {
            get
            {
                var now = _now;
                _now += _step;
                return now;
            }
        }
    }

    /// <summary>A torrent already complete on disk, with nothing left to fetch.</summary>
    private sealed class World
    {
        private readonly MemoryFileSinkFactory _sinks = new();
        private readonly TorrentBuilder _builder;
        private readonly PieceStore _store;

        public World()
        {
            _builder = new TorrentBuilder { Name = "shared.bin", PieceLength = 256 };

            Content = new byte[1024];
            for (int i = 0; i < Content.Length; i++) Content[i] = (byte)(i * 17 % 251);

            _builder.Add("shared.bin", Content);
            _builder.Trackers.Add("http://tracker.test/announce");

            Torrent = TorrentMetainfo.Parse(_builder.Build());
            Picker = new PiecePicker(Torrent.PieceCount);
            _store = new PieceStore(Torrent, _sinks, "/downloads");

            // Everything already there: the swarm goes straight to seeding.
            for (int piece = 0; piece < Torrent.PieceCount; piece++)
            {
                int length = (int)Torrent.LengthOfPiece(piece);

                _store.WriteAsync(piece, Content.AsSpan(piece * 256, length).ToArray(), CancellationToken.None)
                    .GetAwaiter().GetResult();

                Picker.Complete(piece);
            }
        }

        public TorrentMetainfo Torrent { get; }

        public PiecePicker Picker { get; }

        public byte[] Content { get; }

        public SeedingLimits Limits { get; init; } = SeedingLimits.Forever;

        public bool Seed { get; init; } = true;

        public IClockNow? Now { get; init; }

        /// <summary>Whether the peer stays connected after it has everything.</summary>
        public bool Linger { get; init; }

        /// <summary>Short, so a limit reached mid-transfer shows up inside the test's patience.</summary>
        public TimeSpan CheckInterval { get; init; } = TimeSpan.FromMilliseconds(50);

        public List<string> Said { get; } = new();

        public TorrentSwarm Swarm { get; private set; } = null!;

        public async Task RunAsync(int timeoutMilliseconds = 1500)
        {
            var leech = new FakeLeech(Torrent);
            leech.Wants.AddRange(Enumerable.Range(0, Torrent.PieceCount));

            var peer = new IPEndPoint(IPAddress.Parse("10.0.0.9"), 6881);
            var connector = new LeechConnector(Torrent.InfoHash, peer, leech, Linger);

            var trackers = new TrackerPool(new StaticTracker(peer));

            Swarm = new TorrentSwarm(Torrent, connector, trackers, _store, Picker, null, Now)
            {
                Seed = Seed,
                SeedingLimits = Limits,
                SeedingCheckInterval = CheckInterval,
                MaxPeers = 1,
            };

            Swarm.Said += (_, line) => { lock (Said) Said.Add(line); };

            using var cancellation = new CancellationTokenSource(timeoutMilliseconds);

            try
            {
                await Swarm.RunAsync(cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                // Unlimited seeding has no natural end; the deadline is it.
            }
        }
    }

    /// <summary>Connects the one peer to a leech that wants everything.</summary>
    private sealed class LeechConnector : IPeerConnector
    {
        private readonly byte[] _infoHash;
        private readonly IPEndPoint _peer;
        private readonly FakeLeech _leech;
        private readonly bool _linger;

        public LeechConnector(byte[] infoHash, IPEndPoint peer, FakeLeech leech, bool linger)
        {
            _infoHash = infoHash;
            _peer = peer;
            _leech = leech;
            _linger = linger;
        }

        public Task<Stream> ConnectAsync(IPEndPoint peer, CancellationToken cancellationToken)
        {
            if (!peer.Equals(_peer)) return Task.FromException<Stream>(new PeerException("no"));

            var (ours, theirs) = DuplexPipe.Create();

            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await _leech.LeechAsync(theirs, _infoHash, cancellationToken);
                    }
                    finally
                    {
                        // A real peer's socket closes when it has what it came
                        // for. A leech that keeps the connection open instead is
                        // the ordinary private-tracker case, and the one where a
                        // limit checked only between peers is never checked.
                        if (!_linger) theirs.Dispose();
                    }
                },
                CancellationToken.None);

            return Task.FromResult(ours);
        }
    }

    /// <summary>Always returns the same peer.</summary>
    private sealed class StaticTracker : ITrackerClient
    {
        private readonly IPEndPoint _peer;

        public StaticTracker(IPEndPoint peer) => _peer = peer;

        public bool CanAnnounceTo(Uri tracker) => tracker.Scheme is "http" or "https";

        public Task<AnnounceResponse> AnnounceAsync(Uri tracker, AnnounceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new AnnounceResponse(new[] { _peer }, TimeSpan.FromMinutes(1), 1, 1));
    }
}
