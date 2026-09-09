using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetTrans.Services;
using NetTrans.Tidy;
using NetTrans.ViewModels;
using NetTrans.Views.Controls;

namespace NetTrans.Views.Sheets;

/// <summary>
/// 深度整理, as a wizard whose length the folder decides.
///
/// One card per proposed project, then one per kind of file, then the summary.
/// The queue is recomputed from the draft after every answer, so accepting a
/// project can retire a question that no longer has a subject, and skipping one
/// brings it back. Nothing touches the disk until 开始整理 on the last card.
/// </summary>
public sealed partial class TidySheet : UserControl
{
    /// <summary>Names listed on a card before it turns into a wall.</summary>
    private const int Listed = 12;

    private readonly ShellViewModel _viewModel;
    private readonly TidyStore _store = new();
    private readonly CancellationTokenSource _cancellation = new();

    private TidyDraft? _draft;
    private TidyCard? _card;
    private TextBox? _nameBox;
    private TextBox? _folderBox;

    /// <summary>
    /// True until the tree is up: a ComboBox with SelectedIndex set in markup
    /// raises SelectionChanged mid-parse, when the elements declared after it
    /// are still null.
    /// </summary>
    private bool _loading = true;

    public TidySheet(ShellViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();

        PathBox.Text = Folder(0);
        UndoButton.Visibility = _store.Journal.Last is null ? Visibility.Collapsed : Visibility.Visible;

        _loading = false;
    }

    private static string Folder(int index) => index switch
    {
        0 => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        1 => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        _ => "",
    };

    private void OnRootChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (RootBox.SelectedIndex < 2) PathBox.Text = Folder(RootBox.SelectedIndex);
    }

    // ── 第一屏 ────────────────────────────────────────────────────────────

    private async void OnConfirmed(object? sender, EventArgs e)
    {
        if (_draft is null)
        {
            await ScanAsync();
            return;
        }

        if (_card?.Kind == TidyCardKind.Summary)
        {
            await ApplyAsync();
            return;
        }

        Accept();
    }

    private async Task ScanAsync()
    {
        string root = PathBox.Text.Trim();

        if (TidyScan.Refuse(root) is { } why)
        {
            _viewModel.Say($"不能整理这里：{why}");
            return;
        }

        Host.IsRightEnabled = false;
        var options = new TidyOptions
        {
            Depth = DepthBox.SelectedIndex + 1,
            StaleDays = StaleBox.SelectedIndex switch { 1 => 182, 2 => 365, 3 => 730, _ => 0 },
        };

        bool dupes = DupesSwitch.IsOn;
        bool ai = AiSwitch.IsOn;

        try
        {
            _draft = await Task.Run(() => Build(root, options, dupes, ai), _cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception failure)
        {
            _viewModel.Say($"看不了这个目录：{failure.Message}");
            Host.IsRightEnabled = true;
            return;
        }

        Setup.Visibility = Visibility.Collapsed;
        Card.Visibility = Visibility.Visible;
        Host.IsRightEnabled = true;

        Show();
    }

    private TidyDraft Build(string root, TidyOptions options, bool dupes, bool ai)
    {
        var items = TidyScan.Collect(root, options);
        var draft = TidyDraft.From(root, options, items, ProjectFinder.Find(root, items));

        if (dupes) draft.Duplicates = TidyRunner.FindDuplicatesAsync(items, _cancellation.Token).GetAwaiter().GetResult();

        if (ai)
        {
            var unknown = items
                .Where(item => TidyRules.Classify(item.Name).Category == TidyCategory.Unknown)
                .Select(item => item.Name)
                .Distinct()
                .ToList();

            if (unknown.Count > 0)
            {
                // Names only, and only the ones the rules could not place.
                using var classifier = new AiNameClassifier(AiOptions.FromEnvironment());
                draft.Guesses = classifier.ClassifyAsync(unknown, _cancellation.Token).GetAwaiter().GetResult();
            }
        }

        draft.Rebuild();
        return draft;
    }

    // ── 卡片 ──────────────────────────────────────────────────────────────

    /// <summary>Draws whichever card is now at the head of the queue.</summary>
    private void Show()
    {
        if (_draft is null) return;

        _card = TidyQueue.Current(_draft);
        var (at, total) = TidyQueue.Progress(_draft);

        CardBody.Children.Clear();
        CardExtra.Children.Clear();
        _nameBox = null;
        _folderBox = null;

        BackButton.IsEnabled = _draft.CanUndo;
        SkipButton.Visibility = _card.Kind == TidyCardKind.Summary ? Visibility.Collapsed : Visibility.Visible;
        RestButton.Visibility = SkipButton.Visibility;

        Step.Text = _card.Kind == TidyCardKind.Summary ? "最后一步" : $"第 {at} / {total} 项";
        CardTitle.Text = _card.Title;
        CardDetail.Text = _card.Detail;

        switch (_card.Kind)
        {
            case TidyCardKind.Project:
                ShowProject(_draft.Projects.First(project => project.Id == _card.ProjectId));
                Host.RightLabel = "收下这个项目";
                break;

            case TidyCardKind.Summary:
                ShowSummary();
                Host.RightLabel = "开始整理";
                break;

            default:
                ShowBucket(_card);
                Host.RightLabel = "就这么放";
                break;
        }
    }

    private void ShowProject(DraftProject project)
    {
        _nameBox = new TextBox { Text = project.Name, Width = 240, Style = Style("FormTextBoxLeftStyle") };
        _folderBox = new TextBox { Text = project.Destination, Width = 240, Style = Style("FormTextBoxLeftStyle") };

        CardBody.Children.Add(new FormRow { Label = "名字", Trailing = _nameBox, ShowSeparator = false });
        CardBody.Children.Add(new FormRow { Label = "放到", Trailing = _folderBox });

        int n = 0;
        foreach (var path in project.Members.OrderBy(path => path, StringComparer.Ordinal))
        {
            var row = new CheckRow(System.IO.Path.GetFileName(path), Size(path), isChecked: true, showSeparator: true);
            string member = path;

            // Unticking a member is an edit to the draft, not to this screen:
            // it changes what the later type cards contain.
            row.Toggled += (_, on) =>
            {
                if (on) _draft!.AddToProject(project.Id, member);
                else _draft!.RemoveFromProject(project.Id, member);
            };

            CardBody.Children.Add(row);
            if (++n >= Listed) break;
        }

        var loose = _draft!.Loose().Take(Listed).ToList();
        if (loose.Count == 0) return;

        CardExtra.Children.Add(new TextBlock
        {
            Text = "把这些也算进来？",
            Margin = new Thickness(4, 16, 4, 6),
            Style = Style("GroupHeaderTextStyle"),
        });

        var card = new Border { Style = Style("CardStyle") };
        var list = new StackPanel();
        card.Child = list;

        for (int i = 0; i < loose.Count; i++)
        {
            var item = loose[i];
            var row = new CheckRow(item.Name, FormatHelpers.Bytes(item.Size), isChecked: false, showSeparator: i > 0);

            row.Toggled += (_, on) =>
            {
                if (on) _draft!.AddToProject(project.Id, item.Path);
                else _draft!.RemoveFromProject(project.Id, item.Path);
            };

            list.Children.Add(row);
        }

        CardExtra.Children.Add(card);
    }

    private void ShowBucket(TidyCard card)
    {
        var categories = card.Categories ?? Array.Empty<TidyCategory>();
        var files = categories.SelectMany(_draft!.InBucket).ToList();

        if (categories.Count == 1)
        {
            var bucket = _draft.Buckets[categories[0]];

            _folderBox = new TextBox { Text = bucket.Folder, Width = 240, Style = Style("FormTextBoxLeftStyle") };
            CardBody.Children.Add(new FormRow { Label = "放到", Trailing = _folderBox, ShowSeparator = false });

            var month = new IosSwitch { IsOn = bucket.ByMonth };
            month.Toggled += (_, on) => _draft!.SetBucketByMonth(categories[0], on);

            CardBody.Children.Add(new FormRow { Label = "再按月份分一层", Trailing = month });
        }

        for (int i = 0; i < files.Count && i < Listed; i++)
        {
            CardBody.Children.Add(new FormRow
            {
                Label = files[i].Name,
                Value = categories.Count == 1 ? FormatHelpers.Bytes(files[i].Size) : TidyCategories.Folder(_draft.CategoryOf(files[i])),
                ShowSeparator = true,
            });
        }

        if (files.Count > Listed)
        {
            CardExtra.Children.Add(new TextBlock
            {
                Text = $"还有 {files.Count - Listed} 个没列出来。",
                Margin = new Thickness(4, 6, 4, 0),
                Style = Style("NoteTextStyle"),
            });
        }
    }

    private void ShowSummary()
    {
        var plan = TidyPlan.Build(_draft!, DateTimeOffset.Now, System.IO.File.Exists);
        var moves = plan.Where(action => action.Moves).ToList();

        CardDetail.Text = TidyReport.Summary(plan, applied: false);

        for (int i = 0; i < moves.Count && i < 40; i++)
        {
            var action = moves[i];

            var row = new FormRow
            {
                Label = action.Item.Name,
                Value = System.IO.Path.GetDirectoryName(System.IO.Path.GetRelativePath(_draft!.Root, action.Destination!)),
                ShowSeparator = i > 0,
            };

            ToolTipService.SetToolTip(row, $"{action.Reason} · {FormatHelpers.Bytes(action.Item.Size)}");
            CardBody.Children.Add(row);
        }

        if (moves.Count == 0)
        {
            CardBody.Children.Add(new FormRow { Label = "没有需要动的文件", ShowSeparator = false });
        }

        Host.IsRightEnabled = moves.Count > 0;
    }

    // ── 回答 ──────────────────────────────────────────────────────────────

    /// <summary>下一个: take the card as it stands, edits in its boxes included.</summary>
    private void Accept()
    {
        if (_draft is null || _card is null) return;

        if (_card.Kind == TidyCardKind.Project && _card.ProjectId is { } id)
        {
            var project = _draft.Projects.First(entry => entry.Id == id);

            if (_nameBox?.Text.Trim() is { Length: > 0 } name && name != project.Name) _draft.RenameProject(id, name);
            if (_folderBox?.Text.Trim() is { Length: > 0 } folder && folder != project.Destination) _draft.SetProjectDestination(id, folder);

            _draft.AcceptProject(id);
        }
        else
        {
            foreach (var category in _card.Categories ?? Array.Empty<TidyCategory>())
            {
                if (_folderBox?.Text.Trim() is { Length: > 0 } folder && folder != _draft.Buckets[category].Folder)
                {
                    _draft.SetBucketFolder(category, folder);
                }

                _draft.AcceptBucket(category);
            }
        }

        Show();
    }

    private void OnSkipClick(object sender, RoutedEventArgs e)
    {
        if (_draft is null || _card is null) return;

        if (_card.Kind == TidyCardKind.Project && _card.ProjectId is { } id) _draft.SkipProject(id);
        else foreach (var category in _card.Categories ?? Array.Empty<TidyCategory>()) _draft.SkipBucket(category);

        Show();
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        _draft?.Undo();
        Show();
    }

    private void OnRestClick(object sender, RoutedEventArgs e)
    {
        _draft?.AcceptRest();
        Show();
    }

    private async Task ApplyAsync()
    {
        if (_draft is null) return;

        Host.IsRightEnabled = false;

        var plan = TidyPlan.Build(_draft, DateTimeOffset.Now, System.IO.File.Exists);
        var result = await Task.Run(() => TidyRunner.Apply(plan));

        if (result.Moved.Count > 0)
        {
            _store.Journal.Add(new TidyBatch
            {
                Id = DateTime.Now.ToString("yyyyMMdd-HHmmss"),
                When = DateTimeOffset.Now,
                Roots = new List<string> { _draft.Root },
                Moves = result.Moved.ToList(),
            });

            _store.Flush();
        }

        _viewModel.Say(result.Failed.Count > 0
            ? $"整理了 {result.Moved.Count} 个，{result.Failed.Count} 个没能移动"
            : $"整理了 {result.Moved.Count} 个文件，可以从这里还原");

        _viewModel.ActiveSheet = null;
    }

    private async void OnUndoClick(object sender, RoutedEventArgs e)
    {
        if (_store.Journal.Last is not { } batch) return;

        UndoButton.IsEnabled = false;

        var (restored, refused) = await Task.Run(() => TidyJournal.Undo(batch));

        _store.Journal.Remove(batch);
        _store.Flush();

        UndoButton.Visibility = _store.Journal.Last is null ? Visibility.Collapsed : Visibility.Visible;
        UndoButton.IsEnabled = true;

        _viewModel.Say(refused.Count > 0
            ? $"还原了 {restored} 个，{refused.Count} 个没能还原"
            : $"还原了 {restored} 个文件");
    }

    private void OnCancelled(object? sender, EventArgs e)
    {
        _cancellation.Cancel();
        _viewModel.ActiveSheet = null;
    }

    private static string Size(string path)
    {
        try
        {
            return FormatHelpers.Bytes(new System.IO.FileInfo(path).Length);
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>
    /// A style from the app's dictionaries. Card bodies are built in code
    /// rather than markup because their shape depends on the card -- and the
    /// styles still have to be the same ones the rest of the sheet uses.
    /// </summary>
    private static Style Style(string key) => (Style)Application.Current.Resources[key];
}
