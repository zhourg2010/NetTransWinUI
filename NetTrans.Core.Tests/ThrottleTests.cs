using NetTrans.Download;
using Xunit;

namespace NetTrans.Tests;

/// <summary>The speed readouts and the 限速 dropdowns.</summary>
public class ThrottleTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_fresh_meter_reads_zero() =>
        Assert.Equal(0, new SpeedMeter(TimeSpan.FromSeconds(3)).BytesPerSecond(Start));

    [Fact]
    public void Averages_over_the_span_the_samples_cover()
    {
        var meter = new SpeedMeter(TimeSpan.FromSeconds(3));

        meter.Record(1000, Start);
        meter.Record(1000, Start + TimeSpan.FromSeconds(1));

        // 2000 bytes spanning one second.
        Assert.Equal(2000, meter.BytesPerSecond(Start + TimeSpan.FromSeconds(1)), precision: 3);
    }

    [Fact]
    public void Forgets_samples_that_fall_out_of_the_window()
    {
        var meter = new SpeedMeter(TimeSpan.FromSeconds(3));

        meter.Record(9000, Start);
        meter.Record(1000, Start + TimeSpan.FromSeconds(4));

        // The 9000 is older than the window, so only the recent sample counts.
        Assert.Equal(1000, meter.Total - 9000);
        Assert.True(meter.BytesPerSecond(Start + TimeSpan.FromSeconds(4)) < 9000);
    }

    [Fact]
    public void Keeps_a_running_total_regardless_of_the_window()
    {
        var meter = new SpeedMeter(TimeSpan.FromSeconds(1));

        meter.Record(500, Start);
        meter.Record(500, Start + TimeSpan.FromSeconds(10));

        Assert.Equal(1000, meter.Total);
    }

    [Fact]
    public void Reset_clears_the_window_but_not_the_total()
    {
        var meter = new SpeedMeter(TimeSpan.FromSeconds(3));
        meter.Record(1000, Start);

        meter.Reset();

        Assert.Equal(0, meter.BytesPerSecond(Start));
        Assert.Equal(1000, meter.Total);
    }

    [Fact]
    public void A_window_must_be_positive() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpeedMeter(TimeSpan.Zero));

    [Fact]
    public void An_unlimited_bucket_never_waits()
    {
        var bucket = new TokenBucket(0, Start);

        Assert.True(bucket.IsUnlimited);
        Assert.Equal(TimeSpan.Zero, bucket.Take(10_000_000, Start));
    }

    [Fact]
    public void Spends_the_burst_before_it_starts_waiting()
    {
        // 1000 B/s with a one-second burst: the first 1000 bytes are free.
        var bucket = new TokenBucket(1000, Start);

        Assert.Equal(TimeSpan.Zero, bucket.Take(1000, Start));
        Assert.True(bucket.Take(1000, Start) > TimeSpan.Zero);
    }

    [Fact]
    public void Waits_in_proportion_to_the_overdraft()
    {
        var bucket = new TokenBucket(1000, Start);
        bucket.Take(1000, Start);           // burst spent

        var wait = bucket.Take(500, Start); // half a second's worth

        Assert.Equal(0.5, wait.TotalSeconds, precision: 3);
    }

    [Fact]
    public void Refills_as_time_passes()
    {
        var bucket = new TokenBucket(1000, Start);
        bucket.Take(1000, Start);

        // A second later the bucket is full again.
        Assert.Equal(TimeSpan.Zero, bucket.Take(1000, Start + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Refilling_never_exceeds_the_burst()
    {
        var bucket = new TokenBucket(1000, Start);

        // Ten idle seconds must not bank ten seconds of allowance.
        Assert.Equal(TimeSpan.Zero, bucket.Take(1000, Start + TimeSpan.FromSeconds(10)));
        Assert.True(bucket.Take(1000, Start + TimeSpan.FromSeconds(10)) > TimeSpan.Zero);
    }

    [Fact]
    public void Sustained_throughput_settles_at_the_limit()
    {
        var bucket = new TokenBucket(1000, Start);
        var now = Start;
        long moved = 0;

        // Move 200 bytes at a time for ten seconds of simulated time.
        while (now < Start + TimeSpan.FromSeconds(10))
        {
            var wait = bucket.Take(200, now);
            now += wait;
            moved += 200;
        }

        double achieved = moved / (now - Start).TotalSeconds;
        Assert.InRange(achieved, 900, 1200);
    }

    [Fact]
    public void Lowering_the_limit_takes_effect_immediately()
    {
        var bucket = new TokenBucket(10_000, Start);
        bucket.BytesPerSecond = 1000;

        bucket.Take(1000, Start);
        Assert.True(bucket.Take(1000, Start) > TimeSpan.Zero);
    }

    [Fact]
    public void Raising_the_limit_to_zero_means_unlimited()
    {
        var bucket = new TokenBucket(1000, Start);
        bucket.BytesPerSecond = 0;

        Assert.True(bucket.IsUnlimited);
        Assert.Equal(TimeSpan.Zero, bucket.Take(10_000_000, Start));
    }

    [Fact]
    public void A_burst_must_be_positive() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBucket(1000, Start, burstSeconds: 0));
}

/// <summary>Reading the 限速 dropdown's labels back into a rate.</summary>
public class SpeedLimitTests
{
    [Theory]
    [InlineData("不限", 0d)]
    [InlineData("", 0d)]
    [InlineData(null, 0d)]
    [InlineData("128 KB/s", 128 * 1024d)]
    [InlineData("256 KB/s", 256 * 1024d)]
    [InlineData("512 KB/s", 512 * 1024d)]
    [InlineData("1 MB/s", 1024 * 1024d)]
    [InlineData("2 MB/s", 2 * 1024 * 1024d)]
    [InlineData("4 MB/s", 4 * 1024 * 1024d)]
    [InlineData("1.5 MB/s", 1.5 * 1024 * 1024)]
    public void Parses_every_label_the_sheets_offer(string? label, double expected) =>
        Assert.Equal(expected, NetTrans.Download.SpeedLimits.Parse(label));

    [Fact]
    public void An_unreadable_label_means_unlimited() =>
        Assert.Equal(0d, NetTrans.Download.SpeedLimits.Parse("as fast as possible"));
}
