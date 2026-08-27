using System.Text.Json;

namespace NetTrans.Download;

/// <summary>
/// 断点续传 for a playlist, which is a different question from a ranged file.
/// There are no byte offsets to resume at -- a half-written segment is not a
/// valid part of the stream -- so what is remembered is how many whole segments
/// were written and how long the file was at that point.
/// </summary>
/// <param name="Url">The playlist URL. A different one is a different file.</param>
/// <param name="SegmentCount">How many segments the playlist had. A changed count means it was re-cut.</param>
/// <param name="SegmentsDone">Whole segments already written, in play order.</param>
/// <param name="BytesWritten">File length after those segments, which is where the next one goes.</param>
public sealed record PlaylistResumeState(string Url, int SegmentCount, int SegmentsDone, long BytesWritten);

/// <summary>Reads and writes the playlist sidecar next to the target file.</summary>
public sealed class PlaylistResumeStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static PlaylistResumeStore Instance { get; } = new();

    /// <summary>
    /// A distinct suffix from the ranged one: the two states are not
    /// interchangeable, and reading one as the other would resume from a byte
    /// offset that means nothing here.
    /// </summary>
    public static string SidecarPath(string targetPath) => targetPath + ".nettrans-hls";

    public async Task SaveAsync(string targetPath, PlaylistResumeState state, CancellationToken cancellationToken)
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

    public async Task<PlaylistResumeState?> LoadAsync(string targetPath, CancellationToken cancellationToken)
    {
        string path = SidecarPath(targetPath);
        if (!File.Exists(path)) return null;

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer
                .DeserializeAsync<PlaylistResumeState>(stream, Options, cancellationToken)
                .ConfigureAwait(false);
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
