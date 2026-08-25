namespace NetTrans.Media;

/// <summary>
/// One piece of a segmented stream, whichever manifest listed it.
/// </summary>
/// <param name="Url">Absolute.</param>
/// <param name="SequenceNumber">
/// Position in the stream. HLS derives an implicit AES IV from it, so it has to
/// be the manifest's own number rather than a running index.
/// </param>
/// <param name="Duration">Seconds, when the manifest said. Zero when it did not.</param>
/// <param name="RangeOffset">Set when the segment is a slice of a larger file.</param>
/// <param name="RangeLength">Length of that slice.</param>
/// <param name="Key">Null when the segment is in the clear.</param>
public sealed record StreamSegment(
    Uri Url,
    long SequenceNumber,
    double Duration = 0,
    long? RangeOffset = null,
    long? RangeLength = null,
    HlsKey? Key = null);

/// <summary>What kind of track a stream carries, which decides how a file made of it can be used.</summary>
public enum TrackKind
{
    /// <summary>Video and audio together: the file plays as-is. Every HLS rendition, and a muxed DASH one.</summary>
    Muxed,

    /// <summary>Video only. Playable, silent. DASH usually splits this way.</summary>
    Video,

    /// <summary>Audio only.</summary>
    Audio,
}

/// <summary>
/// A stream reduced to the thing a transfer actually needs: an ordered list of
/// segments and the container they build.
///
/// HLS and DASH disagree about almost everything above this line -- one is a
/// text playlist and the other an XML manifest, one numbers segments and the
/// other templates them -- and about nothing below it. So the manifest readers
/// end here, and one transfer serves both.
/// </summary>
/// <param name="Source">The manifest this came from.</param>
/// <param name="Segments">In play order.</param>
/// <param name="InitSegment">Written before everything else, for fMP4. Null for MPEG-TS.</param>
/// <param name="Quality">The label a picker shows: 1080p, or a bitrate when the manifest gave no size.</param>
/// <param name="Container">"ts" or "mp4" -- the file's extension.</param>
/// <param name="Track">What is in it, which decides whether the file is complete on its own.</param>
/// <param name="EstimatedBytes">From duration and bitrate, or zero when neither is stated.</param>
public sealed record SegmentedStream(
    Uri Source,
    IReadOnlyList<StreamSegment> Segments,
    Uri? InitSegment,
    string Quality,
    string Container,
    TrackKind Track = TrackKind.Muxed,
    long EstimatedBytes = 0)
{
    public int SegmentCount => Segments.Count;

    /// <summary>
    /// The suffix that keeps a video track and its audio track from landing on
    /// the same name. Empty for a muxed stream, which needs no explaining.
    /// </summary>
    public string NameSuffix => Track switch
    {
        TrackKind.Video => "-视频",
        TrackKind.Audio => "-音频",
        _ => "",
    };
}
