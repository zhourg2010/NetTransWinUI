using Microsoft.UI.Xaml;
using NetTrans.Download;
using NetTrans.Models;
using NetTrans.Net;
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
    public async Task<string?> VerifyAsync(int id, CancellationToken cancellationToken = default)
    {
        if (Tasks.FirstOrDefault(task => task.Id == id) is not { } task) return null;

        string path = System.IO.Path.Combine(task.SavePath, task.Name);
        if (!System.IO.File.Exists(path)) return null;

        string hash;
        try
        {
            // Hashing a several-gigabyte file is not something to do on the UI
            // thread, and ComputeFileAsync opens the file for async reads.
            hash = await FileHash.ComputeFileAsync(path, cancellationToken: cancellationToken).ConfigureAwait(true);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        task.Model.Sha256 = hash;
        task.Checksum = FileHash.Describe(hash, expected: null);
        task.Model.Log.Add(new LogEntry(DateTime.Now.ToString("HH:mm"), $"SHA-256 {hash}"));
        task.Refresh();

        return task.Checksum;
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
