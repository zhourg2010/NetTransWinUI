namespace NetTrans.Download;

/// <summary>
/// Where a transfer's bytes land. Segments write to disjoint offsets from
/// several connections at once, so implementations must tolerate concurrent
/// writes at different offsets.
/// </summary>
public interface IFileSink : IAsyncDisposable
{
    ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    ValueTask FlushAsync(CancellationToken cancellationToken);
}

/// <summary>Opens the sink for a transfer. Faked in tests with an in-memory buffer.</summary>
public interface IFileSinkFactory
{
    /// <summary>
    /// Opens <paramref name="path"/> for writing, pre-sizing it to
    /// <paramref name="length"/> when that is known (-1 when it is not).
    /// </summary>
    ValueTask<IFileSink> OpenAsync(string path, long length, CancellationToken cancellationToken);

    /// <summary>
    /// Whether a partial file is already there. Resuming needs to know, and
    /// asking the sink rather than the filesystem keeps the transfer testable.
    /// </summary>
    bool Exists(string path);
}
