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
        var settings = settingsStore.Load();

        // `--demo` swaps the real transfers for the handoff's seed data, which
        // is how the UI is worked on without a network or real files.
        IDownloadEngine downloadEngine = Environment.GetCommandLineArgs().Contains("--demo")
            ? new StubDownloadEngine()
            : new HttpDownloadEngine(settings);

        IClipboardWatcher clipboardWatcher = new ClipboardWatcher();

        var shellViewModel = new ShellViewModel(downloadEngine, clipboardWatcher, settingsStore, settings);

        _shell = new ShellHost(shellViewModel);
        _shell.Start();

        MainAppWindow = _shell.MainWindow;
    }
}
