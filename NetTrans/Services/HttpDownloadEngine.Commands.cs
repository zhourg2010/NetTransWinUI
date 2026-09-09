using Microsoft.UI.Xaml;
using NetTrans.Models;
using NetTrans.Net;
using NetTrans.Verify;
using NetTrans.ViewModels;

namespace NetTrans.Services;

/// <summary>
/// The things a person can ask of one task from the inspector: hash it, ask the
/// server whether there is a newer copy.
///
/// Both take a while and neither is part of running the queue, which is why
/// they live apart from it.
/// </summary>
public sealed partial class HttpDownloadEngine
{
    /// <summary>
    /// 校验: hash the file and say whether it is what it should be.
    ///
    /// What it should be comes from the 哈希库 first and the server second, so a
    /// file downloaded before is checked offline and instantly, and one nobody
    /// has published a digest for still gets recorded for next time.
    /// </summary>
    public async Task<string?> VerifyAsync(int id, CancellationToken cancellationToken = default)
    {
        if (Tasks.FirstOrDefault(task => task.Id == id) is not { } task) return null;

        string path = System.IO.Path.Combine(task.SavePath, task.Name);

        VerifyReport report;
        try
        {
            // Hashing a several-gigabyte file is not something to do on the UI
            // thread; FileVerifier reads it off one and only comes back here.
            report = await FileVerifier.VerifyAsync(
                path,
                task.Model.Url,
                expected: null,
                database: _settings.UseHashDatabase ? _hashes.Database : null,
                transport: _settings.CheckChecksumsOnline ? _transport : null,
                progress: null,
                cancellationToken: cancellationToken).ConfigureAwait(true);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        if (report.Outcome == VerifyOutcome.Missing) return null;

        task.Model.Sha256 = report.Sha256;
        task.Checksum = report.Status;
        task.Model.Log.Add(new LogEntry(DateTime.Now.ToString("HH:mm"), report.Detail, IsError: report.IsError));
        task.Refresh();

        _hashes.Flush();

        return report.Detail;
    }

    public async Task<bool> CheckForUpdateAsync(int id, CancellationToken cancellationToken = default)
    {
        if (Tasks.FirstOrDefault(task => task.Id == id) is not { } task) return false;

        var newer = await VersionCheck.CheckAsync(task.Model, _transport, cancellationToken).ConfigureAwait(true);
        if (newer is null) return false;

        task.NewerVersion = newer;
        task.Refresh();
        return true;
    }
}
