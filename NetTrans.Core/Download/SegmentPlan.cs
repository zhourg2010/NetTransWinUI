namespace NetTrans.Download;

/// <summary>
/// How a file is divided across connections, and the progress of each one.
///
/// A server that will not serve ranges, or will not say how long the file is,
/// gets a single unbounded segment -- the transfer still works, it just cannot
/// be split or resumed.
/// </summary>
public sealed class SegmentPlan
{
    private readonly List<Segment> _segments;

    private SegmentPlan(long totalLength, List<Segment> segments, bool bounded)
    {
        TotalLength = totalLength;
        _segments = segments;
        IsBounded = bounded;
    }

    /// <summary>File size in bytes, or -1 when the server did not say.</summary>
    public long TotalLength { get; }

    /// <summary>False when the length is unknown, which also rules out ranges and resume.</summary>
    public bool IsBounded { get; }

    public IReadOnlyList<Segment> Segments => _segments;

    public long Downloaded => _segments.Sum(segment => segment.Downloaded);

    public bool IsComplete => IsBounded
        ? _segments.All(segment => segment.IsComplete)
        : _segments.Count > 0 && _segments[0].IsComplete;

    public double Fraction => !IsBounded || TotalLength <= 0
        ? 0
        : Math.Clamp(Downloaded / (double)TotalLength, 0, 1);

    /// <summary>Connections currently carrying data, which is the row's 连接数.</summary>
    public int ActiveSegmentCount => _segments.Count(segment => !segment.IsComplete);

    /// <summary>
    /// Splits <paramref name="totalLength"/> across <paramref name="segmentCount"/>
    /// connections. Segments are never smaller than <paramref name="minimumSegmentLength"/>,
    /// so a small file is not split into a swarm of tiny requests.
    /// </summary>
    public static SegmentPlan Create(long totalLength, int segmentCount, long minimumSegmentLength = 1024 * 1024)
    {
        if (segmentCount < 1) throw new ArgumentOutOfRangeException(nameof(segmentCount), "A transfer needs at least one connection.");
        if (totalLength <= 0) return Unbounded();

        int count = segmentCount;
        if (minimumSegmentLength > 0)
        {
            count = (int)Math.Min(count, Math.Max(1, totalLength / minimumSegmentLength));
        }

        var segments = new List<Segment>(count);
        long size = totalLength / count;

        for (int i = 0; i < count; i++)
        {
            long start = i * size;
            long end = i == count - 1 ? totalLength - 1 : start + size - 1;
            segments.Add(new Segment(start, end));
        }

        return new SegmentPlan(totalLength, segments, bounded: true);
    }

    /// <summary>A single connection reading to the end of the stream, for a server that will not do ranges.</summary>
    public static SegmentPlan Unbounded() =>
        new(-1, new List<Segment> { new(0, long.MaxValue - 1) }, bounded: false);

    /// <summary>Rebuilds a plan from a resume sidecar.</summary>
    public static SegmentPlan Restore(long totalLength, IEnumerable<SegmentState> states)
    {
        var segments = states.Select(Segment.FromState).OrderBy(segment => segment.Start).ToList();
        if (segments.Count == 0) return Create(totalLength, 1);

        return new SegmentPlan(totalLength, segments, bounded: totalLength > 0);
    }

    public IReadOnlyList<SegmentState> Snapshot() => _segments.Select(segment => segment.ToState()).ToList();

    /// <summary>
    /// The inspector's 96-cell chunk map: 1 where the bytes have landed, 2 for
    /// the cell each live connection is working through, 0 for the rest.
    /// </summary>
    public int[] BlockMap(int cells)
    {
        if (cells <= 0) return Array.Empty<int>();

        var map = new int[cells];
        if (!IsBounded || TotalLength <= 0) return map;

        for (int i = 0; i < cells; i++)
        {
            long cellStart = (long)((double)i / cells * TotalLength);
            long cellEnd = (i == cells - 1 ? TotalLength : (long)((double)(i + 1) / cells * TotalLength)) - 1;
            if (cellEnd < cellStart) cellEnd = cellStart;

            foreach (var segment in _segments)
            {
                // The head of a live connection sits inside this cell.
                if (!segment.IsComplete && segment.Position >= cellStart && segment.Position <= cellEnd)
                {
                    map[i] = 2;
                    break;
                }

                // The cell is entirely behind a segment's write head.
                if (segment.Start <= cellStart && segment.Position > cellEnd) map[i] = 1;
            }
        }

        return map;
    }
}
