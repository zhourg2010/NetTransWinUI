using System.Xml.Linq;
using NetTrans.Net;

namespace NetTrans.Media;

/// <summary>
/// Reads a DASH manifest into something fetchable.
///
/// The awkward part of DASH, and the reason this is not simply the HLS path
/// with a different parser, is that it usually keeps video and audio in
/// separate Representations. Concatenating the best video one gives a silent
/// file. So: a muxed Representation is preferred when the manifest offers one,
/// and when it does not, the video and its best audio come back as two streams
/// for the caller to fetch as two files -- which is honest, where a silent file
/// labelled as the video is not.
/// </summary>
public sealed class DashManifestLoader
{
    private readonly IHttpTransport _transport;

    public DashManifestLoader(IHttpTransport transport) => _transport = transport;

    /// <summary>
    /// What has to be fetched to have the content: one stream when the manifest
    /// is muxed, two when it splits audio from video.
    /// </summary>
    /// <exception cref="NotSupportedException">Live, empty, or not a manifest at all.</exception>
    public async Task<IReadOnlyList<SegmentedStream>> LoadAsync(Uri manifest, CancellationToken cancellationToken = default)
    {
        string text = await PageReader
            .ReadAsync(_transport, manifest, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return Read(text, manifest);
    }

    /// <summary>The parsing half, separated so it can be tested without a server.</summary>
    public static IReadOnlyList<SegmentedStream> Read(string text, Uri manifest)
    {
        if (!Mpd.IsManifest(text)) throw new NotSupportedException("这不是一个 DASH 清单（.mpd）。");

        XDocument document;
        try
        {
            document = XDocument.Parse(text);
        }
        catch (System.Xml.XmlException exception)
        {
            throw new NotSupportedException($"DASH 清单解析失败：{exception.Message}", exception);
        }

        // A dynamic manifest is the live case: it has no last segment, so there
        // is no finished file to download to.
        if (Mpd.IsLive(document)) throw new NotSupportedException("这是直播流，没有结束点，无法作为文件下载。");

        var representations = Mpd.Parse(document, manifest);
        if (representations.Count == 0) throw new NotSupportedException("清单里没有可下载的媒体。");

        // Best muxed track wins outright: one file, and it plays.
        if (representations.FirstOrDefault(r => r.Track == TrackKind.Muxed) is { } muxed)
        {
            return new[] { Stream(muxed, manifest) };
        }

        var video = representations.FirstOrDefault(r => r.Track == TrackKind.Video);
        var audio = representations.FirstOrDefault(r => r.Track == TrackKind.Audio);

        if (video is null && audio is null) throw new NotSupportedException("清单里没有可下载的音视频轨。");

        // Either alone is a complete answer when it is all the manifest has.
        var streams = new List<SegmentedStream>();
        if (video is not null) streams.Add(Stream(video, manifest));
        if (audio is not null) streams.Add(Stream(audio, manifest));

        return streams;
    }

    private static SegmentedStream Stream(DashRepresentation representation, Uri manifest)
    {
        double seconds = representation.Segments.Sum(segment => segment.Duration);

        return new SegmentedStream(
            manifest,
            representation.Segments,
            representation.InitSegment,
            representation.Quality,
            // DASH segments are fMP4 in all but the oldest streams, and an
            // init segment is the giveaway when they are.
            representation.MimeType.Contains("mp2t", StringComparison.OrdinalIgnoreCase) ? "ts" : "mp4",
            representation.Track,
            representation.Bandwidth > 0 && seconds > 0 ? (long)(representation.Bandwidth / 8d * seconds) : 0);
    }
}
