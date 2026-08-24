using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetTrans.Services;

namespace NetTrans.Views.Controls;

/// <summary>
/// `.conn`: one row per live connection -- index, a 4px rate bar, and the rate.
/// Shows the empty-state note when a task has no connections open.
/// </summary>
public sealed class ConnectionList : StackPanel
{
    public void SetConnections(double[] speeds)
    {
        Children.Clear();

        if (speeds.Length == 0)
        {
            Children.Add(new TextBlock
            {
                Text = "当前没有活动连接。",
                Style = (Style)Application.Current.Resources["NoteTextStyle"],
                Padding = new Thickness(12, 16, 12, 16),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            return;
        }

        // The prototype scales each bar against the mean rate, times 62%.
        double mean = speeds.Average();

        for (int i = 0; i < speeds.Length; i++)
        {
            Children.Add(BuildRow(i, speeds[i], mean, showSeparator: i > 0));
        }
    }

    private static Grid BuildRow(int index, double speed, double mean, bool showSeparator)
    {
        var root = new Grid();

        if (showSeparator)
        {
            root.Children.Add(new Border
            {
                Height = 0.5,
                Margin = new Thickness(11, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Background = ThemeBrushes.Get("SepBrush"),
            });
        }

        var layout = new Grid { Padding = new Thickness(11, 7, 11, 7), ColumnSpacing = 9 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });

        var number = new TextBlock
        {
            Text = $"#{index + 1}",
            Style = (Style)Application.Current.Resources["MetaTextStyle"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        layout.Children.Add(number);

        var track = new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = ThemeBrushes.Get("Fill2Brush"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var fill = new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = ThemeBrushes.Get("BlueBrush"),
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        track.Child = fill;
        track.SizeChanged += (_, e) =>
        {
            double ratio = mean <= 0 ? 0 : Math.Min(1, speed / mean * 0.62);
            fill.Width = e.NewSize.Width * ratio;
        };

        Grid.SetColumn(track, 1);
        layout.Children.Add(track);

        var value = new TextBlock
        {
            Text = speed > 0 ? FormatHelpers.Speed(speed) : "空闲",
            Style = (Style)Application.Current.Resources["MetaTextStyle"],
            FontSize = 11.5,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(value, 2);
        layout.Children.Add(value);

        root.Children.Add(layout);
        return root;
    }
}
