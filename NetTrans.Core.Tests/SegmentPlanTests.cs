using NetTrans.Download;
using Xunit;

namespace NetTrans.Tests;

/// <summary>How a file is divided and how far along each piece is.</summary>
public class SegmentPlanTests
{
    [Fact]
    public void Splits_a_file_into_contiguous_ranges()
    {
        var plan = SegmentPlan.Create(1000, 4, minimumSegmentLength: 0);

        Assert.Equal(4, plan.Segments.Count);
        Assert.Equal(0, plan.Segments[0].Start);
        Assert.Equal(999, plan.Segments[^1].End);

        for (int i = 1; i < plan.Segments.Count; i++)
        {
            Assert.Equal(plan.Segments[i - 1].End + 1, plan.Segments[i].Start);
        }

        Assert.Equal(1000, plan.Segments.Sum(segment => segment.Length));
    }

    [Fact]
    public void The_last_segment_absorbs_the_remainder()
    {
        var plan = SegmentPlan.Create(1003, 4, minimumSegmentLength: 0);

        Assert.Equal(250, plan.Segments[0].Length);
        Assert.Equal(253, plan.Segments[^1].Length);
    }

    [Fact]
    public void Will_not_split_below_the_minimum_segment_size()
    {
        var plan = SegmentPlan.Create(3 * 1024 * 1024, 8, minimumSegmentLength: 1024 * 1024);
        Assert.Equal(3, plan.Segments.Count);
    }

    [Fact]
    public void A_small_file_stays_whole()
    {
        var plan = SegmentPlan.Create(100_000, 8, minimumSegmentLength: 1024 * 1024);
        Assert.Single(plan.Segments);
    }

    [Fact]
    public void An_unknown_length_gets_a_single_unbounded_segment()
    {
        var plan = SegmentPlan.Create(-1, 8);

        Assert.False(plan.IsBounded);
        Assert.Single(plan.Segments);
        Assert.Equal(0, plan.Fraction);
    }

    [Fact]
    public void Progress_is_the_sum_of_the_segments()
    {
        var plan = SegmentPlan.Create(1000, 4, minimumSegmentLength: 0);
        plan.Segments[0].Position = 250;   // done
        plan.Segments[1].Position = 300;   // 50 of 250

        Assert.Equal(300, plan.Downloaded);
        Assert.Equal(0.3, plan.Fraction, precision: 6);
        Assert.False(plan.IsComplete);
    }

    [Fact]
    public void Complete_when_every_segment_is_past_its_end()
    {
        var plan = SegmentPlan.Create(1000, 2, minimumSegmentLength: 0);
        foreach (var segment in plan.Segments) segment.Position = segment.End + 1;

        Assert.True(plan.IsComplete);
        Assert.Equal(1000, plan.Downloaded);
        Assert.Equal(1, plan.Fraction);
        Assert.Equal(0, plan.ActiveSegmentCount);
    }

    [Fact]
    public void An_unbounded_segment_can_finish_without_moving_its_position()
    {
        var plan = SegmentPlan.Unbounded();
        plan.Segments[0].Position = 4096;
        plan.Segments[0].MarkComplete();

        Assert.True(plan.IsComplete);
        Assert.Equal(4096, plan.Downloaded);
    }

    [Fact]
    public void Round_trips_through_a_snapshot()
    {
        var plan = SegmentPlan.Create(1000, 4, minimumSegmentLength: 0);
        plan.Segments[0].Position = 120;
        plan.Segments[2].Position = 700;

        var restored = SegmentPlan.Restore(1000, plan.Snapshot());

        Assert.Equal(plan.Downloaded, restored.Downloaded);
        Assert.Equal(plan.Segments.Count, restored.Segments.Count);
        Assert.Equal(120, restored.Segments[0].Position);
        Assert.Equal(700, restored.Segments[2].Position);
    }

    [Fact]
    public void The_chunk_map_marks_finished_cells_and_the_live_head()
    {
        var plan = SegmentPlan.Create(9600, 1, minimumSegmentLength: 0);
        plan.Segments[0].Position = 4800; // exactly half

        var map = plan.BlockMap(96);

        Assert.Equal(96, map.Length);
        Assert.All(map[..48], cell => Assert.Equal(1, cell));
        Assert.Equal(2, map[48]);          // the cell the connection is inside
        Assert.All(map[49..], cell => Assert.Equal(0, cell));
    }

    [Fact]
    public void The_chunk_map_is_empty_for_an_unknown_length()
    {
        var map = SegmentPlan.Unbounded().BlockMap(96);
        Assert.All(map, cell => Assert.Equal(0, cell));
    }

    [Fact]
    public void A_finished_transfer_fills_the_whole_chunk_map()
    {
        var plan = SegmentPlan.Create(9600, 4, minimumSegmentLength: 0);
        foreach (var segment in plan.Segments) segment.Position = segment.End + 1;

        Assert.All(plan.BlockMap(96), cell => Assert.Equal(1, cell));
    }

    [Fact]
    public void A_segment_reports_what_is_left()
    {
        var segment = new Segment(100, 199);
        Assert.Equal(100, segment.Length);
        Assert.Equal(100, segment.Remaining);
        Assert.Equal(0, segment.Downloaded);

        segment.Position = 150;
        Assert.Equal(50, segment.Remaining);
        Assert.Equal(50, segment.Downloaded);

        segment.Position = 200;
        Assert.True(segment.IsComplete);
        Assert.Equal(0, segment.Remaining);
    }

    [Fact]
    public void A_segment_cannot_end_before_it_starts() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new Segment(100, 50));

    [Fact]
    public void A_transfer_needs_at_least_one_connection() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SegmentPlan.Create(1000, 0));
}
