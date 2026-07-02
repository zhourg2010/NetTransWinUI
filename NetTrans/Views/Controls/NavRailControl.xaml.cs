using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetTrans.ViewModels;

namespace NetTrans.Views.Controls;

public sealed partial class NavRailControl : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(ShellViewModel), typeof(NavRailControl), new PropertyMetadata(null));

    public ShellViewModel ViewModel
    {
        get => (ShellViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public NavRailControl()
    {
        InitializeComponent();
    }

    private void OnSectionClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string section })
        {
            ViewModel?.SetSectionCommand.Execute(section);
        }
    }
}
