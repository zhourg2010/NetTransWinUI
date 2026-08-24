using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetTrans.Models;

namespace NetTrans.Tests;

/// <summary>
/// The values generated from the design handoff's own source by
/// tools/golden/generate-golden.mjs. Everything the tests assert against comes
/// from here rather than from numbers typed into the tests by hand.
/// </summary>
public static class Golden
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static GoldenData Data { get; } = Load();

    private static GoldenData Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        string name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("golden.json", StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("golden.json is not embedded in the test assembly");

        return JsonSerializer.Deserialize<GoldenData>(stream, Options)
            ?? throw new InvalidOperationException("golden.json could not be read");
    }

    /// <summary>Rebuilds the handoff's SEED array as the models the app would hold.</summary>
    public static List<DownloadItem> Seed() => Data.Seed.Select(ToItem).ToList();

    public static DownloadItem ToItem(GoldenTask task)
    {
        var item = new DownloadItem
        {
            Id = task.Id,
            Name = task.Name,
            Host = task.Host,
            Kind = task.Kind,
            Size = task.SizeBytes,
            Category = task.Category,
            Tint = task.Tint,
            Done = task.DoneBytes,
            Speed = task.SpeedBytesPerSecond,
            Status = task.Status,
            Connections = task.Connections,
            Checksum = task.Checksum,
            ErrorMessage = task.Error,
            Retries = task.Retries,
            Priority = task.Priority,
            Peers = task.Peers,
            Seeds = task.Seeds,
            Ratio = task.Ratio,
            UploadSpeed = task.UploadBytesPerSecond,
        };

        if (task.NewerVersion is { } newer)
        {
            item.NewerVersion = new NewVersionInfo(newer.Version, newer.SizeBytes, newer.Published);
        }

        return item;
    }

    /// <summary>Rebuilds one row's model from the row fixture, which carries only what the row renders.</summary>
    public static DownloadItem ToItem(GoldenRow row) => new()
    {
        Id = row.Id,
        Name = row.Name,
        Host = "example.test",
        Kind = FileKind.Doc,
        Size = row.SizeBytes,
        Category = "doc",
        Done = row.DoneBytes,
        Speed = row.SpeedBytesPerSecond,
        Status = ToStatus(row.State),
        Checksum = row.Checksum,
        ErrorMessage = row.Err,
        Retries = row.Retries,
    };

    private static DownloadStatus ToStatus(string state) => state switch
    {
        "run" => DownloadStatus.Downloading,
        "paused" => DownloadStatus.Paused,
        "done" => DownloadStatus.Completed,
        "error" => DownloadStatus.Error,
        _ => DownloadStatus.Queued,
    };
}

public sealed record GoldenData(
    IReadOnlyList<GoldenBytes> Bytes,
    IReadOnlyList<GoldenSpeed> Speeds,
    IReadOnlyList<GoldenEta> Etas,
    IReadOnlyList<GoldenEtaFromTask> EtaFromTask,
    IReadOnlyList<GoldenStateName> StateNames,
    IReadOnlyList<GoldenRow> Rows,
    IReadOnlyList<GoldenRingCaption> RingCaptions,
    IReadOnlyList<GoldenSorting> Sorting,
    IReadOnlyList<GoldenFiltering> Filtering,
    IReadOnlyList<GoldenSearching> Searching,
    IReadOnlyList<GoldenFolding> Folding,
    IReadOnlyList<GoldenDockPosition> DockPositions,
    IReadOnlyList<GoldenNearest> Nearest,
    IReadOnlyList<GoldenSpeedCurve> SpeedCurve,
    IReadOnlyList<GoldenTask> Seed,
    GoldenMidpoints Midpoints,
    IReadOnlyList<GoldenEasing> Easing);

public sealed record GoldenBytes(double Mb, long Bytes, string Expected);

public sealed record GoldenSpeed(double Kbs, long BytesPerSecond, string Expected);

public sealed record GoldenEta(double Seconds, string Expected);

public sealed record GoldenEtaFromTask(int Id, long RemainingBytes, long BytesPerSecond, string Expected);

public sealed record GoldenStateName(string State, string Expected);

public sealed record GoldenRow(
    int Id, string Name, string State, long SizeBytes, long DoneBytes, long SpeedBytesPerSecond,
    string? Checksum, string? Err, int Retries, string SubText, string TrailingText, double Percent);

public sealed record GoldenRingCaption(int Id, string Expected);

public sealed record GoldenSorting(string SortKey, string SortDirection, IReadOnlyList<int> Ids);

public sealed record GoldenFiltering(string Tab, string Category, int ActiveCount, int DoneCount, IReadOnlyList<int> Ids);

public sealed record GoldenSearching(string Query, IReadOnlyList<int> Ids);

public sealed record GoldenFolding(int Total, bool Expanded, int Shown, int Hidden, bool CanFold);

public sealed record GoldenDockPosition(DockSide Side, int X, int Y);

public sealed record GoldenNearest(
    int OffsetX, int OffsetY, DockSide? FromRight, DockSide? FromLeft, DockSide? FromBottom, DockSide? FromTop);

public sealed record GoldenSpeedCurve(double SpeedBytesPerSecond, double Random, double Expected);

public sealed record GoldenMidpoints(
    IReadOnlyList<GoldenBytes> Bytes,
    IReadOnlyList<GoldenSpeed> Speeds,
    IReadOnlyList<GoldenPercent> Percents);

public sealed record GoldenPercent(double Percent, string Expected);

public sealed record GoldenEasing(double T, double Expected);

public sealed record GoldenTask(
    int Id, string Name, string Host, FileKind Kind, string Category, string Tint,
    long SizeBytes, long DoneBytes, long SpeedBytesPerSecond, DownloadStatus Status, int Connections,
    string? Checksum, string? Error, int Retries, TaskPriority Priority,
    int? Peers, int? Seeds, double? Ratio, long UploadBytesPerSecond, GoldenNewVersion? NewerVersion);

public sealed record GoldenNewVersion(string Version, long SizeBytes, string Published);
