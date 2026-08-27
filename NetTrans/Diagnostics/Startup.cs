using System.Runtime.ExceptionServices;
using System.Text;
using NetTrans.Interop;

namespace NetTrans.Diagnostics;

/// <summary>
/// Why the app is not on screen.
///
/// An unpackaged WinUI 3 app that throws before its first window is shown dies
/// without a dialog, without an entry anywhere obvious, and without an exit
/// code worth reading. That is a terrible way to hand something to someone:
/// "it does not start" is all either of us gets.
///
/// So every startup step is written to a file as it happens, and anything
/// unhandled -- on any thread, at any point -- is appended with its full stack
/// and put on screen in a plain Win32 message box, which needs no XAML and
/// works when the XAML is exactly what is broken.
/// </summary>
public static class Startup
{
    private static readonly object Gate = new();
    private static readonly DateTime Started = DateTime.Now;

    private static string? _path;
    private static bool _reported;

    /// <summary>Where the log is being written, once <see cref="Install"/> has run.</summary>
    public static string? LogPath => _path;

    /// <summary>
    /// Starts the log and catches everything that would otherwise be silent.
    /// Called first thing in the App constructor, before any XAML is touched.
    /// </summary>
    public static void Install()
    {
        _path = Resolve();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Fatal(e.ExceptionObject as Exception, "AppDomain.UnhandledException");

        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // Not fatal on its own: a task nobody awaited failing is a bug, but
            // the app keeps running, so this is recorded and not shown.
            Log($"unobserved task exception: {Describe(e.Exception)}");
            e.SetObserved();
        };

        Log($"NetTrans starting · {Environment.OSVersion} · {(Environment.Is64BitProcess ? "x64" : "x86")}");
        Log($"exe: {Environment.ProcessPath}");
        Log($"framework: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        Log($"args: {string.Join(' ', Environment.GetCommandLineArgs().Skip(1))}");
    }

    /// <summary>One line in the log, with how long the app has been alive.</summary>
    public static void Log(string message)
    {
        string line = $"[{(DateTime.Now - Started).TotalMilliseconds,8:0} ms] {message}";

        try
        {
            lock (Gate)
            {
                if (_path is not null) File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // A log that cannot be written must not be the thing that stops the
            // app; the message box below is the fallback.
        }

        System.Diagnostics.Debug.WriteLine(line);
    }

    /// <summary>Runs a startup step, logging it either side and reporting a failure properly.</summary>
    public static bool Step(string what, Action step)
    {
        Log($"{what}…");

        try
        {
            step();
            Log($"{what} ok");
            return true;
        }
        catch (Exception exception)
        {
            Fatal(exception, what);
            return false;
        }
    }

    /// <summary>
    /// Records an exception and shows it. Only the first is shown: a failure
    /// during startup usually produces several, and the first is the one that
    /// says something.
    /// </summary>
    public static void Fatal(Exception? exception, string where)
    {
        Log($"FAILED during {where}: {Describe(exception)}");

        lock (Gate)
        {
            if (_reported) return;
            _reported = true;
        }

        string body =
            $"NetTrans 启动失败。\n\n" +
            $"位置：{where}\n\n" +
            $"{Describe(exception)}\n\n" +
            $"完整日志：{_path}";

        try
        {
            NativeMethods.MessageBox(0, body, "NetTrans", NativeMethods.MB_ICONERROR | NativeMethods.MB_SETFOREGROUND);
        }
        catch (Exception)
        {
            // Nothing left to try.
        }
    }

    /// <summary>
    /// The first-chance hook is here for the one failure mode that leaves no
    /// other trace: an exception thrown across the XAML boundary, which WinUI
    /// swallows into a process exit. Only the message is kept -- first-chance
    /// exceptions are normal and this must stay cheap.
    /// </summary>
    private static void OnFirstChance(object? sender, FirstChanceExceptionEventArgs e)
    {
        if (_reported) return;

        Log($"first chance: {e.Exception.GetType().Name}: {e.Exception.Message}");
    }

    private static string Describe(Exception? exception)
    {
        if (exception is null) return "(no exception object)";

        var text = new StringBuilder();

        for (var current = exception; current is not null; current = current.InnerException)
        {
            text.AppendLine($"{current.GetType().FullName}: {current.Message}");

            if (current.StackTrace is { } stack) text.AppendLine(stack);

            if (current.InnerException is not null) text.AppendLine("--- caused by ---");
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// Next to the executable, which is where a portable app's user looks
    /// first; LocalAppData when that directory is read-only.
    /// </summary>
    private static string Resolve()
    {
        const string name = "NetTrans.startup.log";

        try
        {
            string beside = Path.Combine(AppContext.BaseDirectory, name);
            File.WriteAllText(beside, "");
            return beside;
        }
        catch (Exception)
        {
            try
            {
                string directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NetTrans");

                Directory.CreateDirectory(directory);

                string fallback = Path.Combine(directory, name);
                File.WriteAllText(fallback, "");
                return fallback;
            }
            catch (Exception)
            {
                return Path.Combine(Path.GetTempPath(), name);
            }
        }
    }
}
