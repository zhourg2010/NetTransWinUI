using System.Diagnostics;
using NetTrans.Net;

namespace NetTrans.Services;

/// <summary>What 完成后扫描 actually did to a finished file.</summary>
public enum ScanVerdict
{
    /// <summary>No scanner was available, or it did not answer.</summary>
    NotScanned,

    Clean,

    /// <summary>Defender reported a detection. The file is left alone here.</summary>
    ThreatFound,
}

/// <summary>
/// 完成后扫描, in the two parts Windows actually offers.
///
/// The mark of the web is the durable half: it makes SmartScreen and Protected
/// View treat the file as untrusted from now on, and it survives being moved or
/// copied. The on-demand Defender scan is the immediate half, and only exists
/// on machines still using Defender -- a third-party suite is already watching
/// the file system and needs nothing from us.
/// </summary>
public static class FileScan
{
    /// <summary>
    /// Writes the Zone.Identifier stream. Returns false when the volume does
    /// not support alternate streams (FAT32, most network shares) -- worth
    /// reporting, not worth failing a completed download over.
    /// </summary>
    public static bool Mark(string path, string? sourceUrl)
    {
        try
        {
            File.WriteAllText(ZoneIdentifier.StreamPath(path), ZoneIdentifier.Build(sourceUrl));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Asks Defender to look at one file. Never throws: a scanner that is
    /// missing, busy or disabled is not the download's problem.
    /// </summary>
    public static async Task<ScanVerdict> ScanAsync(string path, CancellationToken cancellationToken = default)
    {
        if (Locate() is not { } scanner) return ScanVerdict.NotScanned;

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = scanner,
                // ScanType 3 is a custom scan of exactly what -File names.
                // Remediation is left off: quarantining a file the user just
                // asked for, without telling them, is not ours to do.
                Arguments = $"-Scan -ScanType 3 -File \"{path}\" -DisableRemediation",
                CreateNoWindow = true,
                UseShellExecute = false,
            });

            if (process is null) return ScanVerdict.NotScanned;

            // A scan of one file is quick, but a machine mid-update can stall
            // it indefinitely, and nothing here is worth waiting forever for.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // It exited on its own between the timeout and the kill.
                }

                return ScanVerdict.NotScanned;
            }

            // MpCmdRun answers 0 for clean and 2 for a detection. Anything else
            // is the tool failing to run, which is not a verdict either way.
            return process.ExitCode switch
            {
                0 => ScanVerdict.Clean,
                2 => ScanVerdict.ThreatFound,
                _ => ScanVerdict.NotScanned,
            };
        }
        catch (Exception)
        {
            return ScanVerdict.NotScanned;
        }
    }

    /// <summary>MpCmdRun.exe, or null on a machine that does not run Defender.</summary>
    private static string? Locate()
    {
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            string candidate = Path.Combine(Environment.GetFolderPath(folder), "Windows Defender", "MpCmdRun.exe");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>The line the log shows for a verdict.</summary>
    public static string Describe(ScanVerdict verdict) => verdict switch
    {
        ScanVerdict.Clean => "安全扫描通过",
        ScanVerdict.ThreatFound => "安全扫描发现威胁，请勿打开",
        _ => "已标记来源，未执行扫描",
    };
}
