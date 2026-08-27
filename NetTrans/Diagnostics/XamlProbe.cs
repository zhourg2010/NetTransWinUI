using System.Reflection;
using Microsoft.UI.Xaml;

namespace NetTrans.Diagnostics;

/// <summary>
/// Builds every control in the app one at a time, so a XAML failure names
/// itself.
///
/// A window's XAML load fails as one event: "XAML parsing failed" against
/// MainShell, with no line, no element and no inner exception -- because the
/// thing that actually threw was some control MainShell contains, several
/// levels down. Constructing them individually turns one useless message into
/// a list with exactly one line marked FAILED, and that line has the real
/// exception on it.
///
/// Run with --xamlprobe.
/// </summary>
public static class XamlProbe
{
    public static void Run()
    {
        Startup.Log("── XAML 逐个构造 ──");

        var types = typeof(XamlProbe).Assembly
            .GetTypes()
            .Where(type => !type.IsAbstract && typeof(FrameworkElement).IsAssignableFrom(type))
            .Where(type => type.GetConstructor(Type.EmptyTypes) is not null)
            .OrderBy(type => Depth(type))
            .ThenBy(type => type.FullName, StringComparer.Ordinal)
            .ToList();

        int failed = 0;

        foreach (var type in types)
        {
            try
            {
                _ = Activator.CreateInstance(type);
                Startup.Log($"  ok     {type.FullName}");
            }
            catch (Exception exception)
            {
                failed++;

                var real = Unwrap(exception);

                Startup.Log($"  FAILED {type.FullName}");
                Startup.Log($"         {real.GetType().FullName}: {real.Message}");
                Startup.Log($"         HRESULT 0x{real.HResult:X8}");

                if (real.StackTrace is { } stack) Startup.Log(stack);
            }
        }

        Startup.Log($"── {types.Count} 个控件，{failed} 个失败 ──");
    }

    /// <summary>
    /// Least-nested first: a control that contains others fails because of
    /// them, so the leaves are the interesting part of the list and should be
    /// reached before anything swallows them.
    /// </summary>
    private static int Depth(Type type) => type.FullName?.Count(c => c == '.') ?? 0;

    private static Exception Unwrap(Exception exception) =>
        exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
}
