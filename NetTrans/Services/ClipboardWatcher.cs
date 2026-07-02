using System.Text.RegularExpressions;
using Windows.ApplicationModel.DataTransfer;

namespace NetTrans.Services;

/// <summary>Watches Clipboard.ContentChanged for a plain-text URL, à la the PasteBar InfoBar.</summary>
public sealed partial class ClipboardWatcher : IClipboardWatcher, IDisposable
{
    public event EventHandler<ClipboardUrlDetected>? UrlDetected;

    private bool _started;
    private string? _lastSeen;

    public void Start()
    {
        if (_started) return;
        _started = true;
        Clipboard.ContentChanged += OnContentChanged;
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        Clipboard.ContentChanged -= OnContentChanged;
    }

    private async void OnContentChanged(object? sender, object e)
    {
        try
        {
            var view = Clipboard.GetContent();
            if (!view.Contains(StandardDataFormats.Text)) return;

            var text = (await view.GetTextAsync()).Trim();
            if (text == _lastSeen || !UrlPattern().IsMatch(text)) return;

            _lastSeen = text;
            if (Uri.TryCreate(text, UriKind.Absolute, out var uri))
            {
                UrlDetected?.Invoke(this, new ClipboardUrlDetected(text, uri.Host, null));
            }
        }
        catch (Exception)
        {
            // Clipboard access can throw transiently (owner busy, non-text content mid-copy) — ignore and wait for the next change.
        }
    }

    [GeneratedRegex(@"^https?://\S+\.\S+")]
    private static partial Regex UrlPattern();

    public void Dispose() => Stop();
}
