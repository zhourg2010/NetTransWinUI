using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace NetTrans.Views.Controls;

/// <summary>60s bandwidth sparkline: filled area under an accent-colored line, drawn in a fixed 100x36 box that a Viewbox scales to fit.</summary>
public sealed partial class SparklineControl : UserControl
{
    public static readonly DependencyProperty ValuesProperty =
        DependencyProperty.Register(nameof(Values), typeof(IReadOnlyList<double>), typeof(SparklineControl),
            new PropertyMetadata(null, OnValuesChanged));

    public IReadOnlyList<double> Values
    {
        get => (IReadOnlyList<double>)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public SparklineControl()
    {
        InitializeComponent();
        Loaded += (_, _) => Redraw();
    }

    private static void OnValuesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((SparklineControl)d).Redraw();

    private void Redraw()
    {
        var pts = Values;
        if (pts is null || pts.Count == 0)
        {
            FillPath.Data = null;
            StrokePath.Data = null;
            return;
        }

        const double w = 100, h = 36;
        double max = 1;
        foreach (var v in pts) max = System.Math.Max(max, v);
        max *= 1.15; // headroom, like the reference's fixed max=50 vs ~44 peak

        double stepX = pts.Count > 1 ? w / (pts.Count - 1) : 0;

        var figure = new PathFigure { StartPoint = new Point(0, h - pts[0] / max * h), IsClosed = false };
        var lineSegments = new PathSegmentCollection();
        for (int i = 1; i < pts.Count; i++)
        {
            lineSegments.Add(new LineSegment { Point = new Point(i * stepX, h - pts[i] / max * h) });
        }
        figure.Segments = lineSegments;

        var strokeGeometry = new PathGeometry();
        strokeGeometry.Figures.Add(figure);
        StrokePath.Data = strokeGeometry;
        StrokePath.Stroke = (Brush)Application.Current.Resources["AccentBrush"];

        var fillFigure = new PathFigure { StartPoint = new Point(0, h - pts[0] / max * h), IsClosed = true };
        var fillSegments = new PathSegmentCollection();
        for (int i = 1; i < pts.Count; i++)
        {
            fillSegments.Add(new LineSegment { Point = new Point(i * stepX, h - pts[i] / max * h) });
        }
        fillSegments.Add(new LineSegment { Point = new Point(w, h) });
        fillSegments.Add(new LineSegment { Point = new Point(0, h) });
        fillFigure.Segments = fillSegments;

        var fillGeometry = new PathGeometry();
        fillGeometry.Figures.Add(fillFigure);
        FillPath.Data = fillGeometry;

        var accent = ((SolidColorBrush)Application.Current.Resources["AccentBrush"]).Color;
        FillPath.Fill = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop { Color = Windows.UI.Color.FromArgb(90, accent.R, accent.G, accent.B), Offset = 0 },
                new GradientStop { Color = Windows.UI.Color.FromArgb(0, accent.R, accent.G, accent.B), Offset = 1 },
            },
        };
    }
}
