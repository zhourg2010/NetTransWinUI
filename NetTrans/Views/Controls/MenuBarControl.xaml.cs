using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetTrans.ViewModels;

namespace NetTrans.Views.Controls;

public sealed partial class MenuBarControl : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(ShellViewModel), typeof(MenuBarControl), new PropertyMetadata(null));

    public ShellViewModel? ViewModel
    {
        get => (ShellViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public MenuBarControl()
    {
        InitializeComponent();
    }

    private void OnNewDownloadClick(object sender, RoutedEventArgs e) => ViewModel?.OpenNewDownloadCommand.Execute(null);

    private void OnSettingsClick(object sender, RoutedEventArgs e) => ViewModel?.OpenSettingsCommand.Execute(null);

    private void OnPauseAllClick(object sender, RoutedEventArgs e) => ViewModel?.PauseAllCommand.Execute(null);

    private void OnResumeAllClick(object sender, RoutedEventArgs e) => ViewModel?.ResumeAllCommand.Execute(null);

    private void OnExitClick(object sender, RoutedEventArgs e) => Application.Current.Exit();
}
