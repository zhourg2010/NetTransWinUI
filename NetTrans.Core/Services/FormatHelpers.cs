using System.Globalization;

namespace NetTrans.Services;

/// <summary>
/// Reproduces the handoff's mb() / spd() / eta() output exactly. The prototype
/// keeps sizes in MB and speeds in KB/s; the app keeps bytes, so the thresholds
/// are converted but the rendered strings are identical.
///
/// JavaScript's Math.round and Number.toFixed both round halves away from zero,
/// while .NET's Math.Round defaults to banker's rounding -- 62.5 MB would render
/// as "62 MB" here and "63 MB" in the design. Every rounding step below is
/// therefore pinned to MidpointRounding.AwayFromZero.
/// </summary>
public static class FormatHelpers
{
    private const double Kb = 1024;
    private const double Mb = 1024 * 1024;
    private const double Gb = 1024 * 1024 * 1024;

    /// <summary>mb(): "5.80 GB" at or above 1 GB, otherwise a whole number of MB.</summary>
    public static string Bytes(double bytes) => bytes >= Gb
        ? Fixed(bytes / Gb, 2) + " GB"
        : Fixed(bytes / Mb, 0) + " MB";

    /// <summary>spd(): empty at zero, "1.2 MB/s" at or above 1 MB/s, otherwise whole KB/s.</summary>
    public static string Speed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0) return "";
        return bytesPerSecond >= Mb
            ? Fixed(bytesPerSecond / Mb, 1) + " MB/s"
            : Fixed(bytesPerSecond / Kb, 0) + " KB/s";
    }

    /// <summary>Same as <see cref="Speed"/> but renders an em dash where the design shows one.</summary>
    public static string SpeedOrDash(double bytesPerSecond)
    {
        string s = Speed(bytesPerSecond);
        return s.Length == 0 ? "—" : s;
    }

    /// <summary>eta(): "计算中" past 99 minutes or when stalled, else 剩余 N 分 N 秒.</summary>
    public static string Eta(double remainingBytes, double bytesPerSecond)
    {
        if (bytesPerSecond <= 0) return "计算中";
        return EtaFromSeconds(remainingBytes / bytesPerSecond);
    }

    /// <summary>The same rule expressed over seconds, which is how the prototype states it.</summary>
    public static string EtaFromSeconds(double seconds)
    {
        if (seconds <= 0 || double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds > 5940) return "计算中";

        int minutes = (int)Math.Floor(seconds / 60);
        int rest = (int)Math.Round(seconds % 60, MidpointRounding.AwayFromZero);
        return minutes > 0 ? $"剩余 {minutes} 分 {rest} 秒" : $"剩余 {rest} 秒";
    }

    /// <summary>The island's headline: value and unit split so the unit can be styled down.</summary>
    public static (string Value, string Unit) SpeedParts(double bytesPerSecond) => bytesPerSecond >= Mb
        ? (Fixed(bytesPerSecond / Mb, 1), "MB/s")
        : (Fixed(bytesPerSecond / Kb, 0), "KB/s");

    /// <summary>Number.prototype.toFixed: fixed decimals, halves away from zero.</summary>
    private static string Fixed(double value, int decimals) =>
        Math.Round(value, decimals, MidpointRounding.AwayFromZero)
            .ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}
