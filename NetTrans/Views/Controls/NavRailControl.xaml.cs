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
        if (sender is FrameworkElement { Tag: string section } && ViewModel is not null)
        {
            // Switching sections drops any category filter so the page shows everything again.
            ViewModel.CategoryFilter = "";
            foreach (var cat in new[] { CatApps, CatVideo, CatMusic, CatDocs, CatArchives }) cat.IsChecked = false;
            ViewModel.SetSectionCommand.Execute(section);
        }
    }

    private void OnCategoryClick(object sender, RoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.RadioButton { Tag: string category } radio && ViewModel is not null)
        {
            if (ViewModel.CategoryFilter == category)
            {
                // Clicking the already-active category clears the filter.
                radio.IsChecked = false;
                ViewModel.CategoryFilter = "";
            }
            else
            {
                ViewModel.CategoryFilter = category;
            }
        }
    }
}
