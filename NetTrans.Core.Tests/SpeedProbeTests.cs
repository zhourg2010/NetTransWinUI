using NetTrans.Download;
using NetTrans.Net;
using NetTrans.Tests.Fakes;
using Xunit;

namespace NetTrans.Tests;

/// <summary>
/// 测速, driven by a clock the test owns: the same code that measures a real
/// Ubuntu mirror, with the seconds dictated instead of waited for.
/// </summary>
public class SpeedProbeTests
{
    private static readonly Uri Url = new("https://mirror.example.com/ubuntu/big.gz");

    /// <summary>A stream that spends a fixed amount of the clock per chunk, so throughput is exactly known.</summary>
    private sealed class PacedTransport : IHttpTransport
    {
        private readonly ManualClock _clock;
        private readonly int _chunk;
        private readonly TimeSpan _perChunk;
        private readonly TimeSpan _connect;

        public PacedTransport(ManualClock clock, int chunk, TimeSpan perChunk, TimeSpan connect)
        {
            _clock = clock;
            _chunk = chunk;
            _perChunk = perChunk;
            _connect = connect;
        }

        public long Served { get; private set; }

        public (long From, long? To)? LastRange { get; private set; }

        public Task<RemoteFileInfo> ProbeAsync(Uri url, CancellationToken cancellationToken) =>
            Task.FromResult(new RemoteFileInfo(long.MaxValue, true, null, null, "big.gz"));

        public Task<Stream> OpenAsync(Uri url, long from, long? to, CancellationToken cancellationToken)
        {
            LastRange = (from, to);
            _clock.Advance(_connect);
            return Task.FromResult<Stream>(new PacedStream(this));
        }

        private sealed class PacedStream : Stream
        {
            private readonly PacedTransport _owner;

            public PacedStream(PacedTransport owner) => _owner = owner;

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                int take = Math.Min(buffer.Length, _owner._chunk);
                buffer.Span[..take].Clear();

                _owner._clock.Advance(_owner._perChunk);
                _owner.Served += take;

                return ValueTask.FromResult(take);
            }

            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => long.MaxValue;
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    [Fact]
    public async Task Measures_throughput_over_the_transfer_not_the_handshake()
    {
        var clock = new ManualClock();

        // 1 MB per second of transfer, after half a second of getting there.
        var transport = new PacedTransport(clock, chunk: 128 * 1024, perChunk: TimeSpan.FromSeconds(0.125), connect: TimeSpan.FromSeconds(0.5));

        var sample = await SpeedProbe.MeasureAsync(
            transport, "example", Url, budget: 4 * 1024 * 1024, limit: TimeSpan.FromMinutes(1), clock);

        Assert.Equal(4 * 1024 * 1024, sample.Bytes);
        Assert.Equal(TimeSpan.FromSeconds(0.5), sample.Connect);
        Assert.Equal(1024d * 1024, sample.BytesPerSecond, 0);
        Assert.True(sample.Ok);
    }

    /// <summary>Reading past the budget would measure a different amount from every other mirror.</summary>
    [Fact]
    public async Task Reads_exactly_the_budget_and_asks_for_exactly_that_range()
    {
        var clock = new ManualClock();
        var transport = new PacedTransport(clock, chunk: 100_000, perChunk: TimeSpan.FromSeconds(0.1), connect: TimeSpan.Zero);

        var sample = await SpeedProbe.MeasureAsync(
            transport, "example", Url, budget: 250_000, limit: TimeSpan.FromMinutes(1), clock);

        Assert.Equal(250_000, sample.Bytes);
        Assert.Equal(250_000, transport.Served);
        Assert.NotNull(transport.LastRange);
        Assert.Equal(0, transport.LastRange!.Value.From);
        Assert.Equal(249_999, transport.LastRange.Value.To);
    }

    /// <summary>One mirror crawling must not own the whole run.</summary>
    [Fact]
    public async Task Gives_up_on_a_slow_mirror_at_the_time_limit()
    {
        var clock = new ManualClock();
        var transport = new PacedTransport(clock, chunk: 64 * 1024, perChunk: TimeSpan.FromSeconds(2), connect: TimeSpan.Zero);

        var sample = await SpeedProbe.MeasureAsync(
            transport, "slow", Url, budget: 8 * 1024 * 1024, limit: TimeSpan.FromSeconds(10), clock);

        Assert.True(sample.Bytes < 8 * 1024 * 1024);
        Assert.Equal(TimeSpan.FromSeconds(10), sample.Elapsed);

        // Still a result: 320 KB in 10 s is a measurement, not a failure.
        Assert.True(sample.Ok);
        Assert.Equal(32d * 1024, sample.BytesPerSecond, 0);
    }

    [Fact]
    public async Task A_mirror_that_refuses_is_reported_rather_than_thrown()
    {
        var clock = new ManualClock();
        var site = new FakeWebsite().Broken(Url.AbsoluteUri);

        var sample = await SpeedProbe.MeasureAsync(
            site, "broken", Url, budget: 1024, limit: TimeSpan.FromSeconds(5), clock);

        Assert.False(sample.Ok);
        Assert.Equal("连不上", sample.Error);
        Assert.Equal(0, sample.Bytes);
    }

    [Fact]
    public async Task Measures_every_mirror_in_turn()
    {
        var clock = new ManualClock();
        var transport = new PacedTransport(clock, chunk: 64 * 1024, perChunk: TimeSpan.FromSeconds(0.1), connect: TimeSpan.Zero);

        var mirrors = new[]
        {
            ("上海", new Uri("https://cn.example.com/ubuntu/big.gz")),
            ("东京", new Uri("https://jp.example.com/ubuntu/big.gz")),
        };

        var samples = await SpeedProbe.MeasureAllAsync(
            transport, mirrors, budget: 128 * 1024, limit: TimeSpan.FromSeconds(30), clock);

        Assert.Equal(new[] { "上海", "东京" }, samples.Select(sample => sample.Name));
        Assert.Equal(256 * 1024, transport.Served);
    }

    [Fact]
    public void Ranks_fastest_first_and_failures_last()
    {
        var samples = new[]
        {
            new SpeedSample("慢", Url, 1_000_000, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(11)),
            new SpeedSample("坏", Url, 0, TimeSpan.Zero, TimeSpan.FromSeconds(5), "超时"),
            new SpeedSample("快", Url, 8_000_000, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)),
        };

        var ranked = SpeedReport.Rank(samples);

        Assert.Equal(new[] { "快", "慢", "坏" }, ranked.Select(sample => sample.Name));
        Assert.Contains("最快 快", SpeedReport.Verdict(samples));
    }

    [Fact]
    public void Draws_a_table_with_a_row_per_mirror()
    {
        var samples = new[]
        {
            new SpeedSample("清华", Url, 8 * 1024 * 1024, TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(1.2)),
            new SpeedSample("英国", Url, 0, TimeSpan.Zero, TimeSpan.FromSeconds(5), "超时"),
        };

        var table = SpeedReport.Table(samples);
        var lines = table.Split('\n');

        Assert.Equal(4, lines.Length);
        Assert.StartsWith("| 节点 ", lines[0]);
        Assert.Contains("清华", lines[2]);
        Assert.Contains("超时", lines[3]);
    }
}
