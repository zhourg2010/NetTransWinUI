using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetTrans.Services;

namespace NetTrans.Views.Controls;

/// <summary>
/// `.sched`: the inspector's 本次会话 bar chart. 34px tall, 2px gutters, blue
/// bars at .28 opacity with the most recent handful at full strength -- the
/// prototype hard-codes the last six as `.on`.
/// </summary>
public sealed class SessionBars : Grid
{
    private const int RecentCount = 6;
    private const double BarArea = 34;

    private double[] _samples = Array.Empty<double>();

    public SessionBars()
    {
        ColumnSpacing = 2;
        Padding = new Thickness(11, 10, 11, 8);
        Height = BarArea + 18;
    }

    public void SetSamples(double[] samples)
    {
        _samples = samples;

        if (Children.Count != samples.Length)
        {
            Children.Clear();
            ColumnDefinitions.Clear();
            for (int i = 0; i < samples.Length; i++)
            {
                ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var bar = new Border
                {
                    CornerRadius = new CornerRadius(1.5),
                    Background = ThemeBrushes.Get("BlueBrush"),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    MinHeight = 2,
                };

                SetColumn(bar, i);
                Children.Add(bar);
            }
        }

        double max = 1;
        foreach (double value in samples) max = Math.Max(max, value);

        for (int i = 0; i < Children.Count; i++)
        {
            if (Children[i] is not Border bar) continue;
            bar.Height = Math.Max(2, samples[i] / max * BarArea);
            bar.Opacity = i >= samples.Length - RecentCount ? 1 : 0.28;
        }
    }
}
