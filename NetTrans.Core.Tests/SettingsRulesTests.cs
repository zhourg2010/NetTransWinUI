using NetTrans.Models;
using NetTrans.Services;
using Xunit;

namespace NetTrans.Tests;

/// <summary>
/// The settings the sheet stores as labels, turned into the values the engine
/// and the shell act on.
/// </summary>
public class SettingsRulesTests
{
    [Theory]
    [InlineData("不重试", 0)]
    [InlineData("3 次", 3)]
    [InlineData("10 次", 10)]
    public void Retry_labels_become_a_budget(string label, int expected) =>
        Assert.Equal(expected, SettingsRules.Retries(label));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("每小时一次")]
    public void An_unreadable_retry_setting_falls_back_to_the_default_rather_than_to_none(string? label) =>
        Assert.Equal(3, SettingsRules.Retries(label));

    [Theory]
    [InlineData("无操作", CompletionAction.Nothing)]
    [InlineData("退出程序", CompletionAction.Quit)]
    [InlineData("休眠", CompletionAction.Sleep)]
    [InlineData("关机", CompletionAction.Shutdown)]
    [InlineData("something else", CompletionAction.Nothing)]
    [InlineData(null, CompletionAction.Nothing)]
    public void Completion_labels_become_an_action(string? label, CompletionAction expected) =>
        Assert.Equal(expected, SettingsRules.WhenAllComplete(label));

    [Fact]
    public void Every_action_describes_back_to_the_label_it_came_from()
    {
        foreach (var action in Enum.GetValues<CompletionAction>())
        {
            Assert.Equal(action, SettingsRules.WhenAllComplete(SettingsRules.Describe(action)));
        }
    }

    [Fact]
    public void The_configured_cap_applies_when_night_mode_is_off()
    {
        var settings = Settings(uncappedAtNight: false);
        Assert.Equal(4 * 1024 * 1024, SettingsRules.SpeedLimitAt(settings, At("02:00")));
    }

    [Fact]
    public void Night_mode_lifts_the_cap_inside_the_window_and_not_outside_it()
    {
        var settings = Settings(uncappedAtNight: true);

        Assert.Equal(0, SettingsRules.SpeedLimitAt(settings, At("23:30")));
        Assert.Equal(0, SettingsRules.SpeedLimitAt(settings, At("02:00")));
        Assert.Equal(4 * 1024 * 1024, SettingsRules.SpeedLimitAt(settings, At("07:00")));
        Assert.Equal(4 * 1024 * 1024, SettingsRules.SpeedLimitAt(settings, At("12:00")));
    }

    [Fact]
    public void There_is_nothing_for_night_mode_to_lift_when_there_is_no_cap()
    {
        var settings = Settings(uncappedAtNight: true);
        settings.GlobalSpeedLimit = "不限";

        Assert.Equal(0, SettingsRules.SpeedLimitAt(settings, At("12:00")));
    }

    [Fact]
    public void An_unreadable_window_leaves_the_cap_alone()
    {
        var settings = Settings(uncappedAtNight: true);
        settings.OffPeakStart = "深夜";

        Assert.Equal(4 * 1024 * 1024, SettingsRules.SpeedLimitAt(settings, At("02:00")));
    }

    private static AppSettings Settings(bool uncappedAtNight) => new()
    {
        GlobalSpeedLimit = "4 MB/s",
        UncappedAtNight = uncappedAtNight,
        OffPeakStart = "23:00",
        OffPeakEnd = "07:00",
    };

    private static DateTimeOffset At(string time) =>
        new(2026, 3, 14, int.Parse(time[..2]), int.Parse(time[3..]), 0, TimeSpan.Zero);
}
