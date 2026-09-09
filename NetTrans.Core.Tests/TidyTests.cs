using System.Net;
using System.Text;
using NetTrans.Tidy;
using Xunit;

namespace NetTrans.Tests;

/// <summary>深度整理: what goes where, what is left alone, and putting it all back.</summary>
public class TidyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nettrans-tidy-" + Guid.NewGuid().ToString("N"));

    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    public TidyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Not worth failing a test over.
        }
    }

    private string Write(string relative, string content = "hello")
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);

        // Older than the settle window, so the plan does not hold it back as
        // "probably still being written".
        File.SetLastWriteTimeUtc(path, Now.UtcDateTime.AddDays(-1));
        return path;
    }

    private IReadOnlyList<TidyAction> Plan(TidyOptions? options = null, IReadOnlyDictionary<string, TidyCategory>? ai = null)
    {
        options ??= new TidyOptions();
        var items = TidyScan.Collect(_root, options);

        return TidyPlan.Build(_root, items, options, Now, File.Exists, duplicates: null, aiCategories: ai);
    }

    private static string? Destination(IReadOnlyList<TidyAction> actions, string name, string root) =>
        actions.FirstOrDefault(action => action.Item.Name == name)?.Destination is { } path
            ? Path.GetRelativePath(root, path)
            : null;

    [Theory]
    [InlineData("setup.exe", TidyCategory.Installer)]
    [InlineData("发票.pdf", TidyCategory.Document)]
    [InlineData("cat.jpg", TidyCategory.Image)]
    [InlineData("屏幕截图 2026-01-02 143000.png", TidyCategory.Screenshot)]
    [InlineData("Screenshot_20260102.png", TidyCategory.Screenshot)]
    [InlineData("movie.mkv", TidyCategory.Video)]
    [InlineData("song.flac", TidyCategory.Audio)]
    [InlineData("stuff.7z", TidyCategory.Archive)]
    [InlineData("main.py", TidyCategory.Code)]
    [InlineData("ubuntu.iso", TidyCategory.Disk)]
    [InlineData("thing.torrent", TidyCategory.Torrent)]
    [InlineData("desktop.ini", TidyCategory.Junk)]
    [InlineData("新建文件夹.zzz", TidyCategory.Unknown)]
    public void Reads_a_file_kind_off_its_name(string name, TidyCategory expected) =>
        Assert.Equal(expected, TidyRules.Classify(name).Category);

    [Fact]
    public void Sorts_a_folder_into_buckets()
    {
        Write("setup.exe");
        Write("发票.pdf");
        Write("屏幕截图 2026-01-02.png");

        var plan = Plan();

        Assert.Equal(Path.Combine("安装包", "setup.exe"), Destination(plan, "setup.exe", _root));
        Assert.Equal(Path.Combine("文档", "发票.pdf"), Destination(plan, "发票.pdf", _root));
        Assert.Equal(Path.Combine("图片", "截图", "屏幕截图 2026-01-02.png"), Destination(plan, "屏幕截图 2026-01-02.png", _root));
    }

    /// <summary>Moving a file out from under whatever is writing it is the one mistake this must never make.</summary>
    [Fact]
    public void Leaves_a_file_that_was_just_touched_alone()
    {
        string path = Write("report.pdf");
        File.SetLastWriteTimeUtc(path, Now.UtcDateTime.AddMinutes(-1));

        var action = Assert.Single(Plan(), action => action.Item.Name == "report.pdf");

        Assert.False(action.Moves);
        Assert.Contains("还在写", action.Reason);
    }

    /// <summary>Somebody's transfer is in progress; moving the file it is writing would break it.</summary>
    [Fact]
    public void Leaves_a_half_finished_download_where_the_downloader_put_it()
    {
        Write("movie.mkv.part");

        var partial = Assert.Single(Plan(), action => action.Item.Name == "movie.mkv.part");

        Assert.False(partial.Moves);
        Assert.Contains("没下完", partial.Reason);
    }

    /// <summary>Three files called report.pdf must not all plan to become 文档\report.pdf.</summary>
    [Fact]
    public void Two_files_with_the_same_name_do_not_plan_to_become_one()
    {
        Write("a/report.pdf", "first");
        Write("b/report.pdf", "second");

        var plan = Plan(new TidyOptions { Depth = 3 });
        var destinations = plan.Where(action => action.Moves).Select(action => action.Destination).ToList();

        Assert.Equal(2, destinations.Count);
        Assert.Equal(2, destinations.Distinct().Count());
        Assert.Contains(destinations, path => path!.EndsWith("report (2).pdf"));
    }

    [Fact]
    public void Never_walks_into_the_folders_it_made()
    {
        Write(Path.Combine("文档", "already.pdf"));
        Write("loose.pdf");

        var plan = Plan(new TidyOptions { Depth = 5 });

        // The one already filed is not even collected, so a second run has
        // nothing to shuffle.
        Assert.Equal("loose.pdf", Assert.Single(plan).Item.Name);
    }

    [Fact]
    public void Groups_by_month_when_asked()
    {
        string path = Write("发票.pdf");
        File.SetLastWriteTimeUtc(path, new DateTime(2025, 12, 4, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(
            Path.Combine("文档", "2025-12", "发票.pdf"),
            Destination(Plan(new TidyOptions { Grouping = TidyGrouping.CategoryThenMonth }), "发票.pdf", _root));
    }

    [Fact]
    public void Files_nobody_has_touched_in_years_go_to_the_archive()
    {
        string path = Write("2019年报.pdf");
        File.SetLastWriteTimeUtc(path, new DateTime(2019, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(
            Path.Combine("存档", "2019", "2019年报.pdf"),
            Destination(Plan(new TidyOptions { StaleDays = 365 }), "2019年报.pdf", _root));
    }

    [Fact]
    public async Task Finds_files_that_are_byte_for_byte_the_same()
    {
        string first = Write("ubuntu.iso", "same bytes");
        string second = Write("ubuntu (1).iso", "same bytes");
        Write("other.iso", "different!");

        File.SetLastWriteTimeUtc(second, Now.UtcDateTime.AddHours(-1));

        var options = new TidyOptions();
        var items = TidyScan.Collect(_root, options);
        var duplicates = await TidyRunner.FindDuplicatesAsync(items);

        // The older copy stays; the newer one is the duplicate.
        Assert.Equal(first, Assert.Single(duplicates).Value);
        Assert.Equal(second, duplicates.Keys.Single());

        var plan = TidyPlan.Build(_root, items, options, Now, File.Exists, duplicates);
        Assert.Equal(Path.Combine("重复文件", "ubuntu (1).iso"), Destination(plan, "ubuntu (1).iso", _root));
    }

    [Fact]
    public void Refuses_to_tidy_a_drive_root_or_a_system_directory()
    {
        Assert.NotNull(TidyScan.Refuse(Path.GetPathRoot(Path.GetTempPath())!));
        Assert.NotNull(TidyScan.Refuse(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Assert.NotNull(TidyScan.Refuse(Path.Combine(_root, "没有这个目录")));
        Assert.Null(TidyScan.Refuse(_root));
    }

    [Fact]
    public void Moves_the_files_and_puts_them_all_back()
    {
        Write("setup.exe");
        Write("发票.pdf");

        var plan = Plan();
        var result = TidyRunner.Apply(plan);

        Assert.Equal(2, result.Moved.Count);
        Assert.Empty(result.Failed);
        Assert.True(File.Exists(Path.Combine(_root, "安装包", "setup.exe")));
        Assert.False(File.Exists(Path.Combine(_root, "setup.exe")));

        var journal = new TidyJournal();
        journal.Add(new TidyBatch
        {
            Id = "test",
            When = Now,
            Roots = new List<string> { _root },
            Moves = result.Moved.ToList(),
        });

        var (restored, refused) = TidyJournal.Undo(journal.Last!);

        Assert.Equal(2, restored);
        Assert.Empty(refused);
        Assert.True(File.Exists(Path.Combine(_root, "setup.exe")));
    }

    /// <summary>Undo restores what the tool did, and nothing it did not.</summary>
    [Fact]
    public void Undo_refuses_to_overwrite_something_that_appeared_since()
    {
        Write("setup.exe");

        var result = TidyRunner.Apply(Plan());
        Write("setup.exe", "a different file with the same name");

        var batch = new TidyBatch { Id = "t", When = Now, Roots = new List<string> { _root }, Moves = result.Moved.ToList() };
        var (restored, refused) = TidyJournal.Undo(batch);

        Assert.Equal(0, restored);
        Assert.Contains(refused, line => line.Contains("原位置又有文件了"));
    }

    [Fact]
    public void The_journal_survives_a_round_trip_and_keeps_only_the_recent_runs()
    {
        var journal = new TidyJournal();

        for (int i = 0; i < TidyJournal.Keep + 5; i++)
        {
            journal.Add(new TidyBatch
            {
                Id = $"batch-{i}",
                When = Now.AddMinutes(i),
                Roots = new List<string> { _root },
                Moves = new List<TidyMove> { new($"{_root}/a{i}.txt", $"{_root}/文档/a{i}.txt") },
            });
        }

        string path = Path.Combine(_root, "tidy.json");
        journal.Save(path);

        var reloaded = TidyJournal.Load(path);

        Assert.Equal(TidyJournal.Keep, reloaded.Batches.Count);
        Assert.Equal($"batch-{TidyJournal.Keep + 4}", reloaded.Last!.Id);
    }

    [Fact]
    public void An_empty_run_is_not_written_to_the_journal()
    {
        var journal = new TidyJournal();
        journal.Add(new TidyBatch { Id = "empty", When = Now, Roots = new List<string>(), Moves = new List<TidyMove>() });

        Assert.Empty(journal.Batches);
        Assert.False(journal.Dirty);
    }

    // ---- the optional AI half -------------------------------------------

    [Fact]
    public void Takes_only_the_answers_it_asked_for_and_only_real_categories()
    {
        var asked = new[] { "报销单.zzz", "旅行.qqq" };

        var read = AiNameClassifier.Read(
            """
            这是结果：
            ```json
            {"报销单.zzz": "document", "旅行.qqq": "杂物", "从没问过.bin": "video"}
            ```
            """,
            asked);

        // The invented category and the file that was never asked about are
        // both dropped; a model's reply is data, not instructions.
        Assert.Equal(TidyCategory.Document, Assert.Single(read).Value);
        Assert.Equal("报销单.zzz", read.Keys.Single());
    }

    [Fact]
    public void Prose_instead_of_json_classifies_nothing()
    {
        Assert.Empty(AiNameClassifier.Read("我觉得这些应该放在文档里。", new[] { "a.zzz" }));
        Assert.Empty(AiNameClassifier.Read("", new[] { "a.zzz" }));
    }

    [Fact]
    public async Task Asks_the_model_only_about_what_the_rules_could_not_place()
    {
        var handler = new CapturingHandler("""
            {"choices":[{"message":{"content":"{\"报销单.zzz\": \"document\"}"}}]}
            """);

        using var classifier = new AiNameClassifier(new AiOptions { Model = "test" }, new HttpClient(handler));

        var read = await classifier.ClassifyAsync(new[] { "报销单.zzz" });

        Assert.Equal(TidyCategory.Document, read["报销单.zzz"]);
        Assert.Contains("报销单.zzz", handler.LastBody);
        Assert.DoesNotContain("hello", handler.LastBody);  // contents are never sent
    }

    [Fact]
    public async Task A_model_that_is_not_there_changes_nothing()
    {
        var handler = new CapturingHandler("nope", HttpStatusCode.ServiceUnavailable);
        using var classifier = new AiNameClassifier(new AiOptions(), new HttpClient(handler));

        Assert.Empty(await classifier.ClassifyAsync(new[] { "a.zzz" }));
        Assert.Equal("HTTP 503", classifier.LastError);
    }

    [Fact]
    public void An_ai_answer_only_applies_where_the_rules_gave_up()
    {
        Write("报销单.zzz");
        Write("movie.mkv");

        var plan = Plan(ai: new Dictionary<string, TidyCategory>
        {
            ["报销单.zzz"] = TidyCategory.Document,
            ["movie.mkv"] = TidyCategory.Junk,   // the rules already know better
        });

        Assert.Equal(Path.Combine("文档", "报销单.zzz"), Destination(plan, "报销单.zzz", _root));
        Assert.Equal(Path.Combine("视频", "movie.mkv"), Destination(plan, "movie.mkv", _root));
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;

        public CapturingHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        public string LastBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
