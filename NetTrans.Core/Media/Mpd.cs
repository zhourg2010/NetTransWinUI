using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NetTrans.Media;

/// <summary>One Representation of a DASH manifest, with its segments already resolved.</summary>
/// <param name="Id">The manifest's own id, which $RepresentationID$ substitutes.</param>
/// <param name="Bandwidth">bits per second.</param>
/// <param name="Width">From the Representation or its AdaptationSet; 0 when neither said.</param>
/// <param name="Height">Likewise.</param>
/// <param name="MimeType">"video/mp4", "audio/mp4", ...</param>
/// <param name="Codecs">The codecs attribute, which is how a muxed track is told from a split one.</param>
/// <param name="InitSegment">The initialization segment, absent only for a single-file Representation.</param>
/// <param name="Segments">In play order.</param>
public sealed record DashRepresentation(
    string Id,
    long Bandwidth,
    int Width,
    int Height,
    string MimeType,
    string Codecs,
    Uri? InitSegment,
    IReadOnlyList<StreamSegment> Segments)
{
    /// <summary>
    /// What this Representation carries. DASH usually splits audio from video,
    /// so this is the difference between a file that plays and a silent one.
    /// </summary>
    public TrackKind Track
    {
        get
        {
            bool video = MimeType.StartsWith("video", StringComparison.OrdinalIgnoreCase);
            bool audio = MimeType.StartsWith("audio", StringComparison.OrdinalIgnoreCase);

            // A video Representation whose codecs list an audio codec too is
            // muxed: one file, both tracks, playable on its own.
            if (video && MentionsAudioCodec(Codecs)) return TrackKind.Muxed;
            if (video) return TrackKind.Video;
            if (audio) return TrackKind.Audio;

            // "video/mp2t" and bare "application/..." wrappers say nothing
            // useful; assume the common case rather than mislabel.
            return TrackKind.Muxed;
        }
    }

    public string Quality => Height > 0
        ? $"{Height}p"
        : Track == TrackKind.Audio
            ? $"音轨 {Math.Round(Bandwidth / 1000d):F0} kbps"
            : Bandwidth > 0
                ? $"{Math.Round(Bandwidth / 1000d):F0} kbps"
                : "视频";

    private static bool MentionsAudioCodec(string codecs) =>
        codecs.Split(',', StringSplitOptions.TrimEntries).Any(codec =>
            codec.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase) ||
            codec.StartsWith("ac-3", StringComparison.OrdinalIgnoreCase) ||
            codec.StartsWith("ec-3", StringComparison.OrdinalIgnoreCase) ||
            codec.StartsWith("opus", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// A DASH manifest reader covering the three ways a Representation addresses
/// its segments: a SegmentTemplate (with a duration or an explicit timeline), a
/// SegmentList, and a SegmentBase, which is one file addressed by byte range
/// and so is really just a plain download.
///
/// It is not a DASH player. There is no adaptation, no multi-Period stitching
/// and no live edge -- <see cref="DashManifestLoader"/> refuses those by name
/// rather than producing a file that is quietly wrong.
/// </summary>
public static partial class Mpd
{
    /// <summary>Whether the text is a DASH manifest at all.</summary>
    public static bool IsManifest(string text) =>
        text.Contains("<MPD", StringComparison.Ordinal) || text.Contains(":MPD", StringComparison.Ordinal);

    /// <summary>Whether the manifest describes a live stream rather than a finished one.</summary>
    public static bool IsLive(XDocument document) =>
        string.Equals(document.Root?.Attribute("type")?.Value, "dynamic", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Every Representation the manifest offers, best first, with segments
    /// resolved against the BaseURL chain.
    /// </summary>
    public static IReadOnlyList<DashRepresentation> Parse(XDocument document, Uri manifestUrl)
    {
        var root = document.Root;
        if (root is null) return Array.Empty<DashRepresentation>();

        var total = Duration(root.Attribute("mediaPresentationDuration")?.Value);
        var representations = new List<DashRepresentation>();

        var mpdBase = Resolve(manifestUrl, BaseUrl(root));

        foreach (var period in Elements(root, "Period"))
        {
            var periodBase = Resolve(mpdBase, BaseUrl(period));
            var periodDuration = Duration(period.Attribute("duration")?.Value) ?? total ?? TimeSpan.Zero;

            foreach (var adaptationSet in Elements(period, "AdaptationSet"))
            {
                var setBase = Resolve(periodBase, BaseUrl(adaptationSet));

                foreach (var representation in Elements(adaptationSet, "Representation"))
                {
                    var built = Build(representation, adaptationSet, setBase, periodDuration);
                    if (built is not null) representations.Add(built);
                }
            }
        }

        return representations
            .OrderByDescending(r => r.Track == TrackKind.Audio ? -1 : r.Height)
            .ThenByDescending(r => r.Bandwidth)
            .ToList();
    }

    private static DashRepresentation? Build(
        XElement representation,
        XElement adaptationSet,
        Uri baseUrl,
        TimeSpan periodDuration)
    {
        string id = representation.Attribute("id")?.Value ?? "";
        long bandwidth = Long(Inherited(representation, adaptationSet, "bandwidth"));

        var url = Resolve(baseUrl, BaseUrl(representation));

        // A template can sit on either the Representation or its AdaptationSet.
        var template = Element(representation, "SegmentTemplate") ?? Element(adaptationSet, "SegmentTemplate");
        var list = Element(representation, "SegmentList") ?? Element(adaptationSet, "SegmentList");
        var single = Element(representation, "SegmentBase") ?? Element(adaptationSet, "SegmentBase");

        (Uri? init, IReadOnlyList<StreamSegment> segments) =
            template is not null ? FromTemplate(template, url, id, bandwidth, periodDuration)
            : list is not null ? FromList(list, url)
            : single is not null ? FromBase(url)
            // No segment information at all: a BaseURL that is the whole file.
            : (null, new[] { new StreamSegment(url, 0) });

        if (segments.Count == 0) return null;

        return new DashRepresentation(
            id,
            bandwidth,
            Int(Inherited(representation, adaptationSet, "width")),
            Int(Inherited(representation, adaptationSet, "height")),
            Inherited(representation, adaptationSet, "mimeType") ?? "",
            Inherited(representation, adaptationSet, "codecs") ?? "",
            init,
            segments);
    }

    private static (Uri? Init, IReadOnlyList<StreamSegment> Segments) FromTemplate(
        XElement template,
        Uri baseUrl,
        string id,
        long bandwidth,
        TimeSpan periodDuration)
    {
        string? media = template.Attribute("media")?.Value;
        if (media is null) return (null, Array.Empty<StreamSegment>());

        Uri? init = template.Attribute("initialization")?.Value is { } initTemplate
            ? Resolve(baseUrl, Substitute(initTemplate, id, bandwidth, number: null, time: null))
            : null;

        long startNumber = Long(template.Attribute("startNumber")?.Value, fallback: 1);
        long timescale = Long(template.Attribute("timescale")?.Value, fallback: 1);

        var segments = new List<StreamSegment>();

        // An explicit timeline is exact; a duration has to be divided into the
        // period, which is why the timeline is preferred when both are present.
        if (Element(template, "SegmentTimeline") is { } timeline)
        {
            long number = startNumber;
            long time = 0;
            bool firstEntry = true;

            foreach (var entry in Elements(timeline, "S"))
            {
                if (entry.Attribute("t")?.Value is { } start) time = Long(start);
                else if (firstEntry) time = 0;

                firstEntry = false;

                long duration = Long(entry.Attribute("d")?.Value);
                if (duration <= 0) continue;

                // r is the number of *additional* repeats, and -1 means "until
                // the period ends" -- which only a live manifest uses, and which
                // is refused before we get here.
                long repeats = Long(entry.Attribute("r")?.Value);
                if (repeats < 0) repeats = 0;

                for (long i = 0; i <= repeats; i++)
                {
                    segments.Add(new StreamSegment(
                        Resolve(baseUrl, Substitute(media, id, bandwidth, number, time)),
                        number,
                        duration / (double)timescale));

                    number++;
                    time += duration;
                }
            }

            return (init, segments);
        }

        long segmentDuration = Long(template.Attribute("duration")?.Value);
        if (segmentDuration <= 0 || periodDuration <= TimeSpan.Zero) return (init, Array.Empty<StreamSegment>());

        double seconds = segmentDuration / (double)timescale;
        int count = (int)Math.Ceiling(periodDuration.TotalSeconds / seconds);

        for (int i = 0; i < count; i++)
        {
            long number = startNumber + i;

            segments.Add(new StreamSegment(
                Resolve(baseUrl, Substitute(media, id, bandwidth, number, segmentDuration * i)),
                number,
                seconds));
        }

        return (init, segments);
    }

    private static (Uri? Init, IReadOnlyList<StreamSegment> Segments) FromList(XElement list, Uri baseUrl)
    {
        Uri? init = null;

        if (Element(list, "Initialization") is { } initialization &&
            initialization.Attribute("sourceURL")?.Value is { } source)
        {
            init = Resolve(baseUrl, source);
        }

        var segments = new List<StreamSegment>();
        long number = 0;

        foreach (var entry in Elements(list, "SegmentURL"))
        {
            string? media = entry.Attribute("media")?.Value;
            var url = media is null ? baseUrl : Resolve(baseUrl, media);

            (long? offset, long? length) = Range(entry.Attribute("mediaRange")?.Value);
            segments.Add(new StreamSegment(url, number++, 0, offset, length));
        }

        return (init, segments);
    }

    /// <summary>
    /// A SegmentBase Representation is a single file addressed by byte range.
    /// There is nothing to concatenate, so it is one segment: the whole thing.
    /// </summary>
    private static (Uri? Init, IReadOnlyList<StreamSegment> Segments) FromBase(Uri baseUrl) =>
        (null, new[] { new StreamSegment(baseUrl, 0) });

    /// <summary>"$Number%05d$", "$Time$", "$RepresentationID$", "$Bandwidth$", and "$$" for a literal dollar.</summary>
    internal static string Substitute(string template, string id, long bandwidth, long? number, long? time) =>
        IdentifierPattern().Replace(template, match =>
        {
            string name = match.Groups["name"].Value;
            string format = match.Groups["format"].Value;

            if (name.Length == 0) return "$"; // "$$"

            string? value = name switch
            {
                "RepresentationID" => id,
                "Bandwidth" => bandwidth.ToString(CultureInfo.InvariantCulture),
                "Number" => number?.ToString(CultureInfo.InvariantCulture),
                "Time" => time?.ToString(CultureInfo.InvariantCulture),
                _ => null,
            };

            // An identifier we do not know, or one with no value in this
            // context, is left as written rather than blanked -- a wrong URL
            // that 404s is easier to diagnose than a silently truncated one.
            if (value is null) return match.Value;

            return format.Length > 0 && long.TryParse(value, out long numeric)
                ? numeric.ToString(format.Replace("%0", "D").Replace("d", ""), CultureInfo.InvariantCulture)
                : value;
        });

    /// <summary>"start-end", as DASH writes byte ranges, into an offset and a length.</summary>
    internal static (long? Offset, long? Length) Range(string? value)
    {
        if (value is null) return (null, null);

        var parts = value.Split('-');
        if (parts.Length != 2) return (null, null);
        if (!long.TryParse(parts[0], out long start) || !long.TryParse(parts[1], out long end)) return (null, null);
        if (end < start) return (null, null);

        return (start, end - start + 1);
    }

    /// <summary>ISO 8601 durations, which is how DASH states every length.</summary>
    internal static TimeSpan? Duration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var match = DurationPattern().Match(value);
        if (!match.Success) return null;

        double Part(string name) =>
            match.Groups[name].Success &&
            double.TryParse(match.Groups[name].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                ? parsed
                : 0;

        var total =
            TimeSpan.FromDays(Part("d")) +
            TimeSpan.FromHours(Part("h")) +
            TimeSpan.FromMinutes(Part("m")) +
            TimeSpan.FromSeconds(Part("s"));

        return total > TimeSpan.Zero ? total : null;
    }

    /// <summary>
    /// Elements by local name, ignoring the namespace. DASH manifests are
    /// namespaced and the prefix varies by packager; matching on the local name
    /// is what makes one from any of them readable.
    /// </summary>
    private static IEnumerable<XElement> Elements(XElement parent, string name) =>
        parent.Elements().Where(element => element.Name.LocalName == name);

    private static XElement? Element(XElement parent, string name) => Elements(parent, name).FirstOrDefault();

    /// <summary>An attribute on the Representation, or the AdaptationSet's when it has none.</summary>
    private static string? Inherited(XElement representation, XElement adaptationSet, string name) =>
        representation.Attribute(name)?.Value ?? adaptationSet.Attribute(name)?.Value;

    private static string? BaseUrl(XElement element) => Element(element, "BaseURL")?.Value.Trim();

    private static Uri Resolve(Uri baseUrl, string? relative) =>
        string.IsNullOrEmpty(relative) || !Uri.TryCreate(baseUrl, relative, out var absolute) ? baseUrl : absolute;

    private static long Long(string? value, long fallback = 0) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : fallback;

    private static int Int(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;

    [GeneratedRegex(@"\$(?<name>[A-Za-z]*)(?<format>%0\d+d)?\$")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"^-?P(?:(?<d>[\d.]+)D)?(?:T(?:(?<h>[\d.]+)H)?(?:(?<m>[\d.]+)M)?(?:(?<s>[\d.]+)S)?)?$")]
    private static partial Regex DurationPattern();
}
