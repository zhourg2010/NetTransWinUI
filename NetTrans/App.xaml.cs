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

        // 大文件核对 is a command-line tool that happens to live in a GUI app:
        // it runs, prints, writes its ledger and exits, without ever building a
        // window. On a pool thread, so the progress callbacks are not posted to
        // a UI thread that is blocked waiting for them.
        var argv = Environment.GetCommandLineArgs();
        if (Tools.BigFileCommand.Wanted(argv))
        {
            Startup.Log("大文件核对（命令行）");

            int code = Task.Run(() => Tools.BigFileCommand.Run(argv)).GetAwaiter().GetResult();

            Startup.Log($"大文件核对结束，退出码 {code}");
            Environment.Exit(code);
            return;
        }

        ISettingsStore? settingsStore = null;
        Models.AppSettings? settings = null;
        IDownloadEngine? downloadEngine = null;
        IClipboardWatcher? clipboardWatcher = null;
        ShellViewModel? shellViewModel = null;

        if (!Startup.Step("读取主题", () => ThemeBrushes.SetTheme(Current.RequestedTheme))) return;

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
