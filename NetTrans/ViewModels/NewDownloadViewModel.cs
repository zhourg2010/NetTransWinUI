using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetTrans.Services;

namespace NetTrans.ViewModels;

public sealed partial class NewDownloadViewModel : ObservableObject
{
    private readonly IDownloadEngine _engine;

    [ObservableProperty]
    public partial string Url { get; set; } = "";

    [ObservableProperty]
    public partial string SaveAs { get; set; } = "";

    [ObservableProperty]
    public partial string SaveTo { get; set; } = @"C:\Users\you\Downloads";

    [ObservableProperty]
    public partial string Category { get; set; } = "apps";

    [ObservableProperty]
    public partial string StartOption { get; set; } = "now";

    [ObservableProperty]
    public partial bool AdvancedExpanded { get; set; }

    [ObservableProperty]
    public partial int Connections { get; set; } = 16;

    [ObservableProperty]
    public partial string SpeedLimit { get; set; } = "off";

    [ObservableProperty]
    public partial string AutoExtract { get; set; } = "If archive";

    [ObservableProperty]
    public partial string AfterDownload { get; set; } = "Do nothing";

    [ObservableProperty]
    public partial string HttpReferer { get; set; } = "";

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
            speedLimit));

        Started?.Invoke(this, EventArgs.Empty);
    }
}
