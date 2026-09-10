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

        // 大文件核对 and 深度整理 are command-line tools that happen to live in a
        // GUI app: they run, print, write their own file and exit, without ever
        // building a window. On a pool thread, so progress callbacks are not
        // posted to a UI thread that is blocked waiting for them.
        var argv = Environment.GetCommandLineArgs();

        Func<string[], int>? tool =
            Tools.BigFileCommand.Wanted(argv) ? Tools.BigFileCommand.Execute :
            Tools.TidyCommand.Wanted(argv) ? Tools.TidyCommand.Run :
            null;

        if (tool is not null)
        {
            Startup.Log("命令行工具");

            int code = Task.Run(() => Tools.ConsoleMode.Run(argv, tool)).GetAwaiter().GetResult();

            Startup.Log($"命令行工具结束，退出码 {code}");
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

        // --xamlprobe builds every control on its own instead of starting, so a
        // XAML failure names the control that threw rather than the window that
        // contained it. Sheets are included, which is where the resources are.
        if (argv.Contains("--xamlprobe"))
        {
            XamlProbe.Run(shellViewModel);
            Startup.Log("逐个构造结束");
            Environment.Exit(0);
            return;
        }

        if (!Startup.Step("建立窗口", () =>
        {
            _shell = new ShellHost(shellViewModel!);
            _shell.Start();
        })) return;

        MainAppWindow = _shell!.MainWindow;

        Startup.Log("启动完成");

        // --screens 目录: walks every state the handoff specifies and renders
        // each to a PNG, then exits. Every screen the app has, as a picture --
        // which until now only one of them was.
        int screens = Array.IndexOf(argv, "--screens");
        if (screens >= 0 && screens + 1 < argv.Length)
        {
            string directory = argv[screens + 1];

            _ = _shell.MainWindow.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await _shell.CaptureScreensAsync(directory);
                }
                catch (Exception exception)
                {
                    Startup.Log($"逐屏截图失败：{exception.GetType().Name}: {exception.Message}");
                }

                Environment.Exit(0);
            });
        }
    }
}
