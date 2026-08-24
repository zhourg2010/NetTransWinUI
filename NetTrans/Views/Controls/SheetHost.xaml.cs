using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;

namespace NetTrans.Views.Controls;

/// <summary>
/// The handoff's `Sheet`: grab handle, 取消 / title / 完成 header, scrolling body.
/// Enters with translateY(100%) -> 0 over .34s on the shared bezier.
/// </summary>
[ContentProperty(Name = nameof(Body))]
public sealed partial class SheetHost : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SheetHost), new PropertyMetadata(""));

    public static readonly DependencyProperty LeftLabelProperty =
        DependencyProperty.Register(nameof(LeftLabel), typeof(string), typeof(SheetHost), new PropertyMetadata("取消"));

    public static readonly DependencyProperty RightLabelProperty =
        DependencyProperty.Register(nameof(RightLabel), typeof(string), typeof(SheetHost), new PropertyMetadata("完成"));

    public static readonly DependencyProperty IsRightEnabledProperty =
        DependencyProperty.Register(nameof(IsRightEnabled), typeof(bool), typeof(SheetHost), new PropertyMetadata(true));

    public static readonly DependencyProperty BodyProperty =
        DependencyProperty.Register(nameof(Body), typeof(object), typeof(SheetHost), new PropertyMetadata(null));

    /// <summary>Raised for the left button and for a tap on the scrim.</summary>
    public event EventHandler? Cancelled;

    /// <summary>Raised for the confirming button (添加 / 完成 / 下载).</summary>
    public event EventHandler? Confirmed;

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string LeftLabel
    {
        get => (string)GetValue(LeftLabelProperty);
        set => SetValue(LeftLabelProperty, value);
    }

    public string RightLabel
    {
        get => (string)GetValue(RightLabelProperty);
        set => SetValue(RightLabelProperty, value);
    }

    public bool IsRightEnabled
    {
        get => (bool)GetValue(IsRightEnabledProperty);
        set => SetValue(IsRightEnabledProperty, value);
    }

    public object? Body
    {
        get => GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    public SheetHost()
    {
        InitializeComponent();
        Loaded += (_, _) => PlayEntrance();
        SizeChanged += (_, e) => Sheet.MaxHeight = e.NewSize.Height * 0.88;
    }

    private void PlayEntrance()
    {
        SheetOffset.X = 0;
        SheetOffset.Y = Sheet.ActualHeight > 0 ? Sheet.ActualHeight : 400;

        Animations.Slide(SheetOffset, "Y", 0, 340).Begin();
        Animations.Fade(Scrim, 1, 220).Begin();
    }

    /// <summary>An empty left label means the sheet has no cancel action (the 设置 sheet).</summary>
    private void OnLeftClick(object sender, RoutedEventArgs e) => Cancelled?.Invoke(this, EventArgs.Empty);

    private void OnRightClick(object sender, RoutedEventArgs e) => Confirmed?.Invoke(this, EventArgs.Empty);

    private void OnScrimTapped(object sender, TappedRoutedEventArgs e) => Cancelled?.Invoke(this, EventArgs.Empty);

    private void OnSheetTapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;
}
