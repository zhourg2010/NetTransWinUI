using System.Collections.ObjectModel;
using NetTrans.ViewModels;

namespace NetTrans.Services;

public sealed record NewDownloadRequest(
    string Url,
    string SaveAs,
    string SaveTo,
    string Category,
    int Connections,
    double? SpeedLimitBytesPerSecond);

public interface IDownloadEngine
{
    ObservableCollection<DownloadItemViewModel> ActiveDownloads { get; }
    ObservableCollection<DownloadItemViewModel> CompletedDownloads { get; }

    bool IsThrottled { get; set; }
    double TotalSpeed { get; }
    long BytesTransferredThisMonth { get; }

    void Pause(int id);
    void Resume(int id);
    void Retry(int id);
    void Remove(int id);
    void PauseAll();
    void ResumeAll();

    DownloadItemViewModel AddDownload(NewDownloadRequest request);
}
