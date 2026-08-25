using NetTrans.Download;

namespace NetTrans.Tests.Fakes;

/// <summary>An in-memory stand-in for the target file, so transfers can be tested without touching a disk.</summary>
public sealed class MemoryFileSink : IFileSink
{
    private readonly object _gate = new();
    private byte[] _bytes;

    public MemoryFileSink(long length) => _bytes = new byte[Math.Max(0, length)];

    public bool Flushed { get; private set; }

    public byte[] ToArray()
    {
        lock (_gate) return _bytes.ToArray();
    }

    public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            long required = offset + data.Length;
            if (required > _bytes.Length) Array.Resize(ref _bytes, (int)required);
            data.Span.CopyTo(_bytes.AsSpan((int)offset));
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        Flushed = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (offset < 0 || offset >= _bytes.Length) return ValueTask.FromResult(0);

            int wanted = (int)Math.Min(buffer.Length, _bytes.Length - offset);
            _bytes.AsSpan((int)offset, wanted).CopyTo(buffer.Span);

            return ValueTask.FromResult(wanted);
        }
    }

    public ValueTask TruncateAsync(long length, CancellationToken cancellationToken)
    {
        lock (_gate) Array.Resize(ref _bytes, (int)Math.Max(0, length));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Hands out <see cref="MemoryFileSink"/>s and remembers them by path.</summary>
public sealed class MemoryFileSinkFactory : IFileSinkFactory
{
    public Dictionary<string, MemoryFileSink> Files { get; } = new();

    /// <summary>Marks a path as already holding a partial file, so resume can be tested.</summary>
    public void Seed(string path, MemoryFileSink sink) => Files[path] = sink;

    public ValueTask<IFileSink> OpenAsync(string path, long length, CancellationToken cancellationToken)
    {
        if (!Files.TryGetValue(path, out var sink))
        {
            sink = new MemoryFileSink(length);
            Files[path] = sink;
        }

        return ValueTask.FromResult<IFileSink>(sink);
    }

    public bool Exists(string path) => Files.ContainsKey(path);
}
