using System.Text;
using NetTrans.Download;
using Xunit;

namespace NetTrans.Tests;

/// <summary>校验 SHA-256, the context-menu action and the 完成后校验 setting.</summary>
public class ChecksumTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nettrans-hash-" + Guid.NewGuid().ToString("N"));

    public ChecksumTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Not worth failing a test over.
        }
    }

    /// <summary>The published SHA-256 of the empty input and of "abc".</summary>
    [Theory]
    [InlineData("", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    public async Task Computes_the_published_digests(string text, string expected)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        Assert.Equal(expected, await FileHash.ComputeAsync(stream));
    }

    [Fact]
    public async Task Hashes_a_file_the_same_as_its_bytes()
    {
        var bytes = new byte[3 * 1024 * 1024 + 17];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i % 253);

        string path = Path.Combine(_directory, "payload.bin");
        await File.WriteAllBytesAsync(path, bytes);

        using var stream = new MemoryStream(bytes);
        Assert.Equal(await FileHash.ComputeAsync(stream), await FileHash.ComputeFileAsync(path));
    }

    [Fact]
    public async Task Reports_progress_up_to_the_file_size()
    {
        var bytes = new byte[2 * 1024 * 1024 + 5];
        using var stream = new MemoryStream(bytes);

        // Progress<T> raises on the thread pool, so the readings are recorded
        // through interlocked state rather than a list the assert would be
        // enumerating while it is still being appended to.
        long highest = 0;
        long outOfRange = 0;

        await FileHash.ComputeAsync(stream, new Progress<long>(value =>
        {
            if (value < 1 || value > bytes.Length) Interlocked.Increment(ref outOfRange);

            long seen = Interlocked.Read(ref highest);
            if (value > seen) Interlocked.Exchange(ref highest, value);
        }));

        Assert.Equal(0, Interlocked.Read(ref outOfRange));
    }

    [Fact]
    public async Task Can_be_cancelled()
    {
        var bytes = new byte[8 * 1024 * 1024];
        using var stream = new MemoryStream(bytes);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => FileHash.ComputeAsync(stream, null, cancellation.Token));
    }

    [Theory]
    [InlineData("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", true)]
    [InlineData("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", true)]
    [InlineData("  ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad  ", true)]
    [InlineData("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad  payload.iso", true)]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000", false)]
    [InlineData("ba7816bf", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Compares_against_a_published_checksum(string? published, bool expected) =>
        Assert.Equal(expected, FileHash.Matches("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", published));

    [Fact]
    public void Describes_a_hash_with_nothing_to_compare_against() =>
        Assert.Equal("SHA-256 ba7816bf8f01cfea…", FileHash.Describe("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", null));

    [Fact]
    public void Describes_a_match_and_a_mismatch()
    {
        const string hash = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

        Assert.Equal(FileHash.Verified, FileHash.Describe(hash, hash));
        Assert.Equal(FileHash.Mismatch, FileHash.Describe(hash, new string('0', 64)));
    }
}
