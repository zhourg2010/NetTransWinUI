using System.Text.Json;
using NetTrans.Net;

namespace NetTrans.Download;

/// <summary>
/// The sidecar that makes 断点续传 work across restarts: which URL the partial
/// file came from, how long it was, what validated it, and how far each segment
/// got.
/// </summary>
/// <param name="Url">The URL the partial file was fetched from.</param>
/// <param name="TotalLength">File size at the time the transfer started.</param>
/// <param name="ETag">The server's validator when the transfer started.</param>
/// <param name="LastModified">Fallback validator.</param>
/// <param name="Segments">Per-segment progress.</param>
public sealed record ResumeState(
    string Url,
    long TotalLength,
    string? ETag,
    string? LastModified,
    IReadOnlyList<SegmentState> Segments)
{
    /// <summary>
    /// Whether the half-finished file on disk still matches what the server is
    /// serving. A changed length, ETag or Last-Modified means the remote file
    /// moved on and the partial bytes are garbage.
    /// </summary>
    public bool Matches(RemoteFileInfo info)
    {
        if (info.Length != TotalLength) return false;

        if (!string.IsNullOrEmpty(ETag) && !string.IsNullOrEmpty(info.ETag))
        {
            return string.Equals(ETag, info.ETag, StringComparison.Ordinal);
        }

        if (!string.IsNullOrEmpty(LastModified) && !string.IsNullOrEmpty(info.LastModified))
        {
            return string.Equals(LastModified, info.LastModified, StringComparison.Ordinal);
        }

        // Length alone is weak evidence, but it is all a bare server gives us.
        return true;
    }

    public static ResumeState From(Uri url, RemoteFileInfo info, SegmentPlan plan) =>
        new(url.ToString(), info.Length, info.ETag, info.LastModified, plan.Snapshot());
}

/// <summary>Reads and writes the resume sidecar next to the target file.</summary>
public sealed class ResumeStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static ResumeStore Instance { get; } = new();

    /// <summary>`foo.iso` is accompanied by `foo.iso.nettrans` while it is incomplete.</summary>
    public static string SidecarPath(string targetPath) => targetPath + ".nettrans";

    public async Task SaveAsync(string targetPath, ResumeState state, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.Create(SidecarPath(targetPath));
            await JsonSerializer.SerializeAsync(stream, state, Options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Losing the sidecar costs a restart of the transfer, not the app.
        }
    }

    public async Task<ResumeState?> LoadAsync(string targetPath, CancellationToken cancellationToken)
    {
        string path = SidecarPath(targetPath);
        if (!File.Exists(path)) return null;

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<ResumeState>(stream, Options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public void Delete(string targetPath)
    {
        try
        {
            string path = SidecarPath(targetPath);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // A stale sidecar is harmless: the next run revalidates it anyway.
        }
    }
}
