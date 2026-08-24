using Microsoft.UI.Xaml;
using NetTrans.Services;
using NetTrans.ViewModels;
using NetTrans.Shell;

namespace NetTrans;

public partial class App : Application
{
    private ShellHost? _shell;

    public static Window? MainAppWindow { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        ThemeBrushes.SetTheme(Current.RequestedTheme);

        ISettingsStore settingsStore = new JsonSettingsStore();
        IDownloadEngine downloadEngine = new StubDownloadEngine();
        IClipboardWatcher clipboardWatcher = new ClipboardWatcher();

        var shellViewModel = new ShellViewModel(downloadEngine, clipboardWatcher, settingsStore);

        _shell = new ShellHost(shellViewModel);
        _shell.Start();

        MainAppWindow = _shell.MainWindow;
    }
}
