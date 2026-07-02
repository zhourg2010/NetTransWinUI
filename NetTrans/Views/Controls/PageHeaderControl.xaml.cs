using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetTrans.ViewModels;

namespace NetTrans.Views.Controls;

public sealed partial class PageHeaderControl : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(ShellViewModel), typeof(PageHeaderControl), new PropertyMetadata(null));

    public ShellViewModel ViewModel
    {
        get => (ShellViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public PageHeaderControl()
    {
        InitializeComponent();
    }

    private void OnFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string filter } && ViewModel is not null)
        {
            ViewModel.StatusFilter = filter;
        }
    }
}
