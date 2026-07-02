namespace NetTrans.Services;

public sealed record ClipboardUrlDetected(string Url, string Host, long? EstimatedSize);

public interface IClipboardWatcher
{
    event EventHandler<ClipboardUrlDetected>? UrlDetected;
    void Start();
    void Stop();
}
