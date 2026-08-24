using Microsoft.UI.Xaml.Controls;
using NetTrans.ViewModels;

namespace NetTrans.Views.Sheets;

/// <summary>
/// 种子 / 磁力链.
///
/// The sheet exists because the 新建 menu offers it, but BitTorrent is not
/// implemented: it needs the peer wire protocol, DHT and piece scheduling,
/// which is a project of its own rather than a corner of this one. Listing a
/// torrent's contents and then failing to fetch them would be worse than
/// saying so plainly.
/// </summary>
public sealed partial class TorrentSheet : UserControl
{
    private readonly ShellViewModel _viewModel;

    public TorrentSheet(ShellViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
    }

    private void OnCancelled(object? sender, EventArgs e) => _viewModel.ActiveSheet = null;
}
