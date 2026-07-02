using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetTrans.ViewModels;
using NetTrans.Views.Dialogs;

namespace NetTrans.Views;

public sealed partial class MainPage : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(ShellViewModel), typeof(MainPage),
            new PropertyMetadata(null, OnViewModelChanged));

    public ShellViewModel ViewModel
    {
        get => (ShellViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private Window? _window;

    public MainPage()
    {
        InitializeComponent();
    }

    public void AttachToWindow(Window window)
    {
        _window = window;
        window.ExtendsContentIntoTitleBar = true;
        window.SetTitleBar(TitleBar.DragRegionElement);
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var page = (MainPage)d;
        if (e.OldValue is ShellViewModel oldVm) oldVm.PropertyChanged -= page.OnShellPropertyChanged;
        if (e.NewValue is ShellViewModel newVm) newVm.PropertyChanged += page.OnShellPropertyChanged;
    }

    private bool _dialogShowing;

    private async void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.NewDownloadDialogOpen) && ViewModel.NewDownloadDialogOpen)
        {
            ViewModel.NewDownloadDialogOpen = false;
            if (_dialogShowing) return;
            _dialogShowing = true;
            try
            {
                var vm = new NewDownloadViewModel(ViewModel.Engine, ViewModel.PrefillUrl);
                var dialog = new NewDownloadDialog(vm) { XamlRoot = XamlRoot, RequestedTheme = ViewModel.AppTheme };
                await dialog.ShowAsync();
            }
            finally
            {
                _dialogShowing = false;
            }
        }
        else if (e.PropertyName == nameof(ShellViewModel.SettingsDialogOpen) && ViewModel.SettingsDialogOpen)
        {
            ViewModel.SettingsDialogOpen = false;
            if (_dialogShowing) return;
            _dialogShowing = true;
            try
            {
                var settingsVm = new SettingsViewModel(ViewModel.Settings, ViewModel.SettingsStore);
                var dialog = new SettingsDialog(ViewModel, settingsVm) { XamlRoot = XamlRoot };
                await dialog.ShowAsync();
            }
            finally
            {
                _dialogShowing = false;
            }
        }
    }

    private void OnRowSelected(object? sender, DownloadItemViewModel item)
    {
        if (ViewModel.SelectedItem is not null) ViewModel.SelectedItem.IsSelected = false;
        item.IsSelected = true;
        ViewModel.SelectedItem = item;
        ViewModel.ShowDetailPane = true;
    }
}
