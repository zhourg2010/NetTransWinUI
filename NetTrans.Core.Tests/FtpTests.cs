using NetTrans.Download;
using NetTrans.Models;
using NetTrans.Net;
using NetTrans.Net.Ftp;
using NetTrans.Tests.Fakes;
using Xunit;

namespace NetTrans.Tests;

/// <summary>
/// FTP, against a server in memory.
///
/// Mirrors are still the place a great many large files actually live, and a
/// download manager that cannot take an ftp:// link is missing the oldest half
/// of its job.
/// </summary>
public class FtpTests
{
    private static readonly byte[] Content = Enumerable.Range(0, 4096).Select(i => (byte)(i * 31 % 251)).ToArray();

    private static FakeFtpServer Server() => new FakeFtpServer().Add("/pub/thing.iso", Content);

    [Theory]
    [InlineData("ftp://mirror.test/pub/a.iso", true)]
    [InlineData("ftps://mirror.test/pub/a.iso", true)]
    [InlineData("https://mirror.test/pub/a.iso", false)]
    [InlineData("magnet:?xt=urn:btih:9f2c1a", false)]
    public void The_scheme_decides_which_transport_answers(string url, bool ftp) =>
        Assert.Equal(ftp, FtpTransport.Handles(new Uri(url)));

    [Fact]
    public async Task A_probe_learns_the_size_and_that_it_can_be_split()
    {
        var transport = new FtpTransport(Server());

        var info = await transport.ProbeAsync(new Uri("ftp://mirror.test/pub/thing.iso"), CancellationToken.None);

        Assert.Equal(Content.Length, info.Length);
        Assert.True(info.CanSplit);
        Assert.Equal("thing.iso", info.FileName);
        Assert.NotNull(info.LastModified);
    }

    [Fact]
    public async Task A_server_that_will_not_restart_is_not_split()
    {
        var server = Server();
        server.Restartable = false;

        var info = await new FtpTransport(server)
            .ProbeAsync(new Uri("ftp://mirror.test/pub/thing.iso"), CancellationToken.None);

        // The size is known, so the transfer still runs -- on one connection,
        // from the start, which is the only honest option left.
        Assert.Equal(Content.Length, info.Length);
        Assert.False(info.CanSplit);
    }

    [Fact]
    public async Task The_whole_file_comes_back()
    {
        var transport = new FtpTransport(Server());

        await using var stream = await transport
            .OpenAsync(new Uri("ftp://mirror.test/pub/thing.iso"), 0, null, CancellationToken.None);

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);

        Assert.Equal(Content, buffer.ToArray());
    }

    [Fact]
    public async Task A_restart_sends_only_the_rest_of_it()
    {
        var server = Server();
        var transport = new FtpTransport(server);

        await using var stream = await transport
            .OpenAsync(new Uri("ftp://mirror.test/pub/thing.iso"), 4000, null, CancellationToken.None);

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);

        Assert.Equal(Content.AsSpan(4000).ToArray(), buffer.ToArray());
        Assert.Contains("REST 4000", server.Commands);
    }

    [Fact]
    public async Task An_old_server_with_no_epsv_falls_back_to_pasv()
    {
        var server = Server();
        server.KnowsEpsv = false;

        await using var stream = await new FtpTransport(server)
            .OpenAsync(new Uri("ftp://mirror.test/pub/thing.iso"), 0, null, CancellationToken.None);

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);

        Assert.Equal(Content.Length, buffer.Length);
        Assert.Contains("PASV", server.Commands);
    }

    [Fact]
    public async Task Credentials_in_the_url_are_the_ones_it_logs_in_with()
    {
        var server = Server();
        server.Requires = ("someone", "hunter2");

        await using var stream = await new FtpTransport(server)
            .OpenAsync(new Uri("ftp://someone:hunter2@mirror.test/pub/thing.iso"), 0, null, CancellationToken.None);

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);

        Assert.Equal(Content.Length, buffer.Length);
        Assert.Contains("USER someone", server.Commands);
    }

    [Fact]
    public async Task A_wrong_password_is_reported_rather_than_hung_on()
    {
        var server = Server();
        server.Requires = ("someone", "hunter2");

        var transport = new FtpTransport(server);

        var failure = await Assert.ThrowsAsync<FtpException>(() =>
            transport.ProbeAsync(new Uri("ftp://someone:wrong@mirror.test/pub/thing.iso"), CancellationToken.None));

        Assert.Contains("登录失败", failure.Message);
    }

    [Fact]
    public async Task A_file_that_is_not_there_says_so()
    {
        var transport = new FtpTransport(Server());

        await Assert.ThrowsAsync<FtpException>(() =>
            transport.OpenAsync(new Uri("ftp://mirror.test/pub/missing.iso"), 0, null, CancellationToken.None));
    }

    [Fact]
    public async Task Closing_a_segment_early_ends_that_session()
    {
        var server = Server();
        var transport = new FtpTransport(server);

        // A segmented download stops as soon as its own range is full; FTP has
        // no way to say "enough" other than hanging up, so the session has to go
        // with the stream.
        var stream = await transport.OpenAsync(new Uri("ftp://mirror.test/pub/thing.iso"), 0, 100, CancellationToken.None);

        var head = new byte[100];
        await stream.ReadExactlyAsync(head, CancellationToken.None);
        await stream.DisposeAsync();

        Assert.Equal(Content.AsSpan(0, 100).ToArray(), head);

        // The next segment gets a session of its own rather than inheriting a
        // connection with a half-sent file on it.
        await using var second = await transport
            .OpenAsync(new Uri("ftp://mirror.test/pub/thing.iso"), 100, 200, CancellationToken.None);

        var next = new byte[100];
        await second.ReadExactlyAsync(next, CancellationToken.None);

        Assert.Equal(Content.AsSpan(100, 100).ToArray(), next);
        Assert.True(server.Sessions >= 2);
    }

    [Fact]
    public void The_dispatcher_sends_each_scheme_where_it_belongs()
    {
        var http = new CountingTransport();
        var ftp = new CountingTransport();
        var transport = new SchemeTransport(http, ftp);

        _ = transport.ProbeAsync(new Uri("https://site.test/a.iso"), CancellationToken.None);
        _ = transport.ProbeAsync(new Uri("ftp://mirror.test/a.iso"), CancellationToken.None);
        _ = transport.ProbeAsync(new Uri("ftps://mirror.test/a.iso"), CancellationToken.None);

        Assert.Equal(1, http.Probes);
        Assert.Equal(2, ftp.Probes);
    }

    [Fact]
    public async Task A_whole_transfer_runs_through_the_queue_over_ftp()
    {
        var sinks = new MemoryFileSinkFactory();
        var transport = new SchemeTransport(new FakeHttpTransport(Array.Empty<byte>()), new FtpTransport(Server()));

        var item = new DownloadItem
        {
            Id = 1,
            Name = "thing.iso",
            Host = "mirror.test",
            Kind = FileKind.Disc,
            Size = 0,
            Category = "soft",
            Url = "ftp://mirror.test/pub/thing.iso",
            SavePath = "/downloads",
            RequestedConnections = 4,
        };

        var job = new DownloadJob(item, transport, sinks, new ManualClock(), new DownloadOptions(Connections: 4, MinimumSegmentLength: 512, BufferSize: 256));

        Assert.Equal(JobOutcome.Completed, await job.RunAsync(CancellationToken.None));

        // Four connections, four sessions, four REST offsets -- the whole point
        // of knowing the size before starting.
        Assert.Equal(Content, sinks.Files.Values.Single().ToArray());
        Assert.Equal(4, item.Connections);
    }

    [Theory]
    [InlineData("227 Entering Passive Mode (127,0,0,1,156,64)", "127.0.0.1", 40000)]
    [InlineData("Entering Passive Mode 10,0,0,1,4,1", "10.0.0.1", 1025)]
    public void A_passive_reply_gives_up_its_address(string text, string host, int port)
    {
        Assert.True(FtpSession.TryReadPassive(text, out string? read, out int readPort));
        Assert.Equal(host, read);
        Assert.Equal(port, readPort);
    }

    [Fact]
    public void An_extended_passive_reply_is_just_the_port()
    {
        Assert.True(FtpSession.TryReadExtendedPort("Entering Extended Passive Mode (|||41234|)", out int port));
        Assert.Equal(41234, port);
    }

    [Theory]
    [InlineData("227 nonsense")]
    [InlineData("227 Entering Passive Mode (1,2,3)")]
    public void A_passive_reply_that_makes_no_sense_is_refused(string text) =>
        Assert.False(FtpSession.TryReadPassive(text, out _, out _));

    /// <summary>Counts what it was asked, so the dispatcher can be checked.</summary>
    private sealed class CountingTransport : IHttpTransport
    {
        public int Probes { get; private set; }

        public Task<RemoteFileInfo> ProbeAsync(Uri url, CancellationToken cancellationToken)
        {
            Probes++;
            return Task.FromResult(new RemoteFileInfo(1, false, null, null, "x"));
        }

        public Task<Stream> OpenAsync(Uri url, long from, long? to, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream());
    }
}
