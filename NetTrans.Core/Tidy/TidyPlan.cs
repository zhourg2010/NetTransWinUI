using NetTrans.Services;

namespace NetTrans.Tidy;

/// <summary>One file as the tidier sees it, before anything is decided about it.</summary>
public sealed record TidyItem(string Path, string Name, long Size, DateTimeOffset Modified, bool Hidden = false, bool Link = false);

/// <summary>
/// What is going to happen to one file -- or why nothing is.
/// </summary>
/// <param name="Destination">Full path it would move to. Null means it stays where it is.</param>
public sealed record TidyAction(TidyItem Item, TidyCategory Category, string Reason, string? Destination)
{
    public bool Moves => Destination is not null;
}

public enum TidyGrouping
{
    /// <summary>安装包 / 文档 / 图片…</summary>
    Category,

    /// <summary>2026-01 / 2025-12… for a folder where the date is what people remember.</summary>
    Month,

    /// <summary>文档/2026-01. A year of downloads is a lot of files even after sorting by kind.</summary>
    CategoryThenMonth,
}

public sealed record TidyOptions
{
    public TidyGrouping Grouping { get; init; } = TidyGrouping.Category;

    /// <summary>1 = only the files sitting in the folder itself. Deeper walks the subfolders the user made, never the ones we made.</summary>
    public int Depth { get; init; } = 1;

    /// <summary>Untouched for this many days goes to 存档/年份 instead of its category. 0 turns it off.</summary>
    public int StaleDays { get; init; }

    /// <summary>Move other programs' leftovers into 待清理. Nothing is ever deleted.</summary>
    public bool IncludeJunk { get; init; } = true;

    /// <summary>
    /// A file touched within this long is left alone: it is probably still
    /// being written, and moving a file out from under whatever is writing it
    /// is the one mistake this tool must never make.
    /// </summary>
    public TimeSpan Settle { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Turns a draft into a list of moves -- and only a list. Nothing here touches
/// the disk, which is what makes 预演 the default and the plan reviewable
/// before a single file has been renamed.
/// </summary>
public static class TidyPlan
{
    /// <summary>
    /// The plain path: no projects, every bucket taken as proposed. This is
    /// what `--tidy` without a draft does, and it is the draft path with all
    /// the questions answered the obvious way.
    /// </summary>
    public static IReadOnlyList<TidyAction> Build(
        string root,
        IEnumerable<TidyItem> items,
        TidyOptions options,
        DateTimeOffset now,
        Func<string, bool> exists,
        IReadOnlyDictionary<string, string>? duplicates = null,
        IReadOnlyDictionary<string, TidyCategory>? aiCategories = null)
    {
        var draft = new TidyDraft(root, options, items.ToList())
        {
            Duplicates = duplicates ?? new Dictionary<string, string>(),
            Guesses = aiCategories ?? new Dictionary<string, TidyCategory>(),
        };

        draft.Rebuild();

        return Build(draft, now, exists);
    }

    /// <summary>
    /// The plan the draft describes.
    ///
    /// A card nobody reached counts as accepted: 快进 and "just run it" have to
    /// mean the same thing as walking every card and pressing 下一个, or the
    /// wizard would be changing the outcome rather than confirming it.
    /// </summary>
    public static IReadOnlyList<TidyAction> Build(TidyDraft draft, DateTimeOffset now, Func<string, bool> exists)
    {
        var actions = new List<TidyAction>();

        // Names this run has already claimed. Without it, three files called
        // report.pdf all plan to become 文档\report.pdf and two of them lose.
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool Taken(string path) => claimed.Contains(path) || exists(path);

        foreach (var item in draft.Items)
        {
            var action = Decide(draft, item, now, Taken);
            if (action.Destination is not null) claimed.Add(action.Destination);

            actions.Add(action);
        }

        return actions;
    }

    private static TidyAction Decide(TidyDraft draft, TidyItem item, DateTimeOffset now, Func<string, bool> taken)
    {
        if (Gate(item, draft.Options, now) is { } gated) return gated;

        if (draft.Skipped.Contains(item.Path))
        {
            return new TidyAction(item, draft.CategoryOf(item), "你选了不动", null);
        }

        // A project the person accepted, or one they never got to -- both mean
        // "these belong together".
        if (draft.ProjectOf(item.Path) is { } project)
        {
            return Move(draft.Root, item, TidyCategory.Unknown, $"项目「{project.Name}」", project.Destination, taken);
        }

        var category = draft.CategoryOf(item);

        if (category == TidyCategory.Junk && !draft.Options.IncludeJunk)
        {
            return new TidyAction(item, category, "垃圾文件，本次不管", null);
        }

        var bucket = draft.Buckets.GetValueOrDefault(category);
        if (bucket?.Decision == Decision.Skipped)
        {
            return new TidyAction(item, category, $"{TidyCategories.Folder(category)}：这批不动", null);
        }

        var verdict = TidyRules.Classify(item.Name);

        string reason = draft.Duplicates.TryGetValue(item.Path, out var original)
            ? $"和 {System.IO.Path.GetFileName(original)} 内容相同"
            : category != verdict.Category ? "AI 判断"
            : verdict.Reason;

        string folder = Destination(item, category, bucket, draft.Options, now);

        // Already where it belongs. Saying so is more useful than silence: it
        // is how a second run reads as "nothing left to do".
        string directory = System.IO.Path.Combine(draft.Root, folder);
        if (string.Equals(System.IO.Path.GetDirectoryName(item.Path), directory, StringComparison.OrdinalIgnoreCase))
        {
            return new TidyAction(item, category, "已经在位", null);
        }

        return Move(draft.Root, item, category, reason, folder, taken);
    }

    /// <summary>The four things that are never touched, whatever any card says.</summary>
    private static TidyAction? Gate(TidyItem item, TidyOptions options, DateTimeOffset now)
    {
        if (item.Hidden) return new TidyAction(item, TidyCategory.Unknown, "隐藏或系统文件，不动", null);
        if (item.Link) return new TidyAction(item, TidyCategory.Unknown, "是链接，不动", null);

        if (TidyRules.IsUnfinishedDownload(item.Name))
        {
            return new TidyAction(item, TidyCategory.Junk, "没下完，不动", null);
        }

        return now - item.Modified < options.Settle
            ? new TidyAction(item, TidyCategory.Unknown, "刚动过，可能还在写", null)
            : null;
    }

    private static TidyAction Move(string root, TidyItem item, TidyCategory category, string reason, string folder, Func<string, bool> taken)
    {
        string directory = System.IO.Path.Combine(root, folder);
        string name = SavePathPlanner.UniqueName(directory, item.Name, taken);

        return new TidyAction(item, category, reason, System.IO.Path.Combine(directory, name));
    }

    private static string Destination(TidyItem item, TidyCategory category, DraftBucket? bucket, TidyOptions options, DateTimeOffset now)
    {
        // Old enough that its kind matters less than the year it is from.
        if (options.StaleDays > 0 && (now - item.Modified).TotalDays >= options.StaleDays)
        {
            return System.IO.Path.Combine(TidyCategories.Archive, item.Modified.ToString("yyyy"));
        }

        string folder = bucket?.Folder ?? TidyCategories.Folder(category);
        string month = item.Modified.ToString("yyyy-MM");

        // The card's own switch wins; the run-wide grouping is the default it
        // started from.
        if (bucket?.ByMonth == true) return System.IO.Path.Combine(folder, month);

        return options.Grouping switch
        {
            TidyGrouping.Month => month,
            TidyGrouping.CategoryThenMonth => System.IO.Path.Combine(folder, month),
            _ => folder,
        };
    }
}
