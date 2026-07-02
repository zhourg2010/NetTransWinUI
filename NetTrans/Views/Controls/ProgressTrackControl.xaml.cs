using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NetTrans.Models;
using Windows.UI;

namespace NetTrans.Views.Controls;

/// <summary>
/// .row__progress / .detail__progress — a fill bar sized by Grid Star-column
/// ratios (native, layout-driven "percent width") plus optional segment
/// divider hairlines drawn over the top while actively downloading.
/// </summary>
public sealed partial class ProgressTrackControl : UserControl
{
    public static readonly DependencyProperty PercentProperty =
        DependencyProperty.Register(nameof(Percent), typeof(double), typeof(ProgressTrackControl),
            new PropertyMetadata(0.0, OnAnyChanged));

    public static readonly DependencyProperty StatusProperty =
        DependencyProperty.Register(nameof(Status), typeof(DownloadStatus), typeof(ProgressTrackControl),
            new PropertyMetadata(DownloadStatus.Downloading, OnAnyChanged));

    public static readonly DependencyProperty SegmentCountProperty =
        DependencyProperty.Register(nameof(SegmentCount), typeof(int), typeof(ProgressTrackControl),
            new PropertyMetadata(0, OnSegmentsChanged));

    public double Percent
    {
        get => (double)GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    public DownloadStatus Status
    {
        get => (DownloadStatus)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    public int SegmentCount
    {
        get => (int)GetValue(SegmentCountProperty);
        set => SetValue(SegmentCountProperty, value);
    }

    public ProgressTrackControl()
    {
        InitializeComponent();
        Loaded += (_, _) => { Refresh(); RebuildSegments(); };
    }

    private static void OnAnyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ProgressTrackControl)d).Refresh();
    private static void OnSegmentsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ProgressTrackControl)d).RebuildSegments();

    private void Refresh()
    {
        var pct = System.Math.Clamp(Percent, 0, 100);
        var remainder = System.Math.Max(0.0001, 100 - pct);

        FillHost.ColumnDefinitions[0].Width = new GridLength(System.Math.Max(pct, 0.0001), GridUnitType.Star);
        FillHost.ColumnDefinitions[1].Width = new GridLength(remainder, GridUnitType.Star);

        var accent = (SolidColorBrush)Application.Current.Resources["AccentBrush"];
        Fill.Visibility = Status == DownloadStatus.Queued ? Visibility.Collapsed : Visibility.Visible;
        Fill.Background = Status switch
        {
            DownloadStatus.Paused => (Brush)Application.Current.Resources["Text3Brush"],
            DownloadStatus.Error => (Brush)Application.Current.Resources["ErrBrush"],
            _ => new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0.5),
                EndPoint = new Windows.Foundation.Point(1, 0.5),
                GradientStops =
                {
                    new GradientStop { Color = accent.Color, Offset = 0 },
                    new GradientStop { Color = Lighten(accent.Color, 0.2), Offset = 1 },
                },
            },
        };

        SegmentHost.Visibility = Status == DownloadStatus.Downloading && SegmentCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RebuildSegments()
    {
        SegmentHost.ColumnDefinitions.Clear();
        SegmentHost.Children.Clear();
        if (SegmentCount <= 0) return;

        for (int i = 0; i < SegmentCount; i++)
        {
            SegmentHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (i == SegmentCount - 1) continue;
            var divider = new Border
            {
                BorderThickness = new Thickness(0, 0, 1, 0),
                BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
            };
            Grid.SetColumn(divider, i);
            SegmentHost.Children.Add(divider);
        }
        Refresh();
    }

    private static Color Lighten(Color c, double amount)
    {
        byte Mix(byte v) => (byte)(v + (255 - v) * amount);
        return Color.FromArgb(c.A, Mix(c.R), Mix(c.G), Mix(c.B));
    }
}
