using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetTrans.ViewModels;

namespace NetTrans.Views.Controls;

public sealed partial class PasteInfoBarControl : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(ShellViewModel), typeof(PasteInfoBarControl), new PropertyMetadata(null));

    public ShellViewModel ViewModel
    {
        get => (ShellViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public PasteInfoBarControl()
    {
        InitializeComponent();
    }
}
