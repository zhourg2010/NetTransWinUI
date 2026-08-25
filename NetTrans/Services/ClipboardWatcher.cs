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
            if (text == _lastSeen) return;

            // thunder:// and its cousins are base64 around an ordinary address,
            // and a site that publishes one is offering a download like any
            // other -- so the copied text is unwrapped before it is judged.
            string link = NetTrans.Net.PrivateLinks.Unwrap(text);

            if (!UrlPattern().IsMatch(link) && !link.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) return;

            _lastSeen = text;
            if (Uri.TryCreate(link, UriKind.Absolute, out var uri))
            {
                UrlDetected?.Invoke(this, new ClipboardUrlDetected(link, uri.IsAbsoluteUri && uri.HostNameType != UriHostNameType.Unknown ? uri.Host : "磁力链", null));
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
