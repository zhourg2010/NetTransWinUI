using Microsoft.UI.Xaml.Controls;
using NetTrans.ViewModels;
using NetTrans.Views.Controls;

namespace NetTrans.Views.Sheets;

/// <summary>种子内容: pick which files inside a torrent to fetch.</summary>
public sealed partial class TorrentSheet : UserControl
{
    private static readonly (string Name, string Size, bool Checked)[] Files =
    {
        ("archlinux-2026.08-x86_64.iso", "3.14 GB", true),
        ("archlinux-bootstrap.tar.zst", "182 MB", true),
        ("sha256sums.txt", "1 KB", true),
        ("sha256sums.txt.sig", "1 KB", false),
        ("magnet-mirrors.txt", "2 KB", false),
    };

    private readonly ShellViewModel _viewModel;
    private readonly List<CheckRow> _rows = new();

    public TorrentSheet(ShellViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();

        for (int i = 0; i < Files.Length; i++)
        {
            var row = new CheckRow(Files[i].Name, Files[i].Size, Files[i].Checked, showSeparator: i > 0);
            row.Toggled += (_, _) => UpdateCount();
            _rows.Add(row);
            FileList.Children.Add(row);
        }

        UpdateCount();
    }

    private void UpdateCount()
    {
        int selected = _rows.Count(r => r.IsChecked);
        Host.RightLabel = $"下载 {selected}";
        Host.IsRightEnabled = selected > 0;
    }

    private void OnCancelled(object? sender, EventArgs e) => _viewModel.ActiveSheet = null;

    private void OnConfirmed(object? sender, EventArgs e)
    {
        _viewModel.Say($"已选择 {_rows.Count(r => r.IsChecked)} 个文件");
        _viewModel.ActiveSheet = null;
    }
}
