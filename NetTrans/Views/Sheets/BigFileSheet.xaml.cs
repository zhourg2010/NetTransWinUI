using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetTrans.Download;
using NetTrans.Net;
using NetTrans.Services;
using NetTrans.Verify;
using NetTrans.ViewModels;
using NetTrans.Views.Controls;

namespace NetTrans.Views.Sheets;

/// <summary>
/// 大文件核对: every single file over the threshold, hashed and checked.
///
/// The same run as `--bigfiles`, with the wait made visible -- which is the
/// whole reason it is worth having here as well. Hashing a disk full of ISOs is
/// minutes to hours, so the sheet shows which file it is on and how far in, and
/// 取消 keeps everything already computed.
/// </summary>
public sealed partial class BigFileSheet : UserControl
{
    private const int Shown = 40;

    private readonly ShellViewModel _viewModel;
    private readonly BigFileStore _store = new();
    private readonly CancellationTokenSource _cancellation = new();

    /// <summary>
    /// True until the tree is up: a ComboBox with SelectedIndex set in markup
    /// raises SelectionChanged mid-parse, when the elements declared after it
    /// are still null.
    /// </summary>
    private bool _loading = true;

    public BigFileSheet(ShellViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();

        _loading = false;
    }

    private long Threshold => (SizeBox.SelectedIndex switch { 1 => 2L, 2 => 4L, 3 => 8L, _ => 1L }) * 1024 * 1024 * 1024;

    private IReadOnlyList<string> Roots()
    {
        if (ScopeBox.SelectedIndex == 2 && PathBox.Text.Trim() is { Length: > 0 } custom)
        {
            return new[] { custom };
        }

        if (ScopeBox.SelectedIndex == 1)
        {
            return new[] { System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") };
        }

        return DriveInfo.GetDrives()
            .Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady)
            .Select(drive => drive.RootDirectory.FullName)
            .ToList();
    }

    private void OnScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        PathBox.IsEnabled = ScopeBox.SelectedIndex == 2;
        if (ScopeBox.SelectedIndex != 2) PathBox.Text = "";
    }

    private async void OnScanClick(object sender, RoutedEventArgs e)
    {
        var roots = Roots().Where(Directory.Exists).ToList();
        if (roots.Count == 0)
        {
            _viewModel.Say("这个位置不存在");
            return;
        }

        ScanButton.IsEnabled = false;
        ScanButton.Content = "正在找…";
        Progress.Visibility = Visibility.Visible;
        Progress.Text = "正在扫描…";

        var options = new ScanOptions { MinimumSize = Threshold };
        bool online = OnlineSwitch.IsOn;
        bool rehash = RehashSwitch.IsOn;

        // Constructed here so its callbacks come back to the UI thread.
        var progress = new Progress<BigFileProgress>(state =>
        {
            double share = state.File.Size > 0 ? (double)state.Done / state.File.Size : 0;
            Progress.Text = $"[{state.Index + 1}/{state.Total}] {state.File.Name} {share:P0}";
        });

        IReadOnlyList<BigFileOutcome> outcomes;
        try
        {
            outcomes = await Task.Run(() => Audit(roots, options, online, rehash, progress), _cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception failure)
        {
            _viewModel.Say($"核对失败：{failure.Message}");
            ScanButton.IsEnabled = true;
            ScanButton.Content = "开始核对";
            return;
        }

        Show(outcomes);
    }

    private IReadOnlyList<BigFileOutcome> Audit(
        IReadOnlyList<string> roots,
        ScanOptions options,
        bool online,
        bool rehash,
        IProgress<BigFileProgress> progress)
    {
        var files = BigFileScan.Walk(roots, options, cancellationToken: _cancellation.Token).ToList();

        using var transport = online ? new HttpTransport(userAgent: "NetTrans/1.0") : null;

        var outcomes = BigFileAudit.RunAsync(
            files,
            _store.Ledger,
            SystemClock.Instance,
            // 哈希库 is read for a published digest and never written to; the
            // ledger this run fills is the tool's own file.
            new HashDatabaseStore().Database,
            transport,
            progress,
            new AuditOptions { Rehash = rehash, Online = online },
            _cancellation.Token).GetAwaiter().GetResult();

        BigFileAudit.Prune(_store.Ledger);
        _store.Flush();

        return outcomes;
    }

    private void Show(IReadOnlyList<BigFileOutcome> outcomes)
    {
        Progress.Visibility = Visibility.Collapsed;
        ScanButton.Visibility = Visibility.Collapsed;
        Results.Visibility = Visibility.Visible;
        ResultList.Children.Clear();

        ResultHeader.Text = BigFileReport.Summary(outcomes);

        // Anything that did not match first: that is what a person opened this for.
        var ordered = outcomes
            .OrderByDescending(outcome => outcome.Verdict is VerifyOutcome.Mismatch or VerifyOutcome.Changed)
            .ThenByDescending(outcome => outcome.File.Size)
            .ToList();

        for (int i = 0; i < ordered.Count && i < Shown; i++)
        {
            var outcome = ordered[i];

            var row = new FormRow
            {
                Label = outcome.File.Name,
                Value = $"{FormatHelpers.Bytes(outcome.File.Size)} · {BigFileReport.Describe(outcome.Verdict)}",
                ShowSeparator = i > 0,
                IsError = outcome.Verdict is VerifyOutcome.Mismatch or VerifyOutcome.Changed,
            };

            ToolTipService.SetToolTip(row, $"{outcome.Sha256 ?? "没算出来"}\n{outcome.File.Path}");
            ResultList.Children.Add(row);
        }

        LedgerNote.Text = ordered.Count > Shown
            ? $"还有 {ordered.Count - Shown} 个没列出来。全部记录在 {_store.Path}。"
            : $"记录在 {_store.Path}，和下载用的哈希库分开存。";
    }

    private void OnCancelled(object? sender, EventArgs e)
    {
        // Everything hashed so far is already in the ledger; the flush below
        // keeps it, so a cancelled run is time spent, not time wasted.
        _cancellation.Cancel();
        _store.Flush();
        _viewModel.ActiveSheet = null;
    }

    private void OnConfirmed(object? sender, EventArgs e) => OnCancelled(sender, e);
}
