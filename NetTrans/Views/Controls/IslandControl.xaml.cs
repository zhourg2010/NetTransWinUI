using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace NetTrans.Views.Controls;

/// <summary>
/// The 灵动岛: aggregate ring, total throughput, and -- once the pointer is over
/// it -- the upload/average line plus a 26-bar sparkline.
/// </summary>
public sealed partial class IslandControl : UserControl
{
    private const double Radius = 8;
    private static readonly double DashUnits = 2 * Math.PI * Radius / 3;

    /// <summary>Raised when the pointer enters or leaves, so the window can resize.</summary>
    public event EventHandler<bool>? ExpandedChanged;

    /// <summary>Raised on click -- the island carries the tray menu in this build.</summary>
    public event EventHandler? MenuRequested;

    public bool IsExpanded { get; private set; }

    public IslandControl()
    {
        InitializeComponent();
        Arc.StrokeDashArray = new DoubleCollection { DashUnits, DashUnits };
    }

    public void Update(double fraction, string value, string unit, string subtitle, IReadOnlyList<double> history)
    {
        Arc.StrokeDashOffset = DashUnits * (1 - Math.Clamp(fraction, 0, 1));
        SpeedValue.Text = value;
        SpeedUnit.Text = unit;
        Subtitle.Text = subtitle;

        if (!IsExpanded) return;
        DrawSparkline(history);
    }

    private void DrawSparkline(IReadOnlyList<double> history)
    {
        if (Spark.Children.Count != history.Count)
        {
            Spark.Children.Clear();
            Spark.ColumnDefinitions.Clear();

            for (int i = 0; i < history.Count; i++)
            {
                Spark.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var bar = new Border
                {
                    Background = new SolidColorBrush(Microsoft.UI.Colors.White),
                    CornerRadius = new CornerRadius(1),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    MinHeight = 2,
                };

                Grid.SetColumn(bar, i);
                Spark.Children.Add(bar);
            }
        }

        // The prototype floors the scale at 700 KB/s so a quiet moment does not
        // blow every bar up to full height.
        double max = 700 * 1024;
        foreach (double value in history) max = Math.Max(max, value);

        for (int i = 0; i < Spark.Children.Count; i++)
        {
            if (Spark.Children[i] is not Border bar) continue;
            bar.Height = Math.Max(2, history[i] / max * 26);
            bar.Opacity = 0.3 + i / (double)history.Count * 0.7;
        }
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e) => SetExpanded(true);

    private void OnPointerExited(object sender, PointerRoutedEventArgs e) => SetExpanded(false);

    private void SetExpanded(bool expanded)
    {
        if (IsExpanded == expanded) return;
        IsExpanded = expanded;

        Subtitle.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        Spark.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        Layout.Padding = new Thickness(expanded ? 16 : 14, 0, expanded ? 16 : 14, 0);

        ExpandedChanged?.Invoke(this, expanded);
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        MenuRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }
}
