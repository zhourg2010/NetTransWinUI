namespace NetTrans.Verify;

/// <summary>One oversized file found on disk.</summary>
public sealed record BigFile(string Path, string Name, long Size, DateTimeOffset Modified);

/// <summary>What counts as a big file worth hashing, and what is skipped.</summary>
public sealed record ScanOptions
{
    /// <summary>1 GB. Below this a file is not what this tool is for -- and hashing everything would take all day.</summary>
    public long MinimumSize { get; init; } = 1L * 1024 * 1024 * 1024;

    /// <summary>Follow directory symlinks and junctions. Off, because a junction back up the tree is an infinite scan.</summary>
    public bool FollowLinks { get; init; }

    public IReadOnlySet<string> SkipNames { get; init; } = BigFileScan.SystemFiles;

    public IReadOnlySet<string> SkipExtensions { get; init; } = BigFileScan.SkippedExtensions;

    public IReadOnlySet<string> SkipDirectories { get; init; } = BigFileScan.SystemDirectories;
}

/// <summary>
/// 超大文件扫描: every single file over a gigabyte, minus the ones that are
/// nothing but the machine's own scratch space.
///
/// Skipping those is not tidiness. A page file is a gigabyte of RAM that
/// changes while you read it, so its hash is meaningless before it is written
/// down; a crash dump and a hibernation image are the same story. Hashing them
/// would spend an hour of disk to produce a number that is wrong by the time
/// it lands.
/// </summary>
public static class BigFileScan
{
    /// <summary>Windows' own working files. Live, machine-specific, and never the same twice.</summary>
    public static IReadOnlySet<string> SystemFiles { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "pagefile.sys",
        "swapfile.sys",
        "hiberfil.sys",
        "memory.dmp",
        "dumpstack.log",
        "dumpstack.log.tmp",
        "ntuser.dat",
        "usrclass.dat",
    };

    /// <summary>Dumps, half-finished downloads and scratch files: either machine state or not a whole file yet.</summary>
    public static IReadOnlySet<string> SkippedExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".dmp", ".hdmp", ".mdmp", ".kdmp",
        ".tmp", ".temp", ".swp", ".swap",
        ".part", ".partial", ".crdownload", ".!ut", ".downloading",
        ".nettrans", ".nettrans-hls", ".nettrans-bt",
    };

    /// <summary>
    /// Directory names skipped wherever they appear in the path. Windows and
    /// its component store are the operating system, not the user's files; the
    /// rest is scratch, recycled, or Windows Error Reporting.
    /// </summary>
    public static IReadOnlySet<string> SystemDirectories { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Windows",
        "WinSxS",
        "System Volume Information",
        "$Recycle.Bin",
        "$RECYCLE.BIN",
        "Recovery",
        "Temp",
        "Tmp",
        "Crashpad",
        "WER",
        "ReportQueue",
    };

    /// <summary>
    /// Walks the roots depth-first, yielding files as they are found.
    ///
    /// Streaming rather than a list: a scan of a full disk takes a while, and
    /// the caller can start hashing the first file instead of watching a
    /// progress bar that says nothing.
    /// </summary>
    /// <param name="onError">Called with a directory that could not be read; the walk carries on.</param>
    public static IEnumerable<BigFile> Walk(
        IEnumerable<string> roots,
        ScanOptions? options = null,
        Action<string, Exception>? onError = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ScanOptions();

        var pending = new Stack<string>(roots);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var directory = pending.Pop();

            // The same directory can be reached twice through a junction even
            // when the walk does not follow them: two roots may overlap.
            if (!seen.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)))) continue;

            string[] children;
            FileInfo[] files;

            try
            {
                var info = new DirectoryInfo(directory);
                children = info.EnumerateDirectories()
                    .Where(child => Keep(child, options))
                    .Select(child => child.FullName)
                    .ToArray();

                files = info.EnumerateFiles().ToArray();
            }
            catch (Exception failure) when (failure is UnauthorizedAccessException or IOException or ArgumentException)
            {
                // A scan of C:\ meets directories no user may read. That is
                // normal; reporting them and carrying on is the whole job.
                onError?.Invoke(directory, failure);
                continue;
            }

            foreach (var child in children) pending.Push(child);

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!Keep(file, options)) continue;

                yield return new BigFile(file.FullName, file.Name, file.Length, file.LastWriteTimeUtc);
            }
        }
    }

    /// <summary>
    /// Reads a size off the command line: "1GB", "512m", "2.5 GB", or a plain
    /// number of bytes. Binary multiples, because that is what a file manager
    /// shows and what anyone typing "1GB" at a disk means.
    /// </summary>
    public static long? ParseSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var cleaned = text.Trim().Replace(" ", "").ToUpperInvariant();
        if (cleaned.EndsWith("B") && cleaned.Length > 1 && !char.IsDigit(cleaned[^2])) cleaned = cleaned[..^1];

        long multiplier = cleaned.Length > 0 ? cleaned[^1] switch
        {
            'K' => 1024L,
            'M' => 1024L * 1024,
            'G' => 1024L * 1024 * 1024,
            'T' => 1024L * 1024 * 1024 * 1024,
            _ => 1,
        } : 1;

        if (multiplier > 1) cleaned = cleaned[..^1];
        if (cleaned.EndsWith("B")) cleaned = cleaned[..^1];

        if (!double.TryParse(cleaned, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value)) return null;
        if (value < 0) return null;

        double bytes = value * multiplier;
        return bytes > long.MaxValue ? null : (long)bytes;
    }

    /// <summary>Whether one file is in scope, given the size floor and the skip lists.</summary>
    public static bool Keep(FileInfo file, ScanOptions options)
    {
        if (!options.FollowLinks && file.LinkTarget is not null) return false;
        if (file.Length < options.MinimumSize) return false;
        if (options.SkipNames.Contains(file.Name)) return false;
        if (file.Extension.Length > 0 && options.SkipExtensions.Contains(file.Extension)) return false;

        return true;
    }

    private static bool Keep(DirectoryInfo directory, ScanOptions options)
    {
        if (!options.FollowLinks && directory.LinkTarget is not null) return false;

        return !options.SkipDirectories.Contains(directory.Name);
    }
}
