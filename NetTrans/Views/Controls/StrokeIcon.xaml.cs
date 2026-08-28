using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace NetTrans.Views.Controls;

/// <summary>
/// One icon from the handoff's `I` / `P` maps. Stroke width defaults to the 1.7
/// the prototype uses; call sites override it where the design does (2, 2.1,
/// 2.2, 2.4, 2.6).
/// </summary>
public sealed partial class StrokeIcon : UserControl
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(string), typeof(StrokeIcon),
            new PropertyMetadata(null, (d, _) => ((StrokeIcon)d).Apply()));

    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.Register(nameof(IconSize), typeof(double), typeof(StrokeIcon),
            new PropertyMetadata(24d, (d, _) => ((StrokeIcon)d).ApplySize()));

    public static readonly DependencyProperty ThicknessProperty =
        DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(StrokeIcon),
            new PropertyMetadata(1.7, (d, _) => ((StrokeIcon)d).Apply()));

    /// <summary>Filled glyphs (play / pause) paint instead of stroking.</summary>
    public static readonly DependencyProperty IsFilledProperty =
        DependencyProperty.Register(nameof(IsFilled), typeof(bool), typeof(StrokeIcon),
            new PropertyMetadata(false, (d, _) => ((StrokeIcon)d).Apply()));

    /// <summary>
    /// The icon's path data, as the key in Icons.xaml holds it.
    ///
    /// A string rather than a Geometry on purpose: a Geometry belongs to one
    /// element, so a shared resource cannot be handed to two icons. Each
    /// StrokeIcon builds its own from this.
    /// </summary>
    public string? Data
    {
        get => (string?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public bool IsFilled
    {
        get => (bool)GetValue(IsFilledProperty);
        set => SetValue(IsFilledProperty, value);
    }

    public StrokeIcon()
    {
        InitializeComponent();
        ApplySize();
        RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => Apply());
        Loaded += (_, _) => Apply();
    }

    private void ApplySize()
    {
        Width = IconSize;
        Height = IconSize;
    }

    private void Apply()
    {
        // Parsed per icon: the geometry belongs to this element's Path and
        // cannot be shared with another.
        Glyph.Data = Data is { Length: > 0 } data ? IconResources.Parse(data) : null;

        if (IsFilled)
        {
            Glyph.Fill = Foreground;
            Glyph.Stroke = null;
            Glyph.StrokeThickness = 0;
        }
        else
        {
            Glyph.Fill = null;
            Glyph.Stroke = Foreground;
            Glyph.StrokeThickness = Thickness;
        }
    }
}
