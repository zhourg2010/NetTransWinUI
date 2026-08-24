using System.Diagnostics;

namespace NetTrans.Services;

/// <summary>
/// 打开文件 / 在文件夹中显示 / 打开文件夹. Every one of these hands off to the
/// shell, and every one of them can fail for reasons outside the app (the file
/// was moved, the folder is on a disconnected drive), so they report success
/// rather than throwing into a click handler.
/// </summary>
public static class FileActions
{
    /// <summary>Opens a file with whatever is registered for it.</summary>
    public static bool Open(string path)
    {
        if (!File.Exists(path)) return false;

        return Launch(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>Opens the containing folder with the file selected.</summary>
    public static bool Reveal(string path)
    {
        if (File.Exists(path))
        {
            // The quotes matter: paths with spaces are otherwise split into
            // several arguments and Explorer opens the wrong folder.
            return Launch(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }

        string? folder = Path.GetDirectoryName(path);
        return folder is not null && OpenFolder(folder);
    }

    /// <summary>Opens a folder.</summary>
    public static bool OpenFolder(string path)
    {
        if (!Directory.Exists(path)) return false;

        return Launch(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    /// <summary>Renames a file on disk, keeping it in the same folder.</summary>
    public static bool Rename(string path, string newName, out string newPath)
    {
        newPath = path;

        string? folder = Path.GetDirectoryName(path);
        if (folder is null) return false;

        string target = Path.Combine(folder, newName);
        if (string.Equals(target, path, StringComparison.OrdinalIgnoreCase)) return true;
        if (File.Exists(target)) return false;

        try
        {
            if (File.Exists(path)) File.Move(path, target);
            newPath = target;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Strips what Windows will not accept in a file name.</summary>
    public static string Sanitise(string name)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return name.Trim();
    }

    private static bool Launch(ProcessStartInfo start)
    {
        try
        {
            Process.Start(start);
            return true;
        }
        catch (Exception)
        {
            // No handler registered, shell refused, drive gone -- the caller
            // reports it in the toast lane rather than crashing.
            return false;
        }
    }
}
