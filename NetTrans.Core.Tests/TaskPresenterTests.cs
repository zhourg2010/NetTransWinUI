using NetTrans.Models;
using NetTrans.Services;
using Xunit;

namespace NetTrans.Tests;

/// <summary>The row and inspector strings, against the expressions in Row() and Inspector().</summary>
public class TaskPresenterTests
{
    public static TheoryData<int> RowIds()
    {
        var data = new TheoryData<int>();
        foreach (var row in Golden.Data.Rows) data.Add(row.Id);
        return data;
    }

    public static TheoryData<string, string> StateNames()
    {
        var data = new TheoryData<string, string>();
        foreach (var state in Golden.Data.StateNames) data.Add(state.State, state.Expected);
        return data;
    }

    public static TheoryData<double, string> PercentMidpoints()
    {
        var data = new TheoryData<double, string>();
        foreach (var percent in Golden.Data.Midpoints.Percents) data.Add(percent.Percent, percent.Expected);
        return data;
    }

    [Theory]
    [MemberData(nameof(RowIds))]
    public void SubText_matches_the_prototype(int id)
    {
        var row = Golden.Data.Rows.Single(r => r.Id == id);
        Assert.Equal(row.SubText, TaskPresenter.SubText(Golden.ToItem(row)));
    }

    [Theory]
    [MemberData(nameof(RowIds))]
    public void TrailingText_matches_the_prototype(int id)
    {
        var row = Golden.Data.Rows.Single(r => r.Id == id);
        Assert.Equal(row.TrailingText, TaskPresenter.TrailingText(Golden.ToItem(row)));
    }

    [Theory]
    [MemberData(nameof(RowIds))]
    public void Percent_matches_the_prototype(int id)
    {
        var row = Golden.Data.Rows.Single(r => r.Id == id);
        Assert.Equal(row.Percent, TaskPresenter.Percent(row.DoneBytes, row.SizeBytes), precision: 6);
    }

    [Fact]
    public void RingCaption_matches_the_prototype()
    {
        var seed = Golden.Seed().ToDictionary(t => t.Id);

        foreach (var caption in Golden.Data.RingCaptions)
        {
            Assert.Equal(caption.Expected, TaskPresenter.RingCaption(seed[caption.Id]));
        }
    }

    [Theory]
    [MemberData(nameof(StateNames))]
    public void StatusText_matches_STATE_CN(string state, string expected)
    {
        var status = state switch
        {
            "run" => DownloadStatus.Downloading,
            "paused" => DownloadStatus.Paused,
            "done" => DownloadStatus.Completed,
            "error" => DownloadStatus.Error,
            _ => DownloadStatus.Queued,
        };

        Assert.Equal(expected, TaskPresenter.StatusText(status));
    }

    [Theory]
    [MemberData(nameof(PercentMidpoints))]
    public void PercentText_rounds_halves_away_from_zero(double percent, string expected)
    {
        // Pick a done/size pair that lands exactly on the percentage under test.
        const long size = 200_000;
        long done = (long)Math.Round(size * percent / 100);
        Assert.Equal(expected, TaskPresenter.PercentText(done, size));
    }

    [Fact]
    public void Progress_track_is_hidden_only_when_finished_or_queued()
    {
        Assert.False(TaskPresenter.ShowProgress(DownloadStatus.Completed));
        Assert.False(TaskPresenter.ShowProgress(DownloadStatus.Queued));
        Assert.True(TaskPresenter.ShowProgress(DownloadStatus.Downloading));
        Assert.True(TaskPresenter.ShowProgress(DownloadStatus.Paused));
        Assert.True(TaskPresenter.ShowProgress(DownloadStatus.Error));
    }

    [Fact]
    public void Toggle_label_offers_retry_after_a_failure()
    {
        Assert.Equal("暂停", TaskPresenter.ToggleLabel(DownloadStatus.Downloading));
        Assert.Equal("重试", TaskPresenter.ToggleLabel(DownloadStatus.Error));
        Assert.Equal("继续", TaskPresenter.ToggleLabel(DownloadStatus.Paused));

        // Departs from the prototype: a queued task is waiting for a slot and
        // will start on its own, so the action on offer is to stand it down.
        Assert.Equal("暂停", TaskPresenter.ToggleLabel(DownloadStatus.Queued));
    }

    [Fact]
    public void RingSubtitle_falls_back_to_the_state_name_when_stalled()
    {
        var seed = Golden.Seed();
        var running = seed.Single(t => t.Id == 1);
        var paused = seed.Single(t => t.Id == 4);

        Assert.Equal(FormatHelpers.Speed(running.Speed), TaskPresenter.RingSubtitle(running));
        Assert.Equal("已暂停", TaskPresenter.RingSubtitle(paused));
    }

    [Fact]
    public void NewVersionSubtitle_reads_version_size_and_date()
    {
        var ubuntu = Golden.Seed().Single(t => t.Id == 1);
        Assert.Equal(
            "ubuntu-24.04.3-desktop.iso · 5.90 GB · 发布于 2 天前",
            TaskPresenter.NewVersionSubtitle(ubuntu.NewerVersion!));
    }

    [Fact]
    public void Fraction_is_zero_for_a_task_of_unknown_size() =>
        Assert.Equal(0d, TaskPresenter.Fraction(done: 100, size: 0));

    [Fact]
    public void Fraction_never_exceeds_one() =>
        Assert.Equal(1d, TaskPresenter.Fraction(done: 500, size: 400));
}
