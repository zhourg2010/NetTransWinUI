using NetTrans.Services;
using Xunit;

namespace NetTrans.Tests;

/// <summary>计划时段 -- which almost always wraps past midnight.</summary>
public class OffPeakWindowTests
{
    [Fact]
    public void The_default_window_wraps_past_midnight()
    {
        var window = OffPeakWindow.Parse("23:00", "07:00")!.Value;

        Assert.True(window.WrapsMidnight);
        Assert.True(window.Includes(new TimeOnly(23, 0)));
        Assert.True(window.Includes(new TimeOnly(3, 0)));
        Assert.False(window.Includes(new TimeOnly(22, 59)));
        Assert.False(window.Includes(new TimeOnly(9, 0)));
    }

    [Fact]
    public void A_window_inside_one_day_does_not_wrap()
    {
        var window = OffPeakWindow.Parse("09:00", "17:00")!.Value;

        Assert.False(window.WrapsMidnight);
        Assert.True(window.Includes(new TimeOnly(12, 0)));
        Assert.False(window.Includes(new TimeOnly(3, 0)));
    }

    [Fact]
    public void The_start_is_inside_and_the_end_is_not()
    {
        // So two back-to-back windows meet without overlapping.
        var window = OffPeakWindow.Parse("23:00", "07:00")!.Value;

        Assert.True(window.Includes(new TimeOnly(23, 0)));
        Assert.False(window.Includes(new TimeOnly(7, 0)));
    }

    [Theory]
    [InlineData("23:00", "23:00")]  // a whole day and no day at all read the same
    [InlineData("nonsense", "07:00")]
    [InlineData("23:00", "")]
    [InlineData(null, null)]
    public void An_unreadable_window_is_no_window(string? start, string? end) =>
        Assert.Null(OffPeakWindow.Parse(start, end));

    [Fact]
    public void The_next_change_is_the_far_edge_of_wherever_we_are()
    {
        var window = OffPeakWindow.Parse("23:00", "07:00")!.Value;

        // Inside it at 02:00, so the next change is this morning's end.
        Assert.Equal(At(14, 7, 0), window.NextChange(At(14, 2, 0)));

        // Outside it at 12:00, so the next change is tonight's start.
        Assert.Equal(At(14, 23, 0), window.NextChange(At(14, 12, 0)));

        // Inside it at 23:30, so the next change is tomorrow morning.
        Assert.Equal(At(15, 7, 0), window.NextChange(At(14, 23, 30)));
    }

    private static DateTimeOffset At(int day, int hour, int minute) =>
        new(2026, 3, day, hour, minute, 0, TimeSpan.Zero);
}
