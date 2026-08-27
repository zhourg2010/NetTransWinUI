using System.Collections;

namespace NetTrans.Models;

/// <summary>
/// A transfer's log lines, bounded and safe to read while a transfer is writing
/// to it.
///
/// Two problems with the plain list this replaces. It grew forever -- a torrent
/// logs on every re-announce, so a task left seeding overnight accumulated
/// thousands of entries that the inspector re-rendered whenever the count
/// moved. And it was written from the transfer's threads while the UI walked
/// it, which is an <see cref="InvalidOperationException"/> waiting for a busy
/// moment.
///
/// The oldest lines are the ones dropped: what a row is doing now is what the
/// log is opened for, and the first announce of a torrent that has been running
/// for six hours is not.
/// </summary>
public sealed class TransferLog : IReadOnlyList<LogEntry>
{
    /// <summary>How many lines are kept.</summary>
    public const int Keep = 400;

    /// <summary>How far past <see cref="Keep"/> it is allowed to grow before trimming.</summary>
    private const int Slack = 100;

    private readonly List<LogEntry> _entries = new();
    private readonly object _gate = new();

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>How many lines have been dropped off the front, if any.</summary>
    public int Dropped { get; private set; }

    public LogEntry this[int index]
    {
        get { lock (_gate) return _entries[index]; }
    }

    public void Add(LogEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);

            // Trimmed in batches: removing one entry from the front of a list
            // moves everything else, so doing it on every line would turn a
            // chatty transfer into a memmove loop.
            if (_entries.Count <= Keep + Slack) return;

            int excess = _entries.Count - Keep;
            _entries.RemoveRange(0, excess);
            Dropped += excess;
        }
    }

    /// <summary>A copy, so a reader is not walking a list a transfer is still writing to.</summary>
    public IEnumerator<LogEntry> GetEnumerator()
    {
        LogEntry[] snapshot;
        lock (_gate) snapshot = _entries.ToArray();

        return ((IEnumerable<LogEntry>)snapshot).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
