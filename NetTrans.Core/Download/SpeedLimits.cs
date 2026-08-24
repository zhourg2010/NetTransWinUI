using System.Globalization;

namespace NetTrans.Download;

/// <summary>
/// Turns the 限速 dropdown's labels into bytes per second. The design writes
/// them as display strings ("不限", "512 KB/s"), so this is the one place that
/// knows how to read them back.
/// </summary>
public static class SpeedLimits
{
    /// <summary>Zero means 不限.</summary>
    public static double Parse(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return 0;

        string text = label.Trim();
        if (text is "不限" or "0") return 0;

        int slash = text.IndexOf('/');
        if (slash >= 0) text = text[..slash];

        text = text.Trim();

        double multiplier =
            text.EndsWith("GB", StringComparison.OrdinalIgnoreCase) ? 1024d * 1024 * 1024 :
            text.EndsWith("MB", StringComparison.OrdinalIgnoreCase) ? 1024d * 1024 :
            text.EndsWith("KB", StringComparison.OrdinalIgnoreCase) ? 1024d :
            1;

        string number = new(text.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());

        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value * multiplier
            : 0;
    }
}
