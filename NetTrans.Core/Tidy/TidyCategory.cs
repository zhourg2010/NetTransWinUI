namespace NetTrans.Tidy;

/// <summary>
/// The buckets 深度整理 sorts into. Deliberately few: a folder list a person
/// can hold in their head beats a taxonomy that is right and unusable.
/// </summary>
public enum TidyCategory
{
    /// <summary>Nothing matched. Goes to 其他 unless something else decides.</summary>
    Unknown = 0,

    Installer,
    Document,
    Image,
    Screenshot,
    Video,
    Audio,
    Archive,
    Code,
    Torrent,
    Shortcut,
    Disk,

    /// <summary>Leftovers of other programs. Moved aside, never deleted.</summary>
    Junk,

    /// <summary>Byte-for-byte the same as another file in the same run.</summary>
    Duplicate,
}

public static class TidyCategories
{
    /// <summary>
    /// The folder each bucket becomes. Chinese, because these are folders a
    /// person browses, not identifiers a program parses -- and a screenshot
    /// lands inside 图片 rather than beside it, which is where anyone looking
    /// for one would open first.
    /// </summary>
    private static readonly Dictionary<TidyCategory, string> Folders = new()
    {
        [TidyCategory.Installer] = "安装包",
        [TidyCategory.Document] = "文档",
        [TidyCategory.Image] = "图片",
        [TidyCategory.Screenshot] = System.IO.Path.Combine("图片", "截图"),
        [TidyCategory.Video] = "视频",
        [TidyCategory.Audio] = "音乐",
        [TidyCategory.Archive] = "压缩包",
        [TidyCategory.Code] = "代码",
        [TidyCategory.Torrent] = "种子",
        [TidyCategory.Shortcut] = "快捷方式",
        [TidyCategory.Disk] = "磁盘镜像",
        [TidyCategory.Junk] = "待清理",
        [TidyCategory.Duplicate] = "重复文件",
        [TidyCategory.Unknown] = "其他",
    };

    /// <summary>Where an archived-by-age file goes, under the year it was last touched.</summary>
    public const string Archive = "存档";

    public static string Folder(TidyCategory category) => Folders.GetValueOrDefault(category, "其他");

    /// <summary>Every folder 整理 may create. Used to recognise a directory the tool made, and never to descend into it.</summary>
    public static IReadOnlySet<string> AllFolders { get; } = new HashSet<string>(
        Folders.Values
            .Select(folder => folder.Split(System.IO.Path.DirectorySeparatorChar)[0])
            .Append(Archive)
            .Append("截图"),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>The ids an AI classifier is allowed to answer with. Anything else is discarded.</summary>
    public static IReadOnlyList<string> Names { get; } = new[]
    {
        "installer", "document", "image", "screenshot", "video",
        "audio", "archive", "code", "torrent", "shortcut", "disk", "junk", "unknown",
    };

    public static TidyCategory Parse(string? name) =>
        Enum.TryParse<TidyCategory>(name?.Trim(), ignoreCase: true, out var category) && Names.Contains(category.ToString().ToLowerInvariant())
            ? category
            : TidyCategory.Unknown;
}
