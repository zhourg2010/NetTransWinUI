namespace NetTrans.Services;

/// <summary>
/// Where a finished file actually lands: the 按分类建子文件夹 switch, and the
/// "(2)" suffix that keeps a second download of the same name from silently
/// eating the first.
/// </summary>
public static class SavePathPlanner
{
    /// <summary>
    /// The folder names the switch produces, keyed by the category ids the
    /// shell uses. Chinese, because the folders are for the user to browse,
    /// not for the program to parse.
    /// </summary>
    private static readonly Dictionary<string, string> Folders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["soft"] = "软件",
        ["video"] = "视频",
        ["doc"] = "文档",
        ["music"] = "音乐",
        ["bt"] = "BT",
    };

    /// <summary>
    /// The directory to save into. With the switch off, or for a task with no
    /// category worth splitting on, this is the root unchanged.
    /// </summary>
    public static string Directory(string root, string? category, bool byCategory)
    {
        if (!byCategory || string.IsNullOrWhiteSpace(category)) return root;

        // "all" is the 全部 tab, not a category a file can belong to.
        if (!Folders.TryGetValue(category, out var folder)) return root;

        return Path.Combine(root, folder);
    }

    /// <summary>
    /// A name that is not already taken in <paramref name="directory"/>, as
    /// "name (2).ext", "name (3).ext" and so on -- the convention Windows
    /// itself uses, so the result looks like something the shell produced.
    /// </summary>
    /// <param name="exists">
    /// Asked about full paths. Injected so the rule is testable without a disk,
    /// and so the caller can count a queued-but-not-yet-written name as taken.
    /// </param>
    public static string UniqueName(string directory, string name, Func<string, bool> exists)
    {
        if (!exists(Path.Combine(directory, name))) return name;

        string stem = Path.GetFileNameWithoutExtension(name);
        string extension = Path.GetExtension(name);

        // Bounded rather than while(true): a directory that answers "taken" to
        // everything is a bug somewhere else, and it must not hang the caller.
        for (int n = 2; n < 10_000; n++)
        {
            string candidate = $"{stem} ({n}){extension}";
            if (!exists(Path.Combine(directory, candidate))) return candidate;
        }

        return name;
    }
}
