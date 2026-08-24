using NetTrans.Services;
using Xunit;

namespace NetTrans.Tests;

/// <summary>The stub engine's arithmetic, with randomness injected so it is deterministic.</summary>
public class ProgressSimulatorTests
{
    public static TheoryData<double, double, double> SpeedCurve()
    {
        var data = new TheoryData<double, double, double>();
        foreach (var point in Golden.Data.SpeedCurve) data.Add(point.SpeedBytesPerSecond, point.Random, point.Expected);
        return data;
    }

    [Theory]
    [MemberData(nameof(SpeedCurve))]
    public void Speed_follows_the_prototypes_curve(double speed, double random, double expected) =>
        Assert.Equal(expected, ProgressSimulator.NextSpeed(speed, random), precision: 6);

    [Fact]
    public void Speed_never_falls_below_the_floor()
    {
        Assert.Equal(ProgressSimulator.MinimumSpeed, ProgressSimulator.NextSpeed(1, 0));
        Assert.Equal(ProgressSimulator.MinimumSpeed, ProgressSimulator.NextSpeed(0, 1));
    }

    [Fact]
    public void Progress_stops_at_the_file_size()
    {
        Assert.Equal(1000L, ProgressSimulator.Advance(done: 900, size: 1000, bytesPerSecond: 10_000, seconds: 0.9));
        Assert.Equal(1000L, ProgressSimulator.Advance(done: 1000, size: 1000, bytesPerSecond: 10_000, seconds: 0.9));
    }

    [Fact]
    public void Progress_advances_by_speed_times_elapsed() =>
        Assert.Equal(900L, ProgressSimulator.Advance(done: 0, size: 10_000, bytesPerSecond: 1000, seconds: 0.9));

    [Fact]
    public void Blocks_are_complete_up_to_the_completion_point()
    {
        var blocks = ProgressSimulator.MakeBlocks(0.5, 96, () => 1.0);

        Assert.Equal(96, blocks.Length);
        Assert.All(blocks[..48], block => Assert.Equal(1, block));
        Assert.All(blocks[48..], block => Assert.Equal(0, block));
    }

    [Fact]
    public void In_flight_blocks_only_appear_just_past_the_completion_point()
    {
        // A random draw of 0 always passes the 5% test, so every eligible cell
        // becomes an in-flight one -- that is the widest the band ever gets.
        var blocks = ProgressSimulator.MakeBlocks(0.5, 96, () => 0.0);

        Assert.All(blocks[..48], block => Assert.Equal(1, block));
        Assert.All(blocks[48..60], block => Assert.Equal(2, block));
        Assert.All(blocks[60..], block => Assert.Equal(0, block));
    }

    [Fact]
    public void A_finished_task_has_every_block_complete() =>
        Assert.All(ProgressSimulator.MakeBlocks(1.0, 96, () => 1.0), block => Assert.Equal(1, block));

    [Fact]
    public void An_untouched_task_has_no_complete_blocks() =>
        Assert.DoesNotContain(1, ProgressSimulator.MakeBlocks(0, 96, () => 1.0));

    [Fact]
    public void Connections_split_the_total_rate_with_jitter()
    {
        double total = 800 * 1024;
        var lowest = ProgressSimulator.MakeConnections(8, total, () => 0.0);
        var highest = ProgressSimulator.MakeConnections(8, total, () => 1.0);

        Assert.Equal(8, lowest.Length);
        Assert.All(lowest, value => Assert.Equal(total / 8 * 0.55, value, precision: 6));
        Assert.All(highest, value => Assert.Equal(total / 8 * 1.45, value, precision: 6));
    }

    [Fact]
    public void A_task_with_no_connections_reports_none() =>
        Assert.Empty(ProgressSimulator.MakeConnections(0, 1024, () => 0.5));

    [Fact]
    public void Idle_connections_carry_no_rate() =>
        Assert.All(ProgressSimulator.MakeConnections(4, 0, () => 0.5), value => Assert.Equal(0d, value));

    [Fact]
    public void History_grows_until_it_reaches_capacity()
    {
        double[] history = [];

        for (int i = 1; i <= 3; i++) history = ProgressSimulator.Push(history, i, capacity: 5);

        Assert.Equal(new double[] { 1, 2, 3 }, history);
    }

    [Fact]
    public void History_drops_the_oldest_sample_once_full()
    {
        double[] history = [1, 2, 3, 4, 5];

        history = ProgressSimulator.Push(history, 6, capacity: 5);

        Assert.Equal(new double[] { 2, 3, 4, 5, 6 }, history);
    }

    [Fact]
    public void History_of_zero_capacity_stays_empty() =>
        Assert.Empty(ProgressSimulator.Push([1, 2], 3, capacity: 0));
}
