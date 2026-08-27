using Microsoft.UI.Xaml;
using NetTrans.Download;
using NetTrans.Models;
using NetTrans.Net;
using NetTrans.ViewModels;

namespace NetTrans.Services;

/// <summary>
/// What happens to a task once its bytes have arrived: the completion banner,
/// 完成后校验, 完成后扫描, and the mark-of-the-web.
///
/// Split out of the engine proper because none of it is about running a queue.
/// </summary>
public sealed partial class HttpDownloadEngine
{
    private void OnCoreCompleted(object? sender, DownloadItem item) =>
        _dispatcher.TryEnqueue(async () =>
        {
            if (Tasks.FirstOrDefault(task => task.Id == item.Id) is not { } task) return;

            task.Refresh();
            Completed?.Invoke(this, task);

            // 完成后校验 SHA-256, then see whether the server has moved on. Both
            // are after the fact, so a failure in either must not disturb a
            // transfer that already succeeded.
            try
            {
                if (_settings.VerifyChecksums) await VerifyAsync(item.Id);
                if (_settings.ScanOnCompletion) await ScanAsync(task);
                await CheckForUpdateAsync(item.Id);
            }
            catch (Exception)
            {
                // Housekeeping; the download itself is done.
            }
        });

    /// <summary>
    /// 完成后扫描: mark the file as web-sourced, then let Defender look at it if
    /// this machine has one. Both halves are best-effort by construction.
    /// </summary>
    private static async Task ScanAsync(DownloadItemViewModel task)
    {
        string path = System.IO.Path.Combine(task.SavePath, task.Name);
        if (!System.IO.File.Exists(path)) return;

        bool marked = FileScan.Mark(path, task.Model.Url);

        var verdict = await FileScan.ScanAsync(path).ConfigureAwait(true);

        task.Model.Log.Add(new LogEntry(
            DateTime.Now.ToString("HH:mm"),
            marked ? FileScan.Describe(verdict) : "无法写入来源标记（分区不支持）",
            IsError: verdict == ScanVerdict.ThreatFound));

        task.Refresh();
    }
}
