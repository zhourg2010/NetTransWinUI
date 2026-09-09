namespace NetTrans.Tidy;

/// <summary>Why a file was put where it was put. Shown in the plan, so nothing moves for a reason nobody can read.</summary>
public sealed record TidyVerdict(TidyCategory Category, string Reason);

/// <summary>
/// What a file is, judged from its name alone.
///
/// The extension decides almost everything, and where it does not, the name
/// usually does -- "屏幕截图 2026-01-02.png" is not the same kind of thing as a
/// photograph, even though both are PNGs. Only what is left after all of that
/// is worth asking an AI about.
/// </summary>
public static class TidyRules
{
    private static readonly Dictionary<string, TidyCategory> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".exe"] = TidyCategory.Installer,
        [".msi"] = TidyCategory.Installer,
        [".msix"] = TidyCategory.Installer,
        [".appx"] = TidyCategory.Installer,
        [".apk"] = TidyCategory.Installer,
        [".dmg"] = TidyCategory.Installer,
        [".deb"] = TidyCategory.Installer,
        [".rpm"] = TidyCategory.Installer,

        [".pdf"] = TidyCategory.Document,
        [".doc"] = TidyCategory.Document,
        [".docx"] = TidyCategory.Document,
        [".xls"] = TidyCategory.Document,
        [".xlsx"] = TidyCategory.Document,
        [".csv"] = TidyCategory.Document,
        [".ppt"] = TidyCategory.Document,
        [".pptx"] = TidyCategory.Document,
        [".txt"] = TidyCategory.Document,
        [".md"] = TidyCategory.Document,
        [".rtf"] = TidyCategory.Document,
        [".epub"] = TidyCategory.Document,
        [".mobi"] = TidyCategory.Document,
        [".azw3"] = TidyCategory.Document,

        [".jpg"] = TidyCategory.Image,
        [".jpeg"] = TidyCategory.Image,
        [".png"] = TidyCategory.Image,
        [".gif"] = TidyCategory.Image,
        [".bmp"] = TidyCategory.Image,
        [".webp"] = TidyCategory.Image,
        [".heic"] = TidyCategory.Image,
        [".svg"] = TidyCategory.Image,
        [".psd"] = TidyCategory.Image,

        [".mp4"] = TidyCategory.Video,
        [".mkv"] = TidyCategory.Video,
        [".avi"] = TidyCategory.Video,
        [".mov"] = TidyCategory.Video,
        [".wmv"] = TidyCategory.Video,
        [".flv"] = TidyCategory.Video,
        [".webm"] = TidyCategory.Video,
        [".ts"] = TidyCategory.Video,
        [".m4v"] = TidyCategory.Video,

        [".mp3"] = TidyCategory.Audio,
        [".flac"] = TidyCategory.Audio,
        [".wav"] = TidyCategory.Audio,
        [".aac"] = TidyCategory.Audio,
        [".m4a"] = TidyCategory.Audio,
        [".ogg"] = TidyCategory.Audio,
        [".ape"] = TidyCategory.Audio,

        [".zip"] = TidyCategory.Archive,
        [".rar"] = TidyCategory.Archive,
        [".7z"] = TidyCategory.Archive,
        [".gz"] = TidyCategory.Archive,
        [".bz2"] = TidyCategory.Archive,
        [".xz"] = TidyCategory.Archive,
        [".tar"] = TidyCategory.Archive,
        [".zst"] = TidyCategory.Archive,

        [".c"] = TidyCategory.Code,
        [".h"] = TidyCategory.Code,
        [".cs"] = TidyCategory.Code,
        [".cpp"] = TidyCategory.Code,
        [".py"] = TidyCategory.Code,
        [".js"] = TidyCategory.Code,
        [".json"] = TidyCategory.Code,
        [".xml"] = TidyCategory.Code,
        [".yml"] = TidyCategory.Code,
        [".yaml"] = TidyCategory.Code,
        [".sh"] = TidyCategory.Code,
        [".ps1"] = TidyCategory.Code,
        [".sql"] = TidyCategory.Code,
        [".patch"] = TidyCategory.Code,
        [".diff"] = TidyCategory.Code,

        [".torrent"] = TidyCategory.Torrent,

        [".lnk"] = TidyCategory.Shortcut,
        [".url"] = TidyCategory.Shortcut,

        [".iso"] = TidyCategory.Disk,
        [".img"] = TidyCategory.Disk,
        [".vhd"] = TidyCategory.Disk,
        [".vhdx"] = TidyCategory.Disk,
        [".wim"] = TidyCategory.Disk,

        [".tmp"] = TidyCategory.Junk,
        [".temp"] = TidyCategory.Junk,
        [".bak"] = TidyCategory.Junk,
        [".old"] = TidyCategory.Junk,
    };

    /// <summary>Files other programs leave behind. Nobody put them on the desktop on purpose.</summary>
    private static readonly HashSet<string> JunkNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "desktop.ini", "thumbs.db", ".ds_store", "ehthumbs.db",
    };

    /// <summary>What every screenshot tool on Windows names its output, in both languages.</summary>
    private static readonly string[] ScreenshotHints =
    {
        "屏幕截图", "屏幕快照", "截图", "screenshot", "screen shot", "捕获", "snipaste",
        "qq图片", "微信图片", "image_", "photo_",
    };

    public static TidyVerdict Classify(string fileName)
    {
        string name = System.IO.Path.GetFileName(fileName);

        if (JunkNames.Contains(name)) return new TidyVerdict(TidyCategory.Junk, "别的程序留下的");

        string extension = System.IO.Path.GetExtension(name);

        if (!ByExtension.TryGetValue(extension, out var category))
        {
            return new TidyVerdict(TidyCategory.Unknown, extension.Length > 0 ? $"不认识 {extension}" : "没有扩展名");
        }

        if (category == TidyCategory.Image && LooksLikeScreenshot(name))
        {
            return new TidyVerdict(TidyCategory.Screenshot, "名字像截图");
        }

        return new TidyVerdict(category, $"按扩展名 {extension}");
    }

    /// <summary>
    /// A transfer somebody else is in the middle of. Not a file to file away --
    /// the plan leaves these exactly where the downloader put them.
    /// </summary>
    public static bool IsUnfinishedDownload(string name) =>
        System.IO.Path.GetExtension(name).ToLowerInvariant()
            is ".part" or ".crdownload" or ".partial" or ".downloading" or ".!ut" or ".nettrans";

    public static bool LooksLikeScreenshot(string name)
    {
        var lowered = name.ToLowerInvariant();
        return ScreenshotHints.Any(hint => lowered.Contains(hint, StringComparison.Ordinal));
    }
}
