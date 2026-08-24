using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetTrans.ViewModels;

namespace NetTrans.Views.Sheets;

/// <summary>重命名: renames the file on disk as well as the row.</summary>
public sealed partial class RenameSheet : UserControl
{
    private readonly ShellViewModel _viewModel;
    private readonly DownloadItemViewModel _item;

    public RenameSheet(ShellViewModel viewModel, DownloadItemViewModel item)
    {
        _viewModel = viewModel;
        _item = item;
        InitializeComponent();

        NameBox.Text = item.Name;
        Note.Text = item.IsRunning
            ? "下载中的任务无法重命名，请先暂停。"
            : $"文件保存在 {item.SavePath}";

        Host.IsRightEnabled = !item.IsRunning;

        Loaded += (_, _) =>
        {
            NameBox.Focus(FocusState.Programmatic);

            // Select the stem, not the extension -- that is the part being changed.
            int dot = NameBox.Text.LastIndexOf('.');
            NameBox.Select(0, dot > 0 ? dot : NameBox.Text.Length);
        };
    }

    private void OnCancelled(object? sender, EventArgs e) => _viewModel.ActiveSheet = null;

    private void OnConfirmed(object? sender, EventArgs e)
    {
        string name = NameBox.Text.Trim();
        if (name.Length == 0) return;

        _viewModel.RenameTask(_item, name);
        _viewModel.ActiveSheet = null;
    }
}
