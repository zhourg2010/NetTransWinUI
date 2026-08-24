namespace NetTrans.Services;

/// <summary>
/// Watches for the moment the queue empties, so 全部完成后 fires once per batch
/// rather than once per finished file or once per tick.
///
/// It has to be edge-triggered: an idle queue is the normal state of the app,
/// and shutting the machine down because nothing happens to be running is not
/// what the setting means.
/// </summary>
public sealed class QueueDrain
{
    private bool _busy;

    /// <summary>Whether anything has run since the last drain.</summary>
    public bool IsArmed => _busy;

    /// <summary>
    /// Fed the number of transfers still running or waiting to. True exactly
    /// once per batch, on the call where that number reaches zero having been
    /// above it.
    /// </summary>
    public bool Drained(int runningOrQueued)
    {
        if (runningOrQueued > 0)
        {
            _busy = true;
            return false;
        }

        if (!_busy) return false;

        _busy = false;
        return true;
    }

    /// <summary>
    /// Forgets that anything was running, so the current batch will not fire.
    /// Used when the user cancels the pending action -- 取消 means this batch,
    /// not the setting.
    /// </summary>
    public void Disarm() => _busy = false;
}
