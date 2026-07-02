using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetTrans.Services;

namespace NetTrans.ViewModels;

public sealed partial class NewDownloadViewModel : ObservableObject
{
    private readonly IDownloadEngine _engine;

    [ObservableProperty]
    private string _url = "";

    [ObservableProperty]
    private string _saveAs = "";

    [ObservableProperty]
    private string _saveTo = @"C:\Users\you\Downloads";

    [ObservableProperty]
    private string _category = "apps";

    [ObservableProperty]
    private string _startOption = "now";

    [ObservableProperty]
    private bool _advancedExpanded;

    [ObservableProperty]
    private int _connections = 16;

    [ObservableProperty]
    private string _speedLimit = "off";

    [ObservableProperty]
    private string _autoExtract = "If archive";

    [ObservableProperty]
    private string _afterDownload = "Do nothing";

    [ObservableProperty]
    private string _httpReferer = "";

    public string ResolvedHint => Uri.TryCreate(Url.Split('\n').FirstOrDefault()?.Trim(), UriKind.Absolute, out var uri)
        ? $"Resolved · {uri.Host} · supports resume"
        : "Paste a URL to resolve source details";

    public event EventHandler? Started;

    public NewDownloadViewModel(IDownloadEngine engine, string prefillUrl = "")
    {
        _engine = engine;
        if (!string.IsNullOrWhiteSpace(prefillUrl))
        {
            Url = prefillUrl;
            SaveAs = Path.GetFileName(new Uri(prefillUrl).LocalPath);
        }
    }

    partial void OnUrlChanged(string value) => OnPropertyChanged(nameof(ResolvedHint));

    [RelayCommand]
    private void ToggleAdvanced() => AdvancedExpanded = !AdvancedExpanded;

    [RelayCommand]
    private void Start()
    {
        var firstUrl = Url.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstUrl)) return;

        double? speedLimit = SpeedLimit switch
        {
            "5 MB/s" => 5 * 1024 * 1024,
            "10 MB/s" => 10 * 1024 * 1024,
            "25 MB/s" => 25 * 1024 * 1024,
            _ => null,
        };

        _engine.AddDownload(new NewDownloadRequest(
            firstUrl,
            SaveAs,
            SaveTo,
            Category,
            Connections,
            speedLimit,
            StartOption));

        Started?.Invoke(this, EventArgs.Empty);
    }
}
