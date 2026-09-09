namespace NetTrans.Tidy;

public enum TidyCardKind
{
    /// <summary>One proposed project, with its members.</summary>
    Project,

    /// <summary>One kind of file.</summary>
    Bucket,

    /// <summary>Every kind that has exactly one file, together. Ten cards holding one file each is not a wizard, it is a chore.</summary>
    Loose,

    /// <summary>The whole plan, and the button that runs it.</summary>
    Summary,
}

/// <param name="Categories">What a Loose card covers; a Bucket card has exactly one.</param>
public sealed record TidyCard(
    TidyCardKind Kind,
    string Title,
    string Detail,
    int Count,
    string? ProjectId = null,
    IReadOnlyList<TidyCategory>? Categories = null);

/// <summary>
/// 决策队列: the cards still waiting, derived from the draft every time.
///
/// Nothing here is stored, which is the point. Accepting a project takes its
/// files out of the type buckets, so a bucket card can vanish before it is ever
/// shown; skipping one puts them back and the card returns. A queue fixed at
/// the start would have shown a card for an empty bucket and asked a question
/// with no subject.
/// </summary>
public static class TidyQueue
{
    /// <summary>Everything still undecided, in the order it should be asked, with the summary last.</summary>
    public static IReadOnlyList<TidyCard> Pending(TidyDraft draft)
    {
        var cards = new List<TidyCard>();

        foreach (var project in draft.Projects
                     .Where(project => project.Decision == Decision.Pending && project.Members.Count > 0)
                     .OrderBy(project => project.Source)
                     .ThenByDescending(project => project.Members.Count)
                     .ThenBy(project => project.Name, StringComparer.Ordinal))
        {
            cards.Add(new TidyCard(
                TidyCardKind.Project,
                project.Name,
                project.Reason,
                project.Members.Count,
                ProjectId: project.Id));
        }

        var buckets = draft.Buckets.Values
            .Where(bucket => bucket.Decision == Decision.Pending)
            .Select(bucket => (Bucket: bucket, Files: draft.InBucket(bucket.Category)))
            .Where(entry => entry.Files.Count > 0)
            .ToList();

        foreach (var entry in buckets
                     .Where(entry => entry.Files.Count > 1)
                     .OrderBy(entry => Last(entry.Bucket.Category))
                     .ThenByDescending(entry => entry.Files.Count)
                     .ThenBy(entry => entry.Bucket.Folder, StringComparer.Ordinal))
        {
            cards.Add(new TidyCard(
                TidyCardKind.Bucket,
                TidyCategories.Folder(entry.Bucket.Category),
                $"{entry.Files.Count} 个文件",
                entry.Files.Count,
                Categories: new[] { entry.Bucket.Category }));
        }

        var singles = buckets.Where(entry => entry.Files.Count == 1).ToList();
        if (singles.Count > 0)
        {
            cards.Add(new TidyCard(
                TidyCardKind.Loose,
                "零散文件",
                $"{singles.Count} 个各自成类的文件",
                singles.Count,
                Categories: singles.Select(entry => entry.Bucket.Category).ToList()));
        }

        cards.Add(new TidyCard(TidyCardKind.Summary, "确认", "看一遍再动手", 0));

        return cards;
    }

    /// <summary>The card being asked about now.</summary>
    public static TidyCard Current(TidyDraft draft) => Pending(draft)[0];

    /// <summary>
    /// 第 N / M 项. Both numbers move as decisions are made -- accepting a
    /// project can remove a question nobody now needs to answer -- and saying so
    /// honestly beats a progress bar that lies about what is left.
    /// </summary>
    public static (int At, int Total) Progress(TidyDraft draft)
    {
        int decided = draft.Projects.Count(project => project.Decision != Decision.Pending)
            + draft.Buckets.Values.Count(bucket => bucket.Decision != Decision.Pending);

        return (decided + 1, decided + Pending(draft).Count);
    }

    /// <summary>Junk and duplicates come after the real categories: they are housekeeping, not filing.</summary>
    private static int Last(TidyCategory category) =>
        category is TidyCategory.Junk or TidyCategory.Duplicate ? 1 : 0;
}
