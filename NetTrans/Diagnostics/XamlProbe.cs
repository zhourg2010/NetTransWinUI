using System.Reflection;
using Microsoft.UI.Xaml;
using NetTrans.ViewModels;

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
    /// <param name="viewModel">
    /// The demo shell, when there is one. Sheets take it as their only
    /// constructor argument, so without it every sheet -- the half of the app
    /// most likely to reference a resource that is not there -- would be
    /// skipped by the one check that would have caught it.
    /// </param>
    public static void Run(ShellViewModel? viewModel = null)
    {
        Startup.Log("── XAML 逐个构造 ──");

        var available = new Dictionary<Type, object?>
        {
            [typeof(ShellViewModel)] = viewModel,
            [typeof(DownloadItemViewModel)] = viewModel?.VisibleTasks.FirstOrDefault(),
        };

        var types = typeof(XamlProbe).Assembly
            .GetTypes()
            .Where(type => !type.IsAbstract && typeof(FrameworkElement).IsAssignableFrom(type))
            .Select(type => (Type: type, Arguments: Arguments(type, available)))
            .Where(entry => entry.Arguments is not null)
            .OrderBy(entry => Depth(entry.Type))
            .ThenBy(entry => entry.Type.FullName, StringComparer.Ordinal)
            .ToList();

        int failed = 0;

        foreach (var (type, arguments) in types)
        {
            try
            {
                _ = Activator.CreateInstance(type, arguments!);
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
    /// What to pass this type's constructor, or null when nothing here can
    /// satisfy it. A parameterless constructor answers with an empty array.
    /// </summary>
    private static object?[]? Arguments(Type type, IReadOnlyDictionary<Type, object?> available)
    {
        foreach (var constructor in type.GetConstructors().OrderBy(c => c.GetParameters().Length))
        {
            var parameters = constructor.GetParameters();

            if (parameters.All(parameter => available.GetValueOrDefault(parameter.ParameterType) is not null))
            {
                return parameters.Select(parameter => available[parameter.ParameterType]).ToArray();
            }
        }

        return null;
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
