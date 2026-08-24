using System.Globalization;

namespace NetTrans.Services;

/// <summary>
/// Reproduces the handoff's mb() / spd() / eta() output exactly. The prototype
/// keeps sizes in MB and speeds in KB/s; the app keeps bytes, so the thresholds
/// are converted but the rendered strings are identical.
/// </summary>
public static class FormatHelpers
{
    private const double Kb = 1024;
    private const double Mb = 1024 * 1024;
    private const double Gb = 1024 * 1024 * 1024;

    /// <summary>mb(): "5.80 GB" at or above 1 GB, otherwise a whole number of MB.</summary>
    public static string Bytes(double bytes) => bytes >= Gb
        ? (bytes / Gb).ToString("0.00", CultureInfo.InvariantCulture) + " GB"
        : Math.Round(bytes / Mb).ToString("0", CultureInfo.InvariantCulture) + " MB";

    /// <summary>spd(): empty at zero, "1.2 MB/s" at or above 1 MB/s, otherwise whole KB/s.</summary>
    public static string Speed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0) return "";
        return bytesPerSecond >= Mb
            ? (bytesPerSecond / Mb).ToString("0.0", CultureInfo.InvariantCulture) + " MB/s"
            : Math.Round(bytesPerSecond / Kb).ToString("0", CultureInfo.InvariantCulture) + " KB/s";
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
        double seconds = remainingBytes / bytesPerSecond;
        if (seconds <= 0 || double.IsInfinity(seconds) || seconds > 5940) return "计算中";

        int minutes = (int)(seconds / 60);
        int rest = (int)Math.Round(seconds % 60);
        return minutes > 0 ? $"剩余 {minutes} 分 {rest} 秒" : $"剩余 {rest} 秒";
    }

    /// <summary>The island's headline: value and unit split so the unit can be styled down.</summary>
    public static (string Value, string Unit) SpeedParts(double bytesPerSecond) => bytesPerSecond >= Mb
        ? ((bytesPerSecond / Mb).ToString("0.0", CultureInfo.InvariantCulture), "MB/s")
        : (Math.Round(bytesPerSecond / Kb).ToString("0", CultureInfo.InvariantCulture), "KB/s");
}
