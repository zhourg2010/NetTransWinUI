using NetTrans.Tidy;

namespace NetTrans.Services;

/// <summary>
/// 整理日志 on disk: NetTrans.tidy.json, next to the executable.
///
/// The third of NetTrans' own files, and separate from the other two for the
/// same reason they are separate from each other: this one is a list of moves
/// that can be undone, not a claim about what anything hashes to.
/// </summary>
public sealed class TidyStore
{
    private readonly Lazy<TidyJournal> _journal;

    public TidyStore()
    {
        Path = PortableStorage.PathFor("netX.tidy.json", "tidy.json");
        _journal = new Lazy<TidyJournal>(() => TidyJournal.Load(Path));
    }

    public string Path { get; }

    public string ReportPath => System.IO.Path.ChangeExtension(Path, ".md");

    public TidyJournal Journal => _journal.Value;

    public void Flush()
    {
        if (!_journal.IsValueCreated || !_journal.Value.Dirty) return;

        try
        {
            _journal.Value.Save(Path);
        }
        catch (Exception)
        {
            // Losing the log is bad -- it is what undo runs on -- but it must
            // not turn a completed run into a crash. The files have moved
            // either way, and the report on screen lists every one of them.
        }
    }
}
