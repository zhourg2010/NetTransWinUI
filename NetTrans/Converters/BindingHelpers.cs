using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using NetTrans.Models;
using Windows.UI;

namespace NetTrans.Converters;

/// <summary>Static x:Bind function-binding helpers (e.g. {x:Bind conv:BindingHelpers.Visible(IsError)}).</summary>
public static class BindingHelpers
{
    /// <summary>Parses "#RRGGBB" (a leading '#' is optional). Shared by ShellViewModel.ApplyAccent and SettingsDialog's swatch buttons.</summary>
    public static Color ColorFromHex(string hex)
    {
        hex = hex.TrimStart('#');
        byte r = Convert.ToByte(hex[..2], 16);
        byte g = Convert.ToByte(hex[2..4], 16);
        byte b = Convert.ToByte(hex[4..6], 16);
        return Color.FromArgb(255, r, g, b);
    }

    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility VisibleIfEqual(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility VisibleIfNotNull(object? value) => value is null ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility VisibleIfNull(object? value) => value is null ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility VisibleIfGreaterThanZero(int value) => value > 0 ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility VisibleIfZero(int value) => value == 0 ? Visibility.Visible : Visibility.Collapsed;

    public static bool Equal(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    public static bool Not(bool value) => !value;

    public static string KindLabel(FileKind kind) => kind switch
    {
        FileKind.Iso => "ISO",
        FileKind.Doc => "PDF",
        FileKind.Video => "MKV",
        FileKind.App => "APP",
        FileKind.Zip => "ZIP",
        FileKind.Music => "FLAC",
        FileKind.Image => "IMG",
        _ => "FILE",
    };

    public static SolidColorBrush KindBrush(FileKind kind) => new(KindColor(kind));

    public static Color KindColor(FileKind kind) => kind switch
    {
        FileKind.Iso => ((SolidColorBrush)Application.Current.Resources["AccentBrush"]).Color,
        FileKind.Doc => Color.FromArgb(255, 0xC4, 0x2B, 0x1C),
        FileKind.Video => Color.FromArgb(255, 0x87, 0x64, 0xB8),
        FileKind.App => Color.FromArgb(255, 0x0F, 0x7B, 0x0F),
        FileKind.Zip => Color.FromArgb(255, 0xCA, 0x50, 0x10),
        FileKind.Music => Color.FromArgb(255, 0x03, 0x83, 0x87),
        FileKind.Image => Color.FromArgb(255, 0xB1, 0x46, 0xC2),
        _ => Colors.Gray,
    };

    public static Geometry KindIcon(FileKind kind)
    {
        var key = kind switch
        {
            FileKind.Iso => "IconArchive",
            FileKind.Doc => "IconDoc",
            FileKind.Video => "IconVideo",
            FileKind.App => "IconApp",
            FileKind.Zip => "IconFileZip",
            FileKind.Music => "IconMusic",
            FileKind.Image => "IconImage",
            _ => "IconDoc",
        };
        return (Geometry)Application.Current.Resources[key];
    }

    public static string StatusLabel(DownloadStatus status) => status switch
    {
        DownloadStatus.Downloading => "Downloading",
        DownloadStatus.Queued => "Queued",
        DownloadStatus.Paused => "Paused",
        DownloadStatus.Error => "Error",
        DownloadStatus.Completed => "Completed",
        _ => "",
    };

    public static Brush StatusBrush(DownloadStatus status)
    {
        var res = Application.Current.Resources;
        return status switch
        {
            DownloadStatus.Downloading => (Brush)res["AccentBrush"],
            DownloadStatus.Error => (Brush)res["ErrBrush"],
            DownloadStatus.Completed => (Brush)res["OkBrush"],
            DownloadStatus.Paused => (Brush)res["Text2Brush"],
            _ => (Brush)res["Text3Brush"],
        };
    }

    /// <summary>0-100 percent -> a Star GridLength, so a two-column Grid renders a native, layout-driven fill bar.</summary>
    public static GridLength PercentStar(double percent) => new(Math.Clamp(percent, 0, 100), GridUnitType.Star);
    public static GridLength RemainderStar(double percent) => new(100 - Math.Clamp(percent, 0, 100), GridUnitType.Star);

    public static string FormatConnections(int active, int max) => $"{active} / {max}";
}
