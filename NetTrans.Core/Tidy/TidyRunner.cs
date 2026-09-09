using NetTrans.Download;
using NetTrans.Services;

namespace NetTrans.Tidy;

/// <summary>What a run actually managed.</summary>
public sealed record TidyResult(IReadOnlyList<TidyMove> Moved, IReadOnlyList<string> Failed);

/// <summary>
/// The part that touches the disk, and the only part that does.
///
/// It moves, never copies and never deletes -- a move within a volume is
/// instant and atomic, so a run that is interrupted leaves every file either
/// where it was or where it was going, and the journal knows which.
/// </summary>
public static class TidyRunner
{
    public static TidyResult Apply(IEnumerable<TidyAction> actions, Action<TidyMove>? onMoved = null)
    {
        var moved = new List<TidyMove>();
        var failed = new List<string>();

        foreach (var action in actions)
        {
            if (action.Destination is not { } destination) continue;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                // overwrite: false is the belt to the plan's braces. The plan
                // already picked a free name; if something appeared in the
                // meantime, that file wins and we report it.
                File.Move(action.Item.Path, destination, overwrite: false);

                var move = new TidyMove(action.Item.Path, destination);
                moved.Add(move);
                onMoved?.Invoke(move);
            }
            catch (Exception failure)
            {
                failed.Add($"{action.Item.Name}（{failure.Message}）");
            }
        }

        return new TidyResult(moved, failed);
    }

    /// <summary>
    /// Finds files with identical content, and says which copy to keep.
    ///
    /// Size first, hash only within a size group: on a Downloads folder that is
    /// usually one hash of nothing, because two files sharing a length to the
    /// byte is already unusual.
    /// </summary>
    /// <returns>Duplicate path to the path of the copy being kept.</returns>
    public static async Task<IReadOnlyDictionary<string, string>> FindDuplicatesAsync(
        IEnumerable<TidyItem> items,
        CancellationToken cancellationToken = default)
    {
        var duplicates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in items.Where(item => item.Size > 0).GroupBy(item => item.Size))
        {
            if (group.Count() < 2) continue;

            var byHash = new Dictionary<string, TidyItem>(StringComparer.OrdinalIgnoreCase);

            // Oldest first, so the copy that has been there longest is the one
            // that stays and "ubuntu (2).iso" is the one that moves.
            foreach (var item in group.OrderBy(item => item.Modified))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string hash;
                try
                {
                    hash = await FileHash.ComputeFileAsync(item.Path, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    continue;
                }

                if (byHash.TryGetValue(hash, out var kept)) duplicates[item.Path] = kept.Path;
                else byHash[hash] = item;
            }
        }

        return duplicates;
    }
}

/// <summary>The plan and the result, as something a person reads.</summary>
public static class TidyReport
{
    public static string Summary(IReadOnlyList<TidyAction> actions, bool applied)
    {
        var moves = actions.Where(action => action.Moves).ToList();
        if (moves.Count == 0) return "没有需要动的文件。";

        long bytes = moves.Sum(action => action.Item.Size);
        int stays = actions.Count - moves.Count;

        string counts = string.Join("、", moves
            .GroupBy(action => action.Category)
            .OrderByDescending(group => group.Count())
            .Select(group => $"{TidyCategories.Folder(group.Key)} {group.Count()}"));

        return $"{(applied ? "整理了" : "预演：将整理")} {moves.Count} 个文件（{FormatHelpers.Bytes(bytes)}）：{counts}。" +
               (stays > 0 ? $"另有 {stays} 个保持原样。" : "");
    }

    public static string Table(IReadOnlyList<TidyAction> actions, string root)
    {
        var lines = new List<string>
        {
            "| 文件 | 大小 | 去处 | 依据 |",
            "| --- | ---: | --- | --- |",
        };

        foreach (var action in actions.OrderByDescending(action => action.Moves).ThenBy(action => action.Item.Name))
        {
            string destination = action.Destination is { } path
                ? Path.GetRelativePath(root, path)
                : "（不动）";

            lines.Add($"| {action.Item.Name} | {FormatHelpers.Bytes(action.Item.Size)} | {destination} | {action.Reason} |");
        }

        return string.Join('\n', lines);
    }

    public static string Full(IReadOnlyList<TidyAction> actions, string root, bool applied) =>
        string.Join('\n', new[]
        {
            $"## 深度整理 · {root}",
            "",
            Summary(actions, applied),
            "",
            Table(actions, root),
            "",
            applied
                ? "> 每一次移动都记在 NetTrans.tidy.json 里，`--tidy --undo` 可以整批还原。没有任何文件被删除。"
                : "> 这是预演，磁盘上什么也没动。确认无误后加 --apply。",
        });
}
