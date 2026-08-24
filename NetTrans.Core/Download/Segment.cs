namespace NetTrans.Download;

/// <summary>A segment's progress, as stored in the resume sidecar.</summary>
/// <param name="Start">First byte of the range, inclusive.</param>
/// <param name="End">Last byte of the range, inclusive.</param>
/// <param name="Position">Next byte to fetch; past <paramref name="End"/> once finished.</param>
public readonly record struct SegmentState(long Start, long End, long Position);

/// <summary>One byte range being fetched by one connection.</summary>
public sealed class Segment
{
    public Segment(long start, long end, long position)
    {
        if (end < start) throw new ArgumentOutOfRangeException(nameof(end), "A segment cannot end before it starts.");
        if (position < start) throw new ArgumentOutOfRangeException(nameof(position), "A segment cannot resume before it starts.");

        Start = start;
        End = end;
        _position = position;
    }

    public Segment(long start, long end) : this(start, end, start)
    {
    }

    public long Start { get; }

    /// <summary>Inclusive.</summary>
    public long End { get; }

    private long _position;
    private volatile bool _completed;

    /// <summary>
    /// The next byte to fetch; one past <see cref="End"/> when the segment is
    /// done. Written by the connection that owns the segment and read by the
    /// UI thread, so it goes through Interlocked -- a plain long read is not
    /// atomic on 32-bit, and win-x86 is a shipped target.
    /// </summary>
    public long Position
    {
        get => Interlocked.Read(ref _position);
        set => Interlocked.Exchange(ref _position, value);
    }

    public long Length => End - Start + 1;

    public long Downloaded => Math.Min(Position - Start, Length);

    public long Remaining => Math.Max(0, End - Position + 1);

    public bool IsComplete => _completed || Position > End;

    /// <summary>
    /// Marks a segment done without moving its position -- the case where a
    /// server streams to EOF and the end offset was never known, so Position is
    /// the only record of how many bytes arrived.
    /// </summary>
    public void MarkComplete() => _completed = true;

    public SegmentState ToState() => new(Start, End, Position);

    public static Segment FromState(SegmentState state) => new(state.Start, state.End, state.Position);
}
