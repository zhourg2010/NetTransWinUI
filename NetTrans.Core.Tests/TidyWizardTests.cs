using NetTrans.Tidy;
using Xunit;

namespace NetTrans.Tests;

/// <summary>
/// 向导: finding what belongs together, and the decision queue that grows and
/// shrinks with the answers.
/// </summary>
public class TidyWizardTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "nettrans-wizard-" + Guid.NewGuid().ToString("N"));

    public TidyWizardTests() => Directory.CreateDirectory(_root);

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

    private TidyItem Item(string relative, DateTimeOffset? modified = null)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");

        return new TidyItem(path, Path.GetFileName(path), 1, modified ?? Now.AddDays(-1));
    }

    private TidyDraft Draft(params TidyItem[] items)
    {
        var list = items.ToList();
        return TidyDraft.From(_root, new TidyOptions(), list, ProjectFinder.Find(_root, list));
    }

    // ── the stem ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("建筑报告.png", "建筑报告")]
    [InlineData("建筑报告v1.0.1.docx", "建筑报告")]
    [InlineData("建筑报告 第3版.docx", "建筑报告")]
    [InlineData("建筑报告-最终版.docx", "建筑报告")]
    [InlineData("建筑报告(2).docx", "建筑报告")]
    [InlineData("建筑报告 - 副本.docx", "建筑报告")]
    [InlineData("建筑报告 2026-01-02.docx", "建筑报告")]
    [InlineData("Roadmap_v2.pptx", "roadmap")]
    [InlineData("roadmap-20260102.pptx", "roadmap")]
    [InlineData("invoice rev3.pdf", "invoice")]
    public void Strips_versions_dates_and_copy_marks(string name, string expected) =>
        Assert.Equal(expected, NameStem.Key(name));

    [Fact]
    public void Splits_a_name_into_tokens_without_a_dictionary()
    {
        Assert.Equal(new[] { "建筑报告", "立面图" }, NameStem.Tokens("建筑报告-立面图"));
        Assert.Equal(new[] { "roadmap", "2026" }, NameStem.Tokens("roadmap_2026"));
        Assert.Equal("建筑报告", NameStem.Common(new[] { "建筑报告", "建筑报告-立面图" }));
    }

    /// <summary>Grouping on a word every second file uses would collect the whole desktop.</summary>
    [Fact]
    public void Will_not_group_on_a_word_that_says_nothing()
    {
        Assert.Null(NameStem.Lead("报告"));
        Assert.Null(NameStem.Lead("新建文档"));
        Assert.Null(NameStem.Lead("img"));
        Assert.Equal("建筑报告", NameStem.Lead("建筑报告-立面图"));
    }

    // ── finding projects ──────────────────────────────────────────────────

    [Fact]
    public void Finds_the_two_halves_of_one_piece_of_work()
    {
        var found = ProjectFinder.Find(_root, new[] { Item("建筑报告.png"), Item("建筑报告v1.0.1.docx"), Item("setup.exe") });

        var project = Assert.Single(found);
        Assert.Equal("建筑报告", project.Name);
        Assert.Equal(ProjectSource.Stem, project.Source);
        Assert.Equal(2, project.Members.Count);
        Assert.Contains("建筑报告", project.Reason);
    }

    /// <summary>A folder somebody made by hand is the strongest evidence there is.</summary>
    [Fact]
    public void A_folder_someone_already_made_wins_over_the_name_guess()
    {
        var found = ProjectFinder.Find(_root, new[]
        {
            Item(Path.Combine("投标材料", "封面.png")),
            Item(Path.Combine("投标材料", "正文.docx")),
            Item("建筑报告.png"),
            Item("建筑报告v2.docx"),
        });

        Assert.Equal(new[] { ProjectSource.Folder, ProjectSource.Stem }, found.Select(project => project.Source));
        Assert.Equal("投标材料", found[0].Name);
    }

    [Fact]
    public void One_file_is_not_a_project()
    {
        Assert.Empty(ProjectFinder.Find(_root, new[] { Item("建筑报告.png"), Item("setup.exe") }));
    }

    // ── the queue ─────────────────────────────────────────────────────────

    [Fact]
    public void Asks_about_each_project_then_each_type()
    {
        var draft = Draft(
            Item("建筑报告.png"), Item("建筑报告v1.0.1.docx"),
            Item("发票.pdf"), Item("合同.pdf"),
            Item("a.mp4"), Item("b.mp4"));

        var cards = TidyQueue.Pending(draft);

        Assert.Equal(TidyCardKind.Project, cards[0].Kind);
        Assert.Equal("建筑报告", cards[0].Title);

        // Then the types, biggest first, and the summary last.
        Assert.Equal(new[] { TidyCardKind.Bucket, TidyCardKind.Bucket, TidyCardKind.Summary },
            cards.Skip(1).Select(card => card.Kind));

        Assert.Equal(TidyCardKind.Summary, cards[^1].Kind);
    }

    /// <summary>
    /// A file held by a proposed project is not also asked about by type: it
    /// would be the same question twice, and the project would win anyway.
    /// </summary>
    [Fact]
    public void A_proposed_project_holds_its_files_until_it_is_skipped()
    {
        var draft = Draft(Item("建筑报告.png"), Item("建筑报告v1.docx"), Item("发票.pdf"), Item("合同.pdf"));

        var cards = TidyQueue.Pending(draft);
        Assert.DoesNotContain(cards, card => card.Title == TidyCategories.Folder(TidyCategory.Image));
        Assert.Equal(2, cards.Single(card => card.Title == TidyCategories.Folder(TidyCategory.Document)).Count);

        draft.SkipProject(draft.Projects[0].Id);
        cards = TidyQueue.Pending(draft);

        // The .docx rejoins 文档, and the lone .png turns up on the 零散文件 card.
        Assert.Equal(3, cards.Single(card => card.Title == TidyCategories.Folder(TidyCategory.Document)).Count);
        Assert.Contains(cards, card => card.Kind == TidyCardKind.Loose && card.Categories!.Contains(TidyCategory.Image));
    }

    [Fact]
    public void Accepting_a_project_takes_its_card_off_the_queue()
    {
        var draft = Draft(Item("建筑报告.png"), Item("建筑报告v1.docx"), Item("发票.pdf"), Item("合同.pdf"));

        draft.AcceptProject(draft.Projects[0].Id);
        var cards = TidyQueue.Pending(draft);

        Assert.DoesNotContain(cards, card => card.Kind == TidyCardKind.Project);
        Assert.Contains(cards, card => card.Title == TidyCategories.Folder(TidyCategory.Document));
    }

    /// <summary>Ten cards holding one file each is not a wizard, it is a chore.</summary>
    [Fact]
    public void Types_with_a_single_file_are_asked_about_together()
    {
        var draft = Draft(Item("a.mp4"), Item("setup.exe"), Item("song.mp3"));

        var loose = Assert.Single(TidyQueue.Pending(draft), card => card.Kind == TidyCardKind.Loose);

        Assert.Equal(3, loose.Count);
        Assert.Equal(3, loose.Categories!.Count);
    }

    [Fact]
    public void Progress_counts_what_is_decided_and_what_is_left()
    {
        var draft = Draft(Item("建筑报告.png"), Item("建筑报告v1.docx"), Item("发票.pdf"), Item("合同.pdf"));

        var (at, total) = TidyQueue.Progress(draft);
        Assert.Equal(1, at);

        draft.AcceptProject(draft.Projects[0].Id);
        var (next, _) = TidyQueue.Progress(draft);

        Assert.Equal(2, next);
        Assert.True(total >= 2);
    }

    // ── editing a project ─────────────────────────────────────────────────

    [Fact]
    public void A_file_can_be_added_to_a_project_and_taken_out_again()
    {
        var loose = Item("立面图.png");
        var draft = Draft(Item("建筑报告.png"), Item("建筑报告v1.docx"), loose);

        string id = draft.Projects[0].Id;

        draft.AddToProject(id, loose.Path);
        Assert.Equal(3, draft.Projects[0].Members.Count);
        Assert.DoesNotContain(draft.Loose(), item => item.Path == loose.Path);

        draft.RemoveFromProject(id, loose.Path);
        Assert.Contains(draft.Loose(), item => item.Path == loose.Path);
    }

    [Fact]
    public void Renaming_a_project_moves_its_folder_unless_it_was_set_by_hand()
    {
        var draft = Draft(Item("建筑报告.png"), Item("建筑报告v1.docx"));
        string id = draft.Projects[0].Id;

        draft.RenameProject(id, "投标");
        Assert.Equal(Path.Combine("项目", "投标"), draft.Projects[0].Destination);

        draft.SetProjectDestination(id, "工作");
        draft.RenameProject(id, "投标2026");
        Assert.Equal("工作", draft.Projects[0].Destination);
    }

    [Fact]
    public void Every_decision_can_be_taken_back()
    {
        var draft = Draft(Item("建筑报告.png"), Item("建筑报告v1.docx"), Item("发票.pdf"), Item("合同.pdf"));
        string id = draft.Projects[0].Id;

        draft.AcceptProject(id);
        draft.RenameProject(id, "投标");
        draft.SkipBucket(TidyCategory.Document);

        Assert.True(draft.CanUndo);

        draft.Undo();
        Assert.Equal(Decision.Pending, draft.Buckets[TidyCategory.Document].Decision);

        draft.Undo();
        Assert.Equal("建筑报告", draft.Projects[0].Name);

        draft.Undo();
        Assert.Equal(Decision.Pending, draft.Projects[0].Decision);
        Assert.False(draft.CanUndo);
    }

    // ── the plan the draft produces ───────────────────────────────────────

    [Fact]
    public void Project_members_go_to_the_project_folder_and_the_rest_by_type()
    {
        var draft = Draft(Item("建筑报告.png"), Item("建筑报告v1.docx"), Item("发票.pdf"));
        draft.AcceptProject(draft.Projects[0].Id);

        var plan = TidyPlan.Build(draft, Now, File.Exists);

        Assert.Equal(Path.Combine("项目", "建筑报告", "建筑报告.png"), Relative(plan, "建筑报告.png"));
        Assert.Equal(Path.Combine("项目", "建筑报告", "建筑报告v1.docx"), Relative(plan, "建筑报告v1.docx"));
        Assert.Equal(Path.Combine("文档", "发票.pdf"), Relative(plan, "发票.pdf"));
    }

    /// <summary>快进 and answering every card the obvious way have to mean the same thing.</summary>
    [Fact]
    public void A_card_nobody_reached_counts_as_accepted()
    {
        var draft = Draft(Item("建筑报告.png"), Item("建筑报告v1.docx"), Item("发票.pdf"));

        var untouched = TidyPlan.Build(draft, Now, File.Exists);

        draft.AcceptRest();
        var accepted = TidyPlan.Build(draft, Now, File.Exists);

        Assert.Equal(
            untouched.Select(action => action.Destination),
            accepted.Select(action => action.Destination));
    }

    [Fact]
    public void A_skipped_type_stays_where_it_is()
    {
        var draft = Draft(Item("发票.pdf"), Item("合同.pdf"));
        draft.SkipBucket(TidyCategory.Document);

        Assert.All(TidyPlan.Build(draft, Now, File.Exists), action => Assert.False(action.Moves));
    }

    [Fact]
    public void A_file_taken_out_of_the_run_is_not_moved()
    {
        var item = Item("发票.pdf");
        var draft = Draft(item, Item("合同.pdf"));

        draft.SkipFile(item.Path);

        var action = Assert.Single(TidyPlan.Build(draft, Now, File.Exists), action => action.Item.Path == item.Path);
        Assert.False(action.Moves);
        Assert.Equal("你选了不动", action.Reason);
    }

    // ── the draft on disk ─────────────────────────────────────────────────

    [Fact]
    public void The_draft_survives_a_round_trip_and_plans_the_same_thing()
    {
        var draft = Draft(Item("建筑报告.png"), Item("建筑报告v1.docx"), Item("发票.pdf"), Item("a.mp4"));

        draft.AcceptProject(draft.Projects[0].Id);
        draft.RenameProject(draft.Projects[0].Id, "投标");
        draft.SkipBucket(TidyCategory.Video);

        string path = Path.Combine(_root, "plan.json");
        TidyDraftFile.Save(draft, path);

        var reloaded = TidyDraftFile.Load(path)!;

        Assert.Equal("投标", reloaded.Projects[0].Name);
        Assert.Equal(Decision.Skipped, reloaded.Buckets[TidyCategory.Video].Decision);

        Assert.Equal(
            TidyPlan.Build(draft, Now, File.Exists).Select(action => action.Destination),
            TidyPlan.Build(reloaded, Now, File.Exists).Select(action => action.Destination));

        // Meant to be edited by hand, so it has to be readable.
        string json = File.ReadAllText(path);
        Assert.Contains("投标", json);
        Assert.Contains("\"Skipped\"", json);
    }

    private string? Relative(IReadOnlyList<TidyAction> plan, string name) =>
        plan.FirstOrDefault(action => action.Item.Name == name)?.Destination is { } path
            ? Path.GetRelativePath(_root, path)
            : null;
}
