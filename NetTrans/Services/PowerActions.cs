using System.Diagnostics;
using Microsoft.UI.Xaml;
using NetTrans.Interop;

namespace NetTrans.Services;

/// <summary>
/// Carries out 全部完成后. Each of these ends the session one way or another, so
/// none of them is called without the countdown the shell shows first.
/// </summary>
public static class PowerActions
{
    /// <summary>
    /// Runs the action. Returns false when the system refused -- a policy that
    /// forbids shutdown, or a machine with sleep disabled -- so the caller can
    /// say so rather than leaving the user staring at a window that was
    /// supposed to be gone.
    /// </summary>
    public static bool Run(CompletionAction action) => action switch
    {
        CompletionAction.Quit => Quit(),
        CompletionAction.Sleep => Sleep(),
        CompletionAction.Shutdown => Shutdown(),
        _ => true,
    };

    private static bool Quit()
    {
        Application.Current.Exit();
        return true;
    }

    private static bool Sleep()
    {
        try
        {
            // Not forced: an application that objects gets its say, which is the
            // polite behaviour for something the user set hours ago.
            return NativeMethods.SetSuspendState(hibernate: false, forceCritical: false, disableWakeEvent: false);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool Shutdown()
    {
        try
        {
            // shutdown.exe rather than ExitWindowsEx: it asks for the privilege
            // itself, honours group policy, and gives other applications the
            // usual chance to save. The delay is the OS's own last word.
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = "/s /t 30 /c \"NetTrans 下载已全部完成\"",
                CreateNoWindow = true,
                UseShellExecute = false,
            });

            return process is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
