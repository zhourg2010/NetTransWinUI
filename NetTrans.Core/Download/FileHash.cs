using System.Security.Cryptography;

namespace NetTrans.Download;

/// <summary>
/// 校验 SHA-256, both the context-menu action and the 完成后校验 setting.
/// Streams the file so a 6 GB ISO does not have to fit in memory, and reports
/// progress so the UI can show something during the minute it takes.
/// </summary>
public static class FileHash
{
    private const int BufferSize = 1024 * 1024;

    /// <summary>The state text the 校验 row shows before a hash has been computed.</summary>
    public const string Pending = "SHA-256 待校验";

    /// <summary>The state text once a hash has been computed.</summary>
    public const string Verified = "SHA-256 已校验";

    /// <summary>The state text when a computed hash did not match the one that was expected.</summary>
    public const string Mismatch = "SHA-256 校验失败";

    /// <summary>Hashes a stream, reporting bytes read as it goes. Returns lower-case hex.</summary>
    public static async Task<string> ComputeAsync(
        Stream stream,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var sha = SHA256.Create();
        var buffer = new byte[BufferSize];
        long total = 0;

        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;

            sha.TransformBlock(buffer, 0, read, null, 0);
            total += read;
            progress?.Report(total);
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash ?? Array.Empty<byte>()).ToLowerInvariant();
    }

    /// <summary>Hashes a file on disk.</summary>
    public static async Task<string> ComputeFileAsync(
        string path,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

        return await ComputeAsync(stream, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Compares a computed hash against one published alongside the file.
    /// Whitespace and case vary between checksum files, and many carry the
    /// filename after the hash, so only the leading hex run is compared.
    /// </summary>
    public static bool Matches(string computed, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected)) return false;

        string wanted = new(expected.Trim().TakeWhile(Uri.IsHexDigit).ToArray());
        return wanted.Length == computed.Length
            && string.Equals(wanted, computed, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The 校验 row's text for a hash that has just been computed.</summary>
    public static string Describe(string computed, string? expected) =>
        string.IsNullOrWhiteSpace(expected)
            ? $"SHA-256 {computed[..16]}…"
            : Matches(computed, expected) ? Verified : Mismatch;
}
