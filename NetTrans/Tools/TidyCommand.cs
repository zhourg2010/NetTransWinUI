using NetTrans.Services;
using NetTrans.Tidy;

namespace NetTrans.Tools;

/// <summary>
/// 深度整理, from the command line:
///
///     NetTrans.exe --tidy [路径…] [--apply] [--ask] [--plan 草稿.json]
///                         [--by 分类|月份|两级] [--depth 2] [--stale 365]
///                         [--dupes] [--ai] [--undo]
///
/// Three ways to use it, all the same code underneath:
///
///   --tidy 目录                  预演：算一遍，打印出来，什么也不动
///   --tidy 目录 --ask            一张卡一张卡地问：先项目，再类型
///   --tidy 目录 --plan p.json    产出草稿，人手改，再 --plan p.json --apply
///
/// 预演 by default. Nothing moves until --apply, and everything that moves is
/// written to a journal that --undo replays backwards. Nothing is ever deleted:
/// what looks like rubbish goes to 待清理 and waits for a human.
/// </summary>
internal static class TidyCommand
{
    public const string Flag = "--tidy";

    public static bool Wanted(IEnumerable<string> args) => args.Contains(Flag);

    public static int Run(string[] args)
    {
        var options = Options.Parse(args);
        var store = new TidyStore();

        if (options.Undo) return Undo(store);

        // A draft that has already been reviewed: run exactly what it says and
        // do not scan again -- the folder somebody read is the folder that gets
        // tidied.
        if (options.Plan is { } saved && options.Apply && File.Exists(saved))
        {
            return ApplySaved(saved, store);
        }

        Console.WriteLine($"深度整理{(options.Apply ? "" : "（预演）")} · 日志 {store.Path}");
        Console.WriteLine();

        var actions = new List<TidyAction>();
        var moves = new List<TidyMove>();

        foreach (var root in options.Roots)
        {
            if (TidyScan.Refuse(root) is { } why)
            {
                Console.WriteLine($"跳过 {root}：{why}");
                continue;
            }

            var draft = Draft(root, options);

            if (options.Ask) Ask(draft);
            else draft.AcceptRest();

            if (options.Plan is { } path)
            {
                TidyDraftFile.Save(draft, path);
                Console.WriteLine($"草稿：{path}（改完用 --tidy --plan {path} --apply 执行）");
                Console.WriteLine();
            }

            var planned = TidyPlan.Build(draft, DateTimeOffset.Now, File.Exists);
            actions.AddRange(planned);

            Console.WriteLine($"— {root}");
            Console.WriteLine(TidyReport.Table(planned, root));
            Console.WriteLine();

            if (!options.Apply) continue;

            Move(planned, moves, store, root);
        }

        Console.WriteLine(TidyReport.Summary(actions, options.Apply));

        if (!options.Apply) Console.WriteLine("加 --apply 才会真的移动；移动后 --tidy --undo 可以整批还原。");

        Write(options.Report ?? store.ReportPath, actions, options);

        return actions.Any(action => action.Moves) ? 0 : 3;
    }

    private static TidyDraft Draft(string root, Options options)
    {
        var items = TidyScan.Collect(root, options.Tidy, (directory, failure) =>
            Console.WriteLine($"读不了 {directory}：{failure.Message}"));

        var draft = TidyDraft.From(root, options.Tidy, items, ProjectFinder.Find(root, items, options.Bursts));

        if (options.Duplicates)
        {
            Console.WriteLine("正在按大小分组、对可能重复的文件算哈希…");
            draft.Duplicates = TidyRunner.FindDuplicatesAsync(items).GetAwaiter().GetResult();
        }

        if (options.Ai) draft.Guesses = Guess(items) ?? draft.Guesses;

        draft.Rebuild();
        return draft;
    }

    /// <summary>
    /// The same queue the wizard shows, one card at a time on a terminal.
    /// Enter accepts what is on screen, which is the whole point of proposing
    /// it.
    /// </summary>
    private static void Ask(TidyDraft draft)
    {
        while (true)
        {
            var card = TidyQueue.Current(draft);
            var (at, total) = TidyQueue.Progress(draft);

            if (card.Kind == TidyCardKind.Summary) return;

            Console.WriteLine();
            Console.WriteLine($"[{at}/{total}] {(card.Kind == TidyCardKind.Project ? "项目" : "类型")}：{card.Title}");
            Console.WriteLine($"        {card.Detail}");

            foreach (var line in Preview(draft, card)) Console.WriteLine($"        · {line}");

            Console.Write("回车=接受  n=跳过  r 新名字  d 新目录  a=剩下全按默认  u=撤销 > ");

            var answer = (Console.ReadLine() ?? "").Trim();

            if (answer.Equals("a", StringComparison.OrdinalIgnoreCase))
            {
                draft.AcceptRest();
                return;
            }

            if (answer.Equals("u", StringComparison.OrdinalIgnoreCase))
            {
                draft.Undo();
                continue;
            }

            Answer(draft, card, answer);
        }
    }

    private static void Answer(TidyDraft draft, TidyCard card, string answer)
    {
        bool skip = answer.Equals("n", StringComparison.OrdinalIgnoreCase);
        string? renamed = answer.StartsWith("r ", StringComparison.OrdinalIgnoreCase) ? answer[2..].Trim() : null;
        string? moved = answer.StartsWith("d ", StringComparison.OrdinalIgnoreCase) ? answer[2..].Trim() : null;

        switch (card.Kind)
        {
            case TidyCardKind.Project when card.ProjectId is { } id:
                if (renamed is { Length: > 0 }) draft.RenameProject(id, renamed);
                if (moved is { Length: > 0 }) draft.SetProjectDestination(id, moved);

                if (skip) draft.SkipProject(id);
                else if (renamed is null && moved is null) draft.AcceptProject(id);
                break;

            default:
                foreach (var category in card.Categories ?? Array.Empty<TidyCategory>())
                {
                    if (moved is { Length: > 0 }) draft.SetBucketFolder(category, moved);

                    if (skip) draft.SkipBucket(category);
                    else if (moved is null) draft.AcceptBucket(category);
                }

                break;
        }
    }

    /// <summary>A few names off the card, so the answer is about something visible.</summary>
    private static IEnumerable<string> Preview(TidyDraft draft, TidyCard card)
    {
        var names = card.Kind == TidyCardKind.Project && card.ProjectId is { } id
            ? draft.Projects.First(project => project.Id == id).Members.Select(Path.GetFileName)
            : (card.Categories ?? Array.Empty<TidyCategory>()).SelectMany(draft.InBucket).Select(item => item.Name);

        var listed = names.Take(6).ToList();
        foreach (var name in listed) yield return name!;

        if (card.Count > listed.Count) yield return $"…另外 {card.Count - listed.Count} 个";
    }

    private static int ApplySaved(string path, TidyStore store)
    {
        if (TidyDraftFile.Load(path) is not { } draft)
        {
            Console.WriteLine($"读不了草稿 {path}");
            return 1;
        }

        var planned = TidyPlan.Build(draft, DateTimeOffset.Now, File.Exists);

        Console.WriteLine($"按草稿执行：{path}");
        Console.WriteLine(TidyReport.Table(planned, draft.Root));
        Console.WriteLine();

        var moves = new List<TidyMove>();
        Move(planned, moves, store, draft.Root);

        Console.WriteLine(TidyReport.Summary(planned, applied: true));
        return planned.Any(action => action.Moves) ? 0 : 3;
    }

    private static void Move(IReadOnlyList<TidyAction> planned, List<TidyMove> moves, TidyStore store, string root)
    {
        var result = TidyRunner.Apply(planned);
        moves.AddRange(result.Moved);

        foreach (var failure in result.Failed) Console.WriteLine($"没能移动：{failure}");

        if (result.Moved.Count == 0) return;

        store.Journal.Add(new TidyBatch
        {
            Id = DateTime.Now.ToString("yyyyMMdd-HHmmss"),
            When = DateTimeOffset.Now,
            Roots = new List<string> { root },
            Moves = result.Moved.ToList(),
        });

        store.Flush();
    }

    /// <summary>
    /// Puts the names the rules could not place to a model -- names only, never
    /// a byte of any file -- and says out loud where they are going.
    /// </summary>
    private static IReadOnlyDictionary<string, TidyCategory>? Guess(IReadOnlyList<TidyItem> items)
    {
        var unknown = items
            .Where(item => TidyRules.Classify(item.Name).Category == TidyCategory.Unknown)
            .Select(item => item.Name)
            .Distinct()
            .ToList();

        if (unknown.Count == 0) return null;

        var settings = AiOptions.FromEnvironment();

        Console.WriteLine($"把 {unknown.Count} 个认不出的文件名（只有名字，没有内容）发给 {settings.BaseUrl} 的 {settings.Model}…");

        using var classifier = new AiNameClassifier(settings);
        var answers = classifier.ClassifyAsync(unknown).GetAwaiter().GetResult();

        Console.WriteLine(answers.Count > 0
            ? $"AI 认出了 {answers.Count} 个。"
            : $"AI 没帮上忙（{classifier.LastError ?? "没有可用的答案"}），这些文件按原样归到「其他」。");

        return answers;
    }

    private static int Undo(TidyStore store)
    {
        if (store.Journal.Last is not { } batch)
        {
            Console.WriteLine("日志里没有可以还原的整理。");
            return 3;
        }

        Console.WriteLine($"还原 {batch.When:yyyy-MM-dd HH:mm} 的那次整理，{batch.Moves.Count} 个文件…");

        var (restored, refused) = TidyJournal.Undo(batch);

        foreach (var line in refused) Console.WriteLine($"没还原：{line}");

        store.Journal.Remove(batch);
        store.Flush();

        Console.WriteLine($"还原了 {restored} 个。");
        return refused.Count > 0 ? 2 : 0;
    }

    private static void Write(string path, IReadOnlyList<TidyAction> actions, Options options)
    {
        try
        {
            string report = string.Join("\n\n", options.Roots
                .Select(root => TidyReport.Full(actions.Where(action => action.Item.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase)).ToList(), root, options.Apply)));

            File.WriteAllText(path, report + "\n", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Console.WriteLine($"报告：{path}");
        }
        catch (Exception failure)
        {
            Console.WriteLine($"报告写不出来（{failure.Message}）。");
        }
    }

    private sealed record Options
    {
        public required IReadOnlyList<string> Roots { get; init; }
        public TidyOptions Tidy { get; init; } = new();
        public bool Apply { get; init; }
        public bool Undo { get; init; }
        public bool Duplicates { get; init; }
        public bool Ai { get; init; }
        public bool Ask { get; init; }
        public bool Bursts { get; init; }
        public string? Plan { get; init; }
        public string? Report { get; init; }

        public static Options Parse(string[] args)
        {
            var roots = new List<string>();
            var tidy = new TidyOptions();
            bool apply = false, undo = false, dupes = false, ai = false, ask = false, bursts = false;
            string? plan = null;
            string? report = null;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case Flag or "--demo" or "--no-wait":
                        break;

                    case "--apply":
                        apply = true;
                        break;

                    case "--undo":
                        undo = true;
                        break;

                    case "--dupes":
                        dupes = true;
                        break;

                    case "--ai":
                        ai = true;
                        break;

                    case "--ask":
                        ask = true;
                        break;

                    case "--bursts":
                        bursts = true;
                        break;

                    case "--plan" when i + 1 < args.Length:
                        plan = args[++i];
                        break;

                    case "--report" when i + 1 < args.Length:
                        report = args[++i];
                        break;

                    case "--by" when i + 1 < args.Length:
                        tidy = tidy with
                        {
                            Grouping = args[++i] switch
                            {
                                "month" or "月份" => TidyGrouping.Month,
                                "both" or "两级" => TidyGrouping.CategoryThenMonth,
                                _ => TidyGrouping.Category,
                            },
                        };
                        break;

                    case "--depth" when i + 1 < args.Length:
                        tidy = tidy with { Depth = int.TryParse(args[++i], out int depth) ? Math.Clamp(depth, 1, 8) : 1 };
                        break;

                    case "--stale" when i + 1 < args.Length:
                        tidy = tidy with { StaleDays = int.TryParse(args[++i], out int days) ? Math.Max(0, days) : 0 };
                        break;

                    default:
                        if (i > 0 && !args[i].StartsWith('-') && Directory.Exists(args[i])) roots.Add(args[i]);
                        break;
                }
            }

            return new Options
            {
                Roots = roots.Count > 0 ? roots : Usual(),
                Tidy = tidy,
                Apply = apply,
                Undo = undo,
                Duplicates = dupes,
                Ai = ai,
                Ask = ask,
                Bursts = bursts,
                Plan = plan,
                Report = report,
            };
        }

        /// <summary>桌面 and 下载, which is what anyone means by "整理一下".</summary>
        private static IReadOnlyList<string> Usual()
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var downloads = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

            return new[] { desktop, downloads }.Where(Directory.Exists).ToList();
        }
    }
}
