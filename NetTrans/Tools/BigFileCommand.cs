using System.Diagnostics;
using System.Text;
using NetTrans.Download;
using NetTrans.Net;
using NetTrans.Services;
using NetTrans.Verify;

namespace NetTrans.Tools;

/// <summary>
/// 大文件核对, from the command line:
///
///     NetTrans.exe --bigfiles [路径…] [--min-size 1GB] [--rehash] [--offline] [--report x.md]
///
/// A command line rather than a sheet because of what the job is: hours of
/// disk, run overnight or from a scheduled task, over paths that are typed
/// once. It is the same code either way -- everything below the argument
/// parsing is <see cref="BigFileAudit"/>, which is tested on Linux like the
/// rest of Core.
/// </summary>
internal static class BigFileCommand
{
    public const string Flag = "--bigfiles";

    public static bool Wanted(IEnumerable<string> args) => args.Contains(Flag);

    public static int Execute(string[] args)
    {
        var options = Options.Parse(args);

        var store = new BigFileStore();
        var ledger = store.Ledger;

        Console.WriteLine($"大文件核对 · 门槛 {FormatHelpers.Bytes(options.MinimumSize)} · 账本 {store.Path}");
        Console.WriteLine($"扫描：{string.Join("、", options.Roots)}");
        Console.WriteLine();

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            // Ctrl-C after two hours of hashing must keep what it learned.
            e.Cancel = true;
            cancellation.Cancel();
            Console.WriteLine("正在停下，账本会保存已经算完的部分…");
        };

        var files = Find(options, cancellation.Token);
        if (files.Count == 0)
        {
            Console.WriteLine("没有找到超过门槛的文件。");
            return 3;
        }

        using var transport = options.Online ? new HttpTransport(userAgent: "NetTrans/1.0") : null;

        // 哈希库 is read for a published digest and never written to; the
        // ledger below is this tool's own file.
        var published = new HashDatabaseStore().Database;

        var outcomes = Audit(files, ledger, published, transport, options, cancellation.Token);

        BigFileAudit.Prune(ledger);
        store.Flush();

        string report = BigFileReport.Full(outcomes, options.Roots, options.MinimumSize);

        Console.WriteLine();
        Console.WriteLine(BigFileReport.Summary(outcomes));

        string reportPath = options.Report ?? store.ReportPath;
        try
        {
            File.WriteAllText(reportPath, report + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Console.WriteLine($"报告：{reportPath}");
        }
        catch (Exception failure)
        {
            Console.WriteLine($"报告写不出来（{failure.Message}），上面这行就是结论。");
        }

        return outcomes.Any(outcome => outcome.Verdict is VerifyOutcome.Mismatch or VerifyOutcome.Changed) ? 2 : 0;
    }

    private static List<BigFile> Find(Options options, CancellationToken cancellationToken)
    {
        var files = new List<BigFile>();
        int unreadable = 0;

        foreach (var file in BigFileScan.Walk(options.Roots, options.Scan, (_, _) => unreadable++, cancellationToken))
        {
            files.Add(file);
            Console.Write($"\r找到 {files.Count} 个…");
        }

        Console.WriteLine($"\r找到 {files.Count} 个，共 {FormatHelpers.Bytes(files.Sum(file => file.Size))}" +
                          (unreadable > 0 ? $"（{unreadable} 个目录读不了，已跳过）" : "") + "。");

        return files;
    }

    private static IReadOnlyList<BigFileOutcome> Audit(
        List<BigFile> files,
        BigFileLedger ledger,
        HashDatabase published,
        IHttpTransport? transport,
        Options options,
        CancellationToken cancellationToken)
    {
        var clock = SystemClock.Instance;
        var tick = Stopwatch.StartNew();

        var progress = new Progress<BigFileProgress>(state =>
        {
            if (state.Done == 0 || tick.ElapsedMilliseconds < 400) return;
            tick.Restart();

            double share = state.File.Size > 0 ? (double)state.Done / state.File.Size : 0;
            Console.Write($"\r  [{state.Index + 1}/{state.Total}] {Short(state.File.Name)} {share:P0}   ");
        });

        try
        {
            var outcomes = BigFileAudit.RunAsync(
                files, ledger, clock, published, transport, progress,
                new AuditOptions { Rehash = options.Rehash, Online = options.Online },
                cancellationToken).GetAwaiter().GetResult();

            foreach (var outcome in outcomes) Report(outcome);

            return outcomes;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("\r已停止。");
            return Array.Empty<BigFileOutcome>();
        }
    }

    private static void Report(BigFileOutcome outcome)
    {
        string hash = outcome.Sha256 is { Length: > 16 } full ? full[..16] + "…" : "—";
        string note = outcome.Skipped ? "没变、跳过" : BigFileReport.Describe(outcome.Verdict);

        Console.WriteLine($"\r{FormatHelpers.Bytes(outcome.File.Size),10}  {hash}  {note,-10}  {outcome.File.Path}");
    }

    private static string Short(string name) => name.Length <= 40 ? name : name[..37] + "…";

    /// <summary>Everything the command line can say. Parsed once, so the run itself is about files.</summary>
    private sealed record Options
    {
        public required IReadOnlyList<string> Roots { get; init; }
        public long MinimumSize { get; init; } = new ScanOptions().MinimumSize;
        public bool Rehash { get; init; }
        public bool Online { get; init; } = true;
        public string? Report { get; init; }

        public ScanOptions Scan => new() { MinimumSize = MinimumSize };

        public static Options Parse(string[] args)
        {
            var roots = new List<string>();
            long minimum = new ScanOptions().MinimumSize;
            bool rehash = false;
            bool online = true;
            string? report = null;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case Flag or "--demo" or "--no-wait":
                        break;

                    case "--min-size" when i + 1 < args.Length:
                        minimum = BigFileScan.ParseSize(args[++i]) ?? minimum;
                        break;

                    case "--report" when i + 1 < args.Length:
                        report = args[++i];
                        break;

                    case "--rehash":
                        rehash = true;
                        break;

                    case "--offline":
                        online = false;
                        break;

                    default:
                        // argv[0] is the executable, and an unknown flag is not
                        // a path; anything else is somewhere to look.
                        if (i > 0 && !args[i].StartsWith('-') && Directory.Exists(args[i])) roots.Add(args[i]);
                        break;
                }
            }

            return new Options
            {
                Roots = roots.Count > 0 ? roots : FixedDrives(),
                MinimumSize = minimum,
                Rehash = rehash,
                Online = online,
                Report = report,
            };
        }

        /// <summary>Every fixed disk. Removable and network drives are somebody else's inventory.</summary>
        private static IReadOnlyList<string> FixedDrives() =>
            DriveInfo.GetDrives()
                .Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady)
                .Select(drive => drive.RootDirectory.FullName)
                .ToList();
    }
}
