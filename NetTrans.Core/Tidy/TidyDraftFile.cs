using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetTrans.Verify;

namespace NetTrans.Tidy;

/// <summary>
/// 草稿存盘: the draft as a file, which is also the interface between the
/// wizard and the command line.
///
/// `--plan` writes one, a person or the wizard edits it, `--plan … --apply`
/// executes exactly that and nothing else. It carries the file list it was
/// built from, so applying it is not at the mercy of a second scan finding a
/// different folder than the one somebody reviewed.
/// </summary>
public static class TidyDraftFile
{
    /// <summary>
    /// Indented, enums as names, Chinese unescaped: this file is meant to be
    /// opened and edited by a person, which a wall of \u5DE5 and bare 3s is
    /// not.
    /// </summary>
    private static readonly JsonSerializerOptions Readable = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Save(TidyDraft draft, string path) => JsonStore.Write(path, Of(draft), Readable);

    public static TidyDraft? Load(string path) => JsonStore.Read<DraftFile>(path, Readable) is { } file ? To(file) : null;

    internal static DraftFile Of(TidyDraft draft) => new()
    {
        Version = 1,
        Root = draft.Root,
        Options = draft.Options,
        Items = draft.Items.ToList(),
        Projects = draft.Projects.Select(project => new ProjectRow
        {
            Id = project.Id,
            Name = project.Name,
            Destination = project.Destination,
            Source = project.Source,
            Reason = project.Reason,
            Decision = project.Decision,
            Members = project.Members.ToList(),
        }).ToList(),
        Buckets = draft.Buckets.Values.Select(bucket => new BucketRow
        {
            Category = bucket.Category,
            Folder = bucket.Folder,
            ByMonth = bucket.ByMonth,
            Decision = bucket.Decision,
        }).ToList(),
        Skipped = draft.Skipped.ToList(),
        Duplicates = draft.Duplicates.ToDictionary(pair => pair.Key, pair => pair.Value),
        Guesses = draft.Guesses.ToDictionary(pair => pair.Key, pair => pair.Value),
    };

    internal static TidyDraft To(DraftFile file)
    {
        var draft = new TidyDraft(file.Root, file.Options ?? new TidyOptions(), file.Items)
        {
            Duplicates = file.Duplicates,
            Guesses = file.Guesses,
        };

        foreach (var row in file.Projects)
        {
            draft.Projects.Add(new DraftProject
            {
                Id = row.Id,
                Name = row.Name,
                Destination = row.Destination,
                Source = row.Source,
                Reason = row.Reason,
                Decision = row.Decision,
                Members = new HashSet<string>(row.Members, StringComparer.OrdinalIgnoreCase),
            });
        }

        foreach (var row in file.Buckets)
        {
            draft.Buckets[row.Category] = new DraftBucket
            {
                Category = row.Category,
                Folder = row.Folder,
                ByMonth = row.ByMonth,
                Decision = row.Decision,
            };
        }

        foreach (var path in file.Skipped) draft.Skipped.Add(path);

        draft.Rebuild();
        return draft;
    }

    internal sealed class DraftFile
    {
        public int Version { get; set; }
        public string Root { get; set; } = "";
        public TidyOptions? Options { get; set; }
        public List<TidyItem> Items { get; set; } = new();
        public List<ProjectRow> Projects { get; set; } = new();
        public List<BucketRow> Buckets { get; set; } = new();
        public List<string> Skipped { get; set; } = new();
        public Dictionary<string, string> Duplicates { get; set; } = new();
        public Dictionary<string, TidyCategory> Guesses { get; set; } = new();
    }

    internal sealed class ProjectRow
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Destination { get; set; } = "";
        public ProjectSource Source { get; set; }
        public string Reason { get; set; } = "";
        public Decision Decision { get; set; }
        public List<string> Members { get; set; } = new();
    }

    internal sealed class BucketRow
    {
        public TidyCategory Category { get; set; }
        public string Folder { get; set; } = "";
        public bool ByMonth { get; set; }
        public Decision Decision { get; set; }
    }
}
