using NetTrans.Models;

namespace NetTrans.Services;

/// <summary>What to do once the queue has drained. The 全部完成后 dropdown.</summary>
public enum CompletionAction
{
    Nothing,
    Quit,
    Sleep,
    Shutdown,
}

/// <summary>
/// The settings sheet stores what it shows -- "3 次", "关机" -- because that is
/// what has to survive a restart and reappear in the dropdown. These turn those
/// labels into the values the engine and the shell act on.
/// </summary>
public static class SettingsRules
{
    /// <summary>
    /// 失败自动重试 as a retry budget. Anything unrecognised falls back to the
    /// sheet's own default rather than to zero: a setting we failed to read is
    /// not the user asking us to give up on the first hiccup.
    /// </summary>
    public static int Retries(string? policy) => policy?.Trim() switch
    {
        "不重试" => 0,
        "3 次" => 3,
        "10 次" => 10,
        _ => 3,
    };

    /// <summary>全部完成后, defaulting to doing nothing when unrecognised.</summary>
    public static CompletionAction WhenAllComplete(string? label) => label?.Trim() switch
    {
        "退出程序" => CompletionAction.Quit,
        "休眠" => CompletionAction.Sleep,
        "关机" => CompletionAction.Shutdown,
        _ => CompletionAction.Nothing,
    };

    /// <summary>The label for an action, so the shell can name what it is about to do.</summary>
    public static string Describe(CompletionAction action) => action switch
    {
        CompletionAction.Quit => "退出程序",
        CompletionAction.Sleep => "休眠",
        CompletionAction.Shutdown => "关机",
        _ => "无操作",
    };

    /// <summary>
    /// The global cap in force at a given moment: the configured one, or none
    /// at all while 夜间不限速 is on and the clock is inside 空闲时段.
    /// </summary>
    public static double SpeedLimitAt(AppSettings settings, DateTimeOffset now)
    {
        double configured = Download.SpeedLimits.Parse(settings.GlobalSpeedLimit);
        if (configured <= 0) return 0;
        if (!settings.UncappedAtNight) return configured;

        var window = OffPeakWindow.Parse(settings.OffPeakStart, settings.OffPeakEnd);
        return window is { } offPeak && offPeak.Includes(now) ? 0 : configured;
    }
}
