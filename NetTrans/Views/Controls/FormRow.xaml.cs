using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace NetTrans.Views.Controls;

/// <summary>
/// One iOS grouped-form row. The inspector runs the same row a size down
/// (`.side .frow`: 13.5px text, 8px/11px padding, 38px min height), which is
/// what <see cref="IsCompact"/> switches to.
/// </summary>
[ContentProperty(Name = nameof(Trailing))]
public partial class FormRow : UserControl
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(FormRow), new PropertyMetadata(""));

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(string), typeof(FormRow), new PropertyMetadata(null));

    public static readonly DependencyProperty TrailingProperty =
        DependencyProperty.Register(nameof(Trailing), typeof(object), typeof(FormRow), new PropertyMetadata(null));

    public static readonly DependencyProperty ShowChevronProperty =
        DependencyProperty.Register(nameof(ShowChevron), typeof(bool), typeof(FormRow), new PropertyMetadata(false));

    public static readonly DependencyProperty ShowSeparatorProperty =
        DependencyProperty.Register(nameof(ShowSeparator), typeof(bool), typeof(FormRow), new PropertyMetadata(true));

    public static readonly DependencyProperty IsCompactProperty =
        DependencyProperty.Register(nameof(IsCompact), typeof(bool), typeof(FormRow),
            new PropertyMetadata(false, (d, _) => ((FormRow)d).ApplyDensity()));

    public static readonly DependencyProperty IsErrorProperty =
        DependencyProperty.Register(nameof(IsError), typeof(bool), typeof(FormRow),
            new PropertyMetadata(false, (d, _) => ((FormRow)d).ApplyDensity()));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string? Value
    {
        get => (string?)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public object? Trailing
    {
        get => GetValue(TrailingProperty);
        set => SetValue(TrailingProperty, value);
    }

    public bool ShowChevron
    {
        get => (bool)GetValue(ShowChevronProperty);
        set => SetValue(ShowChevronProperty, value);
    }

    public bool ShowSeparator
    {
        get => (bool)GetValue(ShowSeparatorProperty);
        set => SetValue(ShowSeparatorProperty, value);
    }

    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    /// <summary>Renders the value in --red (the inspector's 状态 row when a task failed).</summary>
    public bool IsError
    {
        get => (bool)GetValue(IsErrorProperty);
        set => SetValue(IsErrorProperty, value);
    }

    public FormRow()
    {
        InitializeComponent();
        ApplyDensity();
    }

    private void ApplyDensity()
    {
        Surface.MinHeight = IsCompact ? 38 : 44;
        RowContent.Padding = IsCompact ? new Thickness(11, 8, 11, 8) : new Thickness(13, 10, 13, 10);

        double size = IsCompact ? 13.5 : 15;
        LabelText.FontSize = size;
        ValueText.FontSize = size;

        ValueText.Foreground = Services.ThemeBrushes.Get(IsError ? "RedBrush" : "Label2Brush");
    }
}
