using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NetTrans.Services;

namespace NetTrans.Views.Controls;

/// <summary>The handoff's `Switch`: an iOS toggle, green when on.</summary>
public sealed partial class IosSwitch : UserControl
{
    public static readonly DependencyProperty IsOnProperty =
        DependencyProperty.Register(nameof(IsOn), typeof(bool), typeof(IosSwitch),
            new PropertyMetadata(false, (d, e) => ((IosSwitch)d).Apply(animate: e.OldValue is bool)));

    /// <summary>Raised after the user flips it, with the new value.</summary>
    public event EventHandler<bool>? Toggled;

    public bool IsOn
    {
        get => (bool)GetValue(IsOnProperty);
        set => SetValue(IsOnProperty, value);
    }

    public IosSwitch()
    {
        InitializeComponent();
        Loaded += (_, _) => Apply(animate: false);
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        IsOn = !IsOn;
        Toggled?.Invoke(this, IsOn);
        e.Handled = true;
    }

    private void Apply(bool animate)
    {
        Track.Background = ThemeBrushes.Get(IsOn ? "GreenBrush" : "Fill2Brush");

        double target = IsOn ? 22 : 2;
        if (animate) Animations.Slide(KnobOffset, "X", target, 240).Begin();
        else KnobOffset.X = target;
    }
}
