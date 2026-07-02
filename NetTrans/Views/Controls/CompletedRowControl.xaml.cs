using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NetTrans.ViewModels;

namespace NetTrans.Views.Controls;

public sealed partial class CompletedRowControl : UserControl
{
    private DownloadItemViewModel? _item;

    public CompletedRowControl()
    {
        InitializeComponent();
        DataContextChanged += (_, args) => _item = args.NewValue as DownloadItemViewModel;
    }

    private void OnRowTapped(object sender, TappedRoutedEventArgs e) { }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e) =>
        RootBorder.Background = (Brush)Application.Current.Resources["BgLayer2Brush"];

    private void OnPointerExited(object sender, PointerRoutedEventArgs e) =>
        RootBorder.Background = (Brush)Application.Current.Resources["BgLayerBrush"];
}
