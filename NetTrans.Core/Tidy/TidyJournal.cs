using NetTrans.Verify;

namespace NetTrans.Tidy;

public sealed record TidyMove(string From, string To);

/// <summary>One run of 整理, kept whole so it can be undone as one.</summary>
public sealed record TidyBatch
{
    public required string Id { get; init; }
    public required DateTimeOffset When { get; init; }
    public required List<string> Roots { get; init; }
    public required List<TidyMove> Moves { get; init; }
}

/// <summary>
/// 整理日志: every move this tool has ever made, newest last, so any run can be
/// put back.
///
/// This is the thing that makes moving somebody's files defensible at all. A
/// tidier without an undo is a tidier nobody should run twice, and its own file
/// again -- NetTrans.tidy.json -- because it has nothing to do with what a file
/// hashes to.
/// </summary>
public sealed class TidyJournal
{
    /// <summary>Enough to undo anything anyone still remembers doing.</summary>
    public const int Keep = 20;

    private readonly List<TidyBatch> _batches = new();

    public IReadOnlyList<TidyBatch> Batches => _batches;

    public bool Dirty { get; private set; }

    public TidyBatch? Last => _batches.Count > 0 ? _batches[^1] : null;

    public void Add(TidyBatch batch)
    {
        if (batch.Moves.Count == 0) return;

        _batches.Add(batch);
        while (_batches.Count > Keep) _batches.RemoveAt(0);

        Dirty = true;
    }

    public void Remove(TidyBatch batch)
    {
        if (_batches.Remove(batch)) Dirty = true;
    }

    /// <summary>
    /// Puts one run back. A file that has since been moved or edited by hand is
    /// left alone and reported rather than overwritten: undo restores what this
    /// tool did, and nothing it did not.
    /// </summary>
    public static (int Restored, List<string> Refused) Undo(TidyBatch batch)
    {
        int restored = 0;
        var refused = new List<string>();

        // Newest first, so a file that was moved twice unwinds in order.
        foreach (var move in Enumerable.Reverse(batch.Moves))
        {
            try
            {
                if (!File.Exists(move.To))
                {
                    refused.Add($"{move.To}（已经不在了）");
                    continue;
                }

                if (File.Exists(move.From))
                {
                    refused.Add($"{move.From}（原位置又有文件了）");
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(move.From)!);
                File.Move(move.To, move.From);
                restored++;
            }
            catch (Exception failure)
            {
                refused.Add($"{move.To}（{failure.Message}）");
            }
        }

        return (restored, refused);
    }

    public static TidyJournal Load(string path)
    {
        var journal = new TidyJournal();

        foreach (var batch in JsonStore.Read<JournalFile>(path)?.Batches ?? Enumerable.Empty<TidyBatch>())
        {
            if (batch.Moves.Count > 0) journal._batches.Add(batch);
        }

        journal.Dirty = false;
        return journal;
    }

    public void Save(string path)
    {
        JsonStore.Write(path, new JournalFile { Version = 1, Batches = _batches.ToList() });
        Dirty = false;
    }

    private sealed class JournalFile
    {
        public int Version { get; set; }
        public List<TidyBatch> Batches { get; set; } = new();
    }
}
