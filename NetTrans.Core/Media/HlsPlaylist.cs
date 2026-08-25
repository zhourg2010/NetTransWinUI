using NetTrans.Net;

namespace NetTrans.Media;

/// <summary>
/// A media playlist ready to fetch, with everything the job needs to name and
/// size the file it is going to write.
/// </summary>
/// <param name="Url">The media playlist itself -- the variant's, when one was chosen.</param>
/// <param name="Media">Its segments.</param>
/// <param name="Quality">The label of the chosen variant, or 视频 for a bare media playlist.</param>
/// <param name="Container">"ts" or "mp4", which decides the file's extension.</param>
public sealed record HlsPlaylist(Uri Url, HlsMedia Media, string Quality, string Container)
{
    public int SegmentCount => Media.Segments.Count;

    /// <summary>
    /// A size estimate from the playlist's own duration and the variant's
    /// bitrate, when both are known. Zero otherwise -- a playlist never states
    /// a byte count, and guessing one badly is worse than showing none.
    /// </summary>
    public long EstimatedBytes { get; init; }

    /// <summary>
    /// The form the transfer works in.
    ///
    /// <paramref name="track"/> is not always Muxed: HLS can put the audio in a
    /// separate #EXT-X-MEDIA rendition, in which case the variant is video only
    /// and fetching it alone gives a silent film.
    /// </summary>
    public SegmentedStream AsStream(TrackKind track = TrackKind.Muxed) => new(
        Url,
        Media.Segments
            .Select(segment => new StreamSegment(
                segment.Url,
                segment.SequenceNumber,
                segment.Duration,
                segment.ByteRangeOffset,
                segment.ByteRangeLength,
                segment.Key))
            .ToList(),
        Media.InitSegment,
        Quality,
        Container,
        track,
        EstimatedBytes);
}

/// <summary>
/// Turns a playlist URL into something fetchable: follows a master playlist to
/// the chosen rendition, reads the media playlist behind it, and refuses the
/// two cases a downloader cannot honestly serve -- a live stream with no end,
/// and SAMPLE-AES, which needs the codec to unpick rather than a key.
/// </summary>
public sealed class HlsPlaylistLoader
{
    /// <summary>A playlist that redirects to another that redirects to another is a loop, not a stream.</summary>
    private const int MaxRedirects = 3;

    private readonly IHttpTransport _transport;

    public HlsPlaylistLoader(IHttpTransport transport) => _transport = transport;

    /// <summary>
    /// The renditions on offer, best first. Empty when the URL is a media
    /// playlist -- there is only one thing to download and no choice to make.
    /// </summary>
    public async Task<IReadOnlyList<HlsVariant>> VariantsAsync(Uri playlist, CancellationToken cancellationToken = default)
    {
        string text = await PageReader.ReadAsync(_transport, playlist, cancellationToken: cancellationToken).ConfigureAwait(false);
        return M3U8.IsMaster(text) ? M3U8.ParseMaster(text, playlist) : Array.Empty<HlsVariant>();
    }

    /// <summary>
    /// Everything that has to be fetched to have the content: one stream when
    /// the variant carries its own audio, two when the master playlist puts the
    /// audio in a separate #EXT-X-MEDIA rendition.
    /// </summary>
    public async Task<IReadOnlyList<SegmentedStream>> LoadStreamsAsync(
        Uri playlist,
        CancellationToken cancellationToken = default)
    {
        string text = await PageReader
            .ReadAsync(_transport, playlist, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!M3U8.IsMaster(text))
        {
            // A bare media playlist has no renditions to speak of: whatever it
            // holds is all there is.
            var only = await LoadAsync(playlist, preferred: null, cancellationToken).ConfigureAwait(false);
            return new[] { only.AsStream() };
        }

        var variants = M3U8.ParseMaster(text, playlist);
        if (variants.Count == 0) throw new NotSupportedException("播放列表里没有可用的清晰度。");

        var best = variants[0];
        var audio = M3U8.AudioFor(best, M3U8.ParseRenditions(text, playlist));

        var video = await LoadAsync(playlist, best, cancellationToken).ConfigureAwait(false);

        // No separate audio track: the variant is a complete stream.
        if (audio?.Url is null) return new[] { video.AsStream() };

        var sound = await LoadAsync(audio.Url, preferred: null, cancellationToken).ConfigureAwait(false);

        return new[]
        {
            video.AsStream(TrackKind.Video),
            sound.AsStream(TrackKind.Audio) with { Quality = $"音轨 {audio.Name}" },
        };
    }

    /// <summary>
    /// Loads the playlist at <paramref name="playlist"/>, following a master to
    /// <paramref name="preferred"/> or, when that is null, to its best
    /// rendition.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// The stream is live, encrypted in a way this cannot decrypt, or empty.
    /// </exception>
    public async Task<HlsPlaylist> LoadAsync(
        Uri playlist,
        HlsVariant? preferred = null,
        CancellationToken cancellationToken = default)
    {
        var url = preferred?.Url ?? playlist;
        string quality = preferred?.Quality ?? "视频";
        long bandwidth = preferred?.Bandwidth ?? 0;

        HlsMedia? media = null;

        for (int hop = 0; hop <= MaxRedirects; hop++)
        {
            string text = await PageReader.ReadAsync(_transport, url, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!M3U8.IsMaster(text))
            {
                media = M3U8.ParseMedia(text, url);
                break;
            }

            // A master playlist where a media one was expected: take the best
            // rendition and look again.
            var best = M3U8.ParseMaster(text, url).FirstOrDefault()
                ?? throw new NotSupportedException("播放列表里没有可用的清晰度。");

            url = best.Url;
            quality = best.Quality;
            bandwidth = best.Bandwidth;
        }

        if (media is null) throw new NotSupportedException("播放列表嵌套过深，无法解析。");
        if (media.Segments.Count == 0) throw new NotSupportedException("播放列表里没有分片。");

        // A live playlist has no last segment, so there is no "finished" to
        // download to. Saying so beats writing a file that never completes.
        if (media.IsLive) throw new NotSupportedException("这是直播流，没有结束点，无法作为文件下载。");

        if (media.Segments.FirstOrDefault(segment => segment.Key?.Method == HlsEncryption.SampleAes) is not null)
        {
            throw new NotSupportedException("分片使用 SAMPLE-AES 加密，需要解码器配合，暂不支持。");
        }

        if (media.Segments.FirstOrDefault(segment => segment.Key is { Method: HlsEncryption.Aes128, KeyUri: null }) is not null)
        {
            throw new NotSupportedException("分片已加密但播放列表没有给出密钥地址。");
        }

        return new HlsPlaylist(url, media, quality, media.InitSegment is null ? "ts" : "mp4")
        {
            // bits per second over seconds, to bytes.
            EstimatedBytes = bandwidth > 0 ? (long)(bandwidth / 8d * media.TotalDuration) : 0,
        };
    }
}
