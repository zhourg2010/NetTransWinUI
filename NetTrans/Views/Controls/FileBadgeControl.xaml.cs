using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NetTrans.Converters;
using NetTrans.Models;
using Windows.UI;

namespace NetTrans.Views.Controls;

public sealed partial class FileBadgeControl : UserControl
{
    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(nameof(Kind), typeof(FileKind), typeof(FileBadgeControl),
            new PropertyMetadata(FileKind.Doc, OnAppearanceChanged));

    public static readonly DependencyProperty BadgeSizeProperty =
        DependencyProperty.Register(nameof(BadgeSize), typeof(double), typeof(FileBadgeControl),
            new PropertyMetadata(36.0, OnAppearanceChanged));

    public FileKind Kind
    {
        get => (FileKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public double BadgeSize
    {
        get => (double)GetValue(BadgeSizeProperty);
        set => SetValue(BadgeSizeProperty, value);
    }

    public FileBadgeControl()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private static void OnAppearanceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((FileBadgeControl)d).Refresh();

    private void Refresh()
    {
        var color = BindingHelpers.KindColor(Kind);
        var tinted = Blend(color, Colors.White, 0.10);

        RootGrid.Width = BadgeSize;
        RootGrid.Height = BadgeSize;
        Chip.Background = new SolidColorBrush(tinted);
        Chip.BorderBrush = new SolidColorBrush(Blend(color, Colors.White, 0.55));
        Glyph.Data = BindingHelpers.KindIcon(Kind);
        Glyph.Stroke = new SolidColorBrush(color);
        Glyph.Width = Glyph.Height = BadgeSize * 0.62;
        TagBorder.Background = new SolidColorBrush(color);
        TagText.Text = BindingHelpers.KindLabel(Kind);
    }

    private static Color Blend(Color fg, Color bg, double bgWeight)
    {
        byte Mix(byte a, byte b) => (byte)(a * (1 - bgWeight) + b * bgWeight);
        return Color.FromArgb(255, Mix(fg.R, bg.R), Mix(fg.G, bg.G), Mix(fg.B, bg.B));
    }
}
