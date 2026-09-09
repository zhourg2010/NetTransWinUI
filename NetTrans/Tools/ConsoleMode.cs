using System.Text;
using NetTrans.Interop;

namespace NetTrans.Tools;

/// <summary>
/// The command-line half of a GUI application.
///
/// A WinExe has no console of its own, so these tools borrow the one they were
/// launched from and open one only when there is none to borrow -- which is
/// what makes `NetTrans.exe --tidy` work from a prompt and still show something
/// when it is double-clicked.
/// </summary>
internal static class ConsoleMode
{
    public static int Run(string[] args, Func<string[], int> command)
    {
        bool allocated = Attach();

        try
        {
            return command(args);
        }
        catch (Exception failure)
        {
            Console.WriteLine($"出错了：{failure.Message}");
            return 1;
        }
        finally
        {
            // Double-clicked rather than run from a prompt: the window is ours
            // and would close with the process, taking the report with it.
            if (allocated && !args.Contains("--no-wait"))
            {
                Console.WriteLine();
                Console.WriteLine("按回车关闭。");
                Console.ReadLine();
            }
        }
    }

    /// <summary>True when it had to open a console, meaning nobody is watching a prompt.</summary>
    private static bool Attach()
    {
        bool allocated = false;

        if (!NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS))
        {
            allocated = NativeMethods.AllocConsole();
        }

        try
        {
            // Before the writer below: setting the encoding throws the cached
            // writers away, which would undo it.
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (Exception)
        {
            // A console that will not take UTF-8 still prints the numbers.
        }

        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });

        return allocated;
    }
}
