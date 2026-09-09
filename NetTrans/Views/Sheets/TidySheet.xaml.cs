using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetTrans.Services;
using NetTrans.Tidy;
using NetTrans.ViewModels;
using NetTrans.Views.Controls;

namespace NetTrans.Views.Sheets;

/// <summary>
/// 深度整理: the command-line tool's plan, as a sheet.
///
/// Same two steps as `--tidy` and `--tidy --apply`, and for the same reason:
/// this moves somebody's files, so it shows the whole plan first -- what goes
/// where, and why -- and the confirming button only becomes 整理 once there is
/// a plan on screen to confirm.
/// </summary>
public sealed partial class TidySheet : UserControl
{
    /// <summary>Long enough to see the shape of the run; the report file has the rest.</summary>
    private const int Shown = 60;

    private readonly ShellViewModel _viewModel;
    private readonly TidyStore _store = new();
    private readonly CancellationTokenSource _cancellation = new();

    private IReadOnlyList<TidyAction> _plan = Array.Empty<TidyAction>();
    private bool _busy;

    public TidySheet(ShellViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();

        PathBox.Text = Folder(0);
        UndoButton.Visibility = _store.Journal.Last is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string Folder(int index) => index switch
    {
        0 => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        1 => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        _ => "",
    };

    private TidyOptions Options => new()
    {
        Grouping = GroupBox.SelectedIndex switch
        {
            1 => TidyGrouping.Month,
            2 => TidyGrouping.CategoryThenMonth,
            _ => TidyGrouping.Category,
        },
        Depth = DepthBox.SelectedIndex + 1,
        StaleDays = StaleBox.SelectedIndex switch { 1 => 182, 2 => 365, 3 => 730, _ => 0 },
    };

    private void OnRootChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RootBox.SelectedIndex < 2) PathBox.Text = Folder(RootBox.SelectedIndex);

        Invalidate();
    }

    private void OnSettingsChanged(object sender, RoutedEventArgs e) => Invalidate();

    private void OnSwitchToggled(object? sender, bool value) => Invalidate();

    /// <summary>Any change to the form invalidates the plan on screen: it was for the old settings.</summary>
    private void Invalidate()
    {
        if (_busy) return;

        _plan = Array.Empty<TidyAction>();
        Results.Visibility = Visibility.Collapsed;
        ResultList.Children.Clear();

        Host.IsRightEnabled = false;
        PreviewButton.IsEnabled = true;
        PreviewButton.Content = "预演";
    }

    private async void OnPreviewClick(object sender, RoutedEventArgs e)
    {
        string root = PathBox.Text.Trim();

        if (TidyScan.Refuse(root) is { } why)
        {
            _viewModel.Say($"不能整理这里：{why}");
            return;
        }

        _busy = true;
        PreviewButton.IsEnabled = false;
        PreviewButton.Content = "正在看…";

        var options = Options;
        bool dupes = DupesSwitch.IsOn;
        bool ai = AiSwitch.IsOn;

        try
        {
            // Off the UI thread: hashing for duplicates and asking a model both
            // take seconds, and the sheet has to stay alive to be cancelled.
            _plan = await Task.Run(() => Plan(root, options, dupes, ai), _cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception failure)
        {
            _viewModel.Say($"看不了这个目录：{failure.Message}");
            PreviewButton.IsEnabled = true;
            PreviewButton.Content = "预演";
            return;
        }
        finally
        {
            _busy = false;
        }

        Show(root);
    }

    private IReadOnlyList<TidyAction> Plan(string root, TidyOptions options, bool dupes, bool ai)
    {
        var items = TidyScan.Collect(root, options);

        var duplicates = dupes
            ? TidyRunner.FindDuplicatesAsync(items, _cancellation.Token).GetAwaiter().GetResult()
            : null;

        IReadOnlyDictionary<string, TidyCategory>? guessed = null;
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
                guessed = classifier.ClassifyAsync(unknown, _cancellation.Token).GetAwaiter().GetResult();
            }
        }

        return TidyPlan.Build(root, items, options, DateTimeOffset.Now, System.IO.File.Exists, duplicates, guessed);
    }

    private void Show(string root)
    {
        var moves = _plan.Where(action => action.Moves).ToList();

        Results.Visibility = Visibility.Visible;
        ResultList.Children.Clear();

        ResultHeader.Text = TidyReport.Summary(_plan, applied: false);

        for (int i = 0; i < moves.Count && i < Shown; i++)
        {
            ResultList.Children.Add(Row(moves[i], root, i > 0));
        }

        MoreNote.Visibility = moves.Count > Shown ? Visibility.Visible : Visibility.Collapsed;
        MoreNote.Text = $"还有 {moves.Count - Shown} 个没列出来，整理后会全部写进报告。";

        PreviewButton.Visibility = Visibility.Collapsed;
        Host.IsRightEnabled = moves.Count > 0;

        if (moves.Count == 0) _viewModel.Say("这个目录已经很整齐了");
    }

    private static FormRow Row(TidyAction action, string root, bool separator)
    {
        var row = new FormRow
        {
            Label = action.Item.Name,
            Value = System.IO.Path.GetDirectoryName(System.IO.Path.GetRelativePath(root, action.Destination!)),
            ShowSeparator = separator,
        };

        ToolTipService.SetToolTip(row, $"{action.Reason} · {FormatHelpers.Bytes(action.Item.Size)}");
        return row;
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

        Invalidate();
    }

    private void OnCancelled(object? sender, EventArgs e)
    {
        _cancellation.Cancel();
        _viewModel.ActiveSheet = null;
    }

    private async void OnConfirmed(object? sender, EventArgs e)
    {
        if (_plan.Count == 0) return;

        Host.IsRightEnabled = false;

        var plan = _plan;
        var result = await Task.Run(() => TidyRunner.Apply(plan));

        if (result.Moved.Count > 0)
        {
            _store.Journal.Add(new TidyBatch
            {
                Id = DateTime.Now.ToString("yyyyMMdd-HHmmss"),
                When = DateTimeOffset.Now,
                Roots = new List<string> { PathBox.Text.Trim() },
                Moves = result.Moved.ToList(),
            });

            _store.Flush();
        }

        _viewModel.Say(result.Failed.Count > 0
            ? $"整理了 {result.Moved.Count} 个，{result.Failed.Count} 个没能移动"
            : $"整理了 {result.Moved.Count} 个文件，可以从这里还原");

        _viewModel.ActiveSheet = null;
    }
}
