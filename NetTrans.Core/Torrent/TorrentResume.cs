namespace NetTrans.Torrent;

/// <summary>
/// 断点续传 for a torrent, which is neither a byte offset nor a segment count
/// but a bitfield: pieces arrive out of order and each is verified on its own,
/// so what has to survive a restart is exactly which ones landed.
/// </summary>
/// <param name="InfoHash">Hex. A different torrent is a different file.</param>
/// <param name="Bitfield">Base64 of the completed-piece bitfield.</param>
public sealed record TorrentResumeState(string InfoHash, string Bitfield)
{
    public static TorrentResumeState From(TorrentMetainfo torrent, PiecePicker picker) =>
        new(Convert.ToHexString(torrent.InfoHash), Convert.ToBase64String(picker.Bitfield()));

    /// <summary>The saved bitfield, or null when it is not this torrent's.</summary>
    public byte[]? BitfieldFor(TorrentMetainfo torrent)
    {
        if (!string.Equals(InfoHash, Convert.ToHexString(torrent.InfoHash), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var bits = Convert.FromBase64String(Bitfield);
            return bits.Length == PeerWire.BitfieldLength(torrent.PieceCount) ? bits : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>Reads and writes the torrent sidecar next to the download.</summary>
public sealed class TorrentResumeStore
{
    private static readonly System.Text.Json.JsonSerializerOptions Options = new() { WriteIndented = false };

    public static TorrentResumeStore Instance { get; } = new();

    public static string SidecarPath(string targetPath) => targetPath + ".nettrans-bt";

    public async Task SaveAsync(string targetPath, TorrentResumeState state, CancellationToken cancellationToken)
    {
        try
        {
            string? directory = Path.GetDirectoryName(SidecarPath(targetPath));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            await using var stream = File.Create(SidecarPath(targetPath));
            await System.Text.Json.JsonSerializer
                .SerializeAsync(stream, state, Options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Losing the sidecar costs a re-download, not the app.
        }
    }

    public async Task<TorrentResumeState?> LoadAsync(string targetPath, CancellationToken cancellationToken)
    {
        string path = SidecarPath(targetPath);
        if (!File.Exists(path)) return null;

        try
        {
            await using var stream = File.OpenRead(path);
            return await System.Text.Json.JsonSerializer
                .DeserializeAsync<TorrentResumeState>(stream, Options, cancellationToken)
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
            // A stale sidecar is harmless: the next run revalidates it.
        }
    }
}
