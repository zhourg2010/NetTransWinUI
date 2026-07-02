using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetTrans.ViewModels;

namespace NetTrans.Views.Dialogs;

public sealed partial class NewDownloadDialog : ContentDialog
{
    public NewDownloadViewModel ViewModel { get; }

    public NewDownloadDialog(NewDownloadViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        Bindings.Update();
    }

    private void OnAdvancedToggleClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ToggleAdvancedCommand.Execute(null);
        AdvancedPanel.Visibility = ViewModel.AdvancedExpanded ? Visibility.Visible : Visibility.Collapsed;
        AdvancedChevron.Data = (Microsoft.UI.Xaml.Media.Geometry)(ViewModel.AdvancedExpanded
            ? Application.Current.Resources["IconChevronDown"]
            : Application.Current.Resources["IconChevron"]);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Hide();

    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        ViewModel.StartCommand.Execute(null);
        Hide();
    }
}
