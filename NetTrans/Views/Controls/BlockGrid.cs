using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using NetTrans.Services;

namespace NetTrans.Views.Controls;

/// <summary>
/// `.blocks`: the 96-cell chunk map, 16 columns, 2.5px gutters, 1.5px radius.
/// Complete cells are --blue; the cells currently in flight are --green and
/// breathe on the CSS's 1.1s pulse.
/// </summary>
public sealed class BlockGrid : Grid
{
    private const int Columns = 16;
    private const double Gap = 2.5;

    private int[] _blocks = Array.Empty<int>();

    public BlockGrid()
    {
        ColumnSpacing = Gap;
        RowSpacing = Gap;
        Padding = new Thickness(11);
        SizeChanged += (_, _) => ApplyHeight();
    }

    public void SetBlocks(int[] blocks)
    {
        bool rebuild = blocks.Length != _blocks.Length || Children.Count == 0;
        _blocks = blocks;

        if (rebuild) Rebuild();
        else Repaint();
    }

    private void Rebuild()
    {
        Children.Clear();
        ColumnDefinitions.Clear();
        RowDefinitions.Clear();

        if (_blocks.Length == 0) return;

        int rows = (int)Math.Ceiling(_blocks.Length / (double)Columns);
        for (int i = 0; i < Columns; i++) ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < rows; i++) RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        for (int i = 0; i < _blocks.Length; i++)
        {
            var cell = new Border { CornerRadius = new CornerRadius(1.5) };
            SetColumn(cell, i % Columns);
            SetRow(cell, i / Columns);
            Children.Add(cell);
        }

        Repaint();
        ApplyHeight();
    }

    private void Repaint()
    {
        for (int i = 0; i < Children.Count && i < _blocks.Length; i++)
        {
            if (Children[i] is not Border cell) continue;

            cell.Background = ThemeBrushes.Get(_blocks[i] switch
            {
                1 => "BlueBrush",
                2 => "GreenBrush",
                _ => "Fill2Brush",
            });

            if (_blocks[i] == 2) StartPulse(cell);
            else StopPulse(cell);
        }
    }

    /// <summary>`@keyframes pulse`: 1 -> .42 -> 1 over 1.1s, forever.</summary>
    private static void StartPulse(Border cell)
    {
        if (cell.Tag is Storyboard) return;

        var frames = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        frames.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), Value = 1 });
        frames.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(550)),
            Value = 0.42,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
        frames.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1100)),
            Value = 1,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });

        Storyboard.SetTarget(frames, cell);
        Storyboard.SetTargetProperty(frames, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(frames);
        cell.Tag = storyboard;
        storyboard.Begin();
    }

    private static void StopPulse(Border cell)
    {
        if (cell.Tag is not Storyboard storyboard) return;
        storyboard.Stop();
        cell.Tag = null;
        cell.Opacity = 1;
    }

    /// <summary>`aspect-ratio: 1` on the cells: the grid's height follows from its width.</summary>
    private void ApplyHeight()
    {
        if (_blocks.Length == 0 || ActualWidth <= 0) return;

        int rows = RowDefinitions.Count;
        double inner = ActualWidth - Padding.Left - Padding.Right;
        double cell = (inner - (Columns - 1) * Gap) / Columns;
        Height = rows * cell + (rows - 1) * Gap + Padding.Top + Padding.Bottom;
    }
}
