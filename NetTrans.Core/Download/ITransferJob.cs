using NetTrans.Models;

namespace NetTrans.Download;

/// <summary>
/// One running transfer, whatever shape it has: a ranged HTTP file or a
/// playlist of segments. The queue drives both through this, so concurrency,
/// pausing and the speed readouts are written once.
/// </summary>
public interface ITransferJob
{
    /// <summary>The task being transferred. Mutated on the thread pool; read on the UI tick.</summary>
    DownloadItem Item { get; }

    /// <summary>Where the bytes are being written. Null until the transfer has worked it out.</summary>
    string? TargetPath { get; }

    /// <summary>Per-task cap in bytes per second; zero or less means 不限.</summary>
    double SpeedLimit { get; set; }

    /// <summary>The cap the transfer is enforcing right now.</summary>
    double EffectiveSpeedLimit { get; }

    double BytesPerSecond { get; }

    /// <summary>One rate per live connection, for the inspector's 连接 tab.</summary>
    double[] ConnectionSpeeds { get; }

    Task<JobOutcome> RunAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Asks the transfer to stop at the next boundary and keep its progress.
    /// Safe to call before <see cref="RunAsync"/> has started.
    /// </summary>
    void Pause();
}
