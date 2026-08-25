using System.Net;
using NetTrans.Download;
using NetTrans.Models;
using NetTrans.Tests.Fakes;
using NetTrans.Torrent;
using Xunit;

namespace NetTrans.Tests;

/// <summary>
/// A torrent driven through the queue's own job interface, against a seeding
/// peer and a tracker that points at it.
/// </summary>
public class TorrentJobTests
{
    [Theory]
    [InlineData("magnet:?xt=urn:btih:9f2c1a3b4d5e6f708192a3b4c5d6e7f809112233")]
    [InlineData("https://site.test/files/thing.torrent")]
    [InlineData("https://site.test/dl.torrent?passkey=abc")]
    [InlineData(@"D:\seeds\thing.torrent")]
    [InlineData("thing.TORRENT")]
    public void A_torrent_is_recognised_from_what_was_pasted(string text) =>
        Assert.True(TorrentUrl.IsTorrent(text));

    [Theory]
    [InlineData("https://example.test/file.iso")]
    [InlineData("https://cdn.test/hls/index.m3u8")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("torrent")]
    public void Anything_else_is_not(string? text) => Assert.False(TorrentUrl.IsTorrent(text));

    [Fact]
    public void A_torrent_url_with_a_passkey_is_still_a_torrent_url()
    {
        // The query is where a private tracker's passkey lives and routinely
        // ends in something extension-shaped; only the path decides.
        Assert.True(TorrentUrl.IsTorrentFile("https://pt.test/download.php/1234/x.torrent?passkey=deadbeef"));
        Assert.False(TorrentUrl.IsTorrentFile("https://pt.test/download.php?id=1&ext=.torrent"));
    }

    [Fact]
    public void The_row_has_something_to_show_before_the_metainfo_arrives()
    {
        Assert.Equal("Some Release", TorrentUrl.Describe(
            "magnet:?xt=urn:btih:9f2c1a3b4d5e6f708192a3b4c5d6e7f809112233&dn=Some%20Release"));

        Assert.StartsWith("磁力链 9f2c1a3b4d5e", TorrentUrl.Describe(
            "magnet:?xt=urn:btih:9f2c1a3b4d5e6f708192a3b4c5d6e7f809112233"));

        Assert.Equal("thing", TorrentUrl.Describe("https://site.test/files/thing.torrent"));
    }

    [Fact]
    public async Task A_torrent_file_downloads_end_to_end_through_the_job()
    {
        var world = new World();

        Assert.Equal(JobOutcome.Completed, await world.RunAsync());

        Assert.Equal(world.Content, world.Written());
        Assert.Equal(DownloadStatus.Completed, world.Item.Status);
        Assert.Equal(world.Item.Size, world.Item.Done);

        // Every piece was checked against the torrent's own SHA-1 on the way
        // in, which is stronger than hashing the file afterwards.
        Assert.Equal(FileHash.Verified, world.Item.Checksum);

        // The BT-only rows are what the inspector shows instead of 校验, and a
        // null there is the dash it was showing before any of this was written.
        Assert.NotNull(world.Item.Ratio);
        Assert.NotNull(world.Item.Seeds);
        Assert.NotNull(world.Item.Peers);
    }

    [Fact]
    public async Task Only_the_chosen_files_are_fetched()
    {
        var world = new World(multiFile: true)
        {
            // The film, not the sample.
            Wanted = new List<string> { "movie.bin" },
        };

        Assert.Equal(JobOutcome.Completed, await world.RunAsync());

        // Three of the four pieces. The third holds the end of the film and the
        // start of the sample, and is fetched whole because a piece is the
        // smallest thing that can be verified.
        Assert.Equal(3, world.Seed.BlocksServed);
        Assert.Contains(world.Item.Log, entry => entry.Message.Contains("只下载 1/2 个文件"));
    }

    [Fact]
    public async Task Choosing_everything_is_the_same_as_choosing_nothing()
    {
        var world = new World(multiFile: true)
        {
            Wanted = new List<string> { "movie.bin", "sample.bin" },
        };

        Assert.Equal(JobOutcome.Completed, await world.RunAsync());
        Assert.DoesNotContain(world.Item.Log, entry => entry.Message.Contains("只下载"));
    }

    [Fact]
    public async Task The_row_learns_the_name_and_size_from_the_metainfo()
    {
        var world = new World();

        // Before: whatever the URL suggested. After: what the torrent says.
        Assert.NotEqual("wanted.bin", world.Item.Name);

        await world.RunAsync();

        Assert.Equal("wanted.bin", world.Item.Name);
        Assert.Equal(world.Content.Length, world.Item.Size);
    }

    [Fact]
    public async Task A_torrent_with_no_reachable_peers_fails_with_a_reason()
    {
        var world = new World();
        world.Connector.Dead.Add(World.SeedAddress.ToString());

        Assert.Equal(JobOutcome.Failed, await world.RunAsync(timeoutMilliseconds: 2000));
        Assert.False(string.IsNullOrEmpty(world.Item.Error()));
    }

    [Fact]
    public async Task A_tracker_that_returns_nothing_is_reported_rather_than_waited_on()
    {
        var world = new World(peers: Array.Empty<IPEndPoint>());

        Assert.Equal(JobOutcome.Failed, await world.RunAsync(timeoutMilliseconds: 2000));
    }

    [Fact]
    public async Task Files_already_on_disk_are_hashed_instead_of_downloaded_again()
    {
        var world = new World();

        // A previous run left everything, but no sidecar -- which is what
        // seeding something already downloaded looks like.
        world.PlaceEverythingOnDisk();

        Assert.Equal(JobOutcome.Completed, await world.RunAsync());

        // Nothing was asked of the seed: it was all verified locally.
        Assert.Equal(0, world.Seed.BlocksServed);
        Assert.Contains(world.Item.Log, entry => entry.Message.Contains("校验完成"));
    }

    [Fact]
    public async Task A_recheck_hashes_the_files_instead_of_believing_the_resume_record()
    {
        var world = new World { Recheck = true };
        world.PlaceEverythingOnDisk();

        Assert.Equal(JobOutcome.Completed, await world.RunAsync());

        // Nothing was asked of the seed, and the log says why: the files were
        // hashed rather than trusted or fetched.
        Assert.Equal(0, world.Seed.BlocksServed);
        Assert.Contains(world.Item.Log, entry => entry.Message.Contains("强制校验"));
        Assert.Contains(world.Item.Log, entry => entry.Message.Contains("校验完成"));
    }

    [Fact]
    public async Task Something_that_is_neither_a_magnet_nor_a_readable_torrent_says_so()
    {
        var world = new World();
        world.Item.Url = "/no/such/file.torrent";

        Assert.Equal(JobOutcome.Failed, await world.RunAsync(timeoutMilliseconds: 2000));
    }

    /// <summary>A torrent, a seed that has it, and a tracker that points at the seed.</summary>
    private sealed class World
    {
        public static readonly IPEndPoint SeedAddress = new(IPAddress.Parse("10.0.0.7"), 6881);

        private readonly MemoryFileSinkFactory _sinks = new();
        private readonly TorrentBuilder _builder;
        private readonly string _directory;

        public World(IPEndPoint[]? peers = null, bool multiFile = false)
        {
            _directory = Path.Combine(Path.GetTempPath(), "nettrans-bt-" + Guid.NewGuid().ToString("N"));

            _builder = new TorrentBuilder { Name = "wanted.bin", PieceLength = 256 };

            Content = new byte[1000];
            for (int i = 0; i < Content.Length; i++) Content[i] = (byte)(i * 41 % 251);

            if (multiFile)
            {
                // A film and the sample clip nobody asked for, which is the
                // shape 选择文件 exists for.
                _builder.Name = "release";
                _builder.Add("movie.bin", Content.AsSpan(0, 700).ToArray());
                _builder.Add("sample.bin", Content.AsSpan(700).ToArray());
            }
            else
            {
                _builder.Add("wanted.bin", Content);
            }

            _builder.Trackers.Add("http://tracker.test/announce");

            Torrent = TorrentMetainfo.Parse(_builder.Build());
            Seed = new FakeSeed(Torrent, Content);

            Connector = new FakePeerConnector(Torrent.InfoHash).Add(SeedAddress, Seed);
            Transport = new TrackerServing(peers ?? new[] { SeedAddress }, _builder.Build());

            Item = new DownloadItem
            {
                Id = 1,
                Name = "thing",
                Host = "tracker.test",
                Kind = FileKind.Disc,
                Size = 0,
                Category = "bt",
                Url = "https://site.test/thing.torrent",
                SavePath = _directory,
                RequestedConnections = 2,
            };
        }

        public TorrentMetainfo Torrent { get; }

        public FakeSeed Seed { get; }

        public FakePeerConnector Connector { get; }

        public TrackerServing Transport { get; }

        public DownloadItem Item { get; }

        public byte[] Content { get; }

        public byte[] Written() => _sinks.Files.Values.Single().ToArray();

        /// <summary>Writes the whole torrent out, as a finished earlier run would have.</summary>
        public void PlaceEverythingOnDisk()
        {
            var store = new PieceStore(Torrent, _sinks, _directory);

            for (int piece = 0; piece < Torrent.PieceCount; piece++)
            {
                int length = (int)Torrent.LengthOfPiece(piece);

                store.WriteAsync(piece, Content.AsSpan(piece * 256, length).ToArray(), CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
        }

        /// <summary>选择文件, as the sheet would have set it.</summary>
        public List<string>? Wanted { get; set; }

        /// <summary>强制校验, as the inspector's row would have set it.</summary>
        public bool Recheck { get; set; }

        public async Task<JobOutcome> RunAsync(int timeoutMilliseconds = 8000)
        {
            var job = new TorrentJob(Item, Transport, _sinks, new ManualClock(), Options(), Connector)
            {
                WantedFiles = Wanted,
                Recheck = Recheck,

                // 下完即停. Seeding has no natural end, and these tests are about
                // what happens up to the last piece -- without this each one
                // sits until its own deadline. Seeding has its own tests.
                SeedingLimits = new SeedingLimits(MaxSeedingTime: TimeSpan.Zero),
            };

            using var cancellation = new CancellationTokenSource(timeoutMilliseconds);

            try
            {
                return await job.RunAsync(cancellation.Token);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
                }
                catch (IOException)
                {
                    // Not worth failing a test over.
                }
            }
        }

        private static DownloadOptions Options() => new(
            Connections: 2,
            BufferSize: 4096,
            MaxRetries: 0,
            RetryDelay: TimeSpan.FromMilliseconds(1));
    }

    /// <summary>Serves the .torrent over HTTP and answers the tracker announce.</summary>
    private sealed class TrackerServing : NetTrans.Net.IHttpTransport
    {
        private readonly IPEndPoint[] _peers;
        private readonly byte[] _torrent;

        public TrackerServing(IPEndPoint[] peers, byte[] torrent)
        {
            _peers = peers;
            _torrent = torrent;
        }

        public Task<NetTrans.Net.RemoteFileInfo> ProbeAsync(Uri url, CancellationToken cancellationToken) =>
            Task.FromResult(new NetTrans.Net.RemoteFileInfo(_torrent.Length, true, null, null, "thing.torrent"));

        public Task<Stream> OpenAsync(Uri url, long from, long? to, CancellationToken cancellationToken)
        {
            if (url.AbsolutePath.EndsWith(".torrent", StringComparison.Ordinal))
            {
                return Task.FromResult<Stream>(new MemoryStream(_torrent));
            }

            var compact = new byte[_peers.Length * 6];

            for (int i = 0; i < _peers.Length; i++)
            {
                _peers[i].Address.GetAddressBytes().CopyTo(compact.AsSpan(i * 6, 4));
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(compact.AsSpan(i * 6 + 4), (ushort)_peers[i].Port);
            }

            var body = Bencode.Encode(Bencode.Dictionary(
                ("interval", Bencode.Number(1800)),
                ("complete", Bencode.Number(1)),
                ("incomplete", Bencode.Number(0)),
                ("peers", Bencode.String(compact))));

            return Task.FromResult<Stream>(new MemoryStream(body));
        }
    }
}

/// <summary>Reads the error off an item without a null dance at every call site.</summary>
internal static class DownloadItemErrorExtensions
{
    public static string Error(this DownloadItem item) => item.ErrorMessage ?? "";
}
