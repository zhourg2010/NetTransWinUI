using NetTrans.Models;

namespace NetTrans.Services;

/// <summary>How many rows are shown and how many the 展开更多 row hides.</summary>
/// <param name="Shown">Rows rendered right now.</param>
/// <param name="Hidden">Rows behind the fold.</param>
/// <param name="CanFold">True once the list is longer than the fold limit.</param>
public readonly record struct FoldResult(int Shown, int Hidden, bool CanFold);

/// <summary>
/// The list pipeline from App2: scope by category, narrow by tab, filter by the
/// search box, sort, then fold to five rows. Written over a projection so the
/// shell can run it on view models while the tests run it on plain models.
/// </summary>
public static class TaskQuery
{
    /// <summary>FOLD in the handoff: rows past the fifth collapse behind 展开更多.</summary>
    public const int FoldLimit = 5;

    /// <summary>Category scope, applied before the tab counts are taken.</summary>
    public static IEnumerable<T> Scoped<T>(IReadOnlyList<T> items, Func<T, DownloadItem> select, string category) =>
        items.Where(item => category == "all" || select(item).Category == category);

    public static int ActiveCount<T>(IReadOnlyList<T> items, Func<T, DownloadItem> select, string category) =>
        Scoped(items, select, category).Count(item => select(item).Status != DownloadStatus.Completed);

    public static int DoneCount<T>(IReadOnlyList<T> items, Func<T, DownloadItem> select, string category) =>
        Scoped(items, select, category).Count(item => select(item).Status == DownloadStatus.Completed);

    /// <summary>The full pipeline, in the order App2 applies it.</summary>
    public static List<T> Apply<T>(
        IReadOnlyList<T> items,
        Func<T, DownloadItem> select,
        string tab,
        string category,
        string search,
        string sortKey,
        string sortDirection)
    {
        var scoped = Scoped(items, select, category);

        var tabbed = tab switch
        {
            "active" => scoped.Where(item => select(item).Status != DownloadStatus.Completed),
            "done" => scoped.Where(item => select(item).Status == DownloadStatus.Completed),
            _ => scoped,
        };

        var searched = string.IsNullOrEmpty(search)
            ? tabbed
            : tabbed.Where(item => select(item).Name.Contains(search, StringComparison.OrdinalIgnoreCase));

        int sign = sortDirection == "desc" ? -1 : 1;
        var comparer = Comparer<T>.Create((a, b) => sign * Compare(select(a), select(b), sortKey));

        // The prototype sorts with a negated comparator rather than reversing an
        // ascending list, and Array.prototype.sort is stable -- so tied rows keep
        // their original order in BOTH directions. OrderBy is stable too, which is
        // why the comparison is negated here instead of the result being reversed.
        return searched.OrderBy(item => item, comparer).ToList();
    }

    private static int Compare(DownloadItem a, DownloadItem b, string sortKey) => sortKey switch
    {
        // localeCompare in the prototype; InvariantCulture is its closest .NET analogue.
        "name" => string.Compare(a.Name, b.Name, StringComparison.InvariantCulture),
        "size" => a.Size.CompareTo(b.Size),
        "progress" => Ratio(a).CompareTo(Ratio(b)),
        "speed" => a.Speed.CompareTo(b.Speed),

        // 加入时间 is `a.id - b.id` in the handoff, i.e. the order tasks were
        // created -- not the queue order, which 移到队首 / 移到队尾 changes.
        _ => a.Id.CompareTo(b.Id),
    };

    /// <summary>How the fold splits a list of <paramref name="total"/> rows.</summary>
    public static FoldResult Fold(int total, bool expanded, int limit = FoldLimit)
    {
        bool canFold = total > limit;
        int shown = canFold && !expanded ? limit : total;
        return new FoldResult(shown, total - shown, canFold);
    }

    /// <summary>The prototype sorts on got/size directly; a zero-size task sorts as zero rather than NaN.</summary>
    private static double Ratio(DownloadItem task) => task.Size <= 0 ? 0 : task.Done / (double)task.Size;
}
