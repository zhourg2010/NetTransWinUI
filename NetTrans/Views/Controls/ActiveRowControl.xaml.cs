using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NetTrans.ViewModels;

namespace NetTrans.Views.Controls;

public sealed partial class ActiveRowControl : UserControl
{
    public event EventHandler<DownloadItemViewModel>? RowSelected;

    private DownloadItemViewModel? _item;

    public ActiveRowControl()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (_item is not null) _item.PropertyChanged -= OnItemPropertyChanged;
        _item = args.NewValue as DownloadItemViewModel;
        if (_item is not null) _item.PropertyChanged += OnItemPropertyChanged;
        RefreshSelectionVisual();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DownloadItemViewModel.IsSelected)) RefreshSelectionVisual();
    }

    private void RefreshSelectionVisual()
    {
        bool selected = _item?.IsSelected == true;
        RootBorder.BorderBrush = selected
            ? (Brush)Application.Current.Resources["AccentBrush"]
            : (Brush)Application.Current.Resources["StrokeCardBrush"];
        RootBorder.BorderThickness = new Thickness(selected ? 1.5 : 1);
    }

    private void OnRowTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_item is not null) RowSelected?.Invoke(this, _item);
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e) =>
        RootBorder.Background = (Brush)Application.Current.Resources["BgLayer2Brush"];

    private void OnPointerExited(object sender, PointerRoutedEventArgs e) =>
        RootBorder.Background = (Brush)Application.Current.Resources["BgLayerBrush"];
}
