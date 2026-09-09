namespace NetTrans.Tidy;

/// <summary>
/// Collecting the files 深度整理 is allowed to touch.
///
/// The folders it creates are never walked into: a second run would otherwise
/// find 文档\report.pdf, decide it belongs in 文档, and shuffle it forever.
/// </summary>
public static class TidyScan
{
    /// <summary>
    /// Roots this refuses to tidy. Sorting a drive root or Program Files into
    /// 文档\ and 其他\ is not tidying, it is breaking the machine -- and the
    /// undo journal is no comfort at that scale.
    /// </summary>
    public static string? Refuse(string root)
    {
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        }
        catch (Exception)
        {
            return "路径看不懂";
        }

        if (!Directory.Exists(full)) return "目录不存在";
        // A root is the one directory with no parent, on either platform.
        if (Directory.GetParent(full) is null) return "这是磁盘根目录";

        var forbidden = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            AppContext.BaseDirectory,
        };

        foreach (var directory in forbidden)
        {
            if (string.IsNullOrEmpty(directory)) continue;

            string other = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            if (string.Equals(full, other, StringComparison.OrdinalIgnoreCase)) return $"这是系统目录（{other}）";
        }

        return null;
    }

    public static IReadOnlyList<TidyItem> Collect(string root, TidyOptions options, Action<string, Exception>? onError = null)
    {
        var items = new List<TidyItem>();
        Walk(new DirectoryInfo(root), Math.Max(1, options.Depth), items, onError);

        return items;
    }

    private static void Walk(DirectoryInfo directory, int depth, List<TidyItem> items, Action<string, Exception>? onError)
    {
        FileInfo[] files;
        DirectoryInfo[] children;

        try
        {
            files = directory.GetFiles();
            children = directory.GetDirectories();
        }
        catch (Exception failure) when (failure is UnauthorizedAccessException or IOException)
        {
            onError?.Invoke(directory.FullName, failure);
            return;
        }

        foreach (var file in files)
        {
            var attributes = file.Attributes;

            items.Add(new TidyItem(
                file.FullName,
                file.Name,
                file.Length,
                file.LastWriteTimeUtc,
                Hidden: attributes.HasFlag(FileAttributes.Hidden) || attributes.HasFlag(FileAttributes.System),
                Link: file.LinkTarget is not null));
        }

        if (depth <= 1) return;

        foreach (var child in children)
        {
            // Ours, or a link out of the tree.
            if (TidyCategories.AllFolders.Contains(child.Name)) continue;
            if (child.LinkTarget is not null) continue;
            if (child.Attributes.HasFlag(FileAttributes.Hidden)) continue;

            Walk(child, depth - 1, items, onError);
        }
    }
}
