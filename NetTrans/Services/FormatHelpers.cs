using System.Globalization;

namespace NetTrans.Services;

/// <summary>Mirrors fmtBytes/fmtSpeed from the reference app.jsx.</summary>
public static class FormatHelpers
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Bytes(double? n)
    {
        if (n is null) return "—";
        double v = n.Value;
        int i = 0;
        while (v >= 1024 && i < Units.Length - 1)
        {
            v /= 1024;
            i++;
        }
        string formatted = v < 10 ? v.ToString("0.00", CultureInfo.InvariantCulture)
            : v < 100 ? v.ToString("0.0", CultureInfo.InvariantCulture)
            : v.ToString("0", CultureInfo.InvariantCulture);
        return $"{formatted} {Units[i]}";
    }

    public static string Speed(double n) => n > 0 ? $"{Bytes(n)}/s" : "—";

    public static string Eta(TimeSpan? eta) => eta is null ? "—" : $"{(int)eta.Value.TotalHours:00}:{eta.Value.Minutes:00}:{eta.Value.Seconds:00}";
}
