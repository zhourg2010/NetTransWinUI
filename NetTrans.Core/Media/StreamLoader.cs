using NetTrans.Net;

namespace NetTrans.Media;

/// <summary>
/// Reads whichever manifest a URL points at and hands back the streams behind
/// it, so a transfer never has to know which of the two formats it got.
/// </summary>
public sealed class StreamLoader
{
    private readonly IHttpTransport _transport;

    public StreamLoader(IHttpTransport transport) => _transport = transport;

    /// <summary>
    /// Every stream that has to be fetched to have the content. One for HLS and
    /// for a muxed DASH manifest; two when DASH splits video from audio.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// The manifest is live, empty, or of a kind that cannot be turned into a
    /// file. The message says which, because the row shows it.
    /// </exception>
    public async Task<IReadOnlyList<SegmentedStream>> LoadAsync(Uri manifest, CancellationToken cancellationToken = default)
    {
        if (PlaylistUrl.IsDash(manifest.AbsoluteUri))
        {
            return await new DashManifestLoader(_transport).LoadAsync(manifest, cancellationToken).ConfigureAwait(false);
        }

        return await new HlsPlaylistLoader(_transport)
            .LoadStreamsAsync(manifest, cancellationToken)
            .ConfigureAwait(false);
    }
}
