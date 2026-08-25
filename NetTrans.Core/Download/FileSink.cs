using Microsoft.Win32.SafeHandles;

namespace NetTrans.Download;

/// <summary>
/// The real sink.
///
/// It writes through <see cref="RandomAccess"/> on a shared handle rather than
/// through a FileStream: a FileStream carries one shared file pointer, so
/// seek-then-write from several connections would race, while RandomAccess
/// takes the offset per call and is safe for concurrent disjoint writes.
/// </summary>
public sealed class FileSink : IFileSink
{
    private readonly SafeFileHandle _handle;

    private FileSink(SafeFileHandle handle) => _handle = handle;

    public static FileSink Open(string path, long length)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // ReadWrite, not Write: serving a block to a peer means reading a piece
        // back out of the file we are still writing into.
        var handle = File.OpenHandle(
            path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read,
            FileOptions.Asynchronous);

        // Pre-allocating keeps the file contiguous and fails fast when the disk
        // is too full, rather than a hour into the transfer.
        if (length > 0 && RandomAccess.GetLength(handle) != length) RandomAccess.SetLength(handle, length);

        return new FileSink(handle);
    }

    public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        RandomAccess.WriteAsync(_handle, data, offset, cancellationToken);

    /// <summary>
    /// Writes already went to the OS through the handle; there is no buffer of
    /// our own to drain, and closing the handle on dispose is what pushes the
    /// last of it to disk. This exists so an in-memory sink can hook the same
    /// point in the transfer.
    /// </summary>
    public ValueTask FlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        long length = RandomAccess.GetLength(_handle);
        if (offset < 0 || offset >= length) return 0;

        int wanted = (int)Math.Min(buffer.Length, length - offset);

        return await RandomAccess
            .ReadAsync(_handle, buffer[..wanted], offset, cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask TruncateAsync(long length, CancellationToken cancellationToken)
    {
        if (length >= 0 && RandomAccess.GetLength(_handle) != length) RandomAccess.SetLength(_handle, length);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _handle.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Opens <see cref="FileSink"/>s.</summary>
public sealed class FileSinkFactory : IFileSinkFactory
{
    public static FileSinkFactory Instance { get; } = new();

    public ValueTask<IFileSink> OpenAsync(string path, long length, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IFileSink>(FileSink.Open(path, length));

    public bool Exists(string path) => File.Exists(path);
}
