namespace NetTrans.Services;

/// <summary>
/// The stub engine's arithmetic, lifted out of the timer so it can be checked:
/// the growth curve, the 96-cell chunk map, the per-connection split and the
/// rolling sample buffers. Randomness is injected, so every rule here is
/// deterministic under test.
/// </summary>
public static class ProgressSimulator
{
    private const double Kb = 1024;

    /// <summary>The handoff's floor on throughput: 60 KB/s.</summary>
    public static readonly double MinimumSpeed = 60 * Kb;

    /// <summary>kbs = max(60, kbs * (0.93 + random * 0.15)), in bytes per second.</summary>
    public static double NextSpeed(double bytesPerSecond, double random01) =>
        Math.Max(MinimumSpeed, bytesPerSecond * (0.93 + random01 * 0.15));

    /// <summary>got = min(size, got + speed * elapsed).</summary>
    public static long Advance(long done, long size, double bytesPerSecond, double seconds) =>
        (long)Math.Min(size, done + bytesPerSecond * seconds);

    /// <summary>
    /// mkBlocks(): cells before the completion point are done, and a scattering
    /// of the next 12% are in flight.
    /// </summary>
    public static int[] MakeBlocks(double fraction, int count, Func<double> random)
    {
        var blocks = new int[count];

        for (int i = 0; i < count; i++)
        {
            double at = i / (double)count;
            blocks[i] = at < fraction ? 1
                : random() < 0.05 && at < fraction + 0.12 ? 2
                : 0;
        }

        return blocks;
    }

    /// <summary>mkConns(): the total rate split across connections, jittered 0.55x to 1.45x.</summary>
    public static double[] MakeConnections(int count, double bytesPerSecond, Func<double> random)
    {
        if (count <= 0) return Array.Empty<double>();

        var values = new double[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = bytesPerSecond > 0 ? bytesPerSecond / count * (0.55 + random() * 0.9) : 0;
        }

        return values;
    }

    /// <summary>Appends a sample, dropping the oldest once the buffer is at <paramref name="capacity"/>.</summary>
    public static double[] Push(double[] history, double value, int capacity)
    {
        if (capacity <= 0) return Array.Empty<double>();

        if (history.Length < capacity)
        {
            var grown = new double[history.Length + 1];
            Array.Copy(history, grown, history.Length);
            grown[^1] = value;
            return grown;
        }

        var next = new double[capacity];
        Array.Copy(history, history.Length - capacity + 1, next, 0, capacity - 1);
        next[^1] = value;
        return next;
    }
}
