using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetTrans.Models;
using NetTrans.Net;
using NetTrans.Services;
using NetTrans.ViewModels;
using NetTrans.Views.Controls;

namespace NetTrans.Views.Sheets;

/// <summary>批量下载: crawl a page, then pick which of the found resources to queue.</summary>
public sealed partial class BatchSheet : UserControl
{
    private readonly ShellViewModel _viewModel;
    private readonly List<(CheckRow Row, DiscoveredLink Link)> _rows = new();
    private readonly CancellationTokenSource _cancellation = new();

    public BatchSheet(ShellViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
    }

    private async void OnScanClick(object sender, RoutedEventArgs e)
    {
        if (!Uri.TryCreate(PageBox.Text.Trim(), UriKind.Absolute, out var start))
        {
            _viewModel.Say("请输入完整的页面地址");
            return;
        }

        FormStage.Visibility = Visibility.Collapsed;
        ScanStage.Visibility = Visibility.Visible;

        var crawler = new PageCrawler(_viewModel.Engine.Transport);

        var options = new CrawlOptions(
            Depth: DepthBox.SelectedIndex,
            SameSiteOnly: SameSiteSwitch.IsOn,
            Extensions: LinkExtractor.ParseExtensions(ExtensionsBox.Text),
            MaxResults: 60);

        IReadOnlyList<DiscoveredLink> found;
        try
        {
            found = await crawler.CrawlAsync(start, options, _cancellation.Token);

            // Sizes are shown per row and drive 最小文件, so they are worth the probes.
            found = await LinkSizer.MeasureAsync(_viewModel.Engine.Transport, found, cancellationToken: _cancellation.Token);
            found = LinkSizer.AtLeast(found, LinkSizer.ParseMinimum(MinimumBox.SelectedItem as string));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            ScanStage.Visibility = Visibility.Collapsed;
            FormStage.Visibility = Visibility.Visible;
            _viewModel.Say($"抓取失败：{exception.Message}");
            return;
        }

        ShowResults(found, crawler.Failures);
    }

    private void ShowResults(IReadOnlyList<DiscoveredLink> found, IReadOnlyList<string> failures)
    {
        ScanStage.Visibility = Visibility.Collapsed;
        ListStage.Visibility = Visibility.Visible;

        if (found.Count == 0)
        {
            FoundHeader.Text = "没有找到可下载的资源";
            Host.IsRightEnabled = false;
        }

        for (int i = 0; i < found.Count; i++)
        {
            var link = found[i];
            string size = link.SizeBytes is { } bytes ? FormatHelpers.Bytes(bytes) : "大小未知";

            var row = new CheckRow(link.Name, size, isChecked: true, showSeparator: i > 0);
            row.Toggled += (_, _) => UpdateCount();

            _rows.Add((row, link));
            FoundList.Children.Add(row);
        }

        if (failures.Count > 0)
        {
            Problems.Text = $"有 {failures.Count} 个页面读取失败，结果可能不完整。";
            Problems.Visibility = Visibility.Visible;
        }

        UpdateCount();
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        foreach (var (row, _) in _rows) row.SetChecked(true);
        UpdateCount();
    }

    private void UpdateCount()
    {
        if (_rows.Count == 0) return;

        int selected = _rows.Count(entry => entry.Row.IsChecked);
        FoundHeader.Text = $"找到 {_rows.Count} 个资源 · 已选 {selected}";
        Host.RightLabel = $"添加 {selected}";
        Host.IsRightEnabled = selected > 0;
    }

    private void OnCancelled(object? sender, EventArgs e)
    {
        _cancellation.Cancel();
        _viewModel.ActiveSheet = null;
    }

    private void OnConfirmed(object? sender, EventArgs e)
    {
        var picked = _rows.Where(entry => entry.Row.IsChecked).Select(entry => entry.Link).ToList();
        if (picked.Count == 0) return;

        foreach (var link in picked)
        {
            _viewModel.Engine.Add(new NewDownloadRequest(
                link.Url.AbsoluteUri,
                _viewModel.Settings.DefaultSavePath,
                CategoryFor(link.Extension),
                Connections: 8,
                TaskPriority.Normal,
                StartNow: true));
        }

        _viewModel.Say($"已添加 {picked.Count} 个任务");
        _viewModel.ActiveSheet = null;
    }

    /// <summary>Puts a crawled file into the category chip it belongs under.</summary>
    private static string CategoryFor(string extension) => extension switch
    {
        "mp4" or "mkv" or "mov" or "webm" or "avi" => "video",
        "mp3" or "flac" or "m4a" or "wav" => "music",
        "exe" or "msi" or "iso" or "dmg" or "zip" or "7z" or "rar" => "soft",
        _ => "doc",
    };
}
