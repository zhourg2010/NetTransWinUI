namespace NetTrans.Tidy;

/// <summary>Where one card has got to. Pending means "still proposed, not yet looked at".</summary>
public enum Decision
{
    Pending,
    Accepted,
    Skipped,
}

/// <summary>One project as it stands in the draft: proposed, then edited by whoever is deciding.</summary>
public sealed class DraftProject
{
    public required string Id { get; init; }
    public required string Name { get; set; }

    /// <summary>Folder under the scanned root. Defaults to 项目\&lt;name&gt;, and is the person's to change.</summary>
    public required string Destination { get; set; }

    public ProjectSource Source { get; init; }
    public string Reason { get; init; } = "";
    public Decision Decision { get; set; } = Decision.Pending;

    public HashSet<string> Members { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public DraftProject Copy() => new()
    {
        Id = Id,
        Name = Name,
        Destination = Destination,
        Source = Source,
        Reason = Reason,
        Decision = Decision,
        Members = new HashSet<string>(Members, StringComparer.OrdinalIgnoreCase),
    };
}

/// <summary>One kind of file, as a card: where its files go, and whether they go at all.</summary>
public sealed class DraftBucket
{
    public required TidyCategory Category { get; init; }
    public required string Folder { get; set; }
    public bool ByMonth { get; set; }
    public Decision Decision { get; set; } = Decision.Pending;

    public DraftBucket Copy() => new() { Category = Category, Folder = Folder, ByMonth = ByMonth, Decision = Decision };
}

/// <summary>
/// 整理草稿: everything decided so far, and nothing on disk.
///
/// The wizard is not a sequence of screens -- it is this object plus a queue
/// derived from it. Accepting a project removes its files from the type buckets
/// and may make a card disappear; skipping one puts them back. That is why the
/// queue is recomputed from the draft after every decision rather than fixed up
/// front, and why the number of cards is a property of the folder rather than
/// of the design.
///
/// Every operation snapshots first, so 上一个 is exact rather than an inverse
/// somebody had to remember to write.
/// </summary>
public sealed class TidyDraft
{
    private readonly Stack<Snapshot> _undo = new();

    public TidyDraft(string root, TidyOptions options, IReadOnlyList<TidyItem> items)
    {
        Root = root;
        Options = options;
        Items = items;
    }

    public string Root { get; }

    public TidyOptions Options { get; }

    public IReadOnlyList<TidyItem> Items { get; }

    public List<DraftProject> Projects { get; private set; } = new();

    public Dictionary<TidyCategory, DraftBucket> Buckets { get; private set; } = new();

    /// <summary>Files the person took out of the run entirely.</summary>
    public HashSet<string> Skipped { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Duplicate path to the copy being kept, when 找重复 ran.</summary>
    public IReadOnlyDictionary<string, string> Duplicates { get; set; } = new Dictionary<string, string>();

    /// <summary>What the model said about names the rules could not place.</summary>
    public IReadOnlyDictionary<string, TidyCategory> Guesses { get; set; } = new Dictionary<string, TidyCategory>();

    /// <summary>Fills the draft with what the finder proposes. Bucket cards are then whatever is left over.</summary>
    public static TidyDraft From(
        string root,
        TidyOptions options,
        IReadOnlyList<TidyItem> items,
        IReadOnlyList<ProjectCandidate> candidates)
    {
        var draft = new TidyDraft(root, options, items);

        int n = 1;
        foreach (var candidate in candidates)
        {
            draft.Projects.Add(new DraftProject
            {
                Id = $"p{n++}",
                Name = candidate.Name,
                Destination = Path.Combine("项目", candidate.Name),
                Source = candidate.Source,
                Reason = candidate.Reason,
                Members = new HashSet<string>(candidate.Members.Select(member => member.Path), StringComparer.OrdinalIgnoreCase),
            });
        }

        draft.Rebuild();
        return draft;
    }

    /// <summary>The project a file currently belongs to, ignoring skipped ones.</summary>
    public DraftProject? ProjectOf(string path) =>
        Projects.FirstOrDefault(project => project.Decision != Decision.Skipped && project.Members.Contains(path));

    /// <summary>What a file counts as when it is not in a project: the rules, with the model's answer where they gave up.</summary>
    public TidyCategory CategoryOf(TidyItem item)
    {
        if (Duplicates.ContainsKey(item.Path)) return TidyCategory.Duplicate;

        var category = TidyRules.Classify(item.Name).Category;

        return category == TidyCategory.Unknown && Guesses.TryGetValue(item.Name, out var guessed)
            ? guessed
            : category;
    }

    /// <summary>Files in no project and not skipped -- what the type cards are made of.</summary>
    public IEnumerable<TidyItem> Loose() =>
        Items.Where(item => !Skipped.Contains(item.Path) && ProjectOf(item.Path) is null);

    /// <summary>Files a bucket would move, for the card's count and list.</summary>
    public IReadOnlyList<TidyItem> InBucket(TidyCategory category) =>
        Loose().Where(item => CategoryOf(item) == category).ToList();

    /// <summary>Adds any bucket the current assignment calls for, and drops the ones that emptied.</summary>
    public void Rebuild()
    {
        var wanted = Loose().Select(CategoryOf).Distinct().ToList();

        foreach (var category in wanted)
        {
            if (Buckets.ContainsKey(category)) continue;

            Buckets[category] = new DraftBucket { Category = category, Folder = TidyCategories.Folder(category) };
        }

        // A bucket whose files all went into projects is not a question worth
        // asking; if they come back, so does it -- with its answer, if it had
        // one.
        foreach (var gone in Buckets.Keys.Where(category => !wanted.Contains(category)).ToList())
        {
            if (Buckets[gone].Decision == Decision.Pending) Buckets.Remove(gone);
        }
    }

    // ── operations ────────────────────────────────────────────────────────

    public void AcceptProject(string id) => Change(() => Find(id).Decision = Decision.Accepted);

    public void SkipProject(string id) => Change(() => Find(id).Decision = Decision.Skipped);

    public void RenameProject(string id, string name) => Change(() =>
    {
        var project = Find(id);
        bool automatic = string.Equals(project.Destination, Path.Combine("项目", project.Name), StringComparison.Ordinal);

        project.Name = name;

        // A destination the person has not touched follows the name; one they
        // typed themselves does not get overwritten.
        if (automatic) project.Destination = Path.Combine("项目", name);
    });

    public void SetProjectDestination(string id, string folder) => Change(() => Find(id).Destination = folder);

    public void AddToProject(string id, string path) => Change(() =>
    {
        foreach (var project in Projects) project.Members.Remove(path);

        Find(id).Members.Add(path);
        Skipped.Remove(path);
    });

    public void RemoveFromProject(string id, string path) => Change(() => Find(id).Members.Remove(path));

    public void AcceptBucket(TidyCategory category) => Change(() => Bucket(category).Decision = Decision.Accepted);

    public void SkipBucket(TidyCategory category) => Change(() => Bucket(category).Decision = Decision.Skipped);

    public void SetBucketFolder(TidyCategory category, string folder) => Change(() => Bucket(category).Folder = folder);

    public void SetBucketByMonth(TidyCategory category, bool byMonth) => Change(() => Bucket(category).ByMonth = byMonth);

    public void SkipFile(string path) => Change(() => Skipped.Add(path));

    public void KeepFile(string path) => Change(() => Skipped.Remove(path));

    /// <summary>快进: everything still pending is taken as proposed.</summary>
    public void AcceptRest() => Change(() =>
    {
        foreach (var project in Projects.Where(project => project.Decision == Decision.Pending))
        {
            project.Decision = Decision.Accepted;
        }

        foreach (var bucket in Buckets.Values.Where(bucket => bucket.Decision == Decision.Pending))
        {
            bucket.Decision = Decision.Accepted;
        }
    });

    public bool CanUndo => _undo.Count > 0;

    /// <summary>上一个: back to exactly the state before the last operation.</summary>
    public void Undo()
    {
        if (_undo.Count == 0) return;

        var snapshot = _undo.Pop();
        Projects = snapshot.Projects;
        Buckets = snapshot.Buckets;
        Skipped = snapshot.Skipped;
    }

    private DraftProject Find(string id) =>
        Projects.FirstOrDefault(project => project.Id == id)
            ?? throw new ArgumentException($"没有这个项目：{id}", nameof(id));

    private DraftBucket Bucket(TidyCategory category) =>
        Buckets.TryGetValue(category, out var bucket)
            ? bucket
            : Buckets[category] = new DraftBucket { Category = category, Folder = TidyCategories.Folder(category) };

    private void Change(Action change)
    {
        _undo.Push(new Snapshot(
            Projects.Select(project => project.Copy()).ToList(),
            Buckets.ToDictionary(pair => pair.Key, pair => pair.Value.Copy()),
            new HashSet<string>(Skipped, StringComparer.OrdinalIgnoreCase)));

        change();
        Rebuild();
    }

    private sealed record Snapshot(
        List<DraftProject> Projects,
        Dictionary<TidyCategory, DraftBucket> Buckets,
        HashSet<string> Skipped);
}
