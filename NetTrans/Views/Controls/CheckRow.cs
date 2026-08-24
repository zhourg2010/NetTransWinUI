using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using NetTrans.Services;

namespace NetTrans.Views.Controls;

/// <summary>
/// A `.frow` used as a checkable list item -- the pattern the 批量下载, 种子内容
/// and 视频嗅探 sheets all share: label, size on the right, and a blue check
/// that dims to --label3 when unselected.
/// </summary>
public sealed class CheckRow : FormRow
{
    private readonly StrokeIcon _check;

    public bool IsChecked { get; private set; }

    /// <summary>Raised after a tap flips the row.</summary>
    public event EventHandler<bool>? Toggled;

    public CheckRow(string label, string value, bool isChecked, bool showSeparator)
    {
        Label = label;
        Value = value;
        ShowSeparator = showSeparator;
        IsChecked = isChecked;

        _check = new StrokeIcon
        {
            Data = (Microsoft.UI.Xaml.Media.Geometry)Application.Current.Resources["IconCheck"],
            IconSize = 16,
            Thickness = 2.6,
            Width = 18,
        };

        Trailing = _check;
        ApplyCheck();

        Tapped += OnTapped;
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        IsChecked = !IsChecked;
        ApplyCheck();
        Toggled?.Invoke(this, IsChecked);
        e.Handled = true;
    }

    public void SetChecked(bool value)
    {
        if (IsChecked == value) return;
        IsChecked = value;
        ApplyCheck();
    }

    private void ApplyCheck() => _check.Foreground = ThemeBrushes.Get(IsChecked ? "BlueBrush" : "Label3Brush");
}
