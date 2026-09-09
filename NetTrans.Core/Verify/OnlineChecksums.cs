using NetTrans.Net;

namespace NetTrans.Verify;

/// <summary>A digest found on the server, and the file it was found in.</summary>
public sealed record OnlineChecksum(HashKind Kind, string Hash, Uri Source);

/// <summary>
/// 联网核对: fetches the checksum files <see cref="ChecksumSources"/> points at
/// and returns the first digest that names this file.
///
/// Every failure is a shrug. A 404 is the normal answer -- most files have no
/// published digest at all -- and a download that already succeeded must not be
/// reported as suspect because a probe timed out.
/// </summary>
public static class OnlineChecksums
{
    /// <summary>A SHA256SUMS for a large release is a few hundred kilobytes; past this it is not one.</summary>
    public const int MaxBytes = 512 * 1024;

    public static async Task<OnlineChecksum?> LookupAsync(
        IHttpTransport transport,
        Uri url,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        foreach (var candidate in ChecksumSources.For(url, fileName))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string text;
            try
            {
                text = await PageReader.ReadAsync(transport, candidate.Url, MaxBytes, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                continue;
            }

            var entries = ChecksumFile.Parse(text);
            if (ChecksumFile.Find(entries, fileName, candidate.SingleEntryWins) is not { } hit) continue;

            return new OnlineChecksum(hit.Kind, hit.Hash, candidate.Url);
        }

        return null;
    }
}
