namespace NetTrans.Services;

/// <summary>
/// Where NetTrans keeps its own files.
///
/// Portable by default: the 设置 sheet promises everything lands next to the
/// executable and nothing is written to the registry. If that directory is
/// read-only -- Program Files, a mounted image, a locked-down share -- the same
/// files go to LocalAppData rather than being lost.
///
/// The probe runs once. Every store asking separately would write a probe file
/// beside the executable each time one is constructed.
/// </summary>
public static class PortableStorage
{
    private static readonly Lazy<bool> BesideExeIsWritable = new(() => IsWritable(AppContext.BaseDirectory));

    /// <summary>True when files are being kept next to the executable.</summary>
    public static bool IsPortable => BesideExeIsWritable.Value;

    /// <param name="besideExe">File name to use next to the executable, e.g. netX.settings.json.</param>
    /// <param name="inAppData">File name to use under LocalAppData\NetTrans, where the prefix is redundant.</param>
    public static string PathFor(string besideExe, string inAppData)
    {
        if (BesideExeIsWritable.Value) return Path.Combine(AppContext.BaseDirectory, besideExe);

        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "netX");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, inAppData);
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, ".nettrans-write-probe");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
