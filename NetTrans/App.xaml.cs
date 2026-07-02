using Microsoft.UI.Xaml;
using NetTrans.Services;
using NetTrans.ViewModels;

namespace NetTrans;

public partial class App : Application
{
    public static Window? MainAppWindow { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        ISettingsStore settingsStore = new JsonSettingsStore();
        IDownloadEngine downloadEngine = new StubDownloadEngine();
        IClipboardWatcher clipboardWatcher = new ClipboardWatcher();

        var shellViewModel = new ShellViewModel(downloadEngine, clipboardWatcher, settingsStore);

        var window = new MainWindow(shellViewModel);
        MainAppWindow = window;
        window.Activate();
    }
}
