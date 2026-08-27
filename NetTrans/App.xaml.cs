using Microsoft.UI.Xaml;
using NetTrans.Diagnostics;
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
        // Before anything else, including the XAML below: an unpackaged WinUI
        // app that throws on the way up leaves nothing behind, so the log has
        // to be open before there is anything to log.
        Startup.Install();

        UnhandledException += (_, e) =>
        {
            e.Handled = true;
            Startup.Fatal(e.Exception, "Application.UnhandledException");
        };

        Startup.Step("载入应用资源 (App.xaml)", InitializeComponent);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Startup.Log("OnLaunched");

        ISettingsStore? settingsStore = null;
        Models.AppSettings? settings = null;
        IDownloadEngine? downloadEngine = null;
        IClipboardWatcher? clipboardWatcher = null;
        ShellViewModel? shellViewModel = null;

        if (!Startup.Step("读取主题", () => ThemeBrushes.SetTheme(Current.RequestedTheme))) return;

        // --xamlprobe builds every control on its own and writes the result to
        // the log, which is how a "XAML parsing failed" with no detail gets
        // turned into the name of the control that actually threw.
        if (Environment.GetCommandLineArgs().Contains("--xamlprobe"))
        {
            XamlProbe.Run();
            Startup.Log("启动完成（探测模式）");
            Exit();
            return;
        }

        if (!Startup.Step("读取设置", () =>
        {
            settingsStore = new JsonSettingsStore();
            settings = settingsStore.Load();
        })) return;

        // `--demo` swaps the real transfers for the handoff's seed data, which
        // is how the UI is worked on without a network or real files.
        bool demo = Environment.GetCommandLineArgs().Contains("--demo");

        if (!Startup.Step(demo ? "建立示例引擎" : "建立下载引擎", () =>
        {
            downloadEngine = demo ? new StubDownloadEngine() : new HttpDownloadEngine(settings!);
        })) return;

        if (!Startup.Step("监听剪贴板", () => clipboardWatcher = new ClipboardWatcher())) return;

        if (!Startup.Step("建立视图模型", () =>
        {
            shellViewModel = new ShellViewModel(downloadEngine!, clipboardWatcher!, settingsStore!, settings!);
        })) return;

        if (!Startup.Step("建立窗口", () =>
        {
            _shell = new ShellHost(shellViewModel!);
            _shell.Start();
        })) return;

        MainAppWindow = _shell!.MainWindow;

        Startup.Log("启动完成");
    }
}
