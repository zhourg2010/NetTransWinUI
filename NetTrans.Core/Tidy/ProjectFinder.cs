namespace NetTrans.Tidy;

/// <summary>Where the guess that these files belong together came from.</summary>
public enum ProjectSource
{
    /// <summary>They are already in one folder. The strongest evidence there is: somebody grouped them by hand.</summary>
    Folder,

    /// <summary>Their names start with the same specific word.</summary>
    Stem,

    /// <summary>They were written within minutes of each other and share a word. Offered, never assumed.</summary>
    Burst,
}

public sealed record ProjectCandidate(string Name, ProjectSource Source, IReadOnlyList<TidyItem> Members, string Reason);

/// <summary>
/// 找出属于一件事的文件.
///
/// Three passes in confidence order, each taking only what the previous one
/// left. Every candidate carries the sentence that explains it, because the
/// person confirming it needs to judge the guess, not trust it.
/// </summary>
public static class ProjectFinder
{
    /// <summary>Fewer than this is not a project, it is a file.</summary>
    public const int MinimumMembers = 2;

    /// <summary>Files written this close together, sharing a word, are offered as a maybe.</summary>
    public static TimeSpan BurstWindow { get; } = TimeSpan.FromMinutes(10);

    public static IReadOnlyList<ProjectCandidate> Find(string root, IReadOnlyList<TidyItem> items, bool includeBursts = false)
    {
        var candidates = new List<ProjectCandidate>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in ByFolder(root, items)) Take(candidates, taken, group);
        foreach (var group in ByStem(items.Where(item => !taken.Contains(item.Path)).ToList())) Take(candidates, taken, group);

        if (includeBursts)
        {
            foreach (var group in ByBurst(items.Where(item => !taken.Contains(item.Path)).ToList())) Take(candidates, taken, group);
        }

        // Best first: hand-made folders, then bigger groups.
        return candidates
            .OrderBy(candidate => candidate.Source)
            .ThenByDescending(candidate => candidate.Members.Count)
            .ThenBy(candidate => candidate.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static void Take(List<ProjectCandidate> candidates, HashSet<string> taken, ProjectCandidate candidate)
    {
        candidates.Add(candidate);
        foreach (var member in candidate.Members) taken.Add(member.Path);
    }

    /// <summary>A subfolder of the scanned directory that already holds several files.</summary>
    private static IEnumerable<ProjectCandidate> ByFolder(string root, IReadOnlyList<TidyItem> items)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

        foreach (var group in items.GroupBy(item => Path.GetDirectoryName(item.Path) ?? "", StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() < MinimumMembers) continue;

            string directory = Path.TrimEndingDirectorySeparator(group.Key);
            if (string.Equals(directory, full, StringComparison.OrdinalIgnoreCase)) continue;

            // Only the folder's own name is proposed; where it ends up is the
            // person's decision on the card.
            yield return new ProjectCandidate(
                Path.GetFileName(directory),
                ProjectSource.Folder,
                group.ToList(),
                $"已经在同一个文件夹里（{group.Count()} 个）");
        }
    }

    /// <summary>Names that start with the same specific word once versions and dates are off.</summary>
    private static IEnumerable<ProjectCandidate> ByStem(IReadOnlyList<TidyItem> items)
    {
        var keyed = items
            .Select(item => (Item: item, Key: NameStem.Key(item.Name)))
            .Where(entry => NameStem.Lead(entry.Key) is not null)
            .ToList();

        foreach (var group in keyed.GroupBy(entry => NameStem.Lead(entry.Key)!, StringComparer.Ordinal))
        {
            if (group.Count() < MinimumMembers) continue;

            var members = group.Select(entry => entry.Item).ToList();

            // The shared leading tokens, which is a better name than the single
            // token the group was keyed on: 建筑报告-立面 rather than 建筑报告
            // when every member has both.
            string common = NameStem.Common(group.Select(entry => entry.Key));
            string name = common.Length >= group.Key.Length ? common : group.Key;

            yield return new ProjectCandidate(
                name,
                ProjectSource.Stem,
                members,
                $"名字都以「{group.Key}」开头（{members.Count} 个）");
        }
    }

    /// <summary>Written within minutes of each other, sharing any specific word.</summary>
    private static IEnumerable<ProjectCandidate> ByBurst(IReadOnlyList<TidyItem> items)
    {
        var ordered = items.OrderBy(item => item.Modified).ToList();
        int at = 0;

        while (at < ordered.Count)
        {
            var window = new List<TidyItem> { ordered[at] };
            int next = at + 1;

            while (next < ordered.Count && ordered[next].Modified - window[0].Modified <= BurstWindow)
            {
                window.Add(ordered[next]);
                next++;
            }

            at = next;

            if (window.Count < MinimumMembers) continue;

            // A burst alone means nothing -- unzipping produces one. It counts
            // only when the names also agree on a word.
            string shared = NameStem.Common(window.Select(item => NameStem.Key(item.Name)));
            if (!NameStem.IsSpecific(shared)) continue;

            yield return new ProjectCandidate(
                shared,
                ProjectSource.Burst,
                window,
                $"同一时段写入，名字里都有「{shared}」（{window.Count} 个）");
        }
    }
}
