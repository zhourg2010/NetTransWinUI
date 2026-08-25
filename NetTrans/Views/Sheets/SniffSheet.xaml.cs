using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetTrans.Media;
using NetTrans.Models;
using NetTrans.Net;
using NetTrans.Services;
using NetTrans.ViewModels;
using NetTrans.Views.Controls;

namespace NetTrans.Views.Sheets;

/// <summary>视频嗅探: read a page, offer the media sources found in it.</summary>
public sealed partial class SniffSheet : UserControl
{
    private readonly ShellViewModel _viewModel;
    private readonly List<(CheckRow Row, MediaSource Source)> _rows = new();
    private readonly CancellationTokenSource _cancellation = new();

    public SniffSheet(ShellViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
    }

    private void OnPageChanged(object sender, TextChangedEventArgs e)
    {
        // Editing the address invalidates the previous probe.
        Results.Visibility = Visibility.Collapsed;
        FoundList.Children.Clear();
        _rows.Clear();

        Host.IsRightEnabled = false;
        ProbeButton.Visibility = Visibility.Visible;
        ProbeButton.IsEnabled = true;
        ProbeButton.Content = "探测视频";
    }

    private async void OnProbeClick(object sender, RoutedEventArgs e)
    {
        if (!Uri.TryCreate(PageBox.Text.Trim(), UriKind.Absolute, out var page))
        {
            _viewModel.Say("请输入完整的网页地址");
            return;
        }

        ProbeButton.IsEnabled = false;
        ProbeButton.Content = "探测中…";

        IReadOnlyList<MediaSource> found;
        try
        {
            found = await new VideoSniffer(_viewModel.Engine.Transport).SniffAsync(page, _cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            ProbeButton.IsEnabled = true;
            ProbeButton.Content = "探测视频";
            _viewModel.Say($"探测失败：{exception.Message}");
            return;
        }

        ShowResults(found);
    }

    private void ShowResults(IReadOnlyList<MediaSource> found)
    {
        ProbeButton.Visibility = Visibility.Collapsed;
        Results.Visibility = Visibility.Visible;

        if (found.Count == 0)
        {
            FoundHeader.Text = "没有在这个页面上找到视频";
            Host.IsRightEnabled = false;
            return;
        }

        FoundHeader.Text = $"找到 {found.Count} 个源";

        for (int i = 0; i < found.Count; i++)
        {
            var source = found[i];

            string size = source.SizeBytes is { } bytes ? FormatHelpers.Bytes(bytes)
                : source.IsPlaylist ? "分片流"
                : "大小未知";

            var row = new CheckRow($"{source.Quality}  {source.Format}", size, isChecked: i == 0, showSeparator: i > 0);
            row.Toggled += (_, _) => Host.IsRightEnabled = _rows.Any(entry => entry.Row.IsChecked);

            _rows.Add((row, source));
            FoundList.Children.Add(row);
        }

        Host.IsRightEnabled = true;
    }

    private void OnCancelled(object? sender, EventArgs e)
    {
        _cancellation.Cancel();
        _viewModel.ActiveSheet = null;
    }

    private void OnConfirmed(object? sender, EventArgs e)
    {
        var picked = _rows.Where(entry => entry.Row.IsChecked).Select(entry => entry.Source).ToList();
        if (picked.Count == 0) return;

        // A playlist goes into the queue like anything else -- the engine gives
        // it a segment transfer rather than a ranged one, for HLS and DASH
        // alike.
        var queued = picked;

        // The page the links were found on. Many sites will not serve the media
        // URL without it, and this is the one moment we know what it was.
        Uri.TryCreate(PageBox.Text.Trim(), UriKind.Absolute, out var page);

        foreach (var source in queued)
        {
            if (page is not null) _viewModel.Engine.RememberReferer(source.Url, page);

            _viewModel.Engine.Add(new NewDownloadRequest(
                source.Url.AbsoluteUri,
                _viewModel.Settings.DefaultSavePath,
                "video",
                // A playlist's parallelism is segments at once, not ranges of
                // one file, and eight of those is already plenty.
                Connections: 8,
                TaskPriority.Normal,
                StartNow: true));
        }

        // A split DASH manifest turns into two files under one task, which is
        // worth saying before it happens rather than after.
        _viewModel.Say(queued.Any(source => PlaylistUrl.IsDash(source.Url.AbsoluteUri))
            ? "已加入下载队列（DASH 若音视频分离会保存为两个文件）"
            : "已加入下载队列");

        _viewModel.ActiveSheet = null;
    }
}
