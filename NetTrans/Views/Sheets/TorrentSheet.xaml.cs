using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NetTrans.Download;
using NetTrans.Models;
using NetTrans.Services;
using NetTrans.Torrent;
using NetTrans.ViewModels;

namespace NetTrans.Views.Sheets;

/// <summary>
/// 种子 / 磁力链.
///
/// A magnet or a .torrent goes into the queue like any other task; the engine
/// gives it a BitTorrent transfer rather than an HTTP one. The settings here
/// are the ones that cannot sensibly be global: whether to fetch in order (for
/// previewing a video), what to hold the upload to, and when to stop seeding --
/// the rule a private tracker's account is actually measured by.
/// </summary>
public sealed partial class TorrentSheet : UserControl
{
    private readonly ShellViewModel _viewModel;

    private string _saveTo;

    /// <summary>One checkbox per file of a .torrent that could be read up front.</summary>
    private readonly List<(CheckBox Box, TorrentEntry File)> _files = new();

    private TorrentMetainfo? _metainfo;

    public TorrentSheet(ShellViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();

        _saveTo = viewModel.Settings.DefaultSavePath;
        SavePathRow.Value = _saveTo;
        SeedingBox.SelectedIndex = 0;
        UploadLimitBox.SelectedIndex = 0;

        // Whatever is on the clipboard is usually why the sheet was opened.
        if (TorrentUrl.IsTorrent(viewModel.PendingUrl)) LinkBox.Text = viewModel.PendingUrl;
    }

    private void OnLinkChanged(object sender, TextChangedEventArgs e)
    {
        // A magnet pasted as thunder:// is still a magnet.
        string text = NetTrans.Net.PrivateLinks.Unwrap(LinkBox.Text);
        bool usable = TorrentUrl.IsTorrent(text);

        Host.IsRightEnabled = usable;

        ShowFiles(text);

        Summary.Text = !usable && text.Length > 0
            ? "无法识别：需要 magnet: 链接，或以 .torrent 结尾的文件路径 / 网址。"
            : TorrentUrl.IsMagnet(text)
                ? "磁力链需要先向 peer 索取元数据，文件列表会在开始后出现。"
                : _metainfo is not null
                    ? "取消勾选的文件不会下载。"
                    : "种子文件会在开始后读取，文件列表随即出现。";
    }

    /// <summary>
    /// Lists what is inside a .torrent that is already on disk, so files can be
    /// deselected before anything is fetched.
    ///
    /// Only for a local file: a magnet has no file list until peers have sent
    /// the metainfo, and a .torrent behind a URL would have to be downloaded
    /// first -- neither is worth blocking a sheet on.
    /// </summary>
    private void ShowFiles(string text)
    {
        _metainfo = ReadTorrent(text);

        _files.Clear();
        FilesPanel.Children.Clear();

        // One file is not a choice, so the card stays out of the way.
        if (_metainfo is not { Files.Count: > 1 })
        {
            FilesCard.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (var file in _metainfo.Files)
        {
            var box = new CheckBox
            {
                IsChecked = true,
                Content = $"{file.Path}  ·  {FormatHelpers.Bytes(file.Length)}",
                MinHeight = 30,
                FontSize = 13,
            };

            box.Checked += OnFileToggled;
            box.Unchecked += OnFileToggled;

            _files.Add((box, file));
            FilesPanel.Children.Add(box);
        }

        FilesCard.Visibility = Visibility.Visible;
        UpdateFilesHeader();
    }

    private static TorrentMetainfo? ReadTorrent(string text)
    {
        if (!TorrentUrl.IsTorrentFile(text)) return null;
        if (Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Scheme is "http" or "https") return null;

        try
        {
            return TorrentMetainfo.Parse(System.IO.File.ReadAllBytes(text));
        }
        catch (Exception)
        {
            // Not readable, or not a torrent after all. The transfer will say
            // so properly; the sheet just does not offer a list.
            return null;
        }
    }

    private void OnFileToggled(object sender, RoutedEventArgs e) => UpdateFilesHeader();

    /// <summary>
    /// The count and what it will actually cost.
    ///
    /// The size is the pieces' size, not the files': a piece straddling a
    /// wanted and an unwanted file still has to be fetched whole, so
    /// deselecting a small file next to a wanted one often saves nothing.
    /// </summary>
    private void UpdateFilesHeader()
    {
        if (_metainfo is null) return;

        var chosen = Chosen();

        Host.IsRightEnabled = chosen.Count > 0 && TorrentUrl.IsTorrent(NetTrans.Net.PrivateLinks.Unwrap(LinkBox.Text));

        long bytes = chosen.Count == 0 ? 0 : FileSelection.BytesFor(_metainfo, chosen);

        FilesHeaderRow.Value = $"{chosen.Count}/{_metainfo.Files.Count} · {FormatHelpers.Bytes(bytes)}";
    }

    private List<TorrentEntry> Chosen() =>
        _files.Where(entry => entry.Box.IsChecked == true).Select(entry => entry.File).ToList();

    private async void OnPickFileTapped(object sender, TappedRoutedEventArgs e)
    {
        string? picked = await FilePrompt.PickTorrentAsync();
        if (picked is null) return;

        // Setting the text raises OnLinkChanged, which is what reads the file
        // and puts its contents on screen.
        LinkBox.Text = picked;
        FileRow.Value = System.IO.Path.GetFileName(picked);
    }

    private async void OnSavePathTapped(object sender, TappedRoutedEventArgs e)
    {
        string? chosen = await FolderPrompt.PickAsync();
        if (chosen is null) return;

        _saveTo = chosen;
        SavePathRow.Value = chosen;
    }

    private void OnConfirmed(object? sender, EventArgs e)
    {
        string link = NetTrans.Net.PrivateLinks.Unwrap(LinkBox.Text);
        if (!TorrentUrl.IsTorrent(link)) return;

        var task = _viewModel.Engine.Add(new NewDownloadRequest(
            link,
            _saveTo,
            "bt",
            // Peers, not connections to one server. Eight is plenty for a
            // healthy swarm and polite to a small one.
            Connections: 8,
            TaskPriority.Normal,
            StartNow: true));

        // The real name arrives with the metainfo; until then the row shows
        // whatever the link itself gave away.
        task.Model.Name = TorrentUrl.Describe(link);
        task.Refresh();

        _viewModel.Engine.ApplyTorrentOptions(task.Id, new TorrentTaskOptions(
            SequentialSwitch.IsOn,
            Limits(),
            UploadLimit(),
            // Everything selected is the same as no selection at all, and the
            // transfer treats it that way.
            _metainfo is null || Chosen().Count == _metainfo.Files.Count
                ? null
                : Chosen().Select(file => file.Path).ToList()));
        _viewModel.Say(TorrentUrl.IsMagnet(link) ? "已加入，正在向 peer 索取元数据…" : "已加入下载队列");

        _viewModel.ActiveSheet = null;
    }

    /// <summary>The 上传限速 dropdown in bytes per second; zero is 不限.</summary>
    private double UploadLimit() => SpeedLimits.Parse(UploadLimitBox.SelectedItem as string);

    /// <summary>The 做种限制 dropdown as the engine's own type.</summary>
    private SeedingLimits Limits() => (SeedingBox.SelectedItem as string) switch
    {
        "分享率 1.0" => SeedingLimits.Ratio(1.0),
        "分享率 2.0" => SeedingLimits.Ratio(2.0),
        "做种 60 分钟" => new SeedingLimits(MaxSeedingTime: TimeSpan.FromHours(1)),

        // Stopping the moment it finishes is the one that makes a leech, so it
        // is spelled out rather than being the default.
        "下完即停" => new SeedingLimits(MaxSeedingTime: TimeSpan.Zero),
        _ => SeedingLimits.Forever,
    };

    private void OnCancelled(object? sender, EventArgs e) => _viewModel.ActiveSheet = null;
}
