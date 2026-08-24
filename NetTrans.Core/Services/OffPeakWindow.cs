namespace NetTrans.Services;

/// <summary>
/// The 空闲时段 range behind 夜间不限速. Almost every useful setting of it wraps
/// past midnight (23:00 – 07:00 is the default the sheet ships with), so the
/// wrap is the normal case here rather than an edge case.
/// </summary>
public readonly record struct OffPeakWindow(TimeOnly Start, TimeOnly End)
{
    /// <summary>
    /// Parses the two "HH:mm" strings the settings sheet stores. Returns null
    /// when either side is unreadable, or when the two are equal -- a zero
    /// width window and a whole day are the same text, and refusing to guess
    /// is better than picking one.
    /// </summary>
    public static OffPeakWindow? Parse(string? start, string? end)
    {
        if (!TimeOnly.TryParse(start, out var from)) return null;
        if (!TimeOnly.TryParse(end, out var to)) return null;
        if (from == to) return null;

        return new OffPeakWindow(from, to);
    }

    /// <summary>Whether the window wraps past midnight, as the default one does.</summary>
    public bool WrapsMidnight => End < Start;

    /// <summary>
    /// The start is inclusive and the end exclusive, so back-to-back windows
    /// meet without overlapping.
    /// </summary>
    public bool Includes(TimeOnly time) =>
        WrapsMidnight ? time >= Start || time < End : time >= Start && time < End;

    public bool Includes(DateTimeOffset moment) => Includes(TimeOnly.FromTimeSpan(moment.TimeOfDay));

    /// <summary>
    /// When the answer to <see cref="Includes(DateTimeOffset)"/> next changes,
    /// so a caller can sleep until then instead of polling.
    /// </summary>
    public DateTimeOffset NextChange(DateTimeOffset from)
    {
        var edge = Includes(from) ? End : Start;

        var today = new DateTimeOffset(from.Year, from.Month, from.Day, edge.Hour, edge.Minute, 0, from.Offset);
        return today > from ? today : today.AddDays(1);
    }
}
