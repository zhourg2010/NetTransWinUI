using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using NetTrans.ViewModels;
using WinUIEx;

namespace NetTrans;

public sealed partial class MainWindow : WindowEx
{
    public MainWindow(ShellViewModel viewModel)
    {
        InitializeComponent();

        Title = "NetTrans";
        Width = 1440;
        Height = 900;
        MinWidth = 960;
        MinHeight = 600;

        ExtendsContentIntoTitleBar = true;
        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };

        RootPage.ViewModel = viewModel;
        RootPage.AttachToWindow(this);
    }
}
