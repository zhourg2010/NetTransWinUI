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

    /// <summary>
    /// Cuts the file to <paramref name="length"/>.
    ///
    /// A playlist transfer needs this and a ranged one does not: a ranged
    /// transfer knows its total length up front and writes into a pre-sized
    /// file, while a playlist restarting over a longer partial file would leave
    /// the old tail sitting behind the new bytes -- a corrupt stream rather
    /// than a shorter one.
    /// </summary>
    ValueTask TruncateAsync(long length, CancellationToken cancellationToken);

    /// <summary>
    /// Reads back what was written, returning how many bytes were available.
    ///
    /// A downloader has no use for this; a BitTorrent client cannot work
    /// without it. Serving a block to a peer means reading a piece back off
    /// disk, and a client that never uploads is one every swarm -- and every
    /// private tracker -- is right to reject.
    /// </summary>
    ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken);
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
