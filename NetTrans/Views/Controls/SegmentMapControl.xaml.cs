using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace NetTrans.Views.Controls;

public sealed partial class SegmentMapControl : UserControl
{
    private const int Columns = 4;

    public static readonly DependencyProperty ValuesProperty =
        DependencyProperty.Register(nameof(Values), typeof(IReadOnlyList<double>), typeof(SegmentMapControl),
            new PropertyMetadata(null, OnValuesChanged));

    public IReadOnlyList<double> Values
    {
        get => (IReadOnlyList<double>)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public SegmentMapControl()
    {
        InitializeComponent();
    }

    private static void OnValuesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((SegmentMapControl)d).Rebuild();

    private void Rebuild()
    {
        RootGrid.Children.Clear();
        RootGrid.RowDefinitions.Clear();
        RootGrid.ColumnDefinitions.Clear();

        var values = Values;
        if (values is null || values.Count == 0) return;

        int rows = (values.Count + Columns - 1) / Columns;
        for (int c = 0; c < Columns; c++) RootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < rows; r++) RootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var accent = ((SolidColorBrush)Application.Current.Resources["AccentBrush"]).Color;

        for (int i = 0; i < values.Count; i++)
        {
            var cell = BuildCell(i + 1, values[i], accent);
            Grid.SetRow(cell, i / Columns);
            Grid.SetColumn(cell, i % Columns);
            RootGrid.Children.Add(cell);
        }
    }

    private static Border BuildCell(int number, double percent, Color accent)
    {
        percent = System.Math.Clamp(percent, 0, 100);

        var outer = new Border
        {
            Height = 26,
            CornerRadius = new CornerRadius(4),
            Background = (Brush)Application.Current.Resources["BgSubtleBrush"],
            BorderBrush = (Brush)Application.Current.Resources["Stroke2Brush"],
            BorderThickness = new Thickness(1),
        };

        var fillGrid = new Grid();
        fillGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(System.Math.Max(percent, 0.0001), GridUnitType.Star) });
        fillGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(System.Math.Max(100 - percent, 0.0001), GridUnitType.Star) });

        var fillBar = new Border
        {
            CornerRadius = new CornerRadius(4, 0, 0, 4),
            Opacity = 0.85,
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0.5),
                EndPoint = new Windows.Foundation.Point(1, 0.5),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb((byte)(accent.A * 0.7), accent.R, accent.G, accent.B), Offset = 0 },
                    new GradientStop { Color = accent, Offset = 1 },
                },
            },
        };
        Grid.SetColumn(fillBar, 0);
        fillGrid.Children.Add(fillBar);

        var numberText = new TextBlock
        {
            Text = number.ToString(),
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["Text1Brush"],
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var root = new Grid();
        root.Children.Add(fillGrid);
        root.Children.Add(numberText);
        outer.Child = root;
        return outer;
    }
}
